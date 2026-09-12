using System;
using Random = UnityEngine.Random;
using SFS.Variables;
using SFS.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace MultiWeatherEngine;

public class StormRenderer : MonoBehaviour
{
	// One complete set of storm visuals: three layers + their mesh buffers.
	// Two of these exist in two different coordinate spaces:
	// near -> Default layer, scale 1 (used when viewDistance < 50000)
	// far -> Scaled Space layer, scaled (activated when viewDistance >= 50000)
	private class RenderSet
	{
		public GameObject backGO;
		public GameObject frontGO;
		public GameObject skyGO;
		public Mesh backMesh;
		public Mesh frontMesh;
		public Mesh skyMesh;
		public Vector3[] bV;
		public Vector3[] fV;
		public Vector3[] sV;
		public Vector2[] bT;
		public Vector2[] fT;
		public Vector2[] sT;
		public Color[] bC;
		public Color[] fC;
		public Color[] sC;
		public int[] bI;
		public int[] fI;
		public int[] sI;
		public int backQuads;
		public int frontQuads;
		public bool isScaled;
		// Renderers + their natural sorting order, so always-on-top can be toggled at runtime.
		public MeshRenderer backR;
		public MeshRenderer frontR;
		public MeshRenderer skyR;
		public int backOrder;
		public int frontOrder;
		public int skyOrder;
	}

	private struct Puff
	{
		public Double2 pos;
		public float size;
		public float baseAlpha;
		public float life;
		public float maxLife;
		public float seed;
		public int kind;
		public Double2 wind;   // 风采样缓存（每 0.1s 采样一次，粒子数×帧率 CPU 大降）
		public float windT;
	}

	// 雨滴缓冲只剩"个数"语义（雨滴位置每帧由 drop 索引确定性算出，不保留状态）：
	// 结构整理——原 Drop 结构的 local/vel/len/alpha 四个字段从未被赋值/读取，
	// 数组本身也只用了 .Length → 改为 int 计数（省 5100×24B 的数组与分配）。
	private int dropCount;
	private float visMul = 1f;   // 粒子雾：按真实能见度缩放的密/稀系数(每帧按所属系统重算)

	private const int SortBack = 20;
	private const int SortFront = 210;
	private const double VisualOuterRho = 4.6;
	private const int SkyNX = 48;
	private const int SkyNY = 34;
	// 缓存 LayerMask 查找（原每帧 2 次字符串查表，白捡）
	private static int cachedDefaultLayer = -1;
	private static int cachedScaledLayer = -1;

	private RenderSet near;
	private RenderSet far;
	private RenderSet A; // active set used by the build functions

	private Texture2D atlas;
	private Texture2D whiteTex;
	private Material mat;
	private Material skyMat;

	private Puff[] puffs;
	private int canopyN;
	private int cloudN;

	private float flashTimer;
	// 雨柱触地落点：风暴中心地形高度（相对海平面，8s 节流采样）。
	// GetTerrainHeightAtAngle 内部有数组分配，不可每帧调。
	private double rainGroundH;
	private int rainGroundFrame = -999;
	private float flashCooldown;
	private double flashS;
	private double flashH;
	private float flashPower;

	private Double2 camG;
	private Vector2 camLocal;
	// far 粒子顶点相对基准 = 台风中心本地坐标(val)；与 GO(太阳系基准)同一参考系 → 稳定。
	private Vector2 alignOrigin;
	// 绝对顶点模式：GO scale=1、顶点全预缩放。farAbsS = farSizeScale×halfCam/depth 校准 1.5x。
	private bool farAbs;
	private float farAbsS;
	private float renderScale = 1f;
	// 传送保护：记录上一帧相机/台风本地坐标，单帧大跳视为传送/世界重建。
	private Vector2 prevCamLocal;
	private Vector2 prevStormLocal;
	private bool hasPrevLoc;
	// 粒子模拟用游戏世界时间差（与逻辑层风暴移动同步，时间加速不甩粒子）
	private double lastSimWT;
	private double simDt;
	// 性能优化（用户：只实时渲染正在影响飞船的风暴，其他风暴粒子不补充不删除
	// 只移动、保留打雷）：puffLive=true 时粒子正常重生/衰减；false 时粒子不死不重生，
	// 只按风场移动（远风暴省 SpawnPuff 开销）。
	// 优化已退役（用户：就是这个导致云被吃——非活区粒子移动但
	// 不重生 = 净流失）：粒子现在任何距离都走完整生命周期。
	// puffLive/puffFreeze 冻结分级整体退役（用户：粒子冻结像卡住，去了）：
	// 粒子任何距离全量更新，字段删除。
	// 生成动画（用户：生成也来搞个动画）：系统生成/类型转变后 0→1（2 秒）
	// 云粒子从透明渐入，配合粒子过渡。public — Manager 生成时置 0 触发。
	public float spawnAnimT = 1f;

	// 优化 — 雨层可见状态：雨整层不可见时（视距裁剪/沙尘暴/有更近系统/贴地淡出）原实现
	// 仍把 frontQuads（默认 5100 quad = 20400 顶点）全清零并整块上传网格——这些顶点全部
	// 退化成零面积三角形，不产生任何像素，纯属浪费。改为直接失活雨层 GameObject 并跳过
	// front 网格 Push（失活层不渲染，网格内容在下次可见时被全量重写，无残留）。
	private bool rainVisible = true;

	// 优化 — 可见性剔除状态（带滞回：远离到 5.5Rmax 且包围球完全在屏幕外才剔除，
	// 回到 4.5Rmax 内或重新进画即恢复，临界不会闪烁）。
	private bool cullSkip;

	// 固化 — 置顶机制(材质 ZTest/queue/sorting，恒开；far 深度=近裁剪2%本就最前)。
	public const int ZTestAlways = 0;
	public const int ZTestNormal = 4;
	public const int TopQueueCloud = 3500;
	public const int TopQueueSky = 3400;
	public const int TopSortingBase = 3500;

	// 固化 — far 台风参数。F3/F4 实时调大小，F5 切换粗细步进(±1/±0.01)。
	public static float farSizeScale = 1.5f;     // 大小倍率(默认 1.5x，用户确认完美)
	public static bool farSizeFine = false;     // F5：false=粗(±1) true=细(±0.01)
	public static float farTilt = 0f;           // 绕视线轴倾斜(弧度, 正=左；默认 0 用户要求去掉左倾)
	public static float farDepthFrac = 0.012f;  // billboard 深度占近-远裁剪比例(保留字段，未参与计算)

	// 下击暴流排线宽（Rmax 倍数，用户实测固化 0.3）。
	public static float downburstGap = 0.3f;

	// 雨滴形状（用户实测固化：长 0.05、宽 0.1 短细雨丝）。
	public static float rainLenScale = 0.05f;       // 雨长倍率
	public static float rainWidScale = 0.1f;        // 雨宽倍率

	// 附属现象判定区（固化，不再热键调）：下暴水平 0.2/垂直 0.04、龙卷水平/垂直 1.0。
	public static float downburstZoneHoriz = 0.2f;   // 下暴水平区
	public static float downburstZoneVert = 0.04f;   // 下暴垂直区
	public static float tornadoZoneHoriz = 1f;       // 龙卷水平区
	public static float tornadoZoneVert = 1f;        // 龙卷垂直区

	// 11 区加强系数（径向剖面 11 段，默认 1.2 除风眼 [5]=1.0；边界 sR 见 WindZoneIndex）。
	public static float[] windZoneGain = new float[11] { 1.2f, 1.2f, 1.2f, 1.2f, 1.2f, 1.0f, 1.2f, 1.2f, 1.2f, 1.2f, 1.2f };
	public static string[] windZoneNames = new string[11] { "外围弱", "较弱", "中", "较强", "强·眼壁", "风眼弱", "强·眼壁", "较强", "中", "较弱", "外围弱" };

	// 暖化压蓝（用户：背景还是蓝的，要其他色克下蓝色）：原 (0.11,0.12,0.17)/
	// (0.34,0.36,0.46) B 通道最高 → 风暴灰布/云底色偏蓝。提 R 压 B → 黄褐暖调
	// （风暴云内/底部光线偏黄褐，与 云中偏黄一致）。
	private static readonly Color CanopyLow = new Color(0.16f, 0.14f, 0.12f);
	private static readonly Color CanopyHigh = new Color(0.44f, 0.41f, 0.34f);
	private static readonly Color CloudLow = new Color(0.3f, 0.32f, 0.38f);
	private static readonly Color CloudHigh = new Color(0.98f, 0.98f, 1f);
	// 巨行星/厚大气云色（用户：木星/土星/海王星特有风暴环境）：
	// atmoClass==2 巨行星：云色向行星表面基准色偏移（大红斑橙红/大白斑亮白/大暗斑青蓝）；
	// atmoClass==1 金星类：硫酸云偏黄。BuildBack 每帧刷新，puff 循环乘 rgb（alpha 不变）。
	private Color cloudTint = Color.white;
	// 优化#4 — 巨行星云色基准 8s 节流采样（GetTerrainColor 纹理采样每帧做是浪费，
	// 行星表面色随风暴移动缓慢变化，8s 刷新一次视觉无差）。
	private float cloudTintTimer;
	// 优化#3 — 天空网格索引一次性（sky 网格 48×34 固定，索引 Rebuild 后设一次即可）。
	private bool skyIdxReady;

	// R3 — 天空眼区开口（审查🔴-R3：天空穹顶 op→1 是"无眼洞实心灰布"，眼稀云画在
	// 灰布上不可见——眼清晰度被架空）：台风眼区在天空网格上开椭圆洞（透出地面/蓝天），
	// 随 eyeSharp 开合。RenderSkyLayer 每帧算好洞参数，WriteSkyCell 逐顶点 op 衰减。
	private Vector2 skyHoleC;

	private float skyHoleRx;

	private float skyHoleRy;

	private float skyHoleStrength;

	// 多天气引擎：每个渲染器绑定一个 WeatherSystem 实例（独立渲染）。
	public WeatherSystem storm;
	private WeatherSystem S => storm;

	private void Awake()
	{
		BuildAssets();
	}

	private void BuildAssets()
	{
		atlas = MakeAtlas();
		string[] array = new string[7] { "Sprites/Default", "Legacy Shaders/Particles/Alpha Blended", "Particles/Alpha Blended", "Mobile/Particles/Alpha Blended", "Particles/Standard Unlit", "UI/Default", "Unlit/Transparent" };
		Shader val = null;
		for (int i = 0; i < array.Length; i++)
		{
			if (val != null)
			{
				break;
			}
			val = Shader.Find(array[i]);
		}
		if (val == null)
		{
			// FindObjectOfType 已过时（CS0618）；FindAnyObjectByType 更快（不要求
			// 最新实例，且不整场景遍历排序）。此 fallback 仅在 7 个内置 shader 全失败时触发。
			SpriteRenderer val2 = UnityEngine.Object.FindAnyObjectByType<SpriteRenderer>();
			if (val2 != null && val2.sharedMaterial != null)
			{
				val = val2.sharedMaterial.shader;
			}
		}
		if (val == null)
		{
			Debug.LogError((object)"[Typhoon] no usable shader found — visuals disabled");
			TyphoonConfig.I.visuals = false;
			return;
		}
		Debug.Log((object)("[Typhoon] cloud shader = " + val.name));
		mat = new Material(val);
		mat.mainTexture = (Texture)atlas;
		mat.renderQueue = 3000;
		whiteTex = new Texture2D(1, 1);
		whiteTex.SetPixel(0, 0, Color.white);
		whiteTex.Apply();
		skyMat = new Material(val);
		skyMat.mainTexture = (Texture)whiteTex;
		skyMat.renderQueue = 3000;

		// Two independent render sets, each living in its own space.
		near = MakeSet("Typhoon Near", false);
		far = MakeSet("Typhoon Far", true);

		Alloc(ref near.sV, ref near.sT, ref near.sC, ref near.sI, 1632);
		Alloc(ref far.sV, ref far.sT, ref far.sC, ref far.sI, 1632);
		ApplyTopMost();
		SetVisibleAll(v: false);
	}

	private RenderSet MakeSet(string prefix, bool scaled)
	{
		RenderSet rs = new RenderSet();
		rs.backOrder = 20;
		// 雨层排最低：frontOrder 210（最高层，雨盖一切）→ 19（云 back=20 之下、
		// sky=18 之上）——雨被云/附属现象盖住（出生点藏云里），但在天空穹顶之上可见。
		rs.frontOrder = 19;
		rs.skyOrder = 18;
		rs.backGO = NewLayer(prefix + " Clouds", rs.backOrder, out rs.backMesh);
		rs.frontGO = NewLayer(prefix + " Foreground", rs.frontOrder, out rs.frontMesh);
		rs.skyGO = NewLayer(prefix + " Sky", rs.skyOrder, out rs.skyMesh);
		rs.backR = rs.backGO.GetComponent<MeshRenderer>();
		rs.frontR = rs.frontGO.GetComponent<MeshRenderer>();
		rs.skyR = rs.skyGO.GetComponent<MeshRenderer>();
		rs.skyR.sharedMaterial = skyMat;
		int defaultLayer = LayerMask.NameToLayer("Default");
		int scaledLayer = LayerMask.NameToLayer("Scaled Space");
		int layer = scaled ? ((scaledLayer >= 0) ? scaledLayer : defaultLayer) : defaultLayer;
		if (layer < 0)
		{
			layer = 0;
		}
		rs.backGO.layer = layer;
		rs.frontGO.layer = layer;
		rs.skyGO.layer = layer;
		rs.isScaled = scaled;
		if (scaled)
		{
			float s = 0.0001f;
			rs.backGO.transform.localScale = new Vector3(s, s, s);
			rs.frontGO.transform.localScale = new Vector3(s, s, s);
			rs.skyGO.transform.localScale = new Vector3(s, s, s);
		}
		return rs;
	}

	private GameObject NewLayer(string name, int order, out Mesh mesh)
	{
		GameObject val = new GameObject(name);
		UnityEngine.Object.DontDestroyOnLoad((UnityEngine.Object)val);
		val.transform.position = Vector3.zero;
		mesh = new Mesh();
		mesh.MarkDynamic();
		mesh.bounds = new Bounds(Vector3.zero, new Vector3(100000000f, 100000000f, 100000000f));
		val.AddComponent<MeshFilter>().sharedMesh = mesh;
		MeshRenderer obj = val.AddComponent<MeshRenderer>();
		obj.sharedMaterial = mat;
		obj.sortingLayerName = "Default";
		obj.sortingOrder = order;
		obj.shadowCastingMode = ShadowCastingMode.Off;
		obj.receiveShadows = false;
		return val;
	}

	// / <summary>
	// / Reproduces the red debug probe's "nothing can occlude me" recipe on the storm itself:
	// / depth test off, transparent queue pushed past everything, sorting order lifted to 3500+.
	// / Applied to both render sets so near/far behave identically.
	// 固化 — 置顶机制恒开（far 深度=近裁剪2%本就最前；这里再压队列/排序兜底）。
	private void ApplyTopMost()
	{
		if (mat != null)
		{
			try
			{
				mat.SetInt("_ZTest", ZTestAlways);
			}
			catch
			{
			}
			mat.renderQueue = TopQueueCloud;
		}
		if (skyMat != null)
		{
			try
			{
				skyMat.SetInt("_ZTest", ZTestAlways);
			}
			catch
			{
			}
			skyMat.renderQueue = TopQueueSky;
		}
		ApplySorting(near);
		ApplySorting(far);
	}

	private static void ApplySorting(RenderSet rs)
	{
		if (rs == null)
		{
			return;
		}
		if (rs.backR != null)
		{
			rs.backR.sortingOrder = TopSortingBase + rs.backOrder;
		}
		if (rs.frontR != null)
		{
			rs.frontR.sortingOrder = TopSortingBase + rs.frontOrder;
		}
		if (rs.skyR != null)
		{
			rs.skyR.sortingOrder = TopSortingBase + rs.skyOrder;
		}
	}


	// / <summary>
	// / Finds the enabled camera that actually renders <paramref name="layer"/>. If several do,
	// / the one drawn last (highest depth) wins, because that is the image the player ends up
	// / looking at. Returns null when no active camera includes the layer in its culling mask,
	// / which by itself is a complete answer to "why can't I see it?".
	// / </summary>
	private static Camera CameraForLayer(int layer)
	{
		if (layer < 0)
		{
			return null;
		}
		int bit = 1 << layer;
		Camera best = null;
		Camera[] all = CachedCameras();
		if (all == null)
		{
			return null;
		}
		for (int i = 0; i < all.Length; i++)
		{
			Camera c = all[i];
			if (!(c == null) && c.isActiveAndEnabled && (c.cullingMask & bit) != 0 && (best == null || c.depth > best.depth))
			{
				best = c;
			}
		}
		return best;
	}

	// 优化 — Camera.allCameras 每次调用都会返回一个新数组（每帧每风暴一次 = 每秒数百次
	// 无谓 GC）。相机集合实际几乎不变，改为 0.25s 节流刷新一次（帧内所有风暴共享同一份）。
	private static Camera[] camCache;

	private static float camCacheTime = -99f;

	private static Camera[] CachedCameras()
	{
		float now = Time.unscaledTime;
		if (camCache == null || now - camCacheTime > 0.25f)
		{
			camCacheTime = now;
			try
			{
				camCache = Camera.allCameras;
			}
			catch
			{
				camCache = null;
			}
		}
		return camCache;
	}

	// / <summary>
	// / Half the vertical extent a camera can see, in that camera's own world units, plus a
	// / depth that sits comfortably between its clip planes. This is the yardstick used to
	// / convert real-world metres into whatever scale the drawing camera happens to work in.
	// / </summary>
	private static float HalfExtent(Camera cam, out float depth)
	{
		float n = Mathf.Max(cam.nearClipPlane, 0.0001f);
		float f = Mathf.Max(cam.farClipPlane, n * 4f);
		float half;
		if (cam.orthographic)
		{
			half = Mathf.Abs(cam.orthographicSize);
			depth = n + (f - n) * 0.02f;
		}
		else
		{
			depth = Mathf.Clamp(n * 20f, n * 2f, f * 0.5f);
			half = depth * Mathf.Tan(cam.fieldOfView * 0.5f * (float)(Math.PI / 180f));
		}
		if (half <= 0f)
		{
			half = 1f;
		}
		return half;
	}

	// 128x128 噪声图集：左半=云团径向模糊点(Canopy)，右半=正弦条(Cloud)。puffs/rain 用它做 UV 纹理。
	private static Texture2D MakeAtlas()
	{
		Texture2D tex = new Texture2D(128, 128, TextureFormat.RGBA32, false);
		Color[] px = new Color[128 * 128];
		for (int i = 0; i < 128; i++)
		{
			float v = (i + 0.5f) / 128f;
			for (int j = 0; j < 128; j++)
			{
				float a;
				if (j < 64)
				{
					float nx = (j + 0.5f) / 64f * 2f - 1f;
					float ny = v * 2f - 1f;
					float d = Mathf.Sqrt(nx * nx + ny * ny);
					a = Mathf.Clamp01(1f - d);
					a = a * a * (3f - 2f * a);
					a *= 0.55f + 0.45f * Mathf.PerlinNoise(j * 0.09f, i * 0.09f);
				}
				else
				{
					float nx2 = (j - 64 + 0.5f) / 64f * 2f - 1f;
					float band = Mathf.Clamp01(1f - Mathf.Abs(nx2));
					band *= band;
					float wave = Mathf.Sin(v * Mathf.PI);
					a = band * Mathf.Pow(wave, 0.55f);
				}
				px[i * 128 + j] = new Color(1f, 1f, 1f, a);
			}
		}
		tex.SetPixels(px);
		tex.wrapMode = TextureWrapMode.Repeat;
		tex.filterMode = FilterMode.Bilinear;
		tex.Apply();
		return tex;
	}

