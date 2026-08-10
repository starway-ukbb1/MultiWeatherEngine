using System;
using System.Collections.Generic;
using SFS;
using SFS.UI;
using SFS.Variables;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiWeatherEngine;

// v2.0 — 多天气引擎管理器：多系统共存、自然生成、演化、移动、合并(大吞小)、F6 气象菜单。
public class TyphoonManager : MonoBehaviour
{
	public static TyphoonManager main;

	public static List<WeatherSystem> systems = new List<WeatherSystem>();

	private readonly List<StormRenderer> renderers = new List<StormRenderer>();

	public WeatherSystem selected;

	public int selectedType = 0;

	public bool menuOpen;

	// v2.3.8 优化#2 — F6 菜单 GUIStyle 缓存（原 OnGUI 每帧 new 8 个 GUIStyle + GUIStyleState
	// → 菜单开着就每帧 GC。首次创建复用；tb 分选中/未选中两态）。
	private GUIStyle mBox;

	private GUIStyle mTitle;

	private GUIStyle mSmall;

	private GUIStyle mBtn;

	private GUIStyle mTbSel;

	private GUIStyle mTb;

	private GUIStyle mLine;

	private GUIStyle mInfo;

	// v2.0.10 — 底部"活跃天气系统"面板展开状态（F9 切换，标题栏点击也可切换）。
	public static bool panelExpanded = true;

	// v2.0.13 — F6 气象菜单可拖动（拖标题栏）。
	private static Vector2 menuOffset = Vector2.zero;
	private static bool menuDragging;
	private static Vector2 menuGrabDelta;

	private double lastWorldTime = double.NaN;

	private string lastWorldKey = "";   // v2.2.1 — 世界变化检测（星球 codeName + 小时级 worldTime）

	private float shakeCooldown;

	private float spawnTimer = 25f;

	private const int MaxSystems = 10;

	private TyphoonHud hud;

	public double pS;

	public double pH;

	public double pU;

	public double pW;

	public double pAirspeed;

	public bool pValid;

	// v2.0.20 — 风场调试诊断（HUD 显示，定位"进风暴本地风不动"断点）。
	public bool diagPValid;
	public string diagType = "-";
	public double diagRo = -1.0;
	public double diagHkm;
	public double diagStrT;
	public double diagStrD;
	public double diagU;
	public double diagW;
	public double diagWindSum;
	// v2.0.53 — 下击暴流诊断（顶掉龙卷调试显示）：dro=玩家相对下暴中心距离/Rmax、spd=出流速度。
	public double diagDro;
	public double diagDspd;
	private float lastDLog = -99f;   // v2.0.53 — 下暴诊断日志节流

	private void Awake()
	{
		main = this;
		hud = ((Component)this).gameObject.AddComponent<TyphoonHud>();
	}

	public static Location ToAirspeedFrame(Location loc)
	{
		try
		{
			if (loc == null || (Object)loc.planet == (Object)null)
			{
				return loc;
			}
			Double2 wind = Double2.zero;
			bool any = false;
			for (int i = 0; i < systems.Count; i++)
			{
				WeatherSystem sys = systems[i];
				if (sys == null || !sys.active || (Object)sys.planet == (Object)null || (Object)(object)sys.planet != (Object)(object)loc.planet)
				{
					continue;
				}
				Double2 v = sys.SampleWind(loc.position);
				if (v.x != 0.0 || v.y != 0.0)
				{
					wind += v;
					any = true;
				}
			}
			if (!any)
			{
				return loc;
			}
			return new Location(loc.time, loc.planet, loc.position, loc.velocity - wind);
		}
		catch
		{
			return loc;
		}
	}

	private void Update()
	{
		HandleInput();
		AdvanceSystems();
		NaturalSpawn();
		CheckWorldChange();
		DrawMapMarkers();
		TryMergeAll();
		ProbePlayer();
		ApplyShake();
	}

	// v2.0.7 — LateUpdate（在 SFS 的 UpdatePostProcessing 之后）叠加海浪增强 + 近距风暴变灰。
	private void LateUpdate()
	{
		BoostWaves();
		ApplyProximityFX();
	}

	// v2.0.16 — 近距风暴因子 0-1.4：视野拉近到风暴占据整个天空时（距中心近 × 视距小 × 强度高）
	// 生效。用于画面变灰（ProximityFX）与抖动增强（ApplyShake）。
	private float ProximityFactor()
	{
		if (!pValid)
		{
			return 0f;
		}
		try
		{
			double vd = 0.0;
			try
			{
				vd = ((Obs<float>)(object)WorldView.main.viewDistance).Value;
			}
			catch
			{
			}
			float vdF = Mathf.Clamp01(1f - (float)(vd / 8000.0));
			if (vdF <= 0.01f)
			{
				return 0f;
			}
			Location pl = GetPlayerLocation();
			if (pl == null || pl.planet == null)
			{
				return 0f;
			}
			double bestRo = 1e9;
			double bestV = 0.0;
			StormType bestType = StormType.Cell;
			int bestCat = 0;
			for (int i = 0; i < systems.Count; i++)
			{
				WeatherSystem s = systems[i];
				if (s == null || !s.active || (Object)s.planet == (Object)null || (Object)s.planet != (Object)pl.planet)
				{
					continue;
				}
				double da = s.centerAngle - pl.position.AngleRadians;
				while (da > Math.PI)
				{
					da -= Math.PI * 2.0;
				}
				while (da < -Math.PI)
				{
					da += Math.PI * 2.0;
				}
				double ro = Math.Abs(da) * s.planet.Radius / s.Rmax;
				if (ro < bestRo)
				{
					bestRo = ro;
					bestV = s.Vmax;
					bestType = s.type;
					bestCat = s.category;
				}
			}
			if (bestRo > 1e8)
			{
				return 0f;
			}
			// v2.0.91 — 台风风眼内灰度削减（用户：风眼区域灰度剪掉、接近无灰）：
			// 玩家在台风风眼（ro < TyphoonEyeR）内 → 画面接近原色（真实风眼晴空无云不灰）。
			if (bestType == StormType.Typhoon && bestRo < WeatherSystem.TyphoonEyeR(bestCat))
			{
				return 0.02f;
			}
			float roF = Mathf.Clamp01(1f - (float)(bestRo / 1.5));
			float strF = Mathf.Clamp01((float)(bestV / 60.0));
			return Mathf.Clamp01(vdF * roF * strF * 1.4f);
		}
		catch
		{
			return 0f;
		}
	}

