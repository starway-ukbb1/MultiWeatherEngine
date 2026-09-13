using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using SFS;
using SFS.Parts;
using SFS.UI;
using SFS.Variables;
using SFS.World;
using SFS.World.Maps;
using SFS.WorldBase;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiWeatherEngine;

// 多天气引擎管理器：多系统共存、自然生成、演化、移动、合并(大吞小)、F6 气象菜单。
public class TyphoonManager : MonoBehaviour
{
	public static TyphoonManager main;

	public static List<WeatherSystem> systems = new List<WeatherSystem>();

	private readonly List<StormRenderer> renderers = new List<StormRenderer>();

	public WeatherSystem selected;

	public int selectedType = 0;

	public bool menuOpen;

	// 底部"活跃天气系统"面板展开状态（F9 切换，标题栏点击也可切换）。
	public static bool panelExpanded = true;

	// F6 气象菜单可拖动（拖标题栏）。
	private static Vector2 menuOffset = Vector2.zero;

	private double lastWorldTime = double.NaN;

	// 世界变化检测：原每帧拼 `codeName + "_" + hour` 字符串比较（每帧 GC 分配），
	// 改 code/hour 分离存储，仅整数/引用比较，字符串只在真正变化时构造。
	private string lastWorldCode = "";

	private long lastWorldHour = -1;

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

	// 风场调试诊断（HUD 显示，定位"进风暴本地风不动"断点）。
	public bool diagPValid;
	public string diagType = "-";
	public double diagRo = -1.0;
	public double diagHkm;
	public double diagStrT;
	public double diagStrD;
	public double diagU;
	public double diagW;
	public double diagWindSum;
	// 下击暴流诊断（顶掉龙卷调试显示）：dro=玩家相对下暴中心距离/Rmax、spd=出流速度。
	public double diagDro;
	public double diagDspd;
	private float lastDLog = -99f;   // 下暴诊断日志节流

	private void Awake()
	{
		main = this;
		hud = ((Component)this).gameObject.AddComponent<TyphoonHud>();
		// 天气音效（程序化合成雷/风/雨）
		((Component)this).gameObject.AddComponent<WeatherAudio>();
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
		CheckSceneBoundary();
		HandleInput();
		// 本帧游戏时间差（世界时间，钳 30s/帧）：系统演化与自然生成共用同一口径。
		double gameDt = AdvanceGameClock();
		AdvanceSystems(gameDt);
		NaturalSpawn(gameDt);
		CheckWorldChange();
		TryMergeAll();
		ProbePlayer();
		CheckDebrisImpacts();
		ApplyShake();
		UpdateWeatherAudio();
	}

	// ===== 风暴抛出的障碍物（树/石）撞击火箭 =====
	// 用户点出的机制落地：游戏原生就有 DestructionReason.RocketCollision / TerrainCollision，
	// 只要障碍物撞上火箭，解体 + 失败菜单全由游戏自己处理。这里只负责"判定撞击 + 触发"：
	// debris 由本 mod 自研模拟（质点 + 气动阻力，见 WeatherSystem.AdvanceDebris），
	// 位置是行星全局坐标，比较前统一转 Unity 世界坐标（与部件 transform.position 同系）。
	// 致命阈值：相对速度 ≥ 25 m/s（低速掠过只推挤不毁，避免"轻轻擦一下就没"）。
	private const double DebrisLethalSpeed = 25.0;
	// 龙卷漏斗内能见度（0-1）：供 StormRenderer 画屏幕级沙尘遮蔽，也供后处理变暗。
	public static float tornadoObscure;
	// 沙尘暴能见度（0-1，DustFactor 结果）：供 StormRenderer 把沙墙/云体粒子按强度增密（粒子雾）。
	public static float dustObscure;
	// 云/雨包裹能见度（逐型对标现实）：rainVisM = 有效水平能见度(米)，rainWrap = 滤镜强度(0-1)，
	// rainDustTint = 是否沙尘主导（褐而非灰）。供 ApplyFog 开真实视距雾 + 后处理灰化。
	public float rainVisM = 1e9f;   // 当前有效水平能见度(米)，HUD 读取展示
	public bool rainVisTornado;     // 当前能见度是否由龙卷沙幕主导（HUD 标签用）
	private float rainWrap;

	// 最近龙卷预警数据（CheckDebrisImpacts 逐帧算，供 WeatherAudio 起播预警曲）：
	// nearestTornadoEta = 最近"逼近中"龙卷的抵达剩余现实秒（-1 = 无逼近中的龙卷）。
	public static float nearestTornadoEta = -1f;
	public static float nearestTornadoDistM = -1f;
	// 该"逼近中"龙卷的核半径（米，-1 = 无）：音频层用它选常规曲/大尺度备选曲。
	public static float nearestTornadoCoreM = -1f;
	// 该龙卷的现实移速（米/现实秒）：音频层用它估算遭遇时长，决定曲子是否提速。
	public static float nearestTornadoSpeedReal = -1f;