	public void Rebuild()
	{
		TyphoonConfig i = TyphoonConfig.I;
		canopyN = Mathf.Clamp(i.canopyPuffs, 0, 4000);
		cloudN = Mathf.Clamp(i.cloudPuffs, 0, 4000);
		// 雨密度 ×3（用户要求）：rainDrops(1700) ×3 钳 12000。
		int num = Mathf.Clamp(i.rainDrops * 3, 0, 12000);
		puffs = new Puff[canopyN + cloudN];
		for (int j = 0; j < canopyN; j++)
		{
			puffs[j] = SpawnPuff(0);
		}
		for (int k = 0; k < cloudN; k++)
		{
			puffs[canopyN + k] = SpawnPuff(1);
		}
		dropCount = num;
		// backQuads 预留 = 粒子数 + 当前真正需要的附属现象 quad 数。
		// 优化第二轮：原固定预留 1200（按"4 龙卷×72 + 4 下暴×64 + 余量"最坏情况买断），
		// 而无现象时这 1200 quad（4800 顶点 ≈ 173 KB/帧）照样随网格全量上传，纯浪费。
		// 改为按实际现象数量动态预留（EnsureBackBuffers 随现象增减扩/缩），典型风暴
		// 云层上传量降 ~30%，几何与视觉完全不变。
		int phenReserve = PhenomenaQuadsNeeded();
		near.backQuads = canopyN + cloudN + phenReserve;
		near.frontQuads = num;
		far.backQuads = near.backQuads;
		far.frontQuads = num;
		skyIdxReady = false;   // 优化#3 — Rebuild 后天空索引需重设
		Alloc(ref near.bV, ref near.bT, ref near.bC, ref near.bI, near.backQuads);
		Alloc(ref near.fV, ref near.fT, ref near.fC, ref near.fI, near.frontQuads);
		Alloc(ref far.bV, ref far.bT, ref far.bC, ref far.bI, far.backQuads);
		Alloc(ref far.fV, ref far.fT, ref far.fC, ref far.fI, far.frontQuads);
		Push(near.backMesh, near.bV, near.bT, near.bC, near.bI, withIndices: true);
		Push(near.frontMesh, near.fV, near.fT, near.fC, near.fI, withIndices: true);
		Push(far.backMesh, far.bV, far.bT, far.bC, far.bI, withIndices: true);
		Push(far.frontMesh, far.fV, far.fT, far.fC, far.fI, withIndices: true);
		// 置顶机制改由 Rebuild/初始化时施加一次（原每帧调用，纯属重复设值）。
		ApplyTopMost();
	}

	// 当前附属现象实际需要的 quad 数（上界，按各现象渲染循环的最大用量估算并留余量）：
	// 龙卷 = 漏斗 14 层×2 + 子涡 2×8 + 卷尘环 8 + 螺旋 16 + 碎片 20 ≈ 88 → 取 110；
	// 下暴 = 8 波次×8 粒子 = 64 → 取 72；阵风锋 = 7 段×2 = 14 → 取 18。
	private int PhenomenaQuadsNeeded()
	{
		WeatherSystem s = S;
		if (s == null)
		{
			return 96;
		}
		int n = s.tornadoes.Count * 128 + s.downbursts.Count * 112 + s.gustFronts.Count * 24;
		n += s.debris.Count * 4;   // 树 2 quad / 石头 1 quad + 余量
		return n + 64;   // 余量（含闪电通道 ~6 quad）
	}

	// 现象数量变化时扩/缩 back 网格容量（只动网格缓冲，不碰粒子状态 → 不产生"重生成"跳动）。
	// 带滞回：容量在 [need, need×2+128] 区间内保持不动，避免现象增删导致频繁重分配。
	private void EnsureBackBuffers()
	{
		if (puffs == null || near == null || far == null)
		{
			return;
		}
		int need = PhenomenaQuadsNeeded();
		int reserve = near.backQuads - puffs.Length;
		if (reserve >= need && reserve <= need * 2 + 128)
		{
			return;
		}
		int cap = puffs.Length + need + Mathf.Max(64, need / 4);
		near.backQuads = cap;
		far.backQuads = cap;
		Alloc(ref near.bV, ref near.bT, ref near.bC, ref near.bI, cap);
		Alloc(ref far.bV, ref far.bT, ref far.bC, ref far.bI, cap);
		Push(near.backMesh, near.bV, near.bT, near.bC, near.bI, withIndices: true);
		Push(far.backMesh, far.bV, far.bT, far.bC, far.bI, withIndices: true);
	}

	private static void Alloc(ref Vector3[] v, ref Vector2[] t, ref Color[] c, ref int[] idx, int quads)
	{
		v = new Vector3[quads * 4];
		t = new Vector2[quads * 4];
		c = new Color[quads * 4];
		idx = new int[quads * 6];
		for (int i = 0; i < quads; i++)
		{
			int num = i * 4;
			int num2 = i * 6;
			idx[num2] = num;
			idx[num2 + 1] = num + 1;
			idx[num2 + 2] = num + 2;
			idx[num2 + 3] = num;
			idx[num2 + 4] = num + 2;
			idx[num2 + 5] = num + 3;
			// UV 是每个 quad 恒定的（WriteQuad 的 uOffset 恒为 0 → 图集左半 canopy 点）：
			// 在这里一次性铺好，运行期不再写、不再上传（优化第二轮，见 Push/WriteQuad 注释）。
			t[num] = new Vector2(0f, 0f);
			t[num + 1] = new Vector2(0f, 1f);
			t[num + 2] = new Vector2(0.5f, 1f);
			t[num + 3] = new Vector2(0.5f, 0f);
		}
	}

	private static void Push(Mesh m, Vector3[] v, Vector2[] t, Color[] c, int[] idx, bool withIndices)
	{
		if (m != null)
		{
			if (withIndices)
			{
				m.Clear();
			}
			m.vertices = v;
			m.colors = c;
			if (withIndices)
			{
				// UV 恒定（见 Alloc）：只在重建时上传一次，运行期每帧重传纯属浪费
				// （默认雨 5100 quad = 20400 顶点 × 8B = 163KB/帧，云层同理）。
				m.uv = t;
				m.triangles = idx;
				// 包围盒只在重建时设一次（运行期恒定，原每帧重设是多余的原生调用）。
				m.bounds = new Bounds(Vector3.zero, new Vector3(100000000f, 100000000f, 100000000f));
			}
		}
	}

	public void Clear()
	{
		SetVisibleAll(v: false);
	}

	private void SetVisibleAll(bool v)
	{
		SetVisible(near, v);
		SetVisible(far, v);
	}

	private void SetVisible(RenderSet s, bool v)
	{
		if (s == null)
		{
			return;
		}
		if (s.backGO != null && s.backGO.activeSelf != v)
		{
			s.backGO.SetActive(v);
		}
		if (s.frontGO != null && s.frontGO.activeSelf != v)
		{
			s.frontGO.SetActive(v);
		}
		if (s.skyGO != null && s.skyGO.activeSelf != v)
		{
			s.skyGO.SetActive(v);
		}
	}

	// canopy 语义注释（渲染层讨论：kind==0 原名"云顶薄云"——审查指出其实际是
	// 风暴底部连片的阴云层 overcast deck（雷暴下方/风暴周围低垂云幕，对应 CanopyLow 颜色
	// 与天空层 botBase 共用）。视觉语义二选一后定：**底部阴云层**（0.12 风速系数注释：非
	// 物理——压低顶层平流，保云顶形态稳定，不把阴云层吹散）。colorBase 保持 暖化
	// 审美（用户确认过云色），CanopyLow 蓝灰为天空层底色（物理云底暗灰）。
	private Puff SpawnPuff(int kind)
	{
		Puff result = new Puff
		{
			kind = kind
		};
		double num;
		double num2;
		double num3;
		// 渲染形态按类型（不再全是台风眼壁环）：
		// 台风：眼壁环+雨带（原样）
		// 飑线：沿线(经度方向)拉长的云带
		// 单体类(单体/多单体/超级单体/MCS)：中心云团（无风眼）
		bool isLine = S.type == StormType.SquallLine;
		bool isRotary = S.type == StormType.Typhoon;
		if (kind == 0)
		{
			if (isLine)
			{
				num = Random.Range(0.1f, 4.2f);
				num2 = Math.Pow(Random.value, 0.55) * 0.85 + 0.12;
			}
			else if (isRotary)
			{
				// 台风大 puff 分布按等级（用户：台风眼依旧存在于每个等级——原
				// Random.Range(0.5f,4.6f) 中心 0~0.5R 无任何 puff，TD/TS 即使 BuildBack 的
				// num6 中心满云也无粒子可画 → 假眼； 只修了 num6 公式漏了分布）。
				float eyeR0 = (float)WeatherSystem.TyphoonEyeR(S.category);
				if (eyeR0 <= 0f)
				{
					num = Math.Pow(Random.value, 1.6) * 2.4;   // TD/TS：中心聚集云团（无眼）
				}
				else
				{
					// cat≥2：眼壁内缘外铺开（中心留空=真眼）；25% 概率放眼内稀云 puff
					// （BuildBack 的 eyeHaze 眼内稀云需要粒子才能渲染出来）。
					if (Random.value < 0.25)
					{
						num = Random.Range(0.02f, eyeR0);
					}
					else
					{
						num = eyeR0 * 0.7f + Random.Range(0f, 3.4f) * (0.25f + 0.75f * Random.value);
					}
				}
				num2 = Math.Pow(Random.value, 0.6) * 0.92 + 0.06;
			}
			else
			{
				// 中心假眼修复（用户：所有非台风系统中间稀两边密）：原 num =
				// Random^0.55×2.0——指数<1 把分布拉向大值（边缘），中心没有大云团，
				// 尺寸×30% 放大后空洞更明显（"疑似台风眼"）。改 ^1.6 中心聚集：
				// 中心大云团密实无眼，边缘仍有 puff 过渡（0.5-2.0Rmax 占 ~58%）。
				num = Math.Pow(Random.value, 1.6) * 2.0;
				num2 = Math.Pow(Random.value, 0.7) * 0.9 + 0.1;
			}
			num3 = ((Random.value < 0.5) ? (-1.0) : 1.0);
			// puff 尺寸减小（×0.45/×0.4），云更碎更圆，减少"方块感"。
			result.size = (float)(S.Rmax * Random.Range(0.2f, 0.6f));
			result.baseAlpha = Random.Range(0.55f, 0.97f);
			result.maxLife = Random.Range(45f, 140f);
		}
		else
		{
			if (isLine)
			{
				// 飑线：沿线拉长（num=沿线距离，长 8R 的云带）
				num = Random.Range(0.2f, 4.0f);
				// 环流注入：主体云出生偏底部（中心上升气流入口），粒子被抬升
				// 到顶部外散 → 出界回收再注入底部 = 自维持环流动画（用户：粒子从中间
				// 向上往外跑时生的太慢——原 num2 全高度均匀随机，底部中心无持续注入，
				// 上升通道稀疏）。指数 >1 偏 0（底部）。
				num2 = Math.Pow(Random.value, 1.6) * 0.9 + 0.1;
			}
			else if (isRotary)
			{
				// 台风小 puff 分布按等级（同上修复）：TD/TS 中心云团；
				// cat≥2 眼壁内环密（eyeR~eyeR+1.2）+ 外环疏，眼内 15% 稀云 puff。
				float eyeR1 = (float)WeatherSystem.TyphoonEyeR(S.category);
				if (eyeR1 <= 0f)
				{
					num = Random.Range(0.05f, 1.8f);
				}
				else
				{
					if (Random.value < 0.15)
					{
						num = Random.Range(0.02f, eyeR1 * 0.9f);
					}
					else
					{
						num = (Random.value < 0.6) ? Random.Range(eyeR1 * 0.8f, eyeR1 + 1.2f) : Random.Range(eyeR1 + 1.2f, 4.2f);
					}
				}
				// 环流注入：台风主体云出生偏底部眼壁（眼壁上升气流入口）——
				// 眼壁环流持续供料（同飑线/单体，指数 >1 偏底部）。
				num2 = Math.Pow(Random.value, 1.8) * 1.0 + 0.05;
			}
			else
			{
				// 单体类：中心云团（无风眼）
				num = Random.Range(0.05f, 1.55f);
				// 环流注入：对流型中心上升核心入口在底部中心（偏底部）。
				num2 = Math.Pow(Random.value, 1.9) * 1.0 + 0.08;
			}
			num3 = ((Random.value < 0.5) ? (-1.0) : 1.0);
			result.size = (float)(S.Rmax * Random.Range(0.05f, 0.18f) * (0.6 + 0.7 * num2));
			result.baseAlpha = Random.Range(0.3f, 0.85f);
			// maxLife 分档（演化讨论：云型稳定隐形大头）：台风 60-180s（大尺度
			// 云系维持久），其他 26-70s（中小系统流动快）。
			result.maxLife = (S.type == StormType.Typhoon) ? Random.Range(60f, 180f) : Random.Range(26f, 70f);
		}
		// 云体悬浮：垂直因子 num2 映射到 [云底, 云顶]（不再从地面开始）。
		result.pos = FromStorm(num3 * num * S.Rmax, S.Hbase + num2 * (S.Htop - S.Hbase));
		result.life = result.maxLife * Random.value;
		result.seed = Random.value;
		return result;
	}

	private Double2 FromStorm(double s, double h)
	{
		double num = (S.planet != null) ? S.planet.Radius : 1.0;
		double num2 = S.centerAngle + s / num;
		double num3 = num + h;
		return new Double2(Math.Cos(num2) * num3, Math.Sin(num2) * num3);
	}