	// v2.2.3 — 沙尘暴能见度因子（用户：能见度，滤镜也同步）：玩家在沙尘暴沙尘层内 →
	// 后处理加昏黄滤镜（模拟能见度骤降），强度越强（特强沙尘暴 <50m 能见度）滤镜越浓。
	// 距离中心 2.5Rmax 内线性衰减（沙尘覆盖区）、沙尘层 Htop 内最强之上衰减（沙尘贴地）、
	// 按 category 放大（浮尘轻、特强重）。SFS 后处理无雾效，用饱和/对比/亮度/B 通道实现。
	private float DustFactor()
	{
		if (!pValid)
		{
			return 0f;
		}
		try
		{
			Location pl = GetPlayerLocation();
			if (pl == null || pl.planet == null)
			{
				return 0f;
			}
			double best = 0.0;
			for (int i = 0; i < systems.Count; i++)
			{
				WeatherSystem s = systems[i];
				if (s == null || !s.active || s.type != StormType.DustStorm || s.planet == null || (Object)s.planet != (Object)pl.planet)
				{
					continue;
				}
				double da = s.centerAngle - pl.position.AngleRadians;
				while (da > Math.PI)
				{
					da -= Math.PI * 2.0;
				}
				while (da < -Math.PI)
				{
					da += Math.PI * 2.0;
				}
				double ro = Math.Abs(da) * s.planet.Radius / s.Rmax;
				if (ro > 2.5)
				{
					continue;
				}
				double distF = 1.0 - ro / 2.5;                                       // 中心最浓、2.5Rmax 边缘归零
				double strF = 0.35 + 0.65 * (s.category / 6.0);                      // 强度：特强沙尘暴能见度最低
				double hF = 1.0 - 0.6 * WeatherSystem.Clamp01(pl.Height / Math.Max(s.Htop, 1.0));   // 沙尘层内最强（Location.Height = 距海平面）
				double v = distF * strF * hF;
				if (v > best)
				{
					best = v;
				}
			}
			return (float)best;
		}
		catch
		{
			return 0f;
		}
	}

	// v2.0.16 — 近距风暴变灰：风暴占据整个天空时画面变灰。
	// v2.0.38 — 全屏灰化大幅减弱（饱和度 0.25→0.55、对比 0.7→0.88、亮度 0.62→0.82）：
	// 全屏后处理会把龙卷卷尘环/漏斗等附属现象一起拉成灰暗（用户反馈"因灰色滤镜看不见
	// 龙卷触底"）——主变灰职责交回 sky 穹顶（海平面以上 maxOp 照旧盖到 1），
	// 地面/飞船区域只轻微压暗，龙卷触底细节保留可见。
	private void ApplyProximityFX()
	{
		float f = ProximityFactor();
		float dust = DustFactor();   // v2.2.3 — 沙尘暴能见度滤镜
		float g0 = Mathf.Max(f, dust);
		if (g0 < 0.02f)
		{
			return;
		}
		try
		{
			if (WorldView.main == null || WorldView.main.postProcessing == null || WorldView.main.postProcessing.Length == 0)
			{
				return;
			}
			PostProcessing pp = WorldView.main.postProcessing[0];
			if (pp == null || pp.postProcessingMaterial == null)
			{
				return;
			}
			Material m = pp.postProcessingMaterial;
			// v2.0.87 — 灰度 3 倍（用户要求）：灰化程度×3——f×3 提前到位且最终更灰
			// （原 f=1 时饱和度 0.55/亮度 0.82，现在 0.15/0.62，接近黑白）。
			float g = Mathf.Clamp01(f * 3f);
			// v2.0.91 — 高度变暗模拟云中（用户：高度 >2000m 颜色逐渐加深加黑，到云中间区域
			// 最黑，再灰和黑逐渐褪去）：以最近系统云带为基准——2000m 起线性变暗，
			// (Hbase+Htop)/2 云中峰值最黑（亮度再降 ~50%）、云顶×1.15 之上完全褪回。
			double hDark = 0.0;
			try
			{
				Location pl2 = GetPlayerLocation();
				if (pl2 != null && pl2.planet != null && pH > 2000.0)
				{
					WeatherSystem ns = null;
					double nRo = 1e9;
					for (int i = 0; i < systems.Count; i++)
					{
						WeatherSystem s = systems[i];
						if (s == null || !s.active || s.planet == null || (Object)s.planet != (Object)pl2.planet)
						{
							continue;
						}
						double da = s.centerAngle - pl2.position.AngleRadians;
						while (da > Math.PI)
						{
							da -= Math.PI * 2.0;
						}
						while (da < -Math.PI)
						{
							da += Math.PI * 2.0;
						}
						double ro = Math.Abs(da) * s.planet.Radius;
						if (ro < nRo)
						{
							nRo = ro;
							ns = s;
						}
					}
					if (ns != null && ns.Htop > 2100.0)
					{
						double peak = (ns.Hbase + ns.Htop) * 0.5;
						double end = ns.Htop * 1.15;
						if (pH < peak)
						{
							hDark = Mathf.Clamp01((float)((pH - 2000.0) / Math.Max(peak - 2000.0, 1.0)));
						}
						else
						{
							hDark = Mathf.Clamp01((float)(1.0 - (pH - peak) / Math.Max(end - peak, 1.0)));
						}
					}
				}
			}
			catch
			{
			}
			float g2 = Mathf.Clamp01(g + (float)hDark * 0.6f);   // 云中：灰度再叠加
			// v2.2.5 — 滤镜去绿（用户：太绿了）：原 _Multiplier 三通道统一降亮后 G 相对
			// 最高（人眼对绿最敏感）→ 画面发绿。沙尘主导时 R 抬 / G 压 / B 大压 → 明确
			// 黄褐色（沙尘暴昏黄）；饱和度沙尘时保留沙黄色相（0.42 而非台风灰化 0.15）。
			float dustBlend = dust / Mathf.Max(g0, 0.01f);
			float satTarget = Mathf.Lerp(0.15f, 0.42f, dustBlend);
			m.SetFloat(Shader.PropertyToID("_Saturation"), Mathf.Lerp(1f, satTarget, g2));
			m.SetFloat(Shader.PropertyToID("_Contrast"), Mathf.Lerp(1f, 0.7f, g2));
			// v2.0.92 — 云中更暗 + 偏黄（用户：还是不够暗、加入一些黄色——真实风暴云内
			// 光线偏黄褐）：亮度再降 0.5→0.7（云中峰值亮度 ~0.19，接近黑）；B 通道压低
			// （hDark=1 时 B×0.55）→ R/G 相对高、B 低 = 黄褐色调，模拟穿云的光线色温。
			// v2.0.93 — 克制 SFS 蓝色静态大气贴图（用户：背景蓝色是游戏自带大气贴图，
			// 需要其他色克一下）：B 通道除云中外，随灰化 g 也压（g=1 时再 ×0.72）——
			// 风暴内蓝色天空背景整体被压向暖灰，不再蓝得扎眼。
			// v2.2.3 — 沙尘能见度滤镜：沙尘主导时（dustBlend→1）B 通道额外压低 → 昏黄
			// 天空（现实沙尘暴视觉）+ 亮度再降（沙尘遮挡阳光）；纯台风时 dust=0 零影响。
			float lum = Mathf.Lerp(1f, 0.62f, g) * (1f - (float)hDark * 0.7f) * (1f - dustBlend * dust * 0.25f);
			float lumR = lum * (1f + dustBlend * dust * 0.14f);    // R 抬 → 黄
			float lumG = lum * (1f - dustBlend * dust * 0.12f);    // G 压 → 去绿
			float bLum = lum * (1f - (float)hDark * 0.45f) * (1f - g * 0.28f) * (1f - dustBlend * dust * 0.5f);
			m.SetVector(Shader.PropertyToID("_Multiplier"), new Vector4(lumR, lumG, bLum, 1f));
		}
		catch
		{
		}
	}