	private void CheckDebrisImpacts()
	{
		tornadoObscure = 0f;
		nearestTornadoEta = -1f;
		nearestTornadoDistM = -1f;
		nearestTornadoCoreM = -1f;
		nearestTornadoSpeedReal = -1f;
		if (systems.Count == 0)
		{
			return;
		}
		try
		{
			GameManager gm = GameManager.main;
			List<Rocket> rockets = (gm != null) ? gm.rockets : null;
			for (int si = 0; si < systems.Count; si++)
			{
				WeatherSystem s = systems[si];
				if (s == null || !s.active || s.planet == null)
				{
					continue;
				}
				// ① 能见度：相机（玩家）离任一龙卷核心多近、是否在云层之下
				if (s.tornadoes.Count > 0)
				{
					try
					{
						Location cam = GetPlayerLocation();
						if (cam != null && (Object)cam.planet == (Object)s.planet)
						{
							s.ToStormFrame(cam.position, out double cs, out double ch);
							double camAng = cam.position.AngleRadians;
							double pr = cam.planet.Radius;
							// 预警曲是"地面龙卷警笛"：玩家高于阈值（默认 12km）不算逼近。
							bool altOk = ch <= Math.Max(0f, TyphoonConfig.I.tornadoThemeMaxAltM);
							for (int ti = 0; ti < s.tornadoes.Count; ti++)
							{
								WeatherSystem.FxInst fx = s.tornadoes[ti];
								if (fx.strength <= 0.15)
								{
									continue;
								}
								// ① 龙卷预警 ETA（与 tornadoObscure 开关无关）：龙卷锚在风暴的
								// 切向偏移上、随风暴整体向 +角 移动 → 玩家在西侧就是逼近中。
								// 供 WeatherAudio 在抵达前 N 现实秒起播预警曲。
								// 计时基准取**漏斗边缘**（扣掉核半径）：风暴移速被 ×0.35 缩放到
								// 4-6 m/s，"距中心 15 秒"只等于 60-90m（那时漏斗早罩住你了）——
								// 扣掉核半径后，"到达"= 漏斗壁碰到你，15 秒 ≈ 230m 外，才是想要的预警量。
								double offM = WeatherSystem.WrapPi(s.centerAngle + fx.sOff * s.Rmax / pr - camAng) * pr;
								double absM = Math.Abs(offM);
								if (altOk && (nearestTornadoDistM < 0f || absM < nearestTornadoDistM))
								{
									nearestTornadoDistM = (float)absM;
								}
								// 接近速度 = 风暴移速 − 飞船自身沿轨道切向速度（+角为正）：
								// 原地不动 = 只靠风暴移速（原行为）；迎上去更早触发、同向逃跑则解除。
								double vT = PlayerOrbitSpeed(cam);
								double closing = s.moveSpeed - vT;
								if (altOk && offM < 0.0 && closing > 0.25)
								{
									double coreM = s.TornadoCoreR(fx);
									double spanM = absM - coreM;
									float warp = Mathf.Max(timeScaleReal, 0.01f);
									// 换算成**现实秒**（÷时间加速倍率）：HUD 倒计时与预警曲都按
									// 玩家真实感受到的时间走，不看时间加速。
									float eta = (float)(Math.Max(0.0, spanM) / closing / warp);
									if (nearestTornadoEta < 0f || eta < nearestTornadoEta)
									{
										nearestTornadoEta = eta;
										nearestTornadoCoreM = (float)coreM;   // 最紧迫的那个决定用哪首曲子
										nearestTornadoSpeedReal = (float)(closing / warp);
									}
								}
								if (!TyphoonConfig.I.tornadoObscure)
								{
									continue;
								}
								double dx = cs - fx.sOff * s.Rmax;
								// 判定半径 = 核半径×4（碎屑云比冷凝漏斗宽；原 Rmax×0.55≈2km
								// 与真正能卷人的风场对不上）。垂直尺度同风场，1.6 次幂衰减。
								float rad = (float)(s.TornadoCoreR(fx) * 4.0);
								float fh = (float)WeatherSystem.Clamp01(1.0 - ch / Math.Max(s.Hbase * 1.2, 1.0));
								double dT = WeatherSystem.Clamp01(Math.Abs(dx) / rad);
								float fd = 1f - (float)Math.Pow(dT, 1.6);
								float f = (float)(fd * fh * Math.Min(1.0, fx.strength));
								if (f > tornadoObscure)
								{
									tornadoObscure = f;
								}
							}
						}
					}
					catch
					{
					}
				}
				// ② 撞击判定
				if (rockets == null || s.debris.Count == 0)
				{
					continue;
				}
				for (int di = s.debris.Count - 1; di >= 0; di--)
				{
					WeatherSystem.DebrisInst d = s.debris[di];
					if (!d.airborne || Math.Abs(d.vs) < 10.0)
					{
						continue;   // 没被刮起来 / 几乎静止的不参与
					}
					double R = s.planet.Radius;
					double ang = s.centerAngle + d.s / R;
					Double2 dGlobal = new Double2(Math.Cos(ang) * (R + d.h), Math.Sin(ang) * (R + d.h));
					Vector2 dWorld = WorldView.ToLocalPosition(dGlobal);
					Vector2 dVel = new Vector2((float)((0.0 - Math.Sin(ang)) * d.vs + Math.Cos(ang) * d.vh),
						(float)(Math.Cos(ang) * d.vs + Math.Sin(ang) * d.vh));
					for (int ri = 0; ri < rockets.Count; ri++)
					{
						Rocket r = rockets[ri];
						if (r == null || r.rb2d == null || r.partHolder == null || (Object)r.location == null)
						{
							continue;
						}
						if ((Object)r.location.Value.planet != (Object)s.planet)
						{
							continue;
						}
						Vector2 rc = r.rb2d.worldCenterOfMass;
						float hitR = RocketHitRadius(r) + (float)d.size;
						if ((dWorld - rc).sqrMagnitude > hitR * hitR)
						{
							continue;
						}
						// 相对速度 → 是否致命
						Vector2 rVel = r.rb2d.linearVelocity;
						double rel = (dVel - rVel).magnitude;
						Vector2 dir = (rVel - dVel).normalized;
						if (TyphoonConfig.I.debrisHurtsRockets && rel >= DebrisLethalSpeed)
						{
							Msg((d.kind == 1 ? "被卷起的树木" : "被卷起的石块") + "砸中 " + rocketName(r) + "（相对速度 " + rel.ToString("0") + " m/s）→ 解体");
							RocketManager.DestroyRocket(r, DestructionReason.RocketCollision);
							s.debris.RemoveAt(di);
							break;
						}
						// 低速：只推挤（让玩家感到"被东西撞了"）
						try
						{
							r.rb2d.AddForce(dir * (float)(0.5 * d.mass * rel), ForceMode2D.Impulse);
						}
						catch
						{
						}
						s.debris.RemoveAt(di);
						break;
					}
				}
			}
		}
		catch
		{
		}
	}

	// 火箭碰撞半径估值（缓存 1s）：取所有部件离质心的最大距离（部件的 Unity 世界坐标）。
	private Rocket hitRocket;
	private float hitRocketR = 8f;
	private float hitRocketT = -99f;

	private float RocketHitRadius(Rocket r)
	{
		if (r == hitRocket && Time.unscaledTime - hitRocketT < 1f)
		{
			return hitRocketR;
		}
		hitRocket = r;
		hitRocketT = Time.unscaledTime;
		hitRocketR = 8f;
		try
		{
			Vector2 c = r.rb2d.worldCenterOfMass;
			List<Part> parts = r.partHolder.parts;
			float max2 = 0f;
			for (int i = 0; i < parts.Count; i++)
			{
				if (parts[i] == null)
				{
					continue;
				}
				Vector2 p = parts[i].transform.position;
				float dd = (p - c).sqrMagnitude;
				if (dd > max2)
				{
					max2 = dd;
				}
			}
			if (max2 > 1f)
			{
				hitRocketR = (float)Math.Sqrt(max2);
			}
		}
		catch
		{
		}
		return hitRocketR;
	}