	private void LateUpdate()
	{
		WeatherSystem s = S;
		if (s == null || !s.active || s.planet == null || !TyphoonConfig.I.visuals)
		{
			SetVisibleAll(v: false);
			return;
		}
		if (puffs == null || puffs.Length != Mathf.Clamp(TyphoonConfig.I.canopyPuffs, 0, 4000) + Mathf.Clamp(TyphoonConfig.I.cloudPuffs, 0, 4000))
		{
			Rebuild();
		}
		WorldView main = WorldView.main;
		if (main == null)
		{
			SetVisibleAll(v: false);
			return;
		}
		Camera main2 = Camera.main;
		if (main2 == null)
		{
			SetVisibleAll(v: false);
			return;
		}
		Location playerLocation = TyphoonManager.GetPlayerLocation();
		if (playerLocation == null || playerLocation.planet != (object)s.planet)
		{
			SetVisibleAll(v: false);
			return;
		}
		float dt = Mathf.Min(Time.deltaTime, 0.2f);
		// 粒子跟不上时间加速修复（用户：似乎有粒子没跟上时间加速）：
		// 粒子位置/生命周期改用游戏世界时间差 simDt（与逻辑层风暴中心移动一致），
		// 动画/闪电/节流仍用现实 dt（视觉节奏）。原粒子用 centerVel×dt_现实（≤0.2s/帧），
		// 而风暴中心一帧移动 moveSpeed×dt_逻辑（时间加速下可 30 游戏秒/帧）→ 时间加速
		// 时粒子每帧落后几百倍被风暴甩出云团 = 云被"吃"/拉变形。simDt 钳 30s/帧与逻辑层
		// 一致 → 粒子与风暴中心严格同步。
		double wtNow = WorldTime.main.worldTime;
		simDt = wtNow - lastSimWT;
		lastSimWT = wtNow;
		if (simDt < 0.0)
		{
			simDt = 0.0;
		}
		else if (simDt > 30.0)
		{
			simDt = 30.0;
		}
		UpdateLightning(dt);
		// 生成/转变动画推进：生成时 spawnAnimT 从 0 渐入（云粒子淡出），
		// 类型转变时淡出淡入（TransitionBlend 控制 alpha 呼吸）。
		if (spawnAnimT < 1f)
		{
			spawnAnimT = Mathf.Min(1f, spawnAnimT + dt / 2f);
		}
		if (s.RebuildPuffsFlag)
		{
			s.RebuildPuffsFlag = false;
			Rebuild();
			spawnAnimT = 0f;
		}
		// 类型转变动画：把过渡包络折进 spawnAnimT（云粒子/雨/附属现象/天空穹顶的 alpha
		// 全链都乘它）→ 3 秒内整团天气淡出、中点换型重建、再淡入，是一条连续曲线。
		// 原实现只在主体云上乘 (1-0.5×blend)：只暗到 50%、薄云/雨/附属完全不参与，
		// 且切换那一帧 alpha 从 0.5 直接掉到 0（动画中间有断点）。
		if (s.transitionAnimT >= 0.0)
		{
			spawnAnimT = Mathf.Min(spawnAnimT, s.TransitionAlphaMul());
		}

		Vector3 position = main2.transform.position;
		camG = WorldView.ToGlobalPosition(new Vector2(position.x, position.y));
		camLocal = WorldView.ToLocalPosition(camG);
		// 活区/冻结分级已整体退役（用户：粒子冻结像卡住，去了）：粒子任何
		// 距离都全量更新，不再需要 puffLive/puffFreeze 判定。

		// 传送保护：飞船传送/世界重建时相机或台风中心本地坐标会单帧大跳，
		// 此时参考系(planet holder)处于中间态，把台风画上去会飞到天空/太空。
		// 检测到跳变(>200km/帧)就隐藏这一帧，等下一帧状态稳定再恢复正确锚定。
		Vector2 stormLocalNow = Vector2.zero;
		if (s != null && s.planet != null)
		{
			stormLocalNow = WorldView.ToLocalPosition(FromStorm(0.0, 0.0));
		}
		bool teleporting = false;
		if (hasPrevLoc)
		{
			float camJump = (camLocal - prevCamLocal).magnitude;
			float stormJump = (stormLocalNow - prevStormLocal).magnitude;
			teleporting = camJump > 200000f || stormJump > 200000f;
		}
		prevCamLocal = camLocal;
		prevStormLocal = stormLocalNow;
		hasPrevLoc = true;
		if (teleporting)
		{
			SetVisibleAll(v: false);
			return;
		}

		// -- Space selection at the 50000 threshold -----------------------------
		float vd = 0f;
		try
		{
			vd = main.viewDistance.Value;
		}
		catch
		{
		}
		bool scaledSpace = main.scaledSpace.Value;
		bool farActive = (vd >= 50000f || scaledSpace);

		float rs = farActive ? 0.0001f : 1f;

		// ===== 优化：不可见风暴整体跳过（不更新粒子 / 不建几何 / 不上传网格） =====
		// 判据（两条同时成立才剔除）：① 相机已在天空穹顶作用半径之外（>5.5Rmax，穹顶
		// 屏覆盖层只在 4.6Rmax 内生效）② 风暴包围球连同 0.35 屏余量完全落在屏幕外。
		// 被相机裁掉的几何本来就不产生任何像素，跳过与"画了但被裁"结果完全一致；
		// 带滞回（5.5 剔除 / 4.5 恢复）→ 临界不会闪烁。仅近空间路径启用：far 缩放空间
		// 用另一套相机与预缩放顶点，判定口径不同，保守不动。剔除期间照常推进 lastSimWT，
		// 避免恢复那一帧 simDt 爆表把粒子整体弹飞。
		if (!farActive && ShouldCull(main2, stormLocalNow))
		{
			cullSkip = true;
			SetVisibleAll(v: false);
			return;
		}
		cullSkip = false;

		// NameToLayer 结果缓存（首次计算，此后零开销）
		if (cachedDefaultLayer < 0)
		{
			cachedDefaultLayer = LayerMask.NameToLayer("Default");
		}
		if (cachedScaledLayer < 0)
		{
			cachedScaledLayer = LayerMask.NameToLayer("Scaled Space");
		}
		int defaultLayer = cachedDefaultLayer;
		int scaledLayer = cachedScaledLayer;
		int layer = farActive ? ((scaledLayer >= 0) ? scaledLayer : defaultLayer) : defaultLayer;
		if (layer < 0)
		{
			layer = 0;
		}

		// Only the active set is shown; the other set belongs to a different space.
		A = farActive ? far : near;
		SetVisible(near, !farActive);
		SetVisible(far, farActive);

		A.backGO.layer = layer;
		A.frontGO.layer = layer;
		A.skyGO.layer = layer;

		// Real metres spanned by half the screen, measured off the main camera. Both sets
		// are sized against this number, which is precisely what makes the crossing seamless.
		float mainHalf = (main2.orthographic ? main2.orthographicSize : (Mathf.Abs(main2.transform.position.z) * Mathf.Tan(main2.fieldOfView * 0.5f * (float)(Math.PI / 180f))));
		if (mainHalf < 1f)
		{
			mainHalf = 1f;
		}

		Vector2 val = Vector2.zero;
		if (s != null && s.planet != null)
		{
			val = WorldView.ToLocalPosition(s.MergedStormC());   // anchor 跟随合并动画中心
		}

		// Who actually draws this layer? Geometry in a layer nobody renders stays invisible no
		// matter how big it is or how carefully it is positioned.
		Camera camL = CameraForLayer(layer);

		bool align;
		float geoHalf;
		float geoRs;
		float goScale;
		Vector3 anchor;
		Quaternion anchorRot = Quaternion.identity;
		Camera geoCam = main2;
		bool viewportPath = farActive && camL != null;
		if (viewportPath)
		{
			// 固化 — far 行星锚定（太阳系基准，与 WorldEnvironment.holder 同构）+ 绝对顶点模式。
			// 台风缩放世界 = (台风太阳系 − 视点太阳系)/10000；Location.GetSolarSystemPosition 纯平移无旋转。
			float worldHalf = ((vd > 1f) ? vd : mainHalf);
			float depth;
			float halfCam = HalfExtent(camL, out depth);
			geoHalf = worldHalf;
			geoRs = 1f;
			goScale = 1f;
			Vector2 planetXY = Vector2.zero;
			try
			{
				double wt = WorldTime.main.worldTime;
				Double2 stormSolar = s.planet.GetSolarSystemPosition(wt) + (Double2)s.MergedStormC();   // anchor 跟随合并动画中心
				Double2 viewSolar = WorldView.main.ViewLocation.GetSolarSystemPosition(wt);
				Double2 diff = (stormSolar - viewSolar) / 10000.0;
				planetXY = new Vector2((float)diff.x, (float)diff.y);
			}
			catch
			{
				planetXY = new Vector2(val.x / 10000f, val.y / 10000f);
			}
			// z 与 holder 同深度(vd/10000) → 透视投影一致 → 屏内；GO scale=1，尺寸全在顶点(×farAbsS)。
			anchor = new Vector3(planetXY.x, planetXY.y, vd / 10000f);
			anchorRot = Quaternion.identity;
			farAbs = true;
			farAbsS = farSizeScale * halfCam / Mathf.Max(depth, 0.0001f);
			align = true;
			alignOrigin = val;
			geoCam = camL;
		}
		else
		{
			// Near-space (rs=1) path — working behaviour.
			// 修复"风暴随火箭飞天"（用户：逻辑层没问题，渲染层风暴能随火箭飞天）：
			// 原 near 模式 align=false（顶点 = ToLocalPosition 绝对本地坐标，相对 WorldView
			// positionOffset）+ anchor=camLocal（GO 锚定相机）→ 最终 = camLocal + 顶点 =
			// 双重偏移：相机（火箭）移动时 GO 跟着动、顶点不动，风暴整体随火箭飞天。
			// 改与 far 同构：GO 锚定风暴中心（val），顶点相对风暴中心（alignOrigin=val），
			// positionOffset 在差值中消掉 → 风暴锚定行星表面，火箭升空/移动风暴不动。
			align = true;
			geoHalf = mainHalf;
			geoRs = 1f;
			goScale = 1f;
			anchor = (Vector3)val;
			alignOrigin = val;
			farAbs = false;
			geoCam = main2;
		}

		renderScale = goScale;
		// 绝对顶点模式(farAbs)：GO scale=1、rotation=identity，台风尺寸全在顶点里。
		Vector3 goScaleV = (farAbs ? Vector3.one : new Vector3(goScale, goScale, goScale));
		Quaternion goRot = (farAbs ? Quaternion.identity : anchorRot);
		A.backGO.transform.localScale = goScaleV;
		A.frontGO.transform.localScale = goScaleV;
		A.skyGO.transform.localScale = goScaleV;
		A.backGO.transform.position = anchor;
		A.frontGO.transform.position = anchor;
		// sky 恒锚定相机（near）：天空穹顶是屏幕覆盖层，必须覆盖相机视野。
		// far（viewportPath）维持锚定风暴中心（顶点已含全屏尺寸）；near 锚定 camLocal。
		A.skyGO.transform.position = viewportPath ? anchor : (Vector3)camLocal;
		A.backGO.transform.rotation = goRot;
		A.frontGO.transform.rotation = goRot;
		A.skyGO.transform.rotation = goRot;

		// 置顶机制（材质 ZTest/queue/sorting，恒开）不再每帧重设：这些值在
		// BuildAssets/Rebuild 里已设好且运行期不变，每帧重设只会反复把材质与渲染器标脏。
		EnsureBackBuffers();   // 现象增减时同步 back 网格容量（见方法注释）
		BuildBack(dt, geoHalf, geoRs, align);
		BuildRain(dt, geoCam, geoHalf, align);
		RenderSkyLayer(geoCam, geoHalf, geoRs, align);
		// 云层每帧全量上传（粒子每帧都在动，无冻结可跳）。
		Push(A.backMesh, A.bV, A.bT, A.bC, A.bI, withIndices: false);
		// 雨层只在可见时上传：整层不可见时 frontGO 已失活，上传 2 万多个退化顶点纯浪费。
		if (rainVisible)
		{
			Push(A.frontMesh, A.fV, A.fT, A.fC, A.fI, withIndices: false);
		}
	}

	private void RenderSkyLayer(Camera cam, float half, float rs, bool align)
	{
		try
		{
			WeatherSystem s = S;
			s.ToStormFrame(camG, out var s2, out var h);
			double num = Math.Abs(s2) / s.Rmax;
			double num2 = Smooth01((4.6 - num) / 1.6);
			num2 *= 1.0 - WeatherSystem.Clamp01((h - s.Htop) / (s.Htop * 0.4));
			if (num2 < 0.02)
			{
				if (A.skyGO.activeSelf)
				{
					A.skyGO.SetActive(false);
				}
				return;
			}
			if (!A.skyGO.activeSelf)
			{
				A.skyGO.SetActive(true);
			}
			float num3 = Mathf.Clamp((float)(s.Vmax / 78.0), 0.5f, 1f);
			Color canopyLow = CanopyLow;
			Color botBase = Color.Lerp(CanopyLow, CanopyHigh, num3 * 0.6f);
			// 龙卷内部能见度骤降（用户要求）：玩家进入漏斗沙尘区（TyphoonManager 逐帧算出的
			// tornadoObscure 0-1）时，天空穹顶整片转沙褐并迅速压到接近不透明 → 屏幕被"沙幕"
			// 糊住（配合 TyphoonManager 的褐化/压暗后处理 = 能见度骤降）。原来穹顶只是灰布
			// （op 上限由强度决定），没有"进沙暴眼"的失明感。
			float obsc = TyphoonManager.tornadoObscure;
			if (TyphoonConfig.I.tornadoObscure && obsc > 0.02f)
			{
				Color dust = new Color(0.52f, 0.44f, 0.35f);
				botBase = Color.Lerp(botBase, dust, obsc);
				canopyLow = Color.Lerp(canopyLow, dust, obsc);
			}
			// 灰布最高浓度由强度决定：弱风暴中心最多 skyOpacity×0.5，
			// 强风暴(Vmax≥45m/s)中心 op→1 完全盖死（分不清天空与陆地）。
			float str = Mathf.Clamp01((float)(s.Vmax / 45.0));
			float maxOp = Mathf.Lerp((float)TyphoonConfig.I.skyOpacity * 0.5f, 1f, str);
			float op = maxOp * (float)num2 * (float)S.MergeFade() * S.DissolveFade() * spawnAnimT;   // （终审🟡-9）补乘 spawnAnimT：天空穹顶生成时随云淡入（原生成瞬间灰布全亮跳变）
			if (TyphoonConfig.I.tornadoObscure && obsc > 0.02f)
			{
				// 龙卷沙幕：穹顶浓度直接拉到接近不透明（能见度骤降的主体）
				op = Mathf.Lerp(op, 1f, obsc);
			}
			float num4 = (cam.aspect > 0.1f) ? cam.aspect : 1f;
			float num5 = half / rs * num4 * 1.2f;
			float num6 = half / rs * 1.2f;
			if (farAbs)
			{
				// 绝对顶点模式：天空网格也预缩放(相对台风中心，GO scale=1)。
				num5 *= farAbsS / 10000f;
				num6 *= farAbsS / 10000f;
			}
			Vector2 cc = (align ? Vector2.zero : camLocal);
			// R3 — 天空眼区开口（审查🔴-R3）：台风（有眼，cat≥2）且能量高时，在天空
			// 网格上开椭圆洞——洞中心 = 风暴中心云中层高投影（洞偏地平线下方则不开，避免
			// 误开），rx=0.6×眼径（只开在眼内不碰眼壁）、ry=0.35×云层厚。随 eyeSharp 开合
			// （能量降眼闭合）。SFS 近空间是相机对齐正交本地空间，stormLocal−alignOrigin
			// 即屏幕偏移（render-scientist 确认，无需投影矩阵）。
			skyHoleStrength = 0f;
			if (s.type == StormType.Typhoon && WeatherSystem.TyphoonEyeR(s.category) > 0.01f)
			{
				float eyeSharp2 = (float)WeatherSystem.Clamp01((s.energy - 30.0) / 50.0);
				if (eyeSharp2 > 0.02f)
				{
					try
					{
						Double2 stormC2 = FromStorm(0.0, 0.0);
						Double2 eyeW = stormC2 + stormC2.normalized * (s.Hbase + 0.4 * (s.Htop - s.Hbase));
						Vector2 eyeL = WorldView.ToLocalPosition(eyeW);
						skyHoleC = align ? (eyeL - alignOrigin) : (eyeL - camLocal);
						if (farAbs)
						{
							skyHoleC *= farAbsS / 10000f;
						}
						skyHoleRx = (float)(WeatherSystem.TyphoonEyeR(s.category) * s.Rmax * 0.6 * rs);
						skyHoleRy = (float)((s.Htop - s.Hbase) * 0.35 * rs);
						if (farAbs)
						{
							skyHoleRx *= farAbsS / 10000f;
							skyHoleRy *= farAbsS / 10000f;
						}
						skyHoleStrength = 0.8f * eyeSharp2;
					}
					catch
					{
						skyHoleStrength = 0f;
					}
				}
			}
			float num7 = 2f * num5 / 48f;
			float num8 = 2f * num6 / 34f;
			Double2 val = camG;
			Double2 normalized = val.normalized;
			val = camG;
			double magnitude = val.magnitude;
			double radius = s.planet.Radius;
			double soft = 0.03;
			int num9 = 0;
			for (int i = 0; i < 34; i++)
			{
				float num10 = 0f - num6 + (float)i * num8;
				float y = num10 + num8;
				for (int j = 0; j < 48; j++)
				{
					float num11 = 0f - num5 + (float)j * num7;
					float x = num11 + num7;
					WriteSkyCell(num9, cc, num11, num10, x, y, normalized, magnitude, radius, soft, op, canopyLow, botBase, num6);
					num9++;
				}
			}
			// 优化#3 — 天空索引一次性：sky 网格 48×34 固定，原每帧 withIndices:true
			// 触发 Clear+SetTriangles 重建相同索引（1632 顶点 × 每帧 = 浪费）。仅 Rebuild 后
			// 首帧带索引，此后只更新顶点/uv/颜色（triangles 保留）。
			Push(A.skyMesh, A.sV, A.sT, A.sC, A.sI, withIndices: !skyIdxReady);
			skyIdxReady = true;
		}
		catch
		{
			if (A.skyGO != null)
			{
				A.skyGO.SetActive(false);
			}
		}
	}

	private void WriteSkyCell(int q, Vector2 cc, float x0, float y0, float x1, float y1, Double2 upDir, double camMag, double R, double soft, float op, Color topBase, Color botBase, float extY)
	{
		int num = q * 4;
		A.sV[num] = new Vector3(cc.x + x0, cc.y + y0, 0f);
		A.sV[num + 1] = new Vector3(cc.x + x0, cc.y + y1, 0f);
		A.sV[num + 2] = new Vector3(cc.x + x1, cc.y + y1, 0f);
		A.sV[num + 3] = new Vector3(cc.x + x1, cc.y + y0, 0f);
		A.sT[num] = Vector2.zero;
		A.sT[num + 1] = new Vector2(0f, 1f);
		A.sT[num + 2] = Vector2.one;
		A.sT[num + 3] = new Vector2(1f, 0f);
		// R3 — 眼区开口：4 个顶点各自按到洞中心距离衰减 op（洞内透出地面/蓝天）。
		float op0 = (skyHoleStrength > 0.001f) ? op * SkyHoleFade(x0, y0) : op;
		float op1 = (skyHoleStrength > 0.001f) ? op * SkyHoleFade(x0, y1) : op;
		float op2 = (skyHoleStrength > 0.001f) ? op * SkyHoleFade(x1, y1) : op;
		float op3 = (skyHoleStrength > 0.001f) ? op * SkyHoleFade(x1, y0) : op;
		A.sC[num] = SkyVertWorld(new Double2((double)x0, (double)y0), upDir, camMag, R, soft, op0, topBase, botBase, extY, y0);
		A.sC[num + 1] = SkyVertWorld(new Double2((double)x0, (double)y1), upDir, camMag, R, soft, op1, topBase, botBase, extY, y1);
		A.sC[num + 2] = SkyVertWorld(new Double2((double)x1, (double)y1), upDir, camMag, R, soft, op2, topBase, botBase, extY, y1);
		A.sC[num + 3] = SkyVertWorld(new Double2((double)x1, (double)y0), upDir, camMag, R, soft, op3, topBase, botBase, extY, y0);
		int num2 = q * 6;
		A.sI[num2] = num;
		A.sI[num2 + 1] = num + 1;
		A.sI[num2 + 2] = num + 2;
		A.sI[num2 + 3] = num;
		A.sI[num2 + 4] = num + 2;
		A.sI[num2 + 5] = num + 3;
	}

	// R3 — 天空眼区开口：网格顶点 (gx,gy) 到洞中心距离 → op 衰减因子
	// （1−strength×exp(−dx²−dy²)，洞内 op 最低 ×0.2，眼壁外不变）。高斯洞边缘柔和。
	private float SkyHoleFade(float gx, float gy)
	{
		double dx = (gx - skyHoleC.x) / Math.Max(skyHoleRx, 0.001f);
		double dy = (gy - skyHoleC.y) / Math.Max(skyHoleRy, 0.001f);
		return (float)(1.0 - skyHoleStrength * Math.Exp(0.0 - (dx * dx + dy * dy)));
	}

	private static Color SkyVertWorld(Double2 V, Double2 upDir, double camMag, double R, double soft, float op, Color topBase, Color botBase, float extY, float oy)
	{
		double num = Math.Sqrt(V.x * V.x + V.y * V.y);
		double num2;
		if (num < 0.001)
		{
			num2 = 0.0;
		}
		else
		{
			double num3 = (V.x * upDir.x + V.y * upDir.y) / num;
			double num4 = Math.Sqrt(Math.Max(0.0, 1.0 - num3 * num3));
			double num5 = R / camMag;
			num2 = Smooth01((num4 - num5) / soft);
		}
		float num6 = Mathf.Clamp01((oy + extY) / (2f * extY));
		Color val = Color.Lerp(botBase, topBase, num6);
		return new Color(val.r, val.g, val.b, op * (float)num2);
	}

	private void UpdateLightning(float dt)
	{
		// 沙尘暴无闪电（干燥系统）：直接禁用
		if (!TyphoonConfig.I.lightning || S.type == StormType.DustStorm)
		{
			flashPower = 0f;
			return;
		}
	// 闪电风暴增强因子：burst 实例强度累加。气象依据：中气旋/眼壁内闪电
	// 密度远高于外围，burst 即"闪电密集区"——冷却缩短、闪点集中、亮度提升。
	// 沙尘暴无闪电（干燥系统，沙尘摩擦起电不产生可见云地闪）。
	double lBoost = 0.0;
	if (S != null && S.lightningBursts.Count > 0)
		{
			for (int bi = 0; bi < S.lightningBursts.Count; bi++)
			{
				lBoost += S.lightningBursts[bi].strength;
			}
		}
		flashTimer -= dt;
		flashCooldown -= dt;
		if (flashTimer > 0f)
		{
			flashPower = Mathf.Max(0f, flashTimer / 0.22f);
			flashPower *= ((Random.value < 0.35f) ? 0.45f : 1f);
			flashPower *= (float)(1.0 + lBoost * 0.3);   // 亮度提升
			return;
		}
		flashPower = 0f;
		if (flashCooldown <= 0f)
		{
			double num = Mathf.Max(0.35f, (float)(S.Vmax / 78.0));
			flashCooldown = Random.Range(1.4f, 6.5f) / (float)(num * (1.0 + lBoost * 2.0));   // 冷却缩短
			flashTimer = 0.22f;
			double num2 = ((Random.value < 0.5) ? (-1.0) : 1.0);
			if (lBoost > 0.05)   // 闪点向最近闪电风暴位置集中
			{
				double bs = S.lightningBursts[0].sOff * S.Rmax;
				flashS = bs + num2 * S.Rmax * Random.Range(0.0f, 0.8f);
			}
			else
			{
				flashS = num2 * S.Rmax * Random.Range(0.8f, 2.6f);
			}
			flashH = S.Htop * Random.Range(0.15f, 0.6f);
		}
	}