	// 海浪增强（实验）：风暴区附近水面波浪幅度（shader 若支持 _WaveHeight 属性则生效）。
	private void BoostWaves()
	{
		if (!pValid)
		{
			return;
		}
		try
		{
			if (WorldView.main == null)
			{
				return;
			}
			Planet planet = WorldView.main.ViewLocation.planet;
			if (planet == null || planet.waterMaterial == null)
			{
				return;
			}
			double wind = Math.Sqrt(pU * pU + pW * pW);
			float f = (float)Math.Min(wind / 30.0, 1.0);
			planet.waterMaterial.SetFloat(Shader.PropertyToID("_WaveHeight"), 0.02f + 0.2f * f);
		}
		catch
		{
		}
	}

	private void AdvanceSystems()
	{
		WorldTime val = WorldTime.main;
		if ((Object)val == (Object)null)
		{
			return;
		}
		double worldTime = val.worldTime;
		if (double.IsNaN(lastWorldTime))
		{
			lastWorldTime = worldTime;
			return;
		}
		double dt = worldTime - lastWorldTime;
		lastWorldTime = worldTime;
		for (int i = systems.Count - 1; i >= 0; i--)
		{
			WeatherSystem sys = systems[i];
			if (sys == null || !sys.active)
			{
				RemoveSystemAt(i);
				continue;
			}
			sys.Advance(dt);
			if (!sys.active)
			{
				Msg(WeatherSystem.TypeName(sys.type) + " 已消散（生命周期结束）");
				RemoveSystemAt(i);
			}
		}
	}

	// ===== 自然生成：有大气行星上，随机时间在玩家附近触发单体 =====
	private void NaturalSpawn()
	{
		if (!TyphoonConfig.I.naturalSpawn || systems.Count >= MaxSystems)
		{
			return;
		}
		spawnTimer -= Time.deltaTime;
		if (spawnTimer > 0f)
		{
			return;
		}
		// v2.2 — 间隔/距离可调（设置页）：30-70s → min-max；12-62km → 12km~distKm
		spawnTimer = TyphoonConfig.I.naturalSpawnMinSec + UnityEngine.Random.Range(0f, Mathf.Max(1f, TyphoonConfig.I.naturalSpawnMaxSec - TyphoonConfig.I.naturalSpawnMinSec));
		Location loc = GetPlayerLocation();
		if (loc == null || (Object)loc.planet == (Object)null || !loc.planet.HasAtmospherePhysics)
		{
			return;
		}
		if (UnityEngine.Random.value > 0.6f)
		{
			return;
		}
		// v2.2.1 — 沙尘暴自然生成（独立天气系统，用户：沙尘暴应该是独立系统才对）：
		// 玩家在沙漠地形时概率触发（蒙古气旋/冷锋驱动干旱区沙尘暴，不依赖雷暴）。
		bool inDesert = false;
		try
		{
			inDesert = WeatherSystem.TerrainAt(loc.planet, loc.position) == TerrainKind.Desert;
		}
		catch
		{
		}
		if (inDesert && UnityEngine.Random.value < 0.55f)
		{
			double leadD = (double)(8000f + UnityEngine.Random.Range(0f, 30000f)) * (UnityEngine.Random.value < 0.5f ? 1.0 : -1.0);
			SpawnSystem(StormType.DustStorm, loc, leadD, 0, true);
			return;
		}
		StormType t = (UnityEngine.Random.value < 0.55f) ? StormType.Cell : ((UnityEngine.Random.value < 0.7f) ? StormType.Multicell : StormType.Supercell);
		double lead = (double)UnityEngine.Random.Range(12000f, Mathf.Max(12001f, TyphoonConfig.I.naturalSpawnDistKm * 1000f)) * (UnityEngine.Random.value < 0.5f ? 1.0 : -1.0);
		SpawnSystem(t, loc, lead, 0, true);   // v2.0.88 — 自然生成静默（不弹提示）
	}

	// ===== v2.2.1 — 预生成：进存档/换星球时给当前行星播种（世界一进去就是活的） =====
	// 世界变化双信号：玩家星球 codeName 变（换星球）或小时级 worldTime 变（重进存档归零）。
	// 只处理玩家当前行星（用户选定：不全局播种，系统数可控不爆 MaxSystems）。
	private void CheckWorldChange()
	{
		try
		{
			if (!TyphoonConfig.I.preSpawnEnabled || TyphoonConfig.I.preSpawnCountPerPlanet <= 0)
			{
				return;
			}
			Location pl = GetPlayerLocation();
			if (pl == null || (Object)pl.planet == (Object)null)
			{
				return;
			}
			double wt = 0.0;
			WorldTime vwt = WorldTime.main;
			if ((Object)vwt != (Object)null)
			{
				wt = vwt.worldTime;
			}
			string key = pl.planet.codeName + "_" + ((long)(wt / 3600.0)).ToString();
			if (key == lastWorldKey)
			{
				return;
			}
			lastWorldKey = key;
			PreSpawnCurrentPlanet(pl);
		}
		catch
		{
		}
	}