	private static string rocketName(Rocket r)
	{
		try
		{
			PlayerController pc = PlayerController.main;
			if ((Object)pc != (Object)null && (Object)(object)pc.player.Value == (Object)(object)r && r.stats != null)
			{
				return "你的" + r.rocketName;
			}
			return r.rocketName;
		}
		catch
		{
			return "火箭";
		}
	}

	// 世界时间推进（游戏秒）。自然生成频率按**游戏时间**计时（用户要求）：时间加速
	// 倍率越高，游戏时间走得越快 → 风暴生成/演化同步快进，与 mod 的能量制生命周期、
	// 移动、升级同一套"时间加速=快进等待"语义。原实现用 Time.deltaTime（现实秒）计时，
	// 于是 1000× 加速下"每 30-70 现实秒一个风暴"= 13-28 游戏小时一个，与现实节奏割裂。
	private double AdvanceGameClock()
	{
		WorldTime val = WorldTime.main;
		if ((Object)val == (Object)null)
		{
			gameDt = 0.0;
			return 0.0;
		}
		double worldTime = val.worldTime;
		if (double.IsNaN(lastWorldTime))
		{
			lastWorldTime = worldTime;
			gameDt = 0.0;
			return 0.0;
		}
		double dt = worldTime - lastWorldTime;
		lastWorldTime = worldTime;
		if (dt < 0.0)
		{
			dt = 0.0;   // 重进存档/换星球世界时间归零：不推进（等下一帧重新建立基准）
		}
		else if (dt > 30.0)
		{
			dt = 30.0;
		}
		gameDt = dt;
		// 时间加速倍率（游戏秒/现实秒）：预警曲的提前量是"现实秒"口径（用户明确要
		// "抵达前 15 现实秒"），而 ETA 是用游戏秒算的（距离÷移速）→ 必须换算，
		// 否则 1000× 加速下 15 游戏秒 = 0.015 现实秒，音乐会等到龙卷贴脸才响。
		float rdt = Time.unscaledDeltaTime;
		if (dt > 0.0 && rdt > 1e-5f)
		{
			timeScaleReal = Mathf.Clamp((float)(dt / rdt), 0.02f, 100000f);
		}
		return dt;
	}

	private double gameDt;

	// 游戏秒 / 现实秒（= SFS 时间加速倍率）。1× 时 ≈ 1。
	public static float timeScaleReal = 1f;

	// fix — 飞行场景边界检测：WorldView.main 只在飞行视图存在。从飞行场景
	// （WorldView.main != null）切到建造/主菜单（== null）时，清空所有天气系统——
	// 它们属于旧世界（行星引用可能已失效），残留会导致 HUD 面板/系统列表跨场景乱入
	// （用户反馈"面板在建造页面都能出现"）。边沿触发：只在 有→无 瞬间清一次。
	private bool lastInWorld;

	private void CheckSceneBoundary()
	{
		bool inWorld = WorldView.main != null;
		if (lastInWorld && !inWorld)
		{
			for (int i = systems.Count - 1; i >= 0; i--)
			{
				RemoveSystemAt(i);
			}
			selected = null;
			menuOpen = false;
			thunderCount.Clear();
			lastWorldCode = "";
			lastWorldHour = -1;
			lastWorldTime = double.NaN;
			Msg("离开世界，天气系统已清空");
		}
		lastInWorld = inWorld;
	}

	// LateUpdate（在 SFS 的 UpdatePostProcessing 之后）叠加海浪增强 + 近距风暴变灰。
	private void LateUpdate()
	{
		BoostWaves();
		ApplyProximityFX();
	}

	// 近距风暴因子 0-1.4：视野拉近到风暴占据整个天空时（距中心近 × 视距小 × 强度高）
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
			// 台风风眼内灰度削减（用户：风眼区域灰度剪掉、接近无灰）：
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