	// 粒子雾：将该风暴真实水平能见度映射为 puff/雨滴 透明度系数。
	// 能见度越低(重暴雨/特强沙尘暴)→系数越高→粒子更密更不透明(墙)；能见度高(轻 MCS)→更稀。
	// 与逐型基准能见度(米)对齐：Cell/Supercell 800、Multicell 900、SquallLine 1000、
	// MCS 1500、Typhoon 1200；强度(Vmax)压缩/放宽（强→密、弱→稀）。
	// 沙尘暴走 GB/T 20480（DustFactor，与 ComputeRainVisibility 同一曲线）：特强<200m→浓。
	private float ComputeVisMul(WeatherSystem s)
	{
		double baseVis;
		if (s.type == StormType.DustStorm)
		{
			float d = TyphoonManager.dustObscure;   // 玩家越深陷沙尘暴越强(0-1)
			baseVis = 8000.0 * (1.0 - 0.975 * (double)d);   // dust=1 → 200m
		}
		else
		{
			switch (s.type)
			{
				case StormType.Cell: baseVis = 800.0; break;
				case StormType.Supercell: baseVis = 800.0; break;
				case StormType.Multicell: baseVis = 900.0; break;
				case StormType.SquallLine: baseVis = 1000.0; break;
				case StormType.MCS: baseVis = 1500.0; break;
				case StormType.Typhoon: baseVis = 1200.0; break;
				default: baseVis = 1500.0; break;
			}
			double inten = Mathf.Clamp01((float)(s.Vmax / 45.0));
			baseVis *= (1.4 - 0.7 * inten);   // 弱系统能见度放宽(更稀)，强系统压缩(更密)
		}
		float obsc = Mathf.Clamp01((float)(1.0 - baseVis / 3000.0));   // vis<3000m 起有遮挡
		return Mathf.Lerp(0.55f, 1.6f, obsc);
	}