	private void PreSpawnCurrentPlanet(Location pl)
	{
		if (!pl.planet.HasAtmospherePhysics)
		{
			return;
		}
		int cur = 0;
		for (int i = 0; i < systems.Count; i++)
		{
			if (systems[i] != null && systems[i].active && (Object)systems[i].planet == (Object)pl.planet)
			{
				cur++;
			}
		}
		int need = TyphoonConfig.I.preSpawnCountPerPlanet - cur;
		for (int n = 0; n < need; n++)
		{
			if (systems.Count >= MaxSystems)
			{
				break;
			}
			// 类型：对流为主（Cell/Multicell/Supercell），台风概率 preSpawnTyphoonChance。
			float r = UnityEngine.Random.value;
			StormType t = (r < TyphoonConfig.I.preSpawnTyphoonChance) ? StormType.Typhoon
				: ((r < TyphoonConfig.I.preSpawnTyphoonChance + 0.55f) ? StormType.Cell
				: ((r < TyphoonConfig.I.preSpawnTyphoonChance + 0.8f) ? StormType.Multicell : StormType.Supercell));
			// 距离 40-140km 沿经度偏移（避开玩家视线 ±20°，比自然生成的 12-62km 更远，不"贴脸"出现）。
			double lead = (double)UnityEngine.Random.Range(40000f, 140000f) * (UnityEngine.Random.value < 0.5f ? 1.0 : -1.0);
			SpawnSystem(t, pl, lead, 0, true);
		}
	}

	// ===== v2.2.1 — 地图标记：M 地图视图显示风暴点+文字标签（颜色=强度色） =====
	// 走 SFS.World.Maps：MapDrawer.DrawPointWithText（点+文字一步），位置 = 风暴中心
	// 行星局部坐标 → GetPosition（mapHolder + pos/1000）。节流 1s 防闪烁/费性能。
	private float mapMarkerTimer = 0f;

	private void DrawMapMarkers()
	{
		if (!TyphoonConfig.I.mapMarkers)
		{
			return;
		}
		mapMarkerTimer -= Time.deltaTime;
		if (mapMarkerTimer > 0f)
		{
			return;
		}
		mapMarkerTimer = 1f;
		try
		{
			// 判空保护：未进世界/地图系统未初始化时 elementDrawer 为 null。
			if (Map.manager == null || !Map.manager.mapMode.Value || Map.elementDrawer == null)
			{
				return;
			}
			for (int i = 0; i < systems.Count; i++)
			{
				WeatherSystem s = systems[i];
				if (s == null || !s.active || (Object)s.planet == (Object)null)
				{
					continue;
				}
				Double2 c = s.MergedStormC();
				Vector2 pos = (Vector2)MapDrawer.GetPosition(s.planet, c);
				Color col = Category.Tint[s.category];
				string txt = WeatherSystem.TypeName(s.type) + "C" + s.category + " " + (WeatherSystem.Clamp01(s.energy / 80.0) * 100.0).ToString("0") + "%";
				Vector2 normal = (Vector2)c.normalized;
				MapDrawer.DrawPointWithText(16, col, txt, 12, col, pos, normal, 0, 0);
			}
		}
		catch
		{
		}
	}

	// ===== 合并：大吞小（v2.0.51 加合并动画：不再瞬间移除） =====
	private void TryMergeAll()
	{
		for (int i = 0; i < systems.Count; i++)
		{
			for (int j = i + 1; j < systems.Count; j++)
			{
				WeatherSystem a = systems[i];
				WeatherSystem b = systems[j];
				if (a == null || b == null || !a.active || !b.active || (Object)(object)a.planet != (Object)(object)b.planet)
				{
					continue;
				}
				double ang = Math.Abs(WeatherSystem.WrapPi(a.centerAngle - b.centerAngle));
				double dist = ang * a.planet.Radius;
				// v2.0.64 — 合并判定放宽：1.2×(Ra+Rb)（边缘刚接触触发）。
				// v2.0.73 — 台风吞噬整个区域（用户：有自然风暴在台风内部都不合并）：任一系统
				// 是台风时，合并距离用台风 Router×0.9（Router=Rmax×9，整个台风影响区域）——
				// 风暴进入台风区域即被吞；非台风仍用 1.2×(Ra+Rb)。
				double mergeDist;
				if (a.type == StormType.Typhoon)
				{
					mergeDist = a.Router * 0.9;
				}
				else if (b.type == StormType.Typhoon)
				{
					mergeDist = b.Router * 0.9;
				}
				else
				{
					mergeDist = (a.Rmax + b.Rmax) * 1.2;
				}
				if (dist > mergeDist)
				{
					continue;
				}
				WeatherSystem big = (a.Rmax >= b.Rmax) ? a : b;
				WeatherSystem small = (a.Rmax >= b.Rmax) ? b : a;
				// v2.0.51 — 合并动画：正在合并中的系统不再参与新合并。
				if (small.mergeAnimT >= 0.0 || big.mergeAnimT >= 0.0)
				{
					continue;
				}
				// 启动合并动画：small 向 big 靠近 + 渐隐（3 秒后移除）。
				small.mergeTarget = big;
				small.mergeMode = (big.type == StormType.Typhoon) ? 1 : 0;   // 台风吞噬=拆散飞向台风
				small.mergeAnimT = 0.0;
				if (big.type == StormType.Typhoon && small.type == StormType.Typhoon)
				{
					// 双台风合并：双方粒子互相靠近中点、重组新台风（big 保留吸收 small）。
					small.mergeMode = 2;
					big.mergeMode = 2;
					big.mergeTarget = small;
					big.mergeAnimT = 0.0;
					big.mergeMidAng = small.mergeMidAng = WeatherSystem.WrapPi((big.centerAngle + small.centerAngle) * 0.5);
				}
				// v2.0.2 — 合并增强递减 + 上限（防无限加强）：越吞越少，且不超基准 3x/2x。
				big.mergeCount++;
				double gain = 1.0 / (1.0 + big.mergeCount * 0.6);
				big.Rmax = Math.Min(big.Rmax * (1.0 + 0.12 * gain), big.rmaxBase * 3.0);
				big.Router = big.Rmax * 9.0;
				big.Vmax = Math.Min(big.Vmax * (1.0 + 0.08 * gain), big.vmaxBase * 2.0);
				// v2.0.99 — 修复：v2.0.98 强度平滑后风场用 vmaxDisplay，合并增强直接改 Vmax
				// 不生效（回归）。同步 vmaxTarget → display 平滑爬升 3 秒呈现合并增强。
				// v2.3.7 — 审查二轮：同步 vmaxTargetBase（原漏同步 → 能量驱动公式下一帧把
				// 合并 +8% 风速抹回旧档位基准，功能回归）+ 合并回补能量 max(70)（重组新生
				// 语义，🟡-13）。
				big.vmaxTargetBase = big.Vmax;
				big.vmaxTarget = big.Vmax;
				big.intensity = 1.0;
				big.stage = 1;
				big.energy = Math.Max(big.energy, 70.0);
				Msg(WeatherSystem.TypeName(small.type) + " 被 " + WeatherSystem.TypeName(big.type) + " 吞并（合并增强 #" + big.mergeCount + "）");
				break;
			}
		}
		// v2.0.51 — 合并动画完成清理：被吞方（mode 0/1）移除；双台风（mode 2）big 保留、重置状态。
		for (int k = systems.Count - 1; k >= 0; k--)
		{
			WeatherSystem s = systems[k];
			if (s.mergeAnimT >= 1.0)
			{
				if (s.mergeMode == 2)
				{
					s.mergeAnimT = -1.0;
					s.mergeTarget = null;
				}
				else
				{
					RemoveSystem(s);
				}
			}
		}
	}