	// 沙尘暴能见度因子（用户：能见度，滤镜也同步）：玩家在沙尘暴沙尘层内 →
	// 后处理加昏黄滤镜（模拟能见度骤降），强度越强（特强沙尘暴 <50m 能见度）滤镜越浓。
	// 距离中心 2.5Rmax 内线性衰减（沙尘覆盖区）、沙尘层 Htop 内最强之上衰减（沙尘贴地）、
	// 按 category 放大（浮尘轻、特强重）。SFS 后处理无雾效，用饱和/对比/亮度/B 通道实现。
	private float DustFactor()
	{
		dustObscure = 0f;
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
			dustObscure = (float)best;
			return (float)best;
		}
		catch
		{
			dustObscure = 0f;
			return 0f;
		}
	}

	// 逐型"被云/雨包裹"能见度（对标现实）：玩家在任一风暴的雨幕/云体内时，
	// 取所有包裹现象中最差（最小）的水平能见度 rainVisM（米），并算滤镜强度 rainWrap。
	// 台风风眼内无雨→不包裹；云顶之上→不在云中；沙尘暴走 GB/T 20480（DustFactor 强度）。
	private void ComputeRainVisibility()
	{
		rainVisM = 1e9f;
		rainWrap = 0f;
		rainVisTornado = false;
		if (!pValid)
		{
			return;
		}
		try
		{
			Location pl = GetPlayerLocation();
			if (pl == null || pl.planet == null)
			{
				return;
			}
			double bestVis = 1e9;
			float bestWrap = 0f;
			// 沙尘暴：DustFactor 强度 → GB/T 20480 能见度（浮尘/扬沙 ~8km → 特强 <200m）
			float dust = DustFactor();
			if (dust > 0.02f)
			{
				double dv = 8000.0 * (1.0 - 0.975 * (double)dust);   // dust=1 → 200m
				if (dv < bestVis)
				{
					bestVis = dv;
				}
				bestWrap = Mathf.Max(bestWrap, dust);
			}
			// 龙卷沙幕能见度（对标现实）：玩家在漏斗/碎屑区 tornadoObscure(0-1)。
			// 龙卷碎屑云能见度可骤降至近 0 → obsc=1 ~10m、obsc=0.5 ~410m（HUD 显示 + 滤镜强度）。
			float tor = (TyphoonConfig.I.tornadoObscure ? tornadoObscure : 0f);
			if (tor > 0.02f)
			{
				double tv = 800.0 * (1.0 - 0.9875 * (double)tor);   // ~10m..800m（贴现实：漏斗内碎屑云能见度数米级）
				if (tv < bestVis)
				{
					bestVis = tv;
					rainVisTornado = true;
				}
				bestWrap = Mathf.Max(bestWrap, tor);
			}
			if (TyphoonConfig.I.rainObscure)
			{
				for (int i = 0; i < systems.Count; i++)
				{
					WeatherSystem s = systems[i];
					if (s == null || !s.active || s.planet == null || (Object)s.planet != (Object)pl.planet)
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
					double ro = Math.Abs(da) * s.planet.Radius / s.Rmax;   // 归一化水平半径
					if (WeatherSystem.IsPrecipFree(s.type))
					{
						// 浓雾：贴地雾层 —— 玩家在雾层内时能见度骤降至数百米（屏幕级白/灰雾）。
						// 原实现把浓雾直接 continue → 进浓雾屏幕毫无变化。
						if (s.type == StormType.DenseFog && ro <= 1.6 && pl.Height <= s.Htop * 1.15)
						{
							double hSpanF = Math.Max(s.Htop - s.Hbase, 1.0);
							double hFf = WeatherSystem.Clamp01(1.0 - Math.Max(0.0, pl.Height - s.Hbase) / hSpanF);
							float wrapF = (float)((1.0 - ro / 1.6) * (0.5 + 0.5 * hFf));
							double visFog = 150.0 + 950.0 * (1.0 - wrapF);   // 雾心 ~150m、边缘 ~1.1km
							if (visFog < bestVis)
							{
								bestVis = visFog;
							}
							bestWrap = Mathf.Max(bestWrap, wrapF);
						}
						continue;   // 其余无降水系统（沙尘暴/尘卷风）：已由 DustFactor 单独处理
					}
					// 台风风眼：无雨、可见蓝天 → 不包裹（风眼内反而看得远）
					double eyeRT = (s.type == StormType.Typhoon) ? WeatherSystem.TyphoonEyeR(s.category) : 0.0;
					if (eyeRT > 0.05 && ro < eyeRT)
					{
						continue;
					}
					if (pl.Height > s.Htop * 1.15)   // 云顶之上：不在云中
					{
						continue;
					}
					double wrapFrac = 1.8;   // 雨幕外缘 ~1.8 Rmax
					if (ro > wrapFrac)
					{
						continue;
					}
					double hSpan = Math.Max(s.Htop - s.Hbase, 1.0);
					double hF = WeatherSystem.Clamp01(1.0 - Math.Max(0.0, pl.Height - s.Hbase) / hSpan);   // 云底以下最浓
					double rF = 1.0 - ro / wrapFrac;   // 中心最浓
					float wrap = (float)(rF * (0.5 + 0.5 * hF));
					double baseV = WeatherSystem.BaseVisM(s.type, s.category);
					if (baseV < 0.0)
					{
						continue;
					}
					double visHere = baseV * (0.6 + 0.4 * (double)wrap);   // 边缘略清
					if (visHere < bestVis)
					{
						bestVis = visHere;
					}
					bestWrap = Mathf.Max(bestWrap, wrap);
				}
			}
			if (bestVis < 1e8)
			{
				rainVisM = (float)bestVis;
				rainWrap = bestWrap;
			}
		}
		catch
		{
		}
	}

	// 真实视距雾：rainVisM < 12km 时开指数雾，密度 = 3.9/rainVisM（rainVisM 处透射 ~2%，
	// 即"游戏里只能看见这个数"）。雾色：沙尘昏黄 / 阴雨冷灰。SFS 自身无雾效，关时还原。
	// 近距风暴变灰：风暴占据整个天空时画面变灰。
	// 全屏灰化大幅减弱（饱和度 0.25→0.55、对比 0.7→0.88、亮度 0.62→0.82）：
	// 全屏后处理会把龙卷卷尘环/漏斗等附属现象一起拉成灰暗（用户反馈"因灰色滤镜看不见
	// 龙卷触底"）——主变灰职责交回 sky 穹顶（海平面以上 maxOp 照旧盖到 1），
	// 地面/飞船区域只轻微压暗，龙卷触底细节保留可见。
	private void ApplyProximityFX()
	{
		float f = ProximityFactor();
		float dust = DustFactor();   // 沙尘暴能见度滤镜
		// 龙卷内部能见度骤降（用户要求）：obsc = 玩家在漏斗沙尘区的程度（0-1，
		// CheckDebrisImpacts 逐帧算）。原来只有"近距风暴变灰"，进龙卷反而还看得清；
		// 现在把它作为最强的滤镜输入 —— 沙褐 + 压暗 + 高对比 = 沙幕糊脸。
		float obsc = (TyphoonConfig.I.tornadoObscure ? tornadoObscure : 0f);
		// 逐型云/雨包裹能见度（对标现实）：算有效能见度（rainVisM，供 HUD/滤镜映射）。
		// SFS 原生不支持雾 → 用后处理滤镜颜色/强度表达"看不清"，能见度越低灰得越狠。
		ComputeRainVisibility();
		// 滤镜强度：空间包裹(rainWrap) + 真实能见度映射(visFactor)。暴雨<1km → visFactor~0.83。
		float visFactor = (rainVisM < 12000f) ? Mathf.Clamp01(1f - rainVisM / 6000f) : 0f;
		float rw = Mathf.Max(rainWrap, visFactor);   // 云/雨灰化强度（已在 ComputeRainVisibility 内按开关判定）
		float g0 = Mathf.Max(f, Mathf.Max(dust, Mathf.Max(obsc, rw)));
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
			// 灰度 3 倍（用户要求）：灰化程度×3——f×3 提前到位且最终更灰
			// （原 f=1 时饱和度 0.55/亮度 0.82，现在 0.15/0.62，接近黑白）。
			float g = Mathf.Clamp01(f * 3f);
			// 高度变暗模拟云中（用户：高度 >2000m 颜色逐渐加深加黑，到云中间区域
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
			float g2 = Mathf.Clamp01(g + (float)hDark * 0.6f + obsc * 0.9f + rw * 0.8f);   // 云中/龙卷沙幕/雨包裹再叠灰
			// 滤镜去绿（用户：太绿了）：原 _Multiplier 三通道统一降亮后 G 相对
			// 最高（人眼对绿最敏感）→ 画面发绿。沙尘主导时 R 抬 / G 压 / B 大压 → 明确
			// 黄褐色（沙尘暴昏黄）；饱和度沙尘时保留沙黄色相（0.42 而非台风灰化 0.15）。
			float dustBlend = Mathf.Max(dust, obsc) / Mathf.Max(g0, 0.01f);
			float rainBlend = rw / Mathf.Max(g0, 0.01f);   // 雨/云包裹占比
			float satTarget = Mathf.Lerp(0.15f, 0.42f, dustBlend);
			// 强沙尘暴→更"糊"（饱和再压一点、趋于均匀褐白化），但仍保沙黄相（不灰化）。
			satTarget = Mathf.Lerp(satTarget, 0.30f, dustBlend * dustBlend);
			// 雨/云包裹：去饱和更强（冷灰），比台风灰化(0.15)更灰 → 凸显"看不清"
			satTarget = Mathf.Lerp(satTarget, 0.08f, rainBlend * (1f - dustBlend));
			m.SetFloat(Shader.PropertyToID("_Saturation"), Mathf.Lerp(1f, satTarget, g2));
			m.SetFloat(Shader.PropertyToID("_Contrast"), Mathf.Lerp(1f, 0.7f, g2));
			// 云中更暗 + 偏黄（用户：还是不够暗、加入一些黄色——真实风暴云内
			// 光线偏黄褐）：亮度再降 0.5→0.7（云中峰值亮度 ~0.19，接近黑）；B 通道压低
			// （hDark=1 时 B×0.55）→ R/G 相对高、B 低 = 黄褐色调，模拟穿云的光线色温。
			// 克制 SFS 蓝色静态大气贴图（用户：背景蓝色是游戏自带大气贴图，
			// 需要其他色克一下）：B 通道除云中外，随灰化 g 也压（g=1 时再 ×0.72）——
			// 风暴内蓝色天空背景整体被压向暖灰，不再蓝得扎眼。
			// 沙尘能见度滤镜：沙尘主导时（dustBlend→1）B 通道额外压低 → 昏黄
			// 天空（现实沙尘暴视觉）+ 亮度再降（沙尘遮挡阳光）；纯台风时 dust=0 零影响。
			float lum = Mathf.Lerp(1f, 0.62f, g) * (1f - (float)hDark * 0.7f) * (1f - dustBlend * dust * 0.4f) * (1f - obsc * 0.55f) * (1f - rw * 0.45f);
			float lumR = lum * (1f + dustBlend * dust * 0.14f);    // R 抬 → 黄
			float lumG = lum * (1f - dustBlend * dust * 0.12f);    // G 压 → 去绿
			// 雨包裹时保留 B 通道（冷灰，不压暖）；沙尘仍压 B（昏黄）。
			float bWarm = 1f - (float)hDark * 0.45f - g * 0.28f * (1f - rainBlend * 0.85f)
				- dustBlend * dust * 0.5f - obsc * 0.35f;
			float bLum = lum * Mathf.Clamp(bWarm, 0.05f, 1.2f) * (1f + rainBlend * 0.05f);
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

	private void AdvanceSystems(double dt)
	{
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

	// ===== 自然生成：有大气行星上，按**游戏时间**间隔在玩家附近触发对流系统 =====
	// 计时口径 = 世界时间（gameDt）：时间加速倍率越高，游戏时间推进越快 → 生成同步快进
	// （与生命周期/移动/升级同一套语义）。间隔与距离均由设置页给出（间隔单位：游戏秒，
	// 设置页以"游戏分钟"呈现）；默认 1-3 游戏小时一个对流系统——现实一个 ~60km 半径的
	// 区域内，强对流生成事件就是这个量级（一天数次）。
	private void NaturalSpawn(double gameDt)
	{
		if (!TyphoonConfig.I.naturalSpawn || systems.Count >= MaxSystems)
		{
			return;
		}
		if (gameDt <= 0.0)
		{
			return;   // 暂停/未进世界：游戏时间不推进 → 不生成
		}
		spawnTimer -= (float)gameDt;
		if (spawnTimer > 0f)
		{
			return;
		}
		// 下一次尝试间隔（游戏秒）：min ~ max 均匀随机。计数器按游戏时间走，
		// 因此这里不再叠加"额外概率门"——间隔本身就是希望玩家看到的生成节奏。
		float minSec = Mathf.Max(1f, TyphoonConfig.I.naturalSpawnMinSec);
		float maxSec = Mathf.Max(minSec + 1f, TyphoonConfig.I.naturalSpawnMaxSec);
		spawnTimer = UnityEngine.Random.Range(minSec, maxSec);
		Location loc = GetPlayerLocation();
		if (loc == null || (Object)loc.planet == (Object)null || !loc.planet.HasAtmospherePhysics)
		{
			return;
		}
		// 类型抽签（2026-09-12 扩展：6 类新天气按地形/相态分布；无降水系统独立于对流族）。
		TerrainKind tk = TerrainKind.Green;
		try
		{
			tk = WeatherSystem.TerrainAt(loc.planet, loc.position);
		}
		catch
		{
		}
		if (tk == TerrainKind.Desert && UnityEngine.Random.value < 0.55f)
		{
			// 干旱区：沙尘暴（Haboob 大范围）与尘卷风（晴空小旋，更常见）二选一
			double leadD = (double)(8000f + UnityEngine.Random.Range(0f, 30000f)) * (UnityEngine.Random.value < 0.5f ? 1.0 : -1.0);
			SpawnSystem((UnityEngine.Random.value < 0.5f) ? StormType.DustStorm : StormType.DustDevil, loc, leadD, 0, true);
			return;
		}
		double lead = (double)UnityEngine.Random.Range(12000f, Mathf.Max(12001f, TyphoonConfig.I.naturalSpawnDistKm * 1000f)) * (UnityEngine.Random.value < 0.5f ? 1.0 : -1.0);
		// 冷季固态降水（冰原/绿地高纬）：冬季风暴（暴雪）与冰暴（冻雨）
		if ((tk == TerrainKind.Ice || tk == TerrainKind.Green) && UnityEngine.Random.value < 0.30f)
		{
			SpawnSystem((UnityEngine.Random.value < 0.6f) ? StormType.WinterStorm : StormType.IceStorm, loc, lead, 0, true);
			return;
		}
		// 海洋：大气河（水汽输送带）与浓雾（海雾）
		if (tk == TerrainKind.Ocean && UnityEngine.Random.value < 0.28f)
		{
			SpawnSystem((UnityEngine.Random.value < 0.6f) ? StormType.AtmosphericRiver : StormType.DenseFog, loc, lead, 0, true);
			return;
		}
		// 陆地静稳辐射雾（浓雾）
		if (UnityEngine.Random.value < 0.14f)
		{
			SpawnSystem(StormType.DenseFog, loc, lead, 0, true);
			return;
		}
		// 温带气旋：大尺度锋面系统，低概率出现
		if (UnityEngine.Random.value < 0.10f)
		{
			SpawnSystem(StormType.ExtratropicalCyclone, loc, lead, 0, true);
			return;
		}
		// 其余为对流族（原抽签：单体 55% / 多单体 15% / 超级单体 30%）
		StormType t = (UnityEngine.Random.value < 0.55f) ? StormType.Cell : ((UnityEngine.Random.value < 0.7f) ? StormType.Multicell : StormType.Supercell);
		SpawnSystem(t, loc, lead, 0, true);   // 自然生成静默（不弹提示）
	}

	// ===== — 预生成：进存档/换星球时给当前行星播种（世界一进去就是活的） =====
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
			string code = pl.planet.codeName;
			long hour = (long)(wt / 3600.0);
			if (code == lastWorldCode && hour == lastWorldHour)
			{
				return;
			}
			lastWorldCode = code;
			lastWorldHour = hour;
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
			// 类型：对流为主（Cell/Multicell/Supercell），台风概率 preSpawnTyphoonChance；
			// 2026-09-12 扩展：其余概率分给 6 类新天气（温带气旋/冬季风暴/大气河/浓雾/沙尘暴/尘卷风），
			// 让进存档时天气图景多样（不再只有对流族）。
			float r = UnityEngine.Random.value;
			float tp = TyphoonConfig.I.preSpawnTyphoonChance;
			StormType t;
			if (r < tp) t = StormType.Typhoon;
			else if (r < tp + 0.40f) t = StormType.Cell;
			else if (r < tp + 0.55f) t = StormType.Multicell;
			else if (r < tp + 0.72f) t = StormType.Supercell;
			else if (r < tp + 0.80f) t = StormType.ExtratropicalCyclone;
			else if (r < tp + 0.87f) t = StormType.WinterStorm;
			else if (r < tp + 0.91f) t = StormType.AtmosphericRiver;
			else if (r < tp + 0.95f) t = StormType.DenseFog;
			else if (r < tp + 0.98f) t = StormType.DustStorm;
			else t = StormType.DustDevil;
			// 距离 40-140km 沿经度偏移（避开玩家视线 ±20°，比自然生成的 12-62km 更远，不"贴脸"出现）。
			double lead = (double)UnityEngine.Random.Range(40000f, 140000f) * (UnityEngine.Random.value < 0.5f ? 1.0 : -1.0);
			SpawnSystem(t, pl, lead, 0, true);
		}
	}

	// ===== — 地图标记：M 地图视图显示风暴轮廓+点+文字标签（颜色=强度色） =====
	// fix — 关键修正（参考 AeroTrajectory mod）：地图元素每帧被 MapManager.DrawMap
	// 开头 DrawReset() 清空——在 Update 里画必被清掉，必须用 Harmony Transpiler patch 插进
	// DrawMap（DrawTrajectories 之后、DrawReset 之后）才能活下来。文字/点用世界坐标 xy
	// （mapHolder.position + pos/1000，DrawTextElement 内部补 z = 视距/1000）；
	// 轮廓用 Map.solidLine.DrawLine（线挂 planet.mapHolder，局部坐标 ÷1000）。
	// 地图标记矩形顶点复用（原每帧 new Vector3[5]×2 → 静态复用，地图开着时
	// 每帧省 2 次数组分配；DrawLine 同步消费不保留引用，安全）。
	private static readonly Vector3[] s_mapPts = new Vector3[5];

	public static void DrawMapMarkers()
	{
		try
		{
			// 判空保护：未进世界/地图系统未初始化时 elementDrawer 为 null；
			// 地图关着（mapMode=false）时直接跳过（省性能，DrawMap 每帧都会跑）。
			if (Map.manager == null || !Map.manager.mapMode.Value || Map.elementDrawer == null)
			{
				return;
			}
			if (!TyphoonConfig.I.mapMarkers)
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
				Color col = Category.Tint[s.category];
				// fix3 — 所有风暴轮廓改矩形（用户：所有风暴改矩形）：
				// 核心矩形半边长 = Rmax（边长 2Rmax = 风暴核心直径，与原圆半径直接对应；
				// 外接圆半径 √2×Rmax ≈ 1.41Rmax，方块比圆多包 41% 角区——要方块就按方块算）。
				// 矩形 4 角 + 闭合回起点 = 5 顶点；行星局部坐标 ÷1000 直接喂 LineDrawer
				// （线挂 planet.mapHolder，DrawReset 每帧清池不累积）。平面近似，小范围够用。
				if (Map.solidLine != null)
				{
					double h = s.Rmax;
					Vector3[] pts = s_mapPts;   // 复用静态数组
					pts[0] = (Vector3)(Vector2)((c + new Double2(-h, -h)) / 1000.0);
					pts[1] = (Vector3)(Vector2)((c + new Double2(h, -h)) / 1000.0);
					pts[2] = (Vector3)(Vector2)((c + new Double2(h, h)) / 1000.0);
					pts[3] = (Vector3)(Vector2)((c + new Double2(-h, h)) / 1000.0);
					pts[4] = pts[0];   // 闭合
					Map.solidLine.DrawLine(pts, s.planet, col, col);
				}
				// 台风外围：虚线矩形（半边长 2.5×Rmax → 边长 5Rmax，对应原 2.5Rmax 虚线环直径；
				// Router=9Rmax 太大不画，2.5Rmax ≈ 外围雨带范围）。
				if (s.type == StormType.Typhoon && Map.dashedLine != null)
				{
					double h2 = s.Rmax * 2.5;
					Vector3[] pts = s_mapPts;   // 复用静态数组
					Color dim = new Color(col.r, col.g, col.b, 0.6f);
					pts[0] = (Vector3)(Vector2)((c + new Double2(-h2, -h2)) / 1000.0);
					pts[1] = (Vector3)(Vector2)((c + new Double2(h2, -h2)) / 1000.0);
					pts[2] = (Vector3)(Vector2)((c + new Double2(h2, h2)) / 1000.0);
					pts[3] = (Vector3)(Vector2)((c + new Double2(-h2, h2)) / 1000.0);
					pts[4] = pts[0];
					Map.dashedLine.DrawLine(pts, s.planet, dim, dim);
				}
				Vector2 pos = (Vector2)MapDrawer.GetPosition(s.planet, c);
				string txt = WeatherSystem.TypeName(s.type) + "C" + s.category + " " + (WeatherSystem.Clamp01(s.energy / 80.0) * 100.0).ToString("0") + "%";
				Vector2 normal = (Vector2)c.normalized;
				MapDrawer.DrawPointWithText(40, col, txt, 44, col, pos, normal, 0, 0);
			}
		}
		catch
		{
		}
	}

	// fix — Harmony Transpiler patch MapManager.DrawMap：在 DrawTrajectories() 调用后
	// 插入 DrawMapMarkers()（此时 DrawReset 已执行完，画的东西能活到渲染）。
	[HarmonyPatch(typeof(MapManager), "DrawMap")]
	private static class MapManager_DrawMap_Patch
	{
		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			List<CodeInstruction> list = instructions.ToList();
			CodeInstruction[] insert = new CodeInstruction[1]
			{
				CodeInstruction.Call(typeof(TyphoonManager), "DrawMapMarkers")
			};
			for (int i = 0; i < list.Count; i++)
			{
				if (CodeInstructionExtensions.Calls(list[i], AccessTools.Method(typeof(MapManager), "DrawTrajectories")))
				{
					list.InsertRange(i + 1, insert);
					return list;
				}
			}
			return list;
		}
	}

	// ===== 合并：大吞小（ 加合并动画：不再瞬间移除） =====
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
				// 合并判定放宽：1.2×(Ra+Rb)（边缘刚接触触发）。
				// 台风吞噬整个区域（用户：有自然风暴在台风内部都不合并）：任一系统
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
				// 合并动画：正在合并中的系统不再参与新合并。
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
				// 合并增强递减 + 上限（防无限加强）：越吞越少，且不超基准 3x/2x。
				big.mergeCount++;
				double gain = 1.0 / (1.0 + big.mergeCount * 0.6);
				big.Rmax = Math.Min(big.Rmax * (1.0 + 0.12 * gain), big.rmaxBase * 3.0);
				big.Router = big.Rmax * 9.0;
				big.Vmax = Math.Min(big.Vmax * (1.0 + 0.08 * gain), big.vmaxBase * 2.0);
				big.MarkWindCirclesDirty();   // 合并改 Rmax/Router → 风圈缓存失效
				// 修复： 强度平滑后风场用 vmaxDisplay，合并增强直接改 Vmax
				// 不生效（回归）。同步 vmaxTarget → display 平滑爬升 3 秒呈现合并增强。
				// 审查二轮：同步 vmaxTargetBase（原漏同步 → 能量驱动公式下一帧把
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
		// 合并动画完成清理：被吞方（mode 0/1）移除；双台风（mode 2）big 保留、重置状态。
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
		// fix — 快捷键仅在飞行场景生效（建造/主菜单按 F6-F9 不改变任何状态）。
		if (WorldView.main == null)
		{
			return;
		}
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
			// 强度切换走 SetCategory（vmaxTarget 平滑过渡 ~3 秒，粒子过渡动画；
			// 附属现象强度同步跟母体）；手动切档重置自然发展进度。
			if (selected.type == StormType.Typhoon && selected.category >= 6)
			{
				selected.MakeHypercane();   // 满档台风再 +1 → 超级飓风（指挥中心手动触发）
				Msg("超级飓风 Hypercane 已激活（峰值 ≈800 km/h）");
			}
			else
			{
				selected.SetCategory((selected.category + 1) % 7);
			}
			selected.naturalProgress = 0.0;
			// （专项 A 可选）— F8 手动切档回补能量到 80（"手动强化=回满成熟能量"，
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
		// 调试按键（F2-F5 风区移动 / Shift+F2 区域黑框）已全部移除（用户要求清理）。
	}

	// silent=true：自然生成调用不弹 Msg（用户：自然生成风暴提示删除；手动保留）。
	// mature=true：手动召唤（指挥中心）直接以成熟期出现（stage=1, energy=80），
	// 召唤后即可加附属现象/看到自然生成；自然生成保持发展期（realistic）。
	public WeatherSystem SpawnSystem(StormType type, Location at, double leadMeters, int catBoost, bool silent = false, bool mature = false)
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
		// 生成避让（用户：禁止任何风暴生成在已有风暴的区域）：新系统位置
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
		if (mature)
		{
			sys.stage = 1;        // 直接成熟（指挥中心召唤即满编）
			sys.energy = 80.0;
		}
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
		r.spawnAnimT = 0f;   // 生成动画：云粒子从透明渐入（2 秒）
		renderers.Add(r);
	}

	private void RemoveSystem(WeatherSystem sys)
	{
		systems.Remove(sys);
		for (int i = renderers.Count - 1; i >= 0; i--)
		{
			// WeatherSystem 不是 UnityEngine.Object，(Object) 强转恒 null 曾导致比较恒 true、渲染器全被误删。
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

	// 玩家沿行星轨道的切向速度（米/游戏秒，+角为正）：切向单位向量（+角）= (-sinθ, cosθ)，
	// velocity 与 position 同参考系。供龙卷预警把飞船自身移动计入"接近速度"。
	private static double PlayerOrbitSpeed(Location cam)
	{
		try
		{
			double th = cam.position.AngleRadians;
			Double2 v = cam.velocity;
			return v.x * (0.0 - Math.Sin(th)) + v.y * Math.Cos(th);
		}
		catch
		{
			return 0.0;
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
			// 合并重复采样：原最近系统分支与叠加分支各调一次 SampleWindLocal（参数
			// 完全相同），每帧省一次全系统风采样（SampleComponents + 附属矢量风计算）。
			Double2 vecW = sys.SampleWindLocal(s, h, playerLocation.position);
			if (Math.Abs(s) < nearestDist)
			{
				nearestDist = Math.Abs(s);
				nearest = sys;
				pS = s;
				pH = h;
				// HUD 显示改用矢量风分解（含龙卷螺旋/下击暴流辐散）：
				// pU=行星切向分量、pW=行星径向（垂直）分量。
				Double2 nrm2 = playerLocation.position.normalized;
				Double2 tanV = new Double2(0.0 - nrm2.y, nrm2.x);
				pU = Double2.Dot(vecW, tanV);
				pW = Double2.Dot(vecW, nrm2);
			}
			wind += vecW;
			any = true;
		}
		if (!any)
		{
			diagPValid = false;
			diagWindSum = 0.0;   // 离所有风暴：风速归零，风声随之静音
			return;
		}
		Double2 val2 = playerLocation.velocity - wind;
		pAirspeed = val2.magnitude;
		pValid = true;
		// 风场诊断：最近系统的类型/ro/高度/附属强度/原始采样分量。
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
			// 下击暴流诊断：dro=玩家相对下暴中心距离/Rmax（ 中心保底出流，
			// dro=0 时 spread=0.5 出流 30 m/s，dro→1.2 满 60 m/s），spd=该点出流速度。
			// dro 缓慢增长=风暴漂移（5-7 m/s）正常现象。
			diagDro = Math.Abs(pS) / Math.Max(nearest.Rmax * 0.9, 300.0);
			diagDspd = nearest.DownburstSpeedAt(pS, pH, playerLocation.position);
			// 下暴诊断文件日志（Player.log），每 2 秒一条防刷屏：
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
		// 近距风暴抖动增强：风暴占据整个天空时（ProximityFactor）强度最多 ×4。
		// 抖动加剧 3 倍（用户要求）：0.045 → 0.135。
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

	// ===== — 天气音效驱动：风声按风暴强度×距离衰减，闪电触发雷声 =====
	// fix — 范围衰减重做：reach 8→2.5Rmax + falloff 平方（1/(1+(d/reach)²)，
	// 玩家进出风暴音量明显变化——原 8Rmax 下可视范围 falloff≈1 恒不变"没范围"）。
	private readonly Dictionary<WeatherSystem, int> thunderCount = new Dictionary<WeatherSystem, int>();

	private void UpdateWeatherAudio()
	{
		WeatherAudio audio = WeatherAudio.main;
		if (audio == null)
		{
			return;
		}
		// 龙卷预警（ETA + 到最近龙卷的距离，CheckDebrisImpacts 本帧已算）→ 音频层。
		// 两个都要：ETA 只表示"逼近中"（龙卷越过玩家后恒 -1），距离用于"抵达后继续播"。
		audio.SetTornadoAlert(nearestTornadoEta, nearestTornadoDistM, nearestTornadoCoreM);
		try
		{
			Location pl = GetPlayerLocation();
			if (pl == null || (Object)pl.planet == (Object)null || !pl.planet.HasAtmospherePhysics)
			{
				audio.targetWind = 0f;
				return;
			}
			if (!pValid)   // 玩家不在任何风暴风场内（或风场未采样）→ 风声静音
			{
				audio.targetWind = 0f;
				return;
			}
			double nearestWindFall = 0.0;   // 最近风暴的距离收束（保留 2.5Rmax 边界）
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
				double dist = Math.Abs(da) * pl.planet.Radius;
				// 影响半径（平方衰减）：风声随风暴尺度 2.5Rmax，但设绝对上限 40km
				// （巨大系统风场不会延伸到上百 km 外仍能听见）；雷声独立绝对上限 22km
				// （现实雷声最远 ~16-25km 可闻，与风暴尺度无关，避免大风暴雷声传遍全行星）。
				double windReach = Math.Min(s.Rmax * 2.5, 40000.0);
				double windFall = 1.0 / (1.0 + (dist / windReach) * (dist / windReach));
				double thReach = Math.Min(s.Rmax * 2.5, 22000.0);
				double thFall = 1.0 / (1.0 + (dist / thReach) * (dist / thReach));
				// 风声不再用"到中心距离×整体Vmax"（风眼中心 dist≈0 反而最响，错误）；
				// 改由玩家当地真实风速驱动（见函数末），这里只记最近风暴的距离收束。
				if (windFall > nearestWindFall)
				{
					nearestWindFall = windFall;
				}
				// 雷声：lightningBursts 新增 → 触发（音量按距离衰减，独立上限）
				int cur = s.lightningBursts.Count;
				if (thunderCount.TryGetValue(s, out int prev) && cur > prev)
				{
					audio.PlayThunder((float)thFall, Mathf.Clamp((float)(da / 0.7), -1f, 1f));
				}
				thunderCount[s] = cur;
			}
			// 字典防泄漏：系统移除后旧 key 不再出现，超出上限直接重建（最多漏一次雷声）
			if (thunderCount.Count > systems.Count + 4)
			{
				thunderCount.Clear();
			}
			// 风声 = 玩家当地真实风速（diagWindSum，含风眼 gain≈0.06 压静）+ 最近风暴距离收束。
			// 风眼内 localWind≈0.06·Vmax → 接近静音；眼壁 localWind 高 → 满响。
			double localWind = diagWindSum / 45.0;   // 45 m/s 满音量
			audio.targetWind = Mathf.Clamp01((float)(localWind * nearestWindFall));
		}
		catch
		{
		}
	}

	// ===== F6 气象菜单：绘制已迁到 TyphoonMenuUi（全面革新版 · SpaceXHUD 同色系） =====
	private void OnGUI()
	{
		// 菜单仅在飞行场景显示（WorldView.main null = 建造/主菜单），防跨场景残留
		if (WorldView.main == null)
		{
			return;
		}
		TyphoonMenuUi.Draw(this);
	}




	// 附属现象统一入口：强制基于底部面板选中系统 + 类型检测。
	// internal（非 private）：由独立类 TyphoonMenuUi 经 main.AddPhenomenon(kind) 调用。
	internal void AddPhenomenon(int kind)
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
		// 下暴宿主白名单（用户：禁用台风/单体/多单体生成下击暴流）
		if (selected.AddDownburst())
		{
			Msg("已为 " + WeatherSystem.TypeName(selected.type) + " 添加下击暴流");
		}
		else
		{
			Msg("✖ " + WeatherSystem.TypeName(selected.type) + " 无法产生下击暴流（仅 超级单体 / 飑线 / MCS 可挂载）");
		}
	}
	else if (kind == 3)   // 阵风锋
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
	else if (kind == 4)   // 闪电风暴
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
	else if (kind == 5)   // 清除附属：渐消全部龙卷/下击暴流/阵风锋/闪电风暴
	{
		selected.ClearPhenomena();
		Msg("已对 " + WeatherSystem.TypeName(selected.type) + " 触发附属现象渐消（龙卷/下暴/阵风锋/闪电）");
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

	// 类型按钮文案预拼接缓存（name + "\n" + desc，Spec 静态不变 → 只拼一次）。
	private static string[] specLabels;

	private static string SpecLabel(int i)
	{
		if (specLabels == null)
		{
			WeatherSystem.TypeSpec[] spec = WeatherSystem.Spec;
			specLabels = new string[spec.Length];
			for (int k = 0; k < spec.Length; k++)
			{
				specLabels[k] = spec[k].name + "\n" + spec[k].desc;
			}
		}
		return specLabels[i];
	}

	// 菜单底部信息 StringBuilder 复用（原每帧多次字符串拼接）。
	private static readonly StringBuilder s_sb = new StringBuilder(128);

	// UI 中文字体（用户：找 UI 问题——SFS 默认字体 FuturaPTBook SDF 无中文
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