	private void BuildBack(float dt, float camHalf, float rs, bool align)
	{
		WeatherSystem s = S;
		visMul = ComputeVisMul(s);   // 云层粒子雾：本系统真实能见度→密/稀
		// 龙卷粒子雾：玩家越深陷漏斗/碎屑区(tornadoObscure→1)，尘墙/碎屑/漏斗壁越密成墙。
		float torObs = (TyphoonConfig.I.tornadoObscure ? TyphoonManager.tornadoObscure : 0f);
		float torVisMul = Mathf.Lerp(1f, 1.5f, Mathf.Clamp01(torObs));
		// 沙尘暴粒子雾：玩家越深陷沙尘暴(dustObscure→1)，沙墙/地面尘带越密成墙(白化感)。
		float dustVisMul = Mathf.Lerp(1f, 1.6f, Mathf.Clamp01(TyphoonManager.dustObscure));
		// 巨行星/厚大气云色（用户：金星/木星/土星/海王星特有风暴环境）：
		// atmoClass==2 云色向行星表面基准色偏移（GetTerrainColor 采样纹理——贴图从北极
		// 投影，UV 映射 SFS 内部处理），atmoClass==1 金星硫酸云偏黄；地球类保持白色。
		// 沙尘暴固定沙色（干燥系统，不管行星大气分级——沙尘层本色）。
		if (s.type == StormType.DustStorm)
		{
			cloudTint = new Color(0.88f, 0.74f, 0.52f);   // 沙黄
		}
		else if (s.atmoClass == 2)
		{
			// 优化#4 — GetTerrainColor 纹理采样 8s 节流（巨行星云色基准随风暴
			// 移动缓慢变化，每帧采样浪费；8s 刷新视觉无差）。
			cloudTintTimer -= dt;
			if (cloudTintTimer <= 0f)
			{
				cloudTintTimer = 8f;
				try
				{
					Color pc = s.planet.GetTerrainColor(S.MergedStormC());
					cloudTint = Color.Lerp(Color.white, pc, 0.55f);
				}
				catch
				{
					cloudTint = new Color(0.85f, 0.62f, 0.4f);   // 兜底：大红斑橙红
				}
			}
		}
		else if (s.atmoClass == 1)
		{
			cloudTint = new Color(1f, 0.93f, 0.75f);   // 金星硫酸云黄
		}
		else
		{
			cloudTint = Color.white;
		}
		float num = (float)TyphoonConfig.I.cloudOpacity;
		int num2 = 0;
		// 图层反转：云层（所属系统）要盖住附属现象（龙卷/下击暴流）。
		// WriteQuad 全 z=0，绘制顺序=覆盖顺序（后画的盖先画的），但缓冲按 index 顺序绘制。
		// 让 puffs 云层写入缓冲后部（index = i + phenomenaQuads），附属现象从 0 开始 →
		// 云层 index 大 = 更上层，龙卷漏斗顶端/下击暴流出生点被云体遮挡（藏进云里）。
		int phenomenaQuads = A.backQuads - puffs.Length;
		// 消散渐隐（用户：粒子消失可能是消散机制问题）：消散期（stage=2 寿命尾段）
		// 全链淡出——粒子/附属/雨 alpha 统一乘 DissolveFade（1→0），寿命到头移除时已
		// 渐隐完毕，不再"啪"一下整团消失。
		float dissolveFade = s.DissolveFade();
		// 每帧常量缓存：原粒子循环与附属现象循环内每 quad 调 MergeFade()/
		// TransitionBlend()（方法调用 + 分支），本帧内取值不变 → 缓存一次全帧复用。
		float mergeFade = s.MergeFade();
		// 风暴中心速度（drift 切向）：粒子绝对速度 = 相对风（SampleWind false）+
		// 本速度（跟随风暴整体移动）——修复原 SampleWind 含不均匀 drift 分量（中心
		// 0.675×drift < 风暴速度 1.0×drift）把粒子推挤到 4.6Rmax 重生消失的"从左到右消"。
		Double2 centerVel = s.CenterVelocity();
		// 去掉 真冻结（用户：粒子那一刻就像卡了一样，所以去了——>15Rmax
		// 冻结时云团完全静止像卡住，视觉差；且粒子全量更新成本可接受）。粒子现在任何
		// 距离都全量构建：移动 + 生命周期 + 重生（云永远在动，远处也流畅）。
		for (int i = 0; i < puffs.Length; i++)
		{
			Puff puff = puffs[i];
			// 粒子性能优化：①风采样缓存——每 0.1s 才 SampleWind 一次（2600 粒子
			// 每帧全量采样是最大开销，缓存后采样量降 1/10，云移动视觉无差）。
			Double2 val;
			if (puff.windT <= 0f)
			{
				// 相对风（不含 drift 平流）：粒子只受旋转/径向/湍流/附属影响，
				// 对称稳定不再被推挤出风暴；整体跟随风暴由 centerVel 保证（canopy 的
				// kind 缩放只作用于相对风，中心速度全量跟随——云顶薄云不再拖尾）。
				val = s.SampleWind(puff.pos, false);
				puff.wind = val;
				puff.windT = 0.1f;
			}
			else
			{
				val = puff.wind;
			}
			puff.windT -= dt;   // 采样缓存保持现实节奏（时间加速下 0.1 游戏秒瞬间耗尽会每帧全量采样， 优化失效）
			ref Double2 pos = ref puff.pos;
			pos += (val * ((puff.kind == 0) ? ((double)simDt * 0.12) : ((double)simDt)) + centerVel * simDt);   // 粒子位置用游戏时间 dt，与风暴中心同步（时间加速不甩粒子）
			// 去掉 性能优化（用户：就是这个导致云被吃——非活区粒子
			// 不死不重生）：life 无条件递减（正常生命周期），粒子在任何距离都走完整
			// 生命周期+重生，云团永远稳定。开销可忽略（2600 粒子 26-140s 寿命 → 每秒
			// 仅 ~30 次重生， 优化收益极小、副作用致命）。
			// life 保持现实 dt（若按游戏时间，时间加速 100x 时全部粒子 ~1 现实秒
			// 重生一轮 = 每秒数千次 SpawnPuff 卡顿；现实节奏下重生稳定，出界回收已兜底守恒）。
			puff.life -= dt;
			s.ToStormFrame(puff.pos, out var s2, out var h);
			double num3 = Math.Abs(s2) / s.Rmax;
		// 垂直归一化到 [云底, 云顶]（num4=0 云底、=1 云顶），云体悬浮在空中。
		double vSpan = Math.Max(1.0, s.Htop - s.Hbase);
		double num4 = (h - s.Hbase) / vSpan;
		// 去掉 性能优化：重生条件不再受 puffLive 限制，粒子在任何
		// 距离都正常生命周期+出界回收（ 已让出界无条件，本版连 life 也放行）。
		// 真冻结（>15Rmax BuildBack return）保留——那是彻底跳过重算，与
		// 的"只移动不重生"是两层不同机制。
		if (puff.life <= 0f || num4 < 0.04 || num4 > 1.2 || num3 > 4.6)
		{
			Double2 rebornOld = puff.pos;   // 重生延续：旧位置备份（life 到点原地续命）
			// 结构整理：粒子"被吹出 4.6Rmax"诊断日志（dbgOutCount/Pos/Neg + 每 2s 拼一条
			// 含 6 次 ToString 的字符串）已删除——该诊断用于定位"粒子从左到右被吃"，
			// 根因（drift 平流被不均匀叠加）修复后已无用途，只留计数器与潜在 GC 噪声。
			puff = SpawnPuff(puff.kind);
			// 重生延续（演化讨论：重生位置改"旧位置+扰动"防闪烁，云型稳定隐形大头）：
			// 寿命到点原地续命（微切向扰动，云不整团跳变）；出界（num3/num4）才随机重生
			// （出界粒子必须回界内，原地会死循环）。
			if (puff.life <= 0f)
			{
				puff.pos = rebornOld + FromStorm(s.Rmax * 0.15 * (Random.value - 0.5), 0.0) - FromStorm(0.0, 0.0);
			}
			s.ToStormFrame(puff.pos, out s2, out h);
			num3 = Math.Abs(s2) / s.Rmax;
			num4 = (h - s.Hbase) / vSpan;
		}
			// 径向 alpha 按类型（不再全是台风眼壁环）：
			// 台风：眼壁环（num5 偏置产生"眼"）
			// 飑线：线状——阵风锋在 ro≈0.9 处最亮，向两侧衰减（沿经度拉长由 SpawnPuff 保证）
			// 单体类：中心云团——ro 中心最浓、单调向外衰减（无风眼）
			// 云缘柔和（用户：云体与蓝天边缘过渡突兀）：
			// 台风眼壁 0.45→0.6（眼壁内外更柔和）；单体径向衰减 1.15→1.7（渐隐范围更大）；
			// 非台风云缘统一加碎絮噪声（边缘过渡带按 seed 径向正弦扰动）→
			// 云团边缘破碎蓬松，不再是一条规则圆弧硬边怼着蓝天。
			bool isRot = s.type == StormType.Typhoon;
			bool isLine = s.type == StormType.SquallLine;
			double num6;
			if (isRot)
			{
				// 台风眼修复：cat≤1(TD/TS) 中心云团无眼；cat≥2 眼壁环。
				// 眼壁按强度分级（TyphoonEyeR：STS 0.22 → 超强 0.40，贴近现实
				// 眼径 30-60km×30%=9-18km）+ 眼内稀云：眼内（ro<眼壁）一层稀薄云
				// （真实台风眼内低云/薄云），台风越强眼内越稀（cat2 0.12 → cat6 0.03）。
				// 眼清晰度由能量驱动（用户：能量制可以搞眼清晰程度，消散时眼自己
				// 就没了）：能量高 → 眼壁锐利 + 眼内稀云（清晰台风眼）；能量降 → 眼壁模糊
				// 变宽 + 眼内被云填（眼壁崩塌）；消散期 → 眼结构消失变均匀云团，配合
				// DissolveFade 整体渐隐——"眼先没了，风暴再散"的自然消散视觉（气象真实：
				// 减弱台风眼壁置换/崩塌，眼会先消失）。
				double eyeSharp = WeatherSystem.Clamp01((s.energy - 30.0) / 50.0);   // 30→0 眼没, 80→1 锐利
				float num5 = (float)WeatherSystem.TyphoonEyeR(s.category);
				// EWRC 眼径外扩（置换期眼壁向外扩展，复强后回缩——眼清晰度+眼径
				// 双重表达"眼先糊后清"，复用能量→眼清晰度联动，玩家肉眼可见置换周期）。
				// （专项 C 打磨）— 外扩段 0.4→0.85 改 0.4→1.0 高斯回缩（原 0.85 处
				// 硬切回缩跳变——外扩在 0.85 突然归零，改为 1.0 平滑回缩）。
				if (s.ewrcT >= 0.0 && s.ewrcT >= 0.4)
				{
					num5 *= 1.0f + 0.3f * (float)(Smooth01((s.ewrcT - 0.4) / 0.3) * (1.0 - Smooth01((s.ewrcT - 0.7) / 0.3)));
				}
				if (num5 <= 0f)
				{
					num6 = 1.0 - Smooth01((num3 - 0.45) / 1.2);   // TD/TS：中心云团（无眼）
				}
				else if (num3 < (double)num5)
				{
					double eyeHaze = 0.15 - s.category * 0.022;
					if (eyeHaze < 0.03)
					{
						eyeHaze = 0.03;
					}
					eyeHaze = eyeHaze + (0.8 - eyeHaze) * (1.0 - eyeSharp);   // 能量降 → 眼内云浓（眼被填）
					// 眼内稀云：中心略稀（0.4×），向眼壁内缘渐浓到 eyeHaze
					num6 = eyeHaze * (0.4 + 0.6 * Smooth01(num3 / (double)num5));
				}
				else
				{
					// R2 台风眼重构（审查🔴-R2：原 Smooth01 单调上升最亮环≈0.85R
					// 落在风/雨峰 0.4R 外侧 0.45R——"亮云墙≠最强风雨"）：眼壁改高斯峰贴
					// eyeR+0.15（σ=lerp(0.4,0.15,eyeSharp)：能量降 σ 变宽=眼壁崩塌，moat 随
					// σ 平移）；峰后 moat 弱云带 0.2（真实台风眼壁外干空隙，不断环）→ 外圈
					// 雨带云回升 0.6（1.8-2.2R）→ 3.8R 外渐隐。
					double wallSigma = 0.4 - 0.25 * eyeSharp;
					double peakR = (double)num5 + 0.15;
					double wall = Math.Exp(0.0 - WeatherSystem.Pow2((num3 - peakR) / wallSigma));
					double outerBand = Math.Exp(0.0 - WeatherSystem.Pow2((num3 - 2.0) / 1.2)) * 0.4;
					num6 = 0.2 + 0.8 * wall + outerBand * (1.0 - Smooth01((num3 - 3.2) / 1.4));
					num6 *= 1.0 - Smooth01((num3 - 3.8) / 0.8);   // 尾部渐隐（4.6R 前归 0）
				}
				// 残余低压逗点化（消散产物讨论：台风消散=变性/残余低压，对称圆盘
				// → 松散逗点状云）：能量 30→10（commaK 0→1）——眼壁尾侧撕环（逗点方向
				// commaDir×s2 负侧 wall 减弱）、加尾臂云带（逗点尾侧窄带）、整体松散
				// （云量减少）。gate atmoClass==0 已在逻辑层（commaK=0 不触发）。
				if (s.commaK > 0.01 && s.type == StormType.Typhoon)
				{
					double dir = (s2 * s.commaDir) / s.Rmax;   // 逗点方向投影（s2 有符号切向）
					double tailArm = 0.45 * Math.Exp(0.0 - WeatherSystem.Pow2((num3 - 2.6) / 1.6)) * Smooth01(dir + 0.5);   // 尾臂在逗点尾侧
					num6 = num6 * (1.0 - 0.55 * s.commaK * Smooth01(0.0 - dir)) + tailArm * s.commaK;   // 尾侧撕环 + 尾臂
					num6 *= 1.0 - 0.35 * s.commaK;   // 整体松散（残余低压云量减少）
				}
			}
			else if (isLine)
			{
				// 飑线弓形剖面（形态签名：审查"飑线最大的错是对称"——改沿 s 非对称：
				// 前缘低弧云墙（ro 0.7 峰）+ 中段高塔 + 尾部低平层状雨盾）。2D 横截面：
				// 前缘云墙→高塔→尾部层状区沿移动方向展开。
				double front = Math.Exp(0.0 - WeatherSystem.Pow2((num3 - 0.7) / 0.55));
				double tail = 0.4 * Math.Exp(0.0 - WeatherSystem.Pow2((num3 - 1.8) / 1.3));
				num6 = 0.3 + 0.7 * front + tail;
			}
			else
			{
				// 非台风差异化（用户：全都特别像）：径向云团宽度按类型——
				// 超级单体=紧凑浓核（0.9）、多单体=稍宽（1.45）、MCS=大而散（1.9）、单体=中等（1.7）。
				// 形态签名：单体改窄高柱（1.7→1.2，孤立柱），超单紧凑浓核不变（墙云
				// 在下方独立调制）。
				double wCell;
				if (s.type == StormType.Supercell)
				{
					wCell = 0.9;
				}
				else if (s.type == StormType.MCS)
				{
					wCell = 1.9;
				}
				else if (s.type == StormType.Multicell)
				{
					wCell = 1.45;
				}
				else
				{
					wCell = 1.2;
				}
				num6 = 1.0 - Smooth01((num3 - 0.35) / wCell);
			}
			// R2 螺旋调制（审查🔴-R2：台风=眼+实心圆盘，无 moat 无螺旋）：粒子切向
			// 坐标 s2 相干斜条纹 = 螺旋雨带/云带投影（seed 每粒子常数只出点状花斑，s2 全局
			// 坐标才出相干螺旋错觉）。独立 if（不挂 if-else 链，链已闭合）。
			if (isRot)
			{
				// 逗点化时螺旋减弱（残余低压结构松散，螺旋雨带模糊消失）
				num6 *= 0.6 + 0.4 * (1.0 - 0.6 * s.commaK) * Math.Sin((s2 / s.Rmax) * 4.2 - num3 * 1.2 + (double)puff.seed * 0.3);
			}
			// 形态签名（渲染层形态学讨论落地，全部落在 num6 的 (s,h) 函数零新架构）：
			if (s.type == StormType.Supercell)
			{
				// 超单墙云：云底下方悬挂的宽扁暗云（低 h、宽 s），绕轴慢漂（seed 相位）。
				double wallCloud = Math.Exp(0.0 - WeatherSystem.Pow2((num4 - 0.08) / 0.16)) * Math.Exp(0.0 - WeatherSystem.Pow2((num3 - 0.65) / 0.9));
				num6 += 0.5 * wallCloud;
			}
			else if (s.type == StormType.MCS)
			{
				// MCS：顶部平砧（高 h 水平宽层）+ 中心 3-5 核塔凸起（num3 正弦塔状）。
				double anvil = Math.Exp(0.0 - WeatherSystem.Pow2((num4 - 0.92) / 0.14)) * Math.Exp(0.0 - WeatherSystem.Pow2((num3 - 1.2) / 1.1));
				double tower = 0.25 * Math.Sin(num3 * 7.0 + (double)puff.seed * 6.283) * Math.Exp(0.0 - WeatherSystem.Pow2((num4 - 0.6) / 0.35));
				num6 += 0.5 * anvil + tower;
			}
			else if (s.type == StormType.Multicell)
			{
				// 多单体：3-5 塔群并排（num3 周期调制形成柱状群），钳 1.2 防 alpha 溢出。
				num6 = Math.Min(1.2, num6 * (1.0 + 0.35 * Math.Sin(num3 * 6.0 + (double)puff.seed * 4.0)));
			}
			if (!isRot)
			{
				double edgeBand = Math.Exp(0.0 - WeatherSystem.Pow2((num6 - 0.5) / 0.42));   // 云缘过渡带（num6≈0.5）
				num6 *= 1.0 - 0.5 * edgeBand * (0.5 + 0.5 * Math.Sin((double)puff.seed * 6.283 + num3 * 9.0));
			}
			else if (num3 > 1.5)
			{
				// （终审🟡-12）— 台风云缘碎絮：眼壁与雨带之外（>1.5R）补碎絮调制
				// （避开眼壁高斯峰与 moat 段），打破同心圆弧硬边，云缘变破碎蓬松
				// （现实台风外缘是破碎的螺旋云带外沿）。
				double edgeBand2 = Math.Exp(0.0 - WeatherSystem.Pow2((num6 - 0.4) / 0.5));
				num6 *= 1.0 - 0.35 * edgeBand2 * (0.5 + 0.5 * Math.Sin((double)puff.seed * 6.283 + num3 * 8.0));
			}
			double num7 = Math.Exp(0.0 - WeatherSystem.Pow2(Math.Max(0.0, num3 - (0.7 + 0.9 * num4)) / 3.4));
			double num8 = Mathf.Clamp01(puff.life / 4f) * Mathf.Clamp01((puff.maxLife - puff.life) / 4f);
			double num9 = (double)puff.baseAlpha * num6 * num7 * num8 * (double)num * (double)mergeFade   // 合并渐隐
				* (double)spawnAnimT * (double)dissolveFade * (double)visMul;   // 粒子雾：真实能见度→密/稀（类型转变淡出淡入已折进 spawnAnimT）
			num9 = Math.Min(1.0, num9);   // alpha 封顶(墙)：低能见度系统近侧 puff 不透明
			// 云底侵蚀（消散产物#2，对流系统通用）：消散期（energy 20→0）云底先
			// 透明、顶部砧云后散——"从下往上散"的对流消散签名（超单/单体/MCS 的砧云残留
			// 是现实消散最标志性的视觉）。erode=Clamp01((20-energy)/20)、
			// hErode=1-erode×(1-Smooth01(num4/0.6))：0.6H 以下先蚀、以上砧云保留。
			// 设置开关 cloudBaseErosion 控制。与残余低压（逗点化改形状 num6）正交——形状
			// 层（30→10 段）与透明度层（20→0 段）相位错开，互不冲突。
			if (TyphoonConfig.I.cloudBaseErosion && s.stage == 2)
			{
				double erode = WeatherSystem.Clamp01((20.0 - s.energy) / 20.0);
				num9 *= 1.0 - erode * (1.0 - Smooth01(num4 / 0.6));
			}
			Color val2;
			if (puff.kind == 0)
			{
				val2 = Color.Lerp(CanopyLow, CanopyHigh, (float)WeatherSystem.Clamp01(num4 * 1.1));
			}
			else
			{
				double num10 = 0.55 + 0.45 * Math.Sin(num3 * 2.6 - s.age * 0.22 + (double)puff.seed * 6.283);
				num9 *= num10;
				if (num4 > 0.8)
				{
					num9 *= 0.55 + 0.45 * Math.Exp(0.0 - WeatherSystem.Pow2((num4 - 0.9) / 0.25));
				}
				val2 = Color.Lerp(CloudLow, CloudHigh, (float)WeatherSystem.Clamp01(num4 * 1.25));
			}
			// 巨行星/厚大气云色（rgb 乘 tint，alpha 不变；白 tint 时中性无开销）
			if (cloudTint.r < 0.999f || cloudTint.g < 0.999f || cloudTint.b < 0.999f)
			{
				val2 = new Color(val2.r * cloudTint.r, val2.g * cloudTint.g, val2.b * cloudTint.b, val2.a);
			}
			if (num9 < 0.004)
			{
				num9 = 0.0;
			}
			if (flashPower > 0.001f)
			{
				double num11 = Math.Sqrt(WeatherSystem.Pow2((s2 - flashS) / (s.Rmax * 1.3)) + WeatherSystem.Pow2((h - flashH) / (s.Htop * 0.45)));
				float num12 = flashPower * (float)Math.Exp((0.0 - num11) * num11);
				if (num12 > 0.002f)
				{
					val2 = Color.Lerp(val2, new Color(0.82f, 0.9f, 1f), Mathf.Clamp01(num12 * 1.6f));   // R15 — 闪电色偏蓝（冷白光，审查🟡-R15 原暖白 (1,0.97,0.85) 不像闪电）
					num9 = Math.Min(1.0, num9 + (double)num12 * 0.5);
				}
			}
			// 云缘白化：边缘逐渐透明 + 向白渐变（真实云缘受光泛白、化入背景）。
			// num6 越小越靠边 → 颜色越白；alpha 由 num9 照常渐隐到 0 → 蓝→白→透明→蓝天，
			// 过渡比纯透明更柔和（白色在蓝天前有弥散感，不突兀）。
			double whiten = (1.0 - num6) * 0.6;
			// R2 moat 白化禁用（审查🔴-R2：moat 弱云带 0.2 会触发 whiten≈0.48
			// 变低 alpha 白雾反亮，moat 变"亮空隙"）：台风下 whiten 只在 num6>0.15 生效，
			// 眼内稀云/moat（0.2）不泛白，外圈（0.6+）正常。
			if (isRot)
			{
				whiten *= Smooth01((num6 - 0.15) / 0.2);
			}
			if (whiten > 0.02)
			{
				val2 = Color.Lerp(val2, Color.white, (float)Math.Min(0.9, whiten));
			}
			val2.a = (float)num9;
			Vector2 val3 = WorldView.ToLocalPosition(puff.pos);
			float num13 = puff.size * rs;
			// 云缘柔和：云体粒子在云团边缘（num6 小）收缩尺寸 → 外圈渐隐蓬松，
			// 与蓝天过渡不再是一道硬边。canopy 云顶层（kind==0）保持连续覆盖不缩。
			if (puff.kind == 1 && num6 < 0.9)
			{
				num13 *= (float)(0.45 + 0.55 * (num6 / 0.9));
			}
			float num14 = camHalf * 6f;
			if (num13 > num14)
			{
				num13 = num14;
			}
			float num15 = 2f;
			if (num13 < num15)
			{
				num13 = num15;
			}
			float num16 = num13 / rs;
			// 图层反转：云层写入缓冲后部（index=i+phenomenaQuads，高层），
			// 附属现象（龙卷/下击暴流）从 index 0 开始（低层）→ 云层盖住附属现象。
			if (i + phenomenaQuads < A.backQuads)
			{
				// 绝对顶点模式：顶点=相对台风中心预缩放(值小、float 精度好)，GO scale=1。
				Vector2 centre = align ? (val3 - alignOrigin) : val3;
				float quadHalf = num16;
				if (farAbs)
				{
					centre = (val3 - alignOrigin) * (farAbsS / 10000f);
					quadHalf = num16 * farAbsS / 10000f;
				}
				WriteQuad(A.bV, A.bT, A.bC, i + phenomenaQuads, centre, quadHalf, quadHalf, Vector2.right, val2, 0f);
			}
			puffs[i] = puff;
		}
		// / — 附属龙卷（粒子 1/2：漏斗）：收缩圆盘列，从云底向下长。
		// grow = min(形成相位, 强度)：形成时向下长、消散时向上缩（消散动画）；
		// 顶端喇叭口接云底（wall cloud，解决"漏斗没接上云体"）；颜色提亮清晰。
		// grow 太小整体隐藏：避免 phase=0 时全部圆盘塌缩在云底叠成大圆盘
		// （"先出现在云底、长好后在地面"的观感错位，用户反馈"生成后突然变位置"）。
		// 多实例遍历（数量限制解除 + 随机位置）：每龙卷独立锚点/强度/相位。
		if (S != null && S.planet != null && num2 < A.backQuads)
		{
			for (int fi = 0; fi < S.tornadoes.Count; fi++)
			{
				WeatherSystem.FxInst fx = S.tornadoes[fi];
				if (fx.strength <= 0.05 || Math.Min(fx.phase, fx.strength) <= 0.04)
				{
					continue;
				}
				// 锚定修复：行星全局偏移 → 一次 ToLocalPosition（与云层 puff 同构，
				// 不再相机本地坐标加偏移——那是"玩家动现象跟着动"的根因）。
				Double2 stormC = FxAnchor(S, fx);   // 实例锚点（合并中心+切向偏移）
				Double2 radialP = stormC.normalized;
				Double2 perpP = new Double2(0.0 - radialP.y, radialP.x);
				double grow = Math.Min(fx.phase, fx.strength);
				// 龙卷生成点上提：顶端（t=1）从云底 Hbase 升到云内 Hbase+8% 云带，
				// 配合图层反转（云层盖附属）→ 漏斗从云中"钻出"而非贴在云底下缘。
				double torRise = (S.Htop - S.Hbase) * 0.08;
				// 生长方向修正：漏斗顶端恒接云底（t=1 → Hbase+torRise），底端 = (Hbase+torRise)×(1−grow)
				// 从云内向下长（grow 0→1 底端下降到地面）。原公式顶端随 grow 上移、底端恒在地面
				// → 看起来"从地上长起来"（方向反了）。
				// 漏斗本体改纯静态形态粒子（用户确认：要有不会动、只构筑形态的粒子）：
				// 去掉 spin 旋转 + sway 摆动，14 层圆盘位置固定（仅随 grow 上下做形成/消散动画），
				// 稳定构筑漏斗骨架；旋转动感全部交给螺旋（贴壁转）/碎片（卷升）/卷尘环（翻卷）。
				int layers = 14;
				bool ropeOut = fx.dissolving;   // 提到循环外（子涡段也要用）
				// 漏斗宽度用 TornadoRefR（基准钳到 4km）而不是 S.Rmax —— 否则 MCS
				// （Rmax 12.4km）的龙卷会被画成 ~1km 宽的怪柱；与风场共用同一基准。
				double torRef = S.TornadoRefR;
				// 尺度倍率（类型 × 超巨型）与风场 TornadoCoreR 共用 → 视觉漏斗 = 吸你的
				// 范围。极端楔形（EF5 级）放到 ~1km 级（El Reno 2013 那种）。
				double sizeK = S.TornadoSizeK(fx);
				for (int li = 0; li < layers && num2 < A.backQuads; li++)
				{
					double t = (double)li / (double)(layers - 1);
					double hh = (S.Hbase + torRise) * (1.0 - grow * (1.0 - t));
					// 细漏斗本体 + 顶端喇叭口（t>0.8 宽展接云底）
					// 可见性加强（用户：自带滤镜下龙卷要更明显）：本体整体加宽 1.6 倍
					// （地面 174 m → 现实等效 580 m，仍在强龙卷 100-500 m 量级上沿），
					// 滤镜灰化+屏幕比例下不再是"一根细线"。
					double rr = torRef * (0.024 + 0.085 * t);
					if (t > 0.8)
					{
						rr += torRef * (t - 0.8) * 0.9;
					}
				// 龙卷类型（variant 自动派生）：楔形=宽实墙、绳状/rope-out=细+透、
				// 水龙卷=白水柱、陆龙卷=细尘柱。dissolving（消散）→ 绳状 rope-out（变细+
				// 底部蛇形摆+底部先透，复用绳状参数块——整合复查确认 rope-out 与 dissolving
				// 是同一件事，共用参数）。
				// 多涡旋（5）主涡略细、卫星（6）正常、gustnado（7）=矮小尘旋
				// （高度只到 60%——阵风锋前沿的弱涡旋，无深对流）。
				// 尺度倍率已并入 TornadoSizeK（类型 × 超巨型）；消散 rope-out 仍单独变细
				rr *= sizeK;
				if (ropeOut && fx.variant != 2)
				{
					rr *= 0.4;
				}
				double tEff = t * ((fx.variant == 7) ? 0.6 : 1.0);   // gustnado 矮（阵风锋小涡旋）
				hh = (S.Hbase + torRise) * (1.0 - grow * (1.0 - tEff));
					Double2 planetPos = stormC + radialP * hh;   // 行星全局 → 一次转换
					Vector2 cen = WorldView.ToLocalPosition(planetPos);   // 静态：无 sway 摆动
					Vector2 qc = align ? (cen - alignOrigin) : cen;
					// rope-out 蛇形摆：消散时底部横向摆动（绳状扭曲感），越近地摆幅越大
					// （终审🟡-8）— 加时间相位 S.age×2.0：原 sway 只依赖层索引 li 与种子，
					// 是固定 S 形（不扭动）；加时间项后蛇形翻卷动画真实。
					if (ropeOut)
					{
						double sway = S.Rmax * 0.12 * Math.Sin(li * 0.9 + S.age * 2.0 + fx.seed * 20.0) * (1.0 - t);
						qc += new Vector2((float)sway, 0f);
					}
					float halfW = (float)rr;
					float halfC = (float)(rr * 0.65);
					float halfD = (float)(rr * 1.35);   // 对比衬底（比壁再宽 35%）
					if (farAbs)
					{
						qc = (cen - alignOrigin) * (farAbsS / 10000f);
						halfW = halfW * farAbsS / 10000f;
						halfC = halfC * farAbsS / 10000f;
						halfD = halfD * farAbsS / 10000f;
					}
					// 漏斗颜色按类型（龙卷滤镜讨论：壁预提亮防灰化发绿；水龙卷白水、
					// 陆龙卷土色）；rope-out 底部先透（alpha × (0.4+0.6×t) 底部消失）。
					// 可见性加强（用户：滤镜下龙卷要更明显）：整体再提亮一档 + 壁下加一层
					// 暗衬底（拉对比：亮壁压在暗衬上，灰化滤镜下也仍是明确的锥体剪影）。
					Color wallCol = new Color(0.86f, 0.89f, 0.97f);
					Color coreCol = new Color(0.96f, 0.98f, 1f);
					if (fx.variant == 3)
					{
						wallCol = new Color(0.90f, 0.94f, 0.98f);   // 水龙卷：白水雾
						coreCol = new Color(0.97f, 0.99f, 1f);
					}
					else if (fx.variant == 4 || fx.variant == 7)
					{
						wallCol = new Color(0.74f, 0.66f, 0.52f);   // 陆龙卷/gustnado：土尘色（提亮一档）
						coreCol = new Color(0.84f, 0.79f, 0.68f);
					}
					float ropeFade = ropeOut ? (0.4f + 0.6f * (float)t) : 1f;
					float aWall = 0.95f * torVisMul * (0.52f + 0.55f * (float)t) * (0.4f + 0.6f * (float)grow) * mergeFade * dissolveFade * ropeFade * spawnAnimT;   // 合并渐隐； 消散渐隐； rope-out 底部先透
					float aDark = aWall * 0.55f;   // 衬底：只在高空壁区给对比（近地由尘柱提供暗部）
					if (aDark > 0.02f)
					{
						WriteQuad(A.bV, A.bT, A.bC, num2++, qc, halfD, halfD, Vector2.right, new Color(0.20f, 0.23f, 0.30f, aDark), 0f);
					}
					WriteQuad(A.bV, A.bT, A.bC, num2++, qc, halfW, halfW, Vector2.right, new Color(wallCol.r, wallCol.g, wallCol.b, aWall), 0f);
					float aCore = 0.32f * torVisMul * (0.5f + 0.5f * (float)t) * (0.4f + 0.6f * (float)grow) * mergeFade * dissolveFade * ropeFade * spawnAnimT;   // 合并渐隐； 消散渐隐； 生成动画
					WriteQuad(A.bV, A.bT, A.bC, num2++, qc, halfC, halfC, Vector2.right, new Color(coreCol.r, coreCol.g, coreCol.b, aCore), 0f);
				}
				// 多涡旋（5）/卫星龙卷（6）子涡：主漏斗外附加 2/1 个小漏斗绕转。
				// 多涡旋=主涡内 2 子涡快速绕转（真实：子涡沿眼壁内侧旋转）、卫星=外侧轨道
				// 1 小漏斗慢绕。subAngle 用现实时间累计（视觉动效，时间加速下不糊成环）。
				// quad 预算 1200 充裕（子涡 8 层×2 实例 ≈ 16 quads/龙卷）。
				int subN = (fx.variant == 5) ? 2 : ((fx.variant == 6) ? 1 : 0);
				if (subN > 0 && !ropeOut && S.planet != null)
				{
					double subR = ((fx.variant == 5) ? torRef * 0.05 : torRef * 0.24) * sizeK;   // 多涡贴主涡 / 卫星外侧轨道
					double subW = (fx.variant == 5) ? 0.30 : 0.42;                     // 子涡宽度（主 rr 倍数）
					fx.subAngle += dt * ((fx.variant == 5) ? 7.0 : 2.2);               // 多涡快绕 / 卫星慢绕（现实时间）
					double baseAng = fx.subAngle + fx.seed;
					for (int si = 0; si < subN && num2 + 8 < A.backQuads; si++)
					{
						double ang = baseAng + si * 3.141592653589793;
						Double2 off = perpP * (Math.Cos(ang) * subR) + radialP * (Math.Sin(ang) * subR);
						Double2 subC = stormC + off;
						for (int li2 = 0; li2 < 8 && num2 < A.backQuads; li2++)
						{
							double t2 = (double)li2 / 7.0;
							double hh2 = (S.Hbase + torRise) * (1.0 - grow * (1.0 - t2));
							double rr2 = torRef * (0.012 + 0.05 * t2) * subW * sizeK;
							Double2 pp2 = subC + radialP * hh2;
							Vector2 cen2 = WorldView.ToLocalPosition(pp2);
							Vector2 qc2 = align ? (cen2 - alignOrigin) : cen2;
							if (farAbs)
							{
								qc2 = (cen2 - alignOrigin) * (farAbsS / 10000f);
								rr2 *= farAbsS / 10000f;
							}
							float aW2 = 0.6f * (0.4f + 0.6f * (float)t2) * (0.4f + 0.6f * (float)grow) * mergeFade * dissolveFade * spawnAnimT;   // （fix-checker ⚠️-3）子涡补乘 spawnAnimT
							WriteQuad(A.bV, A.bT, A.bC, num2++, qc2, (float)rr2, (float)rr2, Vector2.right, new Color(0.66f, 0.70f, 0.84f, aW2), 0f);
						}
					}
				}
			}
		}
		// 地面卷尘环：漏斗底部触地点绕一圈尘土粒子（真实龙卷触地的标志性现象：
		// 地面尘土被吸起绕着底端旋转）。漏斗底部细尖只有 0.015Rmax（超单 9m）远看不可见，
		// 卷尘环让"触地"在视觉上一目了然——底部始终贴地、尘粒绕底端翻卷。
		// grow 太小整体隐藏（与漏斗/螺旋/碎片同一条件，形成/消散动画一致）。
		// 多实例遍历。
		if (S != null && S.planet != null && num2 < A.backQuads)
		{
			for (int fi = 0; fi < S.tornadoes.Count; fi++)
			{
				WeatherSystem.FxInst fx = S.tornadoes[fi];
				if (fx.strength <= 0.05 || Math.Min(fx.phase, fx.strength) <= 0.04)
				{
					continue;
				}
				Double2 stormC = FxAnchor(S, fx);   // 实例锚点
				Double2 radialP = stormC.normalized;                       // 行星全局径向
				Double2 perpP = new Double2(0.0 - radialP.y, radialP.x);   // 行星全局切向
				double grow = Math.Min(fx.phase, fx.strength);
				double torRise = (S.Htop - S.Hbase) * 0.08;
				// 漏斗底端当前高度：grow=1 → 0（贴地）；形成/消散期间随底端一起上下。
				double hhBase = (S.Hbase + torRise) * (1.0 - grow);
				int dustN = 8;
				for (int d = 0; d < dustN && num2 < A.backQuads; d++)
				{
					double ang = S.age * 5.0 + (double)d * 0.785;
					// 尘环半径脉动（尘土被卷着翻，不是规整圆环）
					double torRefD = S.TornadoRefR;
					double sizeKD = S.TornadoSizeK(fx);
					double dustR = torRefD * (0.04 + 0.05 * (0.5 + 0.5 * Math.Sin(S.age * 2.5 + (double)d))) * sizeKD;
					double hhD = hhBase + torRefD * 0.025 * Math.Abs(Math.Sin(S.age * 3.5 + (double)d * 1.3)) * sizeKD;
					Double2 planetPos = stormC + radialP * hhD + perpP * (dustR * Math.Cos(ang));   //
					Vector2 cen = WorldView.ToLocalPosition(planetPos);
					Vector2 qc = align ? (cen - alignOrigin) : cen;
					float half = (float)(torRefD * 0.026 * sizeKD);
					if (farAbs)
					{
						qc = (cen - alignOrigin) * (farAbsS / 10000f);
						half = half * farAbsS / 10000f;
					}
					// 提亮：灰屏（近距风暴变灰后处理）下原土棕(0.6,0.55,0.48)会被拉成
					// 和背景一样的灰暗 → 卷尘环改用更亮更暖的亮尘色(0.82,0.74,0.62) + alpha 提升，
					// 即使在轻微灰化下触地尘环依然醒目。
					float a = 0.72f * (0.4f + 0.6f * (float)grow) * (0.55f + 0.45f * (float)Math.Abs(Math.Sin(S.age * 3.0 + (double)d))) * mergeFade * dissolveFade * spawnAnimT;   // 合并渐隐； 消散渐隐
					WriteQuad(A.bV, A.bT, A.bC, num2++, qc, half, half, Vector2.right, new Color(0.82f, 0.74f, 0.62f, a), 0f);
				}
			}
		}
		// 附属龙卷（粒子 2/2：螺旋）：亮白小粒子绕漏斗轴螺旋旋转，
		// 在漏斗半径范围内流动 + 整体来回摆动（螺旋动感）。
		// grow 太小整体隐藏：避免 phase=0 时全部圆盘塌缩在云底叠成大圆盘
		// （"先出现在云底、长好后在地面"的观感错位，用户反馈"生成后突然变位置"）。
		// 多实例遍历。
		if (S != null && S.planet != null && num2 < A.backQuads)
		{
			for (int fi = 0; fi < S.tornadoes.Count; fi++)
			{
				WeatherSystem.FxInst fx = S.tornadoes[fi];
				if (fx.strength <= 0.05 || Math.Min(fx.phase, fx.strength) <= 0.04)
				{
					continue;
				}
				Double2 stormC = FxAnchor(S, fx);   // 实例锚点
				Double2 radialP = stormC.normalized;                       // 行星全局径向
				Double2 perpP = new Double2(0.0 - radialP.y, radialP.x);   // 行星全局切向
				double grow = Math.Min(fx.phase, fx.strength);
				// 螺旋生成点随漏斗顶端上提（云内 8% 云带），与漏斗同一基准。
				double torRise = (S.Htop - S.Hbase) * 0.08;
				// 螺旋粒子同样从云内向下长（uu=1 云底、uu=0 底端）。
				int spirN = 16;
				for (int si = 0; si < spirN && num2 < A.backQuads; si++)
				{
					double uu = (double)((si + S.age * 3.0) % (double)spirN) / (double)spirN;
					double hh = (S.Hbase + torRise) * (1.0 - grow * (1.0 - uu));
					double torRefS = S.TornadoRefR;
					double sizeKS = S.TornadoSizeK(fx);
					double helixR = torRefS * (0.04 + 0.065 * uu) * sizeKS;   // 螺旋贴边（沿漏斗壁旋转，边界显形）
					double ang2 = S.age * 7.0 + uu * 34.0 + (double)si;
					double sway2 = 0.06 * Math.Sin(S.age * 2.2 + (double)si * 1.7);  // 来回摆动
					Double2 planetPos = stormC + radialP * hh + perpP * (helixR * Math.Cos(ang2) + sway2 * torRefS * sizeKS);   //
					Vector2 cen = WorldView.ToLocalPosition(planetPos);
					Vector2 qc = align ? (cen - alignOrigin) : cen;
					float half = (float)(torRefS * 0.012 * sizeKS);
					if (farAbs)
					{
						qc = (cen - alignOrigin) * (farAbsS / 10000f);
						half = half * farAbsS / 10000f;
					}
					float a = 0.85f * torVisMul * (0.5f + 0.5f * (float)grow) * (1f - 0.35f * (float)uu) * mergeFade * dissolveFade * spawnAnimT;   // 合并渐隐； 消散渐隐
					WriteQuad(A.bV, A.bT, A.bC, num2++, qc, half, half, Vector2.right, new Color(0.88f, 0.93f, 1f, a), 0f);
				}
			}
		}
		// 附属龙卷（粒子 3/3：碎片 debris）：地面被卷起的碎屑，绕漏斗轴螺旋
		// 上升（模拟 EF5 级"车大小碎片空中飞"），土棕色调。
		// grow 太小整体隐藏：避免 phase=0 时全部圆盘塌缩在云底叠成大圆盘
		// （"先出现在云底、长好后在地面"的观感错位，用户反馈"生成后突然变位置"）。
		// 多实例遍历。
		if (S != null && S.planet != null && num2 < A.backQuads)
		{
			for (int fi = 0; fi < S.tornadoes.Count; fi++)
			{
				WeatherSystem.FxInst fx = S.tornadoes[fi];
				if (fx.strength <= 0.05 || Math.Min(fx.phase, fx.strength) <= 0.04)
				{
					continue;
				}
				Double2 stormC = FxAnchor(S, fx);   // 实例锚点
				Double2 radialP = stormC.normalized;                       // 行星全局径向
				Double2 perpP = new Double2(0.0 - radialP.y, radialP.x);   // 行星全局切向
				double grow = Math.Min(fx.phase, fx.strength);
				// 碎片生成点随漏斗顶端上提（云内 8% 云带），与漏斗同一基准。
				double torRise = (S.Htop - S.Hbase) * 0.08;
				int debN = 20;
				for (int di2 = 0; di2 < debN && num2 < A.backQuads; di2++)
				{
					double uu = (double)((di2 + S.age * 4.0) % (double)debN) / (double)debN;
					double hh = (S.Hbase + torRise) * (1.0 - grow * (1.0 - uu));   // 从地面被卷到云内（螺旋上升）
					double torRefB = S.TornadoRefR;
					double sizeKB = S.TornadoSizeK(fx);
					double dR = torRefB * (0.02 + 0.09 * uu) * sizeKB;
					double ang3 = S.age * 9.0 + uu * 26.0 + (double)di2 * 2.2;
					double sway3 = 0.05 * Math.Sin(S.age * 3.0 + (double)di2) * torRefB * sizeKB;
					Double2 planetPos = stormC + radialP * hh + perpP * (dR * Math.Cos(ang3) + sway3);   //
					Vector2 cen = WorldView.ToLocalPosition(planetPos);
					Vector2 qc = align ? (cen - alignOrigin) : cen;
					float half = (float)(torRefB * 0.01 * sizeKB);
					if (farAbs)
					{
						qc = (cen - alignOrigin) * (farAbsS / 10000f);
						half = half * farAbsS / 10000f;
					}
					float a = 0.85f * torVisMul * (float)grow * (1f - 0.35f * (float)uu) * mergeFade * dissolveFade * spawnAnimT;   // 合并渐隐； 消散渐隐
					WriteQuad(A.bV, A.bT, A.bC, num2++, qc, half, half, Vector2.right, new Color(0.66f, 0.52f, 0.36f, a), 0f);
				}
			}
		}
		// / — 附属下击暴流：亮灰粒子从云中垂直向下冲出，近地面向四周扩散。
		// 形成动画：alpha × 实例相位（fx.phase）逐渐增强。
		// 下暴多实例遍历（数量限制解除 + 随机位置）。
		if (S != null && S.planet != null && num2 < A.backQuads)
		{
			for (int fdi = 0; fdi < S.downbursts.Count; fdi++)
			{
				WeatherSystem.FxInst fx = S.downbursts[fdi];
				if (fx.strength <= 0.05)
				{
					continue;
				}
				Double2 stormC = FxAnchor(S, fx);   // 实例锚点
				Double2 radialP = stormC.normalized;                       // 行星径向（风暴中心方向）
				Double2 perpP = new Double2(0.0 - radialP.y, radialP.x);   // 行星切向
				double fall = (S.age * 0.9) % 1.0;
				double ph2 = fx.phase;
			// //// — 下击暴流=连续气流 + 生命周期克隆 + 多排并排：
			// ：8 波次 × 8 粒子，排线收窄到 1.0Rmax（粒子间距最小，密集一排）；
			// 这套"多波次并排下落+触地掀开+渐隐"渲染器可复用于降雨（用户洞察）。
			int dropsN = 64;
			int waves = 8;
			int perWave = dropsN / waves;
			double maxSpread = 0.0;   // 本实例触地扩散程度（供地面出流锋用）
			for (int di = 0; di < dropsN && num2 < A.backQuads; di++)
			{
				int wi = di / perWave;            // 波次
				int pi = di % perWave;            // 波内序号
				// 波次错相位（连续流）；波内粒子同相位 = 并排。
				double tt = ((double)wi / (double)waves + fall) % 1.0;
				// 出生点藏在云内，下落出云。发射器高度 20%→40% 云带（现实性体检：
				// 微下击暴流源于中层（2-4km AGL），不是紧贴云底；SFS 云带 0.3-1.2km
				// 对应中层 ≈ 40% 云带），下落距离更长、下冲感更对。
				// 扩散纯水平（下击暴流出流=直线风，不抬升不卷尘）。
				double hh = (S.Hbase + (S.Htop - S.Hbase) * 0.4) * (1.0 - tt);
				// 触地立即横移 + y 完全归零：扩散起点从 tt=0.88 推迟到 0.96（窗口 0.04），
				// 且 spread>0 后 hh 强制归 0——粒子垂直砸到底的那一瞬间 y 方向完全归零、
				// 立即沿排线向左右冲（不再有"贴着地面缓慢滑行"的阶段）。
				double spread = Smooth(WeatherSystem.Clamp01((tt - 0.96) / 0.04));
				if (spread > maxSpread)
				{
					maxSpread = spread;
				}
				if (spread > 0.01)
				{
					hh = 0.0;   // 触地：y 移动完全归零
				}
				// 排线宽度（间距）由 downburstGap 固化（0.3Rmax）。
				double lineW = S.Rmax * downburstGap;
				double offsetX = ((double)pi / (double)(perWave - 1) - 0.5) * lineW;
				// 视觉改造（本次）：下冲锥 —— 上层窄、贴地宽（真实下击暴流像蘑菇云下坠，
				// 不是等宽的一排雨丝）。tt=1（云内）0.5×、tt=0（地面）1.0×。
				offsetX *= (0.5 + 0.5 * (1.0 - tt));
				// 触地后全部向左右冲：沿排线（perp）向两端滑开
				// （offsetX 延伸，左半排往左、右半排往右；2.5→4 拐弯后加速冲开）。
				double offsetFinal = offsetX * (1.0 + spread * 4.0);
				// 地面遮挡判定：视线先穿过行星球面则被挡（不画）。
				Double2 centerP = stormC + radialP * hh;   // 实例锚点基准
				Double2 pPlanet = centerP + perpP * offsetFinal + radialP * 0.0;
				if (BlockedByPlanet(camG, pPlanet, S.planet.Radius))
				{
					continue;
				}
				// 生命周期 alpha：出生淡入（藏云里）→ 下落全亮 →
				// 触地扩散后渐隐到 0（碰地面往旁边冲，逐渐变透明，把自己删了）。
				float born = Mathf.Clamp01((float)(tt / 0.12));
				float fade = 1f - (float)spread;
				float a = 0.9f * (float)fx.strength * (float)ph2 * born * (0.25f + 0.75f * fade) * mergeFade * dissolveFade * spawnAnimT;   // 合并渐隐； 消散渐隐
				if (a < 0.02f)
				{
					continue;   // 已淡出 = 已删除
				}
				// 高度方向 = radialDir（行星径向，从地表向天空）。
				// 锚定修复：行星全局偏移 → 一次 ToLocalPosition（与云层 puff 同构）
				Double2 planetPos = stormC + radialP * hh + perpP * offsetFinal;
				Vector2 cen = WorldView.ToLocalPosition(planetPos);
				Vector2 qc = align ? (cen - alignOrigin) : cen;
				// 视觉改造（用户：下击暴流效果可以改）：原来是一串圆点（像雨滴），
				// 现在改成沿下落方向拉长的"气流条"——下落段细长（下冲感），触地扩散段
				// 横向摊开同时颜色由冷灰转土尘（掀尘）：
				//   halfH = 沿径向（下落）的长半轴；halfW = 横向窄半轴。
				float halfW = (float)(S.Rmax * 0.016);
				float halfH = (float)(S.Rmax * 0.055 * (1.0 + 1.6 * spread));
				if (farAbs)
				{
					qc = (cen - alignOrigin) * (farAbsS / 10000f);
					halfW = halfW * farAbsS / 10000f;
					halfH = halfH * farAbsS / 10000f;
				}
				Vector2 fallAxis = new Vector2((float)radialP.x, (float)radialP.y);
				Color gustCol = Color.Lerp(new Color(0.76f, 0.81f, 0.90f), new Color(0.70f, 0.61f, 0.48f), (float)spread);
				WriteQuad(A.bV, A.bT, A.bC, num2++, qc, halfW, halfH, fallAxis, new Color(gustCol.r, gustCol.g, gustCol.b, a), 0f);
			}
			// 下冲柱（本次改造）：从云中 40% 云带垂到地面的亮柱，随下落相位脉动 ——
			// 远看就能认出"这里挂着一股下击暴流"，不再只是一排雨丝。
			if (num2 + 1 < A.backQuads)
			{
				float shaftPulse = 0.62f + 0.38f * Mathf.Sin((float)(S.age * 2.2 + fx.seed));
				float shaftA = 0.30f * (float)fx.strength * (float)ph2 * shaftPulse * mergeFade * dissolveFade * spawnAnimT;
				if (shaftA > 0.02f)
				{
					double colH = S.Hbase + (S.Htop - S.Hbase) * 0.4;
					Double2 cp = stormC + radialP * (colH * 0.45);
					Vector2 cv = WorldView.ToLocalPosition(cp);
					Vector2 cq = align ? (cv - alignOrigin) : cv;
					float chx = (float)(S.Rmax * 0.035);
					float chy = (float)(colH * 0.45);
					if (farAbs)
					{
						cq = (cv - alignOrigin) * (farAbsS / 10000f);
						chx *= farAbsS / 10000f;
						chy *= farAbsS / 10000f;
					}
					WriteQuad(A.bV, A.bT, A.bC, num2++, cq, chx, chy, new Vector2((float)radialP.x, (float)radialP.y), new Color(0.82f, 0.86f, 0.93f, shaftA), 0f);
				}
			}
			// 触地出流锋（"掀尘"，本次改造为三层）：尘体（厚而淡）+ 亮前锋（贴地薄亮带，
			// 出流头部的尘墙）+ 外圈余波（更远更淡）→ 一圈圈向外推的扩散层次。
			if (maxSpread > 0.05 && num2 + 4 < A.backQuads)
			{
				float sa = 0.62f * (float)maxSpread * (1f - (float)maxSpread) * (float)fx.strength * (float)ph2 * mergeFade * dissolveFade * spawnAnimT;
				if (sa > 0.02f)
				{
					double lineW0 = S.Rmax * downburstGap;
					Vector2 tanAxis = new Vector2((float)perpP.x, (float)perpP.y);
					for (int side = -1; side <= 1; side += 2)
					{
						double offS = side * (0.35 + 2.6 * maxSpread) * lineW0 * 0.5;
						Double2 sp = stormC + perpP * offS;
						Vector2 sc = WorldView.ToLocalPosition(sp);
						Vector2 sq = align ? (sc - alignOrigin) : sc;
						float shx = (float)(S.Rmax * 0.16 * (0.6 + 0.8 * maxSpread));
						float shy = (float)(S.Rmax * 0.03 * (1.0 + 0.9 * maxSpread));
						float shxB = shx * 0.7f;
						float shyB = (float)(S.Rmax * 0.012 * (1.0 + 1.2 * maxSpread));
						if (farAbs)
						{
							sq = (sc - alignOrigin) * (farAbsS / 10000f);
							shx *= farAbsS / 10000f;
							shy *= farAbsS / 10000f;
							shxB *= farAbsS / 10000f;
							shyB *= farAbsS / 10000f;
						}
						// ① 尘体
						WriteQuad(A.bV, A.bT, A.bC, num2++, sq, shx, shy, tanAxis, new Color(0.72f, 0.64f, 0.52f, sa), 0f);
						// ② 亮前锋（贴地领先边，略偏外）
						Double2 bp = sp + perpP * (side * (0.55 + 3.4 * maxSpread) * lineW0 * 0.5);
						Vector2 bv = WorldView.ToLocalPosition(bp);
						Vector2 bq = align ? (bv - alignOrigin) : bv;
						if (farAbs)
						{
							bq = (bv - alignOrigin) * (farAbsS / 10000f);
						}
						WriteQuad(A.bV, A.bT, A.bC, num2++, bq, shxB, shyB, tanAxis, new Color(0.88f, 0.82f, 0.71f, sa * 0.85f), 0f);
						// ③ 外圈余波
						Double2 op = stormC + perpP * (side * (0.9 + 4.4 * maxSpread) * lineW0 * 0.5);
						Vector2 ov = WorldView.ToLocalPosition(op);
						Vector2 oq = align ? (ov - alignOrigin) : ov;
						float ohx = shx * 1.15f;
						float ohy = shy * 0.8f;
						if (farAbs)
						{
							oq = (ov - alignOrigin) * (farAbsS / 10000f);
							ohx *= farAbsS / 10000f;
							ohy *= farAbsS / 10000f;
						}
						WriteQuad(A.bV, A.bT, A.bC, num2++, oq, ohx, ohy, tanAxis, new Color(0.68f, 0.60f, 0.48f, sa * 0.45f), 0f);
					}
				}
			}
			// 触地冲击尘（本次改造）：撞地瞬间在正中掀起的暖色尘云，随 spread 涨落
			if (maxSpread > 0.25 && num2 + 1 < A.backQuads)
			{
				float ba = 0.5f * (float)fx.strength * (float)ph2 * (float)maxSpread * (1f - (float)maxSpread) * mergeFade * dissolveFade * spawnAnimT;
				if (ba > 0.02f)
				{
					Double2 ip = stormC + radialP * (S.Rmax * 0.02);
					Vector2 iv = WorldView.ToLocalPosition(ip);
					Vector2 iq = align ? (iv - alignOrigin) : iv;
					float ihx = (float)(S.Rmax * (0.06 + 0.14 * maxSpread));
					float ihy = (float)(S.Rmax * (0.02 + 0.05 * maxSpread));
					if (farAbs)
					{
						iq = (iv - alignOrigin) * (farAbsS / 10000f);
						ihx *= farAbsS / 10000f;
						ihy *= farAbsS / 10000f;
					}
					WriteQuad(A.bV, A.bT, A.bC, num2++, iq, ihx, ihy, new Vector2((float)perpP.x, (float)perpP.y), new Color(0.78f, 0.67f, 0.52f, ba), 0f);
				}
			}
			}
		}
		// 阵风锋：弧状云墙（沿切向铺开的柱形云墙，地面至墙顶）+ 地面尘浪（柱底薄带）。
		// 气象模型：冷池出流推进前沿（shelf cloud），云墙后部为强出流区。
		// 沙尘暴（Haboob）：同一冷池出流经过沙漠/干地卷沙成墙——云墙沙黄、尘浪深沙色。
		// 视觉/现实体检（v2.2.3 第二轮）修正两点：
		//  ①墙高改"绝对高度并受云顶钳制"：原 haboob 墙 1.2×Rmax = 4.4km，比整个沙尘层顶
		//    （Htop 1.2km）还高 3.7 倍、正常阵风锋 0.5×Rmax 在飑线也达 3km（云顶 3.6km）——
		//    现实 Haboob 沙墙 1-2km、shelf cloud 0.5-1.5km，都不该顶穿系统云顶。
		//  ②线状系统（飑线/MCS/沙尘暴）的出流边界要沿整条线铺开：原恒 7 段×0.3Rmax
		//    = 1.8Rmax ≈ 11km，而飑线云带 8R ≈ 47km → 云墙只盖中间一小段（视觉断裂）。
		if (S.gustFronts.Count > 0)
		{
			bool haboob = S.terrainKind == TerrainKind.Desert || S.type == StormType.DustStorm;   // （终审🟡-11）加宿主：沙尘暴恒沙墙（出海也保持 Haboob 身份），地形仅作增强因子
			double wallHM = haboob ? Math.Min(S.Rmax * 1.2, S.Htop * 0.85) : Math.Min(S.Rmax * 0.5, S.Htop * 0.45);
			double dustH = haboob ? 0.06 : 0.025;   // 尘浪更浓
			bool lineMode = WeatherSystem.Spec[(int)S.type].line > 0.05;   // 线状系统：出流边界沿整条线
			int segHalf = lineMode ? 5 : 3;
			double segStep = lineMode ? 0.42 : 0.3;   // 线状 11 段×0.42Rmax ≈ 4.6Rmax 宽
			float segHalfX = (float)(S.Rmax * (lineMode ? 0.16 : 0.11));
			Color wallCol = haboob ? new Color(0.78f, 0.62f, 0.40f, 1f) : new Color(0.82f, 0.84f, 0.88f, 1f);
			Color dustCol = haboob ? new Color(0.55f, 0.42f, 0.28f, 1f) : new Color(0.62f, 0.58f, 0.52f, 1f);
			Double2 gfStorm = S.MergedStormC();
			Double2 gfRadial = gfStorm.normalized;
			Double2 gfTan = new Double2(0.0 - gfRadial.y, gfRadial.x);
			Vector2 gfTanV = new Vector2((float)gfTan.x, (float)gfTan.y);
			for (int gi = 0; gi < S.gustFronts.Count && num2 + 32 < phenomenaQuads; gi++)
			{
				WeatherSystem.FxInst gf = S.gustFronts[gi];
				if (gf.strength <= 0.05)
				{
					continue;
				}
				double grow = Math.Min(gf.phase, gf.strength);
				if (grow <= 0.04)
				{
					continue;
				}
				double gfC = gf.sOff * S.Rmax;
				for (int ai = -segHalf; ai <= segHalf; ai++)
				{
					double off = gfC + ai * S.Rmax * segStep;
					Double2 baseP = gfStorm + gfTan * off;
					Double2 topP = baseP + gfRadial * (wallHM * grow);
					Vector2 bL = WorldView.ToLocalPosition(baseP);
					Vector2 tL = WorldView.ToLocalPosition(topP);
					Vector2 cenL = (bL + tL) * 0.5f;
					Vector2 qc = align ? (cenL - alignOrigin) : cenL;
					Vector2 axis = (tL - bL).normalized;
					float halfY = (float)(wallHM * 0.5 * grow);
					float halfX = segHalfX;
					float wa = 0.75f * torVisMul * dustVisMul * (float)grow * mergeFade * (1f - Mathf.Abs(ai) * 0.1f) * dissolveFade * spawnAnimT;   // 消散渐隐； 生成动画
					if (farAbs)
					{
						qc = (cenL - alignOrigin) * (farAbsS / 10000f);
						halfY = halfY * farAbsS / 10000f;
						halfX = halfX * farAbsS / 10000f;
					}
					if (wa > 0.02f)
					{
						WriteQuad(A.bV, A.bT, A.bC, num2++, qc, halfX, halfY, axis, new Color(wallCol.r, wallCol.g, wallCol.b, wa), 0f);
					}
					// 地面尘浪（柱底薄带，随云墙一起推进；沙漠=浓沙墙）
					Vector2 qc2 = align ? (bL - alignOrigin) : bL;
					float hx2 = (float)(S.Rmax * 0.22);
					float hy2 = (float)(S.Rmax * dustH);
					float da = 0.5f * torVisMul * dustVisMul * (float)grow * mergeFade * (1f - Mathf.Abs(ai) * 0.12f) * dissolveFade * spawnAnimT;   // 消散渐隐； 生成动画
					if (farAbs)
					{
						qc2 = (bL - alignOrigin) * (farAbsS / 10000f);
						hx2 = hx2 * farAbsS / 10000f;
						hy2 = hy2 * farAbsS / 10000f;
					}
					if (da > 0.02f)
					{
						WriteQuad(A.bV, A.bT, A.bC, num2++, qc2, hx2, hy2, gfTanV, new Color(dustCol.r, dustCol.g, dustCol.b, da), 0f);
					}
				}
			}
		}
		// 闪电放电通道（视觉补强）：原来闪电只有"闪光照亮云体 + 雷声"，没有放电通道本身
		// （视觉体检结论：这是附属现象里唯一"看得见的缺失"）。这里按 UpdateLightning 选出
		// 的闪击点画一条锯齿通道：从云中层（flashH）折线落到地面（地形高度，钳 ≥0），
		// 5 段细长 quad，只在闪光峰值（flashPower>0.35）出现，颜色冷白偏蓝。
		// 锯齿相位由 storm.seed 决定 → 通道形态稳定（不每帧跳变），随闪光出现/消失。
		if (TyphoonConfig.I.lightning && S.type != StormType.DustStorm && flashPower > 0.35f
			&& num2 + 8 < phenomenaQuads)
		{
			Double2 boStorm = S.MergedStormC();
			Double2 boRadial = boStorm.normalized;
			Double2 boPerp = new Double2(0.0 - boRadial.y, boRadial.x);
			double boGround = Math.Max(0.0, rainGroundH);
			double boTop = Math.Max(flashH, boGround + 150.0);
			int boSegs = 5;
			float boW = (float)(S.Rmax * 0.006);
			float boA = Mathf.Clamp01(flashPower * 1.35f);
			double prevS = flashS;
			double prevH = boTop;
			for (int bi = 1; bi <= boSegs && num2 + 2 < phenomenaQuads; bi++)
			{
				double f = (double)bi / (double)boSegs;
				double h2 = boTop + (boGround - boTop) * f;
				double jitter = S.Rmax * 0.1 * Math.Sin(bi * 2.399 + S.seed * 0.37) * (1.0 - f * 0.5);
				double s2 = flashS + jitter;
				Double2 p0 = boStorm + boRadial * prevH + boPerp * prevS;
				Double2 p1 = boStorm + boRadial * h2 + boPerp * s2;
				Vector2 l0 = WorldView.ToLocalPosition(p0);
				Vector2 l1 = WorldView.ToLocalPosition(p1);
				Vector2 cenB = (l0 + l1) * 0.5f;
				Vector2 axisB = (l1 - l0).normalized;
				float halfLen = (l1 - l0).magnitude * 0.5f + boW;
				float halfWB = boW;
				Vector2 qcB = align ? (cenB - alignOrigin) : cenB;
				if (farAbs)
				{
					qcB = (cenB - alignOrigin) * (farAbsS / 10000f);
					halfLen *= farAbsS / 10000f;
					halfWB *= farAbsS / 10000f;
				}
				WriteQuad(A.bV, A.bT, A.bC, num2++, qcB, halfWB, halfLen, axisB, new Color(0.86f, 0.93f, 1f, boA), 0f);
				prevS = s2;
				prevH = h2;
			}
		}
		// ===== 风暴卷起的障碍物（树木 / 石头）：真实障碍物，能撞击火箭（见 TyphoonManager） =====
		// 与其它附属现象同区（phenomena 前缀）：云层写在更后面的 index → 云会盖住它们，
		// 符合"物体被卷进云里就糊掉"的观感。石头=土褐团块、树=深绿冠 + 棕干（带自转翻滚）。
		if (S.debris.Count > 0 && num2 + 3 < phenomenaQuads)
		{
			Double2 dbStorm = S.MergedStormC();
			Double2 dbRadial = dbStorm.normalized;
			Double2 dbPerp = new Double2(0.0 - dbRadial.y, dbRadial.x);
			for (int di = 0; di < S.debris.Count && num2 + 3 < phenomenaQuads; di++)
			{
				WeatherSystem.DebrisInst d = S.debris[di];
				Double2 dp = dbStorm + dbPerp * d.s + dbRadial * d.h;
				Vector2 dcen = WorldView.ToLocalPosition(dp);
				Vector2 dqc = align ? (dcen - alignOrigin) : dcen;
				float dHalf = (float)d.size;
				if (farAbs)
				{
					dqc = (dcen - alignOrigin) * (farAbsS / 10000f);
					dHalf *= farAbsS / 10000f;
				}
				Vector2 dAxis = new Vector2((float)Math.Cos(d.spin), (float)Math.Sin(d.spin));
				if (d.kind == 1)
				{
					// 树：树干（沿自转轴）+ 树冠（顶端、更宽）
					WriteQuad(A.bV, A.bT, A.bC, num2++, dqc, dHalf * 0.2f, dHalf * 0.8f, dAxis, new Color(0.30f, 0.22f, 0.15f, 0.95f), 0f);
					Vector2 crown = dqc + dAxis * (dHalf * 0.7f);
					WriteQuad(A.bV, A.bT, A.bC, num2++, crown, dHalf * 0.95f, dHalf * 0.85f, Vector2.right, new Color(0.22f, 0.40f, 0.20f, 0.95f), 0f);
				}
				else
				{
					WriteQuad(A.bV, A.bT, A.bC, num2++, dqc, dHalf, dHalf, dAxis, new Color(0.40f, 0.36f, 0.32f, 0.98f), 0f);
				}
			}
		}
		// 11 区分段黑框 / 风圈线调试绘制（Shift+F2）已移除（用户：清理调试按键）。
		// 风圈信息仍由 HUD 文字显示（风圈 7级≈xx km 等）。
		// 图层反转后 puffs 写入 index i+phenomenaQuads（后部），附属现象占 index 0..phenomenaQuads-1，
		// 全部 backQuads 都被填满（puffs.Length+phenomenaQuads == backQuads）→ 隐藏起点=backQuads（无未用顶点）。
		HideRest(A.bV, A.bC, puffs.Length + phenomenaQuads, A.backQuads);
	}