	private void HandleInput()
	{
		bool flag = Input.GetKey((KeyCode)304) || Input.GetKey((KeyCode)303);
		if (Input.GetKeyDown((KeyCode)288)) // F7 — 仅解散选中系统（召唤走 F6 菜单）
		{
			if (flag)
			{
				TyphoonConfig.I.hud = !TyphoonConfig.I.hud;
				return;
			}
			if (selected != null && selected.active)
			{
				RemoveSystem(selected);
				selected = null;
				Msg("已解散选中的天气系统");
			}
			return;
		}
		if (Input.GetKeyDown((KeyCode)287)) // F6 — 气象菜单开关
		{
			menuOpen = !menuOpen;
			Msg("气象菜单 " + (menuOpen ? "开" : "关") + "（F6）");
			return;
		}
		if (Input.GetKeyDown((KeyCode)289) && selected != null && selected.active) // F8 强度
		{
			// v2.0.98 — 强度切换走 SetCategory（vmaxTarget 平滑过渡 ~3 秒，粒子过渡动画；
			// 附属现象强度同步跟母体）；手动切档重置自然发展进度。
			selected.SetCategory((selected.category + 1) % 7);
			selected.naturalProgress = 0.0;
			// v2.2（专项 A 可选）— F8 手动切档回补能量到 80（"手动强化=回满成熟能量"，
			// god mode 期待落地；仍守 80 封顶不破设计）。
			selected.energy = Math.Max(selected.energy, 80.0);
			Msg(WeatherSystem.TypeName(selected.type) + " 强度 -> " + WeatherSystem.StrengthName(selected.type, selected.category) + "  (" + selected.vmaxTarget.ToString("0") + " m/s)");
			return;
		}
		if (Input.GetKeyDown((KeyCode)290)) // F9 — 底部活跃系统面板 展开/收起
		{
			panelExpanded = !panelExpanded;
			Msg("活跃天气系统面板 " + (panelExpanded ? "展开" : "收起") + "（F9）");
			return;
		}
		// v2.2 — 调试按键（F2-F5 风区移动 / Shift+F2 区域黑框）已全部移除（用户要求清理）。
	}

	// v2.0.88 — silent=true：自然生成调用不弹 Msg（用户：自然生成风暴提示删除；手动保留）。
	public WeatherSystem SpawnSystem(StormType type, Location at, double leadMeters, int catBoost, bool silent = false)
	{
		if (at == null || (Object)at.planet == (Object)null)
		{
			Msg("需要先进入一个世界（有星球）才能生成");
			return null;
		}
		if (!at.planet.HasAtmospherePhysics)
		{
			Msg(at.planet.codeName + " 没有大气，无法生成对流系统");
			return null;
		}
		if (systems.Count >= MaxSystems)
		{
			Msg("天气系统已达上限 " + MaxSystems);
			return null;
		}
		// v2.0.73 — 生成避让（用户：禁止任何风暴生成在已有风暴的区域）：新系统位置
		// 落在任一现有系统区域（台风=Router、其他=2.4×Rmax 有效区）内则拒绝生成。
		double newAngle = at.position.AngleRadians + leadMeters / at.planet.Radius;
		for (int gi = 0; gi < systems.Count; gi++)
		{
			WeatherSystem ex = systems[gi];
			if (ex == null || !ex.active || (Object)ex.planet != (Object)at.planet)
			{
				continue;
			}
			double gang = Math.Abs(WeatherSystem.WrapPi(ex.centerAngle - newAngle)) * ex.planet.Radius;
			double exZone = (ex.type == StormType.Typhoon) ? ex.Router * 0.9 : ex.Rmax * 2.4;
			if (gang < exZone)
			{
				Msg("拒绝生成：" + WeatherSystem.TypeName(type) + " 与 " + WeatherSystem.TypeName(ex.type) + " 区域重叠（禁止风暴生成在已有风暴区域）");
				return null;
			}
		}
		WeatherSystem sys = new WeatherSystem();
		double angle = newAngle;
		sys.seed = UnityEngine.Random.Range(1, 100000);
		sys.Configure(type, at.planet, angle, catBoost);
		systems.Add(sys);
		CreateRenderer(sys);
		if (!silent)
		{
			Msg(WeatherSystem.TypeName(type) + " 生成：" + WeatherSystem.StrengthName(type, sys.category) + "  半径 " + (sys.Rmax / 1000.0).ToString("0.#") + " km  云顶 " + (sys.Htop / 1000.0).ToString("0.#") + " km  峰值风 " + sys.Vmax.ToString("0") + " m/s");
		}
		if (selected == null)
		{
			selected = sys;
		}
		return sys;
	}

	private void CreateRenderer(WeatherSystem sys)
	{
		GameObject go = new GameObject("TyphoonRenderer_" + WeatherSystem.TypeName(sys.type));
		go.transform.parent = base.transform;
		StormRenderer r = go.AddComponent<StormRenderer>();
		r.storm = sys;
		r.Rebuild();
		r.spawnAnimT = 0f;   // v2.0.98 — 生成动画：云粒子从透明渐入（2 秒）
		renderers.Add(r);
	}

	private void RemoveSystem(WeatherSystem sys)
	{
		systems.Remove(sys);
		for (int i = renderers.Count - 1; i >= 0; i--)
		{
			// v2.0.1 — WeatherSystem 不是 UnityEngine.Object，(Object) 强转恒 null 曾导致比较恒 true、渲染器全被误删。
			if (renderers[i] != null && renderers[i].storm == sys)
			{
				renderers[i].Clear();
				Object.Destroy(renderers[i].gameObject);
				renderers.RemoveAt(i);
			}
		}
		if (selected == sys)
		{
			selected = null;
		}
	}

	private void RemoveSystemAt(int idx)
	{
		if (idx < 0 || idx >= systems.Count)
		{
			return;
		}
		WeatherSystem sys = systems[idx];
		systems.RemoveAt(idx);
		for (int i = renderers.Count - 1; i >= 0; i--)
		{
			if (renderers[i] != null && renderers[i].storm == sys)
			{
				renderers[i].Clear();
				Object.Destroy(renderers[i].gameObject);
				renderers.RemoveAt(i);
			}
		}
		if (selected == sys)
		{
			selected = null;
		}
	}

	public static Location GetPlayerLocation()
	{
		try
		{
			PlayerController val = PlayerController.main;
			if ((Object)val == (Object)null || (Object)(object)((Obs_Destroyable<Player>)(object)val.player).Value == (Object)null)
			{
				return null;
			}
			Player value = ((Obs_Destroyable<Player>)(object)val.player).Value;
			if ((Object)value == (Object)null || (Object)(object)value.location == (Object)null)
			{
				return null;
			}
			return value.location.Value;
		}
		catch
		{
			return null;
		}
	}

	// ===== 玩家风场采样（所有系统叠加） =====
	private void ProbePlayer()
	{
		pValid = false;
		Location playerLocation = GetPlayerLocation();
		if (playerLocation == null || (Object)playerLocation.planet == (Object)null)
		{
			return;
		}
		Double2 wind = Double2.zero;
		bool any = false;
		WeatherSystem nearest = null;
		double nearestDist = double.MaxValue;
		for (int i = 0; i < systems.Count; i++)
		{
			WeatherSystem sys = systems[i];
			if (sys == null || !sys.active || (Object)(object)sys.planet != (Object)(object)playerLocation.planet)
			{
				continue;
			}
			sys.Probe(playerLocation.position, out var s, out var h, out var u, out var w);
			if (Math.Abs(s) < nearestDist)
			{
				nearestDist = Math.Abs(s);
				nearest = sys;
				pS = s;
				pH = h;
				// v2.0.23 — HUD 显示改用矢量风分解（含龙卷螺旋/下击暴流辐散）：
				// pU=行星切向分量、pW=行星径向（垂直）分量。
				Double2 vecW = sys.SampleWindLocal(s, h, playerLocation.position);
				Double2 nrm2 = playerLocation.position.normalized;
				Double2 tanV = new Double2(0.0 - nrm2.y, nrm2.x);
				pU = Double2.Dot(vecW, tanV);
				pW = Double2.Dot(vecW, nrm2);
			}
			wind += sys.SampleWindLocal(s, h, playerLocation.position);
			any = true;
		}
		if (!any)
		{
			diagPValid = false;
			return;
		}
		Double2 val2 = playerLocation.velocity - wind;
		pAirspeed = val2.magnitude;
		pValid = true;
		// v2.0.20 — 风场诊断：最近系统的类型/ro/高度/附属强度/原始采样分量。
		diagPValid = true;
		diagWindSum = wind.magnitude;
		if (nearest != null)
		{
			diagType = WeatherSystem.TypeName(nearest.type);
			diagRo = Math.Abs(pS) / nearest.Rmax;
			diagHkm = pH / 1000.0;
			diagStrT = nearest.tornadoStrength;
			diagStrD = nearest.downburstStrength;
			diagU = pU;
			diagW = pW;
			// v2.0.53 — 下击暴流诊断：dro=玩家相对下暴中心距离/Rmax（v2.0.56 中心保底出流，
			// dro=0 时 spread=0.5 出流 30 m/s，dro→1.2 满 60 m/s），spd=该点出流速度。
			// dro 缓慢增长=风暴漂移（5-7 m/s）正常现象。
			diagDro = Math.Abs(pS) / Math.Max(nearest.Rmax * 0.9, 300.0);
			diagDspd = nearest.DownburstSpeedAt(pS, pH, playerLocation.position);
			// v2.0.53 — 下暴诊断文件日志（Player.log），每 2 秒一条防刷屏：
			// str=强度 dro=距离比(0.4-1.6 起效) spd=出流速度 u/w=风分量。
			if (nearest.downburstStrength > 0.05 && Time.unscaledTime - lastDLog > 2f)
			{
				lastDLog = Time.unscaledTime;
				Debug.Log("[Typhoon] 下暴诊断 类型=" + diagType + " str=" + diagStrD.ToString("0.00") + " dro=" + diagDro.ToString("0.00") + " spd=" + diagDspd.ToString("0") + "m/s u=" + diagU.ToString("0") + " w=" + diagW.ToString("0") + " h=" + diagHkm.ToString("0.00") + "km");
			}
		}
		_ = nearest;
	}

	private void ApplyShake()
	{
		if (!TyphoonConfig.I.cameraShake || !pValid)
		{
			return;
		}
		shakeCooldown -= Time.unscaledDeltaTime;
		if (shakeCooldown > 0f)
		{
			return;
		}
		shakeCooldown = 0.12f;
		double num = Math.Sqrt(pU * pU + pW * pW);
		if (num < 12.0)
		{
			return;
		}
		PlayerController val = PlayerController.main;
		if ((Object)val == (Object)null)
		{
			return;
		}
		Location playerLocation = GetPlayerLocation();
		if (playerLocation == null)
		{
			return;
		}
		// v2.0.16 — 近距风暴抖动增强：风暴占据整个天空时（ProximityFactor）强度最多 ×4。
		// v2.0.85 — 抖动加剧 3 倍（用户要求）：0.045 → 0.135。
		float num2 = (float)(Math.Min(num / 70.0, 1.4) * 0.135 * TyphoonConfig.I.cameraShakeScale);
		num2 *= 1f + ProximityFactor() * 3f;
		try
		{
			val.CreateShakeEffect(num2, 0.18f, 100000f, WorldView.ToLocalPosition(playerLocation.position));
		}
		catch
		{
		}
	}