	// 调试黑框绘制（DrawZoneRect/DrawEdge/DrawZoneTick）已移除（用户：清理调试按键）。

	// 附属现象实例锚点（行星全局）：合并动画中心 + 切向偏移（sOff×Rmax，沿经度方向）。
	// 多实例龙卷/下暴各自独立位置（随机合适位置生成，数量限制解除）。
	private static Double2 FxAnchor(WeatherSystem s, WeatherSystem.FxInst fx)
	{
		Double2 c = s.MergedStormC();
		Double2 rp = c.normalized;
		Double2 pd = new Double2(0.0 - rp.y, rp.x);
		return c + pd * (fx.sOff * s.Rmax);
	}

	// 行星球面遮挡判定：视线从 camPos 到 p，若先与行星表面（半径 R）相交 → 被挡。
	private static bool BlockedByPlanet(Double2 camPos, Double2 p, double R)
	{
		Double2 V = p - camPos;
		double a = Double2.Dot(V, V);
		if (a < 0.0001)
		{
			return false;
		}
		double b = 2.0 * Double2.Dot(camPos, V);
		double c = Double2.Dot(camPos, camPos) - R * R;
		double disc = b * b - 4.0 * a * c;
		if (disc <= 0.0)
		{
			return false;
		}
		double sq = Math.Sqrt(disc);
		double t1 = (0.0 - b - sq) / (2.0 * a);
		return t1 > 0.001 && t1 < 1.0;
	}