	// ===== F6 气象菜单（v2.0.13 横版 · 可拖动 · 深空色系） =====
	private void OnGUI()
	{
		if (!menuOpen || !TyphoonConfig.I.hud)
		{
			return;
		}
		float bw = 680f;
		float bh = 390f;   // v2.3.8.8 — 7 类型网格 3 行修复后内容到底 ~422px，360 太紧 → 390
		float baseX = ((float)Screen.width - bw) * 0.5f;
		float baseY = 70f;
		Rect w = new Rect(baseX + menuOffset.x, baseY + menuOffset.y, bw, bh);
		Event e = Event.current;
		// v2.0.13 — 拖动：按住标题栏移动。
		// v2.3.9 — 修复关闭按钮无响应（用户：指挥中心关闭按钮无法发挥作用）：拖动热区
		// 原覆盖整个标题栏（含右上角关闭按钮），MouseDown 先命中热区 e.Use() 消费事件，
		// 按钮永远收不到点击。热区排除右侧 80px（关闭按钮区域）。
		if (e.type == EventType.MouseDown && e.button == 0 && new Rect(w.x, w.y, bw - 80f, 34f).Contains(e.mousePosition))
		{
			menuDragging = true;
			menuGrabDelta = e.mousePosition - new Vector2(w.x, w.y);
			e.Use();
		}
		else if (e.type == EventType.MouseDrag && menuDragging)
		{
			menuOffset = e.mousePosition - menuGrabDelta - new Vector2(baseX, baseY);
			e.Use();
		}
		else if (e.type == EventType.MouseUp && menuDragging)
		{
			menuDragging = false;
			e.Use();
		}
		// 深空色系背景（v2.3.8 优化#2 — GUIStyle 缓存）
		if (mBox == null)
		{
			mBox = new GUIStyle(GUI.skin.box);
			mBox.alignment = TextAnchor.UpperLeft;
			mBox.fontSize = 12;
			mBox.font = CnFont();   // v2.3.8.6 — 中文字体（原方块）
			mBox.normal.background = DeepSpaceTex();
			mBox.border = new RectOffset(8, 8, 8, 8);
		}
		GUIStyle box = mBox;
		GUI.Box(w, GUIContent.none, box);
		float x = w.x + 14f;
		float y = w.y + 10f;
		if (mTitle == null)
		{
			mTitle = new GUIStyle(GUI.skin.label);
			mTitle.fontSize = 17;
			mTitle.fontStyle = FontStyle.Bold;
			mTitle.font = CnFont();
			mTitle.normal.textColor = new Color(0.72f, 0.86f, 1f);
		}
		GUIStyle title = mTitle;
		GUI.Label(new Rect(x, y, 420f, 24f), "◆ 气象指挥中心", title);
		if (mSmall == null)
		{
			mSmall = new GUIStyle(GUI.skin.label);
			mSmall.fontSize = 11;
			mSmall.font = CnFont();
			mSmall.normal.textColor = new Color(0.52f, 0.64f, 0.8f);
		}
		GUIStyle small = mSmall;
		GUI.Label(new Rect(x + 170f, y + 8f, 420f, 18f), "点击类型即刻召唤 · 拖标题栏可移动", small);
		if (mBtn == null)
		{
			mBtn = new GUIStyle(GUI.skin.button);
			mBtn.fontSize = 12;
			mBtn.font = CnFont();
			mBtn.normal.textColor = new Color(0.82f, 0.91f, 1f);
		}
		GUIStyle btn = mBtn;
		if (GUI.Button(new Rect(w.x + w.width - 64f, y, 50f, 22f), "关闭", btn))
		{
			menuOpen = false;
			return;
		}
		y += 32f;
		// 类型网格：3 列 × 3 行（7 类型 ceil(7/3)=3 行，按钮含名称 + 简介两行）——v2.2（终审🟢-11）注释修正
		float cw = 208f;
		float ch = 52f;
		float gapX = 10f;
		int n = WeatherSystem.Spec.Length;
		for (int i = 0; i < n; i++)
		{
			int col = i % 3;
			int row = i / 3;
			float bx = x + col * (cw + gapX);
			float by = y + row * (ch + 8f);
			bool sel = i == selectedType;
			// v2.3.8 优化#2 — tb 两态缓存（循环内不再每帧 new）
			if (mTbSel == null)
			{
				mTbSel = new GUIStyle(GUI.skin.box);
				mTbSel.fontSize = 13;
				mTbSel.fontStyle = FontStyle.Bold;
				mTbSel.alignment = TextAnchor.MiddleCenter;
				mTbSel.font = CnFont();
				mTbSel.normal.textColor = new Color(0.55f, 0.86f, 1f);
			}
			if (mTb == null)
			{
				mTb = new GUIStyle(GUI.skin.button);
				mTb.fontSize = 13;
				mTb.fontStyle = FontStyle.Bold;
				mTb.alignment = TextAnchor.MiddleCenter;
				mTb.font = CnFont();
				mTb.normal.textColor = new Color(0.85f, 0.92f, 1f);
			}
			GUIStyle tb = sel ? mTbSel : mTb;
			if (GUI.Button(new Rect(bx, by, cw, ch), WeatherSystem.Spec[i].name + "\n" + WeatherSystem.Spec[i].desc, tb))
			{
				selectedType = i;
				Location loc = GetPlayerLocation();
				if (loc != null && loc.planet != null)
				{
					// v2.2.7 — F6 召唤偏移视距自适应（用户：放台风从左到右逐渐消失——原固定
					// 60km 偏移超出 SFS 相机可视距离（far clip），风暴近侧可见、远侧被裁 →
					// 渐变消失）。保证召唤的风暴落在可视范围内（下限 4km、上限视距×0.55，
					// 不超配置值）。
					double leadF6 = TyphoonConfig.I.spawnLeadDistanceMeters;
					try
					{
						double vdF6 = ((Obs<float>)(object)WorldView.main.viewDistance).Value;
						leadF6 = Math.Min(leadF6, Math.Max(4000.0, vdF6 * 0.55));
					}
					catch
					{
					}
					SpawnSystem((StormType)i, loc, leadF6, 0);
				}
			}
		}
		// v2.3.8.8 — 修复沙尘暴与下方按钮重叠（用户：沙尘暴选项和下方的按钮重叠了）：
		// 网格按 col=i%3, row=i/3 排列，7 类型 = 3 行，但 y 只推进 2 行高度（2*(ch+8)）——
		// 第 3 行（沙尘暴）正好压到分隔线/操作按钮上。改按实际行数推进（ceil(n/3)）。
		y += ((n + 2) / 3) * (ch + 8f) + 12f;
		// 分隔线（v2.3.8 优化#2 — GUIStyle + GUIStyleState 缓存，原每帧 new 两个）
		if (mLine == null)
		{
			mLine = new GUIStyle(GUI.skin.label);
			mLine.font = CnFont();
			mLine.normal.background = SolidLine();
		}
		GUI.Label(new Rect(x, y - 8f, bw - 28f, 2f), "", mLine);
		// 操作行（作用于底部监控面板选中的系统）——v2.1.2 两行：行1 现象添加，行2 全局操作
		float opW = 122f;
		float opGap = 8f;
		if (GUI.Button(new Rect(x, y, opW, 24f), "加龙卷", btn))
		{
			AddPhenomenon(1);
		}
		if (GUI.Button(new Rect(x + (opW + opGap), y, opW, 24f), "加下暴", btn))
		{
			AddPhenomenon(2);
		}
		if (GUI.Button(new Rect(x + (opW + opGap) * 2f, y, opW, 24f), "加阵风锋", btn))   // v2.1.2
		{
			AddPhenomenon(3);
		}
		if (GUI.Button(new Rect(x + (opW + opGap) * 3f, y, opW, 24f), "加闪电", btn))     // v2.1.2
		{
			AddPhenomenon(4);
		}
		y += 32f;
		if (GUI.Button(new Rect(x, y, opW, 24f), "清除附属", btn))
		{
			if (selected != null && selected.active)
			{
				selected.ClearPhenomena();
				Msg("已清除 " + WeatherSystem.TypeName(selected.type) + " 的附属现象");
			}
			else
			{
				Msg("请先在底部监控面板选中一个风暴");
			}
		}
		if (GUI.Button(new Rect(x + (opW + opGap), y, opW, 24f), "全部分散", btn))
		{
			DespawnAll();
		}
		if (GUI.Button(new Rect(x + (opW + opGap) * 2f, y, opW, 24f), "风眼对准", btn))
		{
			if (selected != null && selected.active)
			{
				Location loc = GetPlayerLocation();
				if (loc != null)
				{
					selected.centerAngle = loc.position.AngleRadians;
					Msg("风眼已对准当前位置");
				}
			}
		}
		y += 32f;
		// 底部信息：选中系统状态
		string info;
		if (selected != null && selected.active)
		{
			info = "已选中  " + WeatherSystem.TypeName(selected.type) + "  " + WeatherSystem.StrengthName(selected.type, selected.category)
				+ "  [" + (selected.stage == 0 ? "发展" : (selected.stage == 1 ? "成熟" : "消散")) + "]"
				+ "  峰风 " + selected.Vmax.ToString("0") + " m/s  半径 " + (selected.Rmax / 1000.0).ToString("0.##") + " km  云顶 " + (selected.Htop / 1000.0).ToString("0.0") + " km"
				+ "   [F8] 换档 [F7] 解散";
		}
		else
		{
			info = "未选中系统 — 在底部监控面板点击风暴后，方可添加龙卷 / 下击暴流";
		}
		// v2.3.8 优化#2 — infoSt 缓存
		if (mInfo == null)
		{
			mInfo = new GUIStyle(GUI.skin.label);
			mInfo.fontSize = 12;
			mInfo.font = CnFont();
			mInfo.normal.textColor = new Color(0.78f, 0.88f, 1f);
		}
		GUIStyle infoSt = mInfo;
		GUI.Label(new Rect(x, y, bw - 28f, 20f), info, infoSt);
	}