	private static double Smooth(double x)
	{
		double t = WeatherSystem.Clamp01(x);
		return t * t * (3.0 - 2.0 * t);
	}

	private void BuildRain(float dt, Camera cam, float camHalf, bool align)
	{
		WeatherSystem s = S;
		visMul = ComputeVisMul(s);   // 雨幕粒子雾：本系统真实能见度→密/稀(暴雨成墙)
		if (dropCount <= 0)
		{
			SkipRain();
			return;
		}
		// 沙尘暴无降雨（干燥系统，只有沙尘）——整层雨隐藏
		if (S.type == StormType.DustStorm)
		{
			SkipRain();
			return;
		}
		// 性能（用户明确：视距 1500 内才渲染雨、单体/多单体 2500；且只渲染
		// 离玩家最近系统的雨）—— 放宽的 3000/4000 回归 1500/2500（"过早消失"
		// 那轮已定位是落地淡出问题并修复，与视距裁剪无关）。
		// 注：可见性判定每帧都跑（很便宜），只有下面的逐滴构建循环才隔帧。
		try
		{
			double vd = ((Obs<float>)(object)WorldView.main.viewDistance).Value;
			double rainCut = (S.type == StormType.Cell || S.type == StormType.Multicell) ? 2500.0 : 1500.0;
			if (vd > rainCut)
			{
				SkipRain();
				return;
			}
		}
		catch
		{
		}
		Double2 val = camG;
		s.ToStormFrame(val, out var s2, out var h);
		// 远风暴雨整层跳过：玩家距风暴 >15Rmax 时雨幕亚像素不可见，连每滴计算都省
		// （与云层 puffFreeze 冻结同步）。
		if (Math.Abs(s2) > s.Rmax * 15.0)
		{
			SkipRain();
			return;
		}
		// 只渲染离玩家最近系统的雨（多系统并存时性能优化）：存在比本系统更近的
		// 系统 → 本系统雨整层隐藏（远系统雨条被近系统遮挡/亚像素，纯浪费）。
		try
		{
			double distMe = Math.Abs(s2);
			for (int si = 0; si < TyphoonManager.systems.Count; si++)
			{
				WeatherSystem ws = TyphoonManager.systems[si];
				if (ws == null || !ws.active || ws.planet == null || ws == S)
				{
					continue;
				}
				if ((object)ws.planet != (object)s.planet)
				{
					continue;
				}
				ws.ToStormFrame(camG, out double wsS, out _);
				if (Math.Abs(wsS) < distMe * 0.9)   // 0.9 容错：等距时本系统优先
				{
					SkipRain();
					return;
				}
			}
		}
		catch
		{
		}
		double num = Math.Abs(s2) / s.Rmax;
		// 技术债修复：雨幕轮廓原为台风眼壁雨带（0.55Rmax 处才下雨）全局复用 →
		// 单体/超单中心反而没雨。按类型分流：台风=眼壁雨带环；对流型=中心降水最猛、
		// 单调向外衰减（对流单体的降雨集中在核心上升区）。
		// 台风恢复下雨（用户：台风一滴雨都没有是问题）： 曾按"台风完全
		// 不下雨"关闭 num2=0，实测台风没雨不对 → 恢复台风雨带（0.15 起始、1.6 峰值）。
		double num2;
		if (S.type == StormType.Typhoon)
		{
			// 审查🟡-10：台风雨带主峰移到眼壁（真实台风最大降雨在眼壁内侧，
			// 眼壁对流最旺盛；原峰在 1.6Rmax 比眼壁远 4-7 倍，视觉"眼壁无水外围暴雨"）。
			// 外圈 1.6Rmax 保留为螺旋雨带次峰。
			double eyePeak = (double)WeatherSystem.TyphoonEyeR(S.category);
			if (eyePeak <= 0.01)
			{
				eyePeak = 0.3;   // TD/TS 无眼 → 0.3 近似
			}
			// R1 — 次峰改真高斯（审查🔴-R1：原 max(0,num−1.6)/2.2 在 num<1.6 恒 0.5
		// 是常数平台不是峰）：外圈螺旋雨带真正形成"第二峰"——中心 1.7R、σ0.9，峰值 0.5
		// （低于眼壁主峰 0.9，气象正确：眼壁降雨最猛、外圈螺旋带次之），配合 lineW 加宽
		// 铺到 3.5R。2D 横截面：斜向雨丝带从眼壁斜向外下延伸（s2 相干调制在 ）。
		num2 = Smooth01((num - 0.15) / 0.3) * (Math.Exp(0.0 - WeatherSystem.Pow2((num - eyePeak) / 0.25)) * 0.9 + Math.Exp(0.0 - WeatherSystem.Pow2((num - 1.7) / 0.9)) * 0.5);
		}
		else
		{
			num2 = Smooth01((1.5 - num) / 1.2) * Math.Exp(0.0 - WeatherSystem.Pow2(num / 1.9));
		}
		// 雨"空中不可见"修复（用户：在空中就不可见）：旧因子以玩家高度 > 云底
		// 1.2 倍（Hbase×2.0 处归零）就整层隐藏雨——对流系统云底 SFS 只有 150-300m，
		// 玩家飞几百米雨就没了，而雨柱实际从云底垂到地面，进云/高空往下依然可见。
		// 改为云顶之上才淡出：h < Htop 全可见，Htop→Htop×1.5 线性淡出（太高雨柱细节亚像素）。
		num2 *= 1.0 - WeatherSystem.Clamp01((h - s.Htop) / (s.Htop * 0.5));   // 原 Hbase×1.2
		num2 *= 0.55 + 0.45 * Math.Sin(num * 2.6 - s.age * 0.22);
		if (h < -200.0)
		{
			num2 = 0.0;
		}
		num2 = WeatherSystem.Clamp01(num2);
		float num3 = (float)TyphoonConfig.I.rainScale;
		// far 下不再因 camHalf(=geoHalf=worldHalf) > Rmax×4 整体隐藏雨
		// （far 视距该值恒成立，导致雨在缩小视野时突然消失），改由下方 farAbs 分支的
		// "够小消失"判定（雨条 < 屏 0.5% 才消失）。
		if (num2 < 0.02 || (!farAbs && (double)camHalf > s.Rmax * 4.0))
		{
			SkipRain();
			return;
		}
		// 本帧雨可见：确保雨层 GameObject 激活（可能刚被 SkipRain 失活）。
		rainVisible = true;
		if (A != null && A.frontGO != null && !A.frontGO.activeSelf)
		{
			A.frontGO.SetActive(true);
		}
		// 雨幕隔帧更新：细丝透明连续流，30fps 更新视觉无差，CPU 减半。
		// 跳过帧不写顶点（保留上帧网格，由 LateUpdate 末尾照常 Push）——隔帧只省最贵的
		// 逐滴构建循环，上面的可见性判定必须每帧跑（否则隐藏态会在奇数帧漏画旧雨幕）。
		if ((Time.frameCount & 1) == 1)
		{
			return;
		}
		Double2 normalized = val.normalized;
		Double2 val3 = new Double2(0.0 - normalized.y, normalized.x);
		Double2 val4 = s.SampleWind(val);
		Vector2 val5 = new Vector2((float)Double2.Dot(val4, val3), (float)Double2.Dot(val4, normalized));
		float num7 = camHalf * 0.16f * num3;   // （终审🟢-6）— 删除死变量 num4/num5/num6（定义后从未被读取）
		float halfW = Mathf.Min(Mathf.Max(camHalf * 0.007f * num3, 0.06f), camHalf * 0.4f);
		if (farAbs)
		{
			// far 修复：雨基准从 camHalf(=geoHalf=worldHalf, 雨幕/雨条被放大几十倍
			// 溢出屏幕)改为 Rmax 相关 → 雨随风暴同步缩小、聚集在风暴区域；
			// 雨条换算成屏幕尺寸后 < 屏半高 0.5% 则整体消失（够小消失，避免亚像素闪烁）。
			float depth2;
			float halfCam2 = HalfExtent(cam, out depth2);
			float sR = (float)s.Rmax;
			num7 = sR * 0.16f * num3;
			halfW = Mathf.Min(Mathf.Max(sR * 0.007f * num3, sR * 0.002f), sR * 0.4f);
			if (num7 * (farAbsS / 10000f) < halfCam2 * 0.005f)
			{
				SkipRain();
				return;
			}
			// 顶点预缩放：米 → 缩放世界单位（GO scale=1）。
			num7 *= farAbsS / 10000f;
			halfW *= farAbsS / 10000f;
		}
		float num8 = (float)TyphoonConfig.I.rainOpacity * (float)num2 * (float)s.MergeFade() * spawnAnimT * s.DissolveFade();   // 生成动画雨随云渐入； 消散渐隐
		// 雨改细长条 + 随机密集（用户：不要并排、要密集、发射器放云里、图层最低）：
		// 粒子独立随机横向分布（确定性伪随机，不排成排）、错相位连续下落（任意时刻各高度
		// 都有雨 = 密集雨幕）、发射器在云内（Hbase+15% 云带，雨从云里冒出来被云盖住）；
		// 细长条（halfW 极窄 × halfL 长，长轴=下落方向 radialDir）。
		// 锚定修复（用户：玩家动现象跟着动）：原 cen = stormLocal(ToLocalPosition 后)
		// + radialDir×hh + perp×offsetX（相机本地坐标再加偏移，相机旋转/移动时偏移方向错位）。
		// 改与云层 puff 同构：行星全局偏移 → 一次 ToLocalPosition（stormC + radialP×hh + perpP×offsetX）。
		// 边缘稀疏：横向分布改幂分布（u^1.5 向中心聚集）→ 越边缘的雨越不密集。
		Double2 stormC = FromStorm(0.0, 0.0);
		Double2 radialP = stormC.normalized;                       // 行星全局径向（高度方向）
		Double2 perpP = new Double2(0.0 - radialP.y, radialP.x);   // 行星全局切向（水平）
		Vector2 radialDir = new Vector2((float)radialP.x, (float)radialP.y);   // 相机本地近似垂直（axis 长轴）
		Vector2 perp = new Vector2((float)perpP.x, (float)perpP.y);
		double fall = (s.age * 1.6) % 1.0;
		// 雨柱触地修复（用户：雨在落到地面前就消失）：原落点 hh=0 = 海平面
		// （FromStorm 基准 = planet.Radius），地形高于海平面（山地/高原）时雨滴到海平面
		// 就开始淡出、实际地面还在上方 → 雨柱悬空/提前消失。改用风暴中心地形高度做落点
		// 基准（8s 节流采样，GetTerrainHeightAtAngle 内部有数组分配不可每帧调）。
		if (Time.frameCount - rainGroundFrame > 480)
		{
			rainGroundFrame = Time.frameCount;
			double gh = 0.0;
			try
			{
				gh = s.planet.GetTerrainHeightAtAngle(s.centerAngle, false);
			}
			catch
			{
			}
			rainGroundH = Math.Max(0.0, gh);   // 海上钳到海平面
		}
		int dropsN = dropCount;
		float rainW = Mathf.Max((float)(s.Rmax * 0.004) * num3 * rainWidScale, 0.03f);   // 极细（雨宽， 可调）
		float rainL = Mathf.Max((float)(s.Rmax * 0.09) * num3 * rainLenScale, 0.3f);     // 长条（雨长， 可调）
		// R1 — 雨幕横向范围加宽（审查🔴-R1：原恒 1.6R，外圈螺旋雨带次峰 1.6-1.8R
		// 无粒子可达 = 峰是空的）：台风 3.5R（螺旋雨带完整铺开）、对流 2.5R（中心降水，
		// 不需要外圈）。雨滴中心偏置 u^1.5 分布 + sin 摆动 → 最远 0.64×lineW，加宽后眼壁
		// 带瞬时密度只降 ~13%（中心仍密），配下方边缘渐隐防硬切。
		double lineW = s.Rmax * ((S.type == StormType.Typhoon) ? 3.5 : 2.5);
		// 雨随风倾斜（用户：按风速倾斜，8 级开始维持极度倾斜 85° 不再继续）：
		// 水平风速 vH（val5.x=切向风分量）→ 倾斜角 = min(85°, 5°/m/s×vH)（8级 17.2 m/s → 86 → 锁 85°）；
		// 长轴 = 垂直方向旋转 tiltRad 朝顺风方向（雨被吹斜成斜雨丝/横雨）。
		// R4 雨倾角改物理映射（审查🟡-R4：原 5°/m/s 线性 17m/s 就 85°，物理
		// = atan(vH/vt)≈atan(vH/9.5)——雨滴末速度 ~9.5m/s，8 级风 17.2m/s → 61°，85°
		// 需 ~103m/s）：atan 映射封 80°。
		double vH = Math.Abs(val5.x);
		double tiltRad = Math.Min(80.0, Math.Atan(vH / 9.5) * 180.0 / Math.PI) * Math.PI / 180.0;
		Vector2 vertDir = radialDir;                                  // 垂直（下落）
		Vector2 hzDir = perp * ((val5.x >= 0f) ? 1f : -1f);           // 水平顺风方向
		Vector2 axisRain = (vertDir * (float)Math.Cos(tiltRad) + hzDir * (float)Math.Sin(tiltRad)).normalized;
		int num9 = 0;
		Color val8 = default(Color);
		for (int i = 0; i < dropsN; i++)
		{
			// 更随机：相位加 hash 扰动（雨幕不规整铺开，错落自然）
			double phRand = ((double)((i * 2246822519u + 777u) % 1000) / 1000.0) * 0.15;
			// 雨"落地消失"修复（用户：雨过早消失指的是落地消失，程序落地高度与
			// 实际不一样，放宽到负数）：tt 范围 [0,1) → [0,1.3)——1.0=落地，1.0~1.3 穿地
			// 阶段（雨砸进地面以下一点才完全消失）；fade 起点推迟到 tt=1.0（真正触地才淡出）。
			// 落点改实际地形高度：hh = ground + (emit−ground)×(1−tt)，tt=1 触到
			// 风暴中心真实地面（不再固定海平面 0）；山顶高于发射器时落点钳到发射器（雨柱截断）。
			double tt = ((double)i / (double)dropsN + fall + phRand) % 1.3;   // 错相位连续流（密集雨幕）
			// 发射器在云内：起点 = Hbase + 15% 云带（雨从云里冒出来）；tt=1 触地、tt>1 穿地负数
			double emitH = s.Hbase + (s.Htop - s.Hbase) * 0.15;
			double groundH = Math.Min(rainGroundH, emitH);   // 落点不高于发射器
			double hh = groundH + (emitH - groundH) * (1.0 - tt);
			// 边缘稀疏：u^1.5 幂分布（u 均匀 0-1，u^1.5 偏向 0）→ 中心密、边缘稀
			double u = ((double)((i * 2654435761u) % 10000) / 10000.0);
			// 更随机：横向加时间摆动（雨幕横向漂移，不呆板）
			double offsetX = (Math.Pow(u, 1.5) * 2.0 - 1.0) * lineW * 0.5 + Math.Sin((double)i * 7.31 + s.age * 3.0) * lineW * 0.14;
			// 生命周期：出生淡入（云里冒头）→ 下落全亮 → 落地（tt=1）后淡出删除（负数穿地）
			float born = Mathf.Clamp01((float)(tt / 0.1));
			float fade = 1f - (float)Smooth(WeatherSystem.Clamp01((tt - 1.0) / 0.3));
			// R1 — 雨幕边缘渐隐（lineW 加宽后防硬切）：距中心 >0.6×lineW 的雨滴在
			// 0.8R 过渡带内淡出（0.6×lineW → 0.6×lineW+0.8R 完全消失）。用每雨滴横向偏移
			// offsetX 而非相机距离（相机靠近风暴中心时边缘渐隐依然正确）。
			float edgeFade = (float)Smooth01((0.6 * lineW - Math.Abs(offsetX)) / Math.Max(0.8 * s.Rmax, 1.0));
			float a = num8 * born * fade * edgeFade * visMul;   // 粒子雾：暴雨更密(墙)
			if (a < 0.02f)
			{
				continue;
			}
			// 行星全局偏移 → 一次 ToLocalPosition（与云层 puff 同构，锚定在地表不随玩家飘）
			Double2 planetPos = stormC + radialP * hh + perpP * offsetX;
			Vector2 cen = WorldView.ToLocalPosition(planetPos);
			Vector2 qc = align ? (cen - alignOrigin) : cen;
			float hw = rainW;
			float hl = rainL;
			if (farAbs)
			{
				qc = (cen - alignOrigin) * (farAbsS / 10000f);
				hw = hw * farAbsS / 10000f;
				hl = hl * farAbsS / 10000f;
			}
			// 雨滴提亮（灰屏修复）：近距风暴灰化（ProximityFX 饱和度 0.55/亮度 0.82）
			// 把雨丝原蓝白(0.8,0.86,0.95)压成灰扑扑 → 改近白(0.95,0.98,1) + alpha 抬满，
			// 灰暗背景下雨丝保持最亮的高光感（和卷尘环/漏斗提亮同一策略）。
			// R16 提亮绑近距×强度（审查🟡-R16：Vmax-only 不够——灰化是"近距×强度"，
			// 远看强风暴不灰屏、雨不该提亮；复用本方法相机近距因子 num=|s2|/Rmax，零跨类耦合，
			// 与天空灰布同涨同消）。
			float proxR = (float)Smooth01((4.6 - num) / 1.6);
			float boostR = 1f + 0.5f * Mathf.Clamp01((float)(s.Vmax / 45.0)) * proxR;
			val8 = Color.Lerp(new Color(0.8f, 0.86f, 0.95f, Mathf.Min(1f, a)), new Color(0.95f, 0.98f, 1f, Mathf.Min(1f, a * 1.15f)), Mathf.Clamp01(boostR - 1f));
			// 泥雨（消散产物#3）：本系统附近有消散沙尘暴（muddyFactor，逻辑层
			// 8s 节流 O(n) 算出）时雨色插向泥褐——沙尘与降水混合的"泥雨"（SAL/湿沉降
			// 现实机制，跨系统产物）。设置开关 muddyRain 控制。
			if (TyphoonConfig.I.muddyRain && s.muddyFactor > 0.01)
			{
				val8 = Color.Lerp(val8, new Color(0.72f, 0.62f, 0.45f, val8.a), (float)s.muddyFactor * 0.7f);
			}
			if (flashPower > 0.001f)
			{
				val8 = Color.Lerp(val8, new Color(1f, 1f, 0.95f, val8.a), flashPower * 0.7f);
			}
			if (num9 < A.frontQuads)
			{
				WriteQuad(A.fV, A.fT, A.fC, num9++, qc, hw, hl, axisRain, val8, 0f);   // 长轴=倾斜轴（随风）
			}
		}
		HideRest(A.fV, A.fC, num9, A.frontQuads);
	}