	// v2.0.13 — 附属现象统一入口：强制基于底部面板选中系统 + 类型检测。
	private void AddPhenomenon(int kind)
	{
		if (selected == null || !selected.active)
		{
			Msg("请先在底部监控面板选中一个风暴（点击系统行）");
			return;
		}
		if (kind == 1)
		{
			if (selected.AddTornado())
			{
				Msg("已为 " + WeatherSystem.TypeName(selected.type) + " 添加龙卷");
			}
			else
			{
				Msg("✖ " + WeatherSystem.TypeName(selected.type) + " 无法产生龙卷（仅 超级单体 / 飑线 / MCS 可挂载）");
			}
		}
	else if (kind == 2)
	{
		// v2.0.93 — 下暴宿主白名单（用户：禁用台风/单体/多单体生成下击暴流）
		if (selected.AddDownburst())
		{
			Msg("已为 " + WeatherSystem.TypeName(selected.type) + " 添加下击暴流");
		}
		else
		{
			Msg("✖ " + WeatherSystem.TypeName(selected.type) + " 无法产生下击暴流（仅 超级单体 / 飑线 / MCS 可挂载）");
		}
	}
	else if (kind == 3)   // v2.1.2 — 阵风锋
	{
		if (selected.AddGustFront())
		{
			Msg("已为 " + WeatherSystem.TypeName(selected.type) + " 添加阵风锋");
		}
		else
		{
			Msg("✖ " + WeatherSystem.TypeName(selected.type) + " 无法产生阵风锋（仅 超级单体 / 飑线 / MCS 可挂载）");
		}
	}
	else if (kind == 4)   // v2.1.2 — 闪电风暴
	{
		if (selected.AddLightningBurst())
		{
			Msg("已为 " + WeatherSystem.TypeName(selected.type) + " 添加闪电风暴");
		}
		else
		{
			Msg("✖ 无法添加闪电风暴");
		}
	}
}

	private static Texture2D deepSpaceTex;

	private static Texture2D DeepSpaceTex()
	{
		if (deepSpaceTex == null)
		{
			deepSpaceTex = new Texture2D(1, 1);
			deepSpaceTex.SetPixel(0, 0, new Color(0.016f, 0.024f, 0.05f, 0.94f));
			deepSpaceTex.Apply();
		}
		return deepSpaceTex;
	}

	private static Texture2D lineTex;

	private static Texture2D SolidLine()
	{
		if (lineTex == null)
		{
			lineTex = new Texture2D(1, 1);
			lineTex.SetPixel(0, 0, new Color(1f, 1f, 1f, 0.14f));
			lineTex.Apply();
		}
		return lineTex;
	}

	// v2.3.8.6 — UI 中文字体（用户：找 UI 问题——SFS 默认字体 FuturaPTBook SDF 无中文
	// 字形，HUD/菜单全部中文显示方块 □，日志 \u8D28 was not found 刷屏）。运行时从 OS
	// 加载中文字体（微软雅黑/黑体等），失败回退默认字体（不崩）。static 缓存只建一次。
	private static Font cnFont;

	public static Font CnFont()
	{
		if (cnFont == null)
		{
			try
			{
				cnFont = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "Noto Sans CJK SC" }, 14);
			}
			catch
			{
				cnFont = null;
			}
		}
		return cnFont;
	}

	public void DespawnAll()
	{
		for (int i = systems.Count - 1; i >= 0; i--)
		{
			RemoveSystemAt(i);
		}
		selected = null;
		Msg("全部天气系统已消散（剩余 " + systems.Count + "）");
	}

	public static void Msg(string s)
	{
		Debug.Log((object)("[Typhoon] " + s));
		try
		{
			MsgDrawer.main.Log(s);
		}
		catch
		{
		}
	}
}