	private static void WriteQuad(Vector3[] v, Vector2[] t, Color[] c, int q, Vector2 centre, float halfW, float halfH, Vector2 axis, Color col, float uOffset)
	{
		int num = q * 4;
		Vector2 val = axis;
		Vector2 val2;
		if (!(val.sqrMagnitude > 0.0001f))
		{
			val2 = Vector2.up;
		}
		else
		{
			val = axis;
			val2 = val.normalized;
		}
		Vector2 val3 = val2;
		Vector2 val4 = new Vector2(val3.y, 0f - val3.x) * halfW;
		Vector2 val5 = val3 * halfH;
		v[num] = new Vector3(centre.x - val4.x - val5.x, centre.y - val4.y - val5.y, 0f);
		v[num + 1] = new Vector3(centre.x - val4.x + val5.x, centre.y - val4.y + val5.y, 0f);
		v[num + 2] = new Vector3(centre.x + val4.x + val5.x, centre.y + val4.y + val5.y, 0f);
		v[num + 3] = new Vector3(centre.x + val4.x - val5.x, centre.y + val4.y - val5.y, 0f);
		// 优化第二轮：UV 不再每帧写（uOffset 恒为 0 → 每个 quad 的 UV 都是常量，
		// 已在 Alloc 中一次性铺好并只上传一次）。
		c[num] = col;
		c[num + 1] = col;
		c[num + 2] = col;
		c[num + 3] = col;
	}

	private static void HideRest(Vector3[] v, Color[] c, int from, int total)
	{
		for (int i = from; i < total; i++)
		{
			int num = i * 4;
			v[num] = Vector3.zero;
			v[num + 1] = Vector3.zero;
			v[num + 2] = Vector3.zero;
			v[num + 3] = Vector3.zero;
			c[num] = Color.clear;
			c[num + 1] = Color.clear;
			c[num + 2] = Color.clear;
			c[num + 3] = Color.clear;
		}
	}

	// 优化 — 雨层整层不可见（视距裁剪 / 沙尘暴 / 有更近系统 / 贴地淡出）：失活雨层
	// GameObject 并标记本轮跳过 front 网格上传。原实现把 frontQuads 全部清零（默认 5100
	// quad = 20400 个顶点 + 同样数量的颜色）再整块上传，这些退化三角形不产生任何像素。
	private void SkipRain()
	{
		rainVisible = false;
		if (A != null && A.frontGO != null && A.frontGO.activeSelf)
		{
			A.frontGO.SetActive(false);
		}
	}

	// 优化 — 不可见风暴判定：相机在天空穹顶作用半径之外（rho>5.5，滞回 4.5），
	// 且风暴包围球（水平 4.6Rmax 云团 + 垂直云顶，×1.45 安全余量）连同 0.35 屏余量
	// 完全落在屏幕外 → 判定为不可见（此时画了也会被相机裁掉，不产生任何像素）。
	private bool ShouldCull(Camera cam, Vector2 stormLocal)
	{
		WeatherSystem s = S;
		if (s == null || s.planet == null || cam == null)
		{
			return false;
		}
		double rho;
		try
		{
			s.ToStormFrame(camG, out double cs, out double _);
			rho = Math.Abs(cs) / Math.Max(s.Rmax, 1.0);
		}
		catch
		{
			return false;
		}
		double enter = cullSkip ? 4.5 : 5.5;   // 滞回：进入剔除 5.5Rmax、恢复 4.5Rmax
		if (rho < enter)
		{
			return false;
		}
		// 包围球世界半径（水平 4.6Rmax 云团 + 垂直云顶），×1.45 安全余量吃透视/附属现象/雨幕。
		float stormR = (float)(4.6 * s.Rmax + Math.Max(s.Htop, 1.0)) * 1.45f;
		Vector3 vp = cam.WorldToViewportPoint(new Vector3(stormLocal.x, stormLocal.y, 0f));
		float halfH;
		if (cam.orthographic)
		{
			// SFS 世界相机是正交投影（CameraManager: orthographicSize = tan(fov/2)×视距），
			// 屏幕横向可视范围就是 ±orthographicSize —— 球心在相机后方（vp.z<=0）不剔除。
			if (vp.z <= 0f)
			{
				return false;
			}
			halfH = Mathf.Abs(cam.orthographicSize);
		}
		else
		{
			if (vp.z <= stormR)
			{
				return false;   // 相机在包围球内/球后：投影判定不成立（此时必然看得见）
			}
			halfH = vp.z * Mathf.Tan(cam.fieldOfView * 0.5f * ((float)Math.PI / 180f));
		}
		if (halfH <= 0.0001f)
		{
			return false;
		}
		float aspect = (cam.aspect > 0.05f) ? cam.aspect : 1f;
		float rx = stormR / (2f * halfH * aspect);
		float ry = stormR / (2f * halfH);
		float margin = 0.35f;   // 屏外余量：留足安全距离，绝不误剔可见风暴
		if (vp.x + rx < -margin || vp.x - rx > 1f + margin || vp.y + ry < -margin || vp.y - ry > 1f + margin)
		{
			return true;
		}
		return false;
	}

	private static double Smooth01(double x)
	{
		if (x <= 0.0)
		{
			return 0.0;
		}
		if (x >= 1.0)
		{
			return 1.0;
		}
		return x * x * (3.0 - 2.0 * x);
	}
}
