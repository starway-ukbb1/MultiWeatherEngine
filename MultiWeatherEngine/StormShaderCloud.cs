using System;
using System.IO;
using SFS.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace MultiWeatherEngine;

// 渲染架构 B —— shader 程序噪声风暴云。
// 每场风暴一块 quad（风暴本地米坐标，x=切向 y=径向高度），shader 内做
// FBM 噪声（风平流 + 差速旋转）+ 类型形状掩码（眼/线/砧/墙云/尘柱/贴地雾），
// 取代原 canopy/cloud 粒子云体。龙卷/下暴/阵风锋等附属现象仍走粒子网格。
// bundle 缺失时 Available=false → StormRenderer 自动回退完整粒子云路径。
public class StormShaderCloud : MonoBehaviour
{
	private static Shader _shader;
	private static bool _tried;
	public static bool Available => _shader != null;

	public static void TryLoad(string modFolder)
	{
		if (_tried)
		{
			return;
		}
		_tried = true;
		try
		{
			string path = Path.Combine(modFolder ?? string.Empty, "mwe_shaders");
			if (!File.Exists(path))
			{
				Debug.LogWarning("[MWE] mwe_shaders bundle 缺失 → 保留粒子云渲染");
				return;
			}
			AssetBundle bundle = AssetBundle.LoadFromFile(path);
			if (bundle == null)
			{
				Debug.LogWarning("[MWE] mwe_shaders bundle 加载失败 → 保留粒子云渲染");
				return;
			}
			_shader = bundle.LoadAsset<Shader>("Assets/MWEShaders/StormCloud.shader");
			if (_shader == null)
			{
				Shader[] all = bundle.LoadAllAssets<Shader>();
				if (all != null && all.Length > 0)
				{
					_shader = all[0];
				}
			}
			Debug.Log("[MWE] 风暴噪声云 shader: " + (Available ? "加载成功（渲染架构 B 生效）" : "bundle 内未找到"));
		}
		catch (Exception ex)
		{
			Debug.LogError("[MWE] shader bundle: " + ex);
		}
	}

	private GameObject _go;
	private Mesh _mesh;
	private MeshRenderer _mr;
	private MaterialPropertyBlock _mpb;
	private readonly Vector3[] _verts = new Vector3[4];
	private double _advT;
	private double _lastWT = -1.0;
	private float _seed = -1f;
	// 每风暴稳定形状参数缓存（形状掩码每帧重算，uniform 直传）
	private float _extX = 1f;
	private float _yMin;
	private float _yMax = 1f;

	private static readonly int PExtX = Shader.PropertyToID("_ExtX");
	private static readonly int PYMin = Shader.PropertyToID("_YMin");
	private static readonly int PYMax = Shader.PropertyToID("_YMax");
	private static readonly int PRmax = Shader.PropertyToID("_Rmax");
	private static readonly int PEyeR = Shader.PropertyToID("_EyeR");
	private static readonly int PSpiral = Shader.PropertyToID("_SpiralAmt");
	private static readonly int PLine = Shader.PropertyToID("_LineAmt");
	private static readonly int PAnvil = Shader.PropertyToID("_AnvilAmt");
	private static readonly int PWall = Shader.PropertyToID("_WallAmt");
	private static readonly int PDust = Shader.PropertyToID("_DustAmt");
	private static readonly int PFog = Shader.PropertyToID("_FogAmt");
	private static readonly int PWindT = Shader.PropertyToID("_WindT");
	private static readonly int PWindR = Shader.PropertyToID("_WindR");
	private static readonly int PAdvT = Shader.PropertyToID("_AdvT");
	private static readonly int PRotSpd = Shader.PropertyToID("_RotSpeed");
	private static readonly int PSeed = Shader.PropertyToID("_Seed");
	private static readonly int PTint = Shader.PropertyToID("_Tint");
	private static readonly int PAlpha = Shader.PropertyToID("_Alpha");
	private static readonly int PDensity = Shader.PropertyToID("_Density");
	private static readonly int PCoverage = Shader.PropertyToID("_Coverage");
	private static readonly int PSunUp = Shader.PropertyToID("_SunUp");
	private static readonly int PDetail = Shader.PropertyToID("_Detail");

	private static readonly Bounds BigBounds = new Bounds(Vector3.zero, new Vector3(1e8f, 1e8f, 1e8f));

	public void SetVisible(bool v)
	{
		if (_go != null && _go.activeSelf != v)
		{
			_go.SetActive(v);
		}
	}

	public void FrameUpdate(WeatherSystem s, Vector3 anchor, bool farAbs, float farAbsS, int layer, float alphaMul)
	{
		if (!Available || s == null || s.planet == null)
		{
			SetVisible(false);
			return;
		}
		if (_go == null)
		{
			Build();
		}
		// 平流时间：游戏世界时间差（与 StormRenderer.simDt 同口径：钳 30s 防时间加速甩云）
		try
		{
			double wt = WorldTime.main.worldTime;
			if (_lastWT < 0.0)
			{
				_lastWT = wt;
			}
			double d = wt - _lastWT;
			_lastWT = wt;
			if (d < 0.0)
			{
				d = 0.0;
			}
			else if (d > 30.0)
			{
				d = 30.0;
			}
			_advT = (_advT + d) % 100000.0;
		}
		catch
		{
		}

		// ---- 形状参数 ----
		bool line = s.type == StormType.SquallLine || s.type == StormType.AtmosphericRiver
			|| s.type == StormType.Derecho || s.type == StormType.DustStorm;
		bool rotary = s.type == StormType.Typhoon || s.type == StormType.PolarVortex || s.type == StormType.Megastorm;
		double R = Math.Max(s.Rmax, 1.0);
		double span = Math.Max(1.0, s.Htop - s.Hbase);
		_extX = (float)(R * (line ? 4.7 : 2.8));
		_yMin = (float)(s.Hbase - 0.15 * span);
		_yMax = (float)(s.Htop + 0.55 * span);
		float eyeR = 0f;
		if (rotary)
		{
			try
			{
				eyeR = (float)WeatherSystem.TyphoonEyeR(s.category);
			}
			catch
			{
			}
		}
		// ---- 风场（中心半高采样，投影到风暴本地切向/径向） ----
		float windT = 0f;
		float windR = 0f;
		float rotSpd = 0f;
		try
		{
			double a0 = s.centerAngle;
			double pr = s.planet.Radius;
			double ang = a0;
			double rr = pr + s.Hbase + 0.5 * span;
			Double2 pos = new Double2(Math.Cos(ang) * rr, Math.Sin(ang) * rr);
			Double2 w = s.SampleWind(pos, false);
			Double2 rad = new Double2(Math.Cos(a0), Math.Sin(a0));
			Double2 tan = new Double2(0.0 - Math.Sin(a0), Math.Cos(a0));
			windT = (float)(w.x * tan.x + w.y * tan.y);
			windR = (float)(w.x * rad.x + w.y * rad.y);
			if (rotary)
			{
				double spin = Math.Min(Math.Abs(s.Vmax) / R * 2.0, 0.05);
				rotSpd = (float)spin * ((windT >= 0f) ? 1f : -1f);
			}
		}
		catch
		{
		}

		// ---- alpha（cloudOpacity × 生成/转变动画 × 消散 × 合并） ----
		float alpha = (float)TyphoonConfig.I.cloudOpacity * Mathf.Clamp01(alphaMul)
			* (float)s.DissolveFade() * s.MergeFade();
		if (alpha <= 0.003f)
		{
			SetVisible(false);
			return;
		}

		// ---- 类型外观（与 BuildBack cloudTint 同映射） ----
		Color tint;
		float coverage = 0.55f;
		float density = 1f;
		float fog = 0f;
		float dust = 0f;
		switch (s.type)
		{
			case StormType.Firestorm:
				tint = new Color(0.95f, 0.46f, 0.20f);
				break;
			case StormType.PolarVortex:
				tint = new Color(0.84f, 0.90f, 1.0f);
				break;
			case StormType.Megastorm:
				tint = new Color(0.52f, 0.46f, 0.62f);
				break;
			case StormType.DustStorm:
				tint = new Color(0.88f, 0.74f, 0.52f);
				dust = 0.55f;   // 沙尘暴主体带一点尘柱感
				break;
			case StormType.DustDevil:
				tint = new Color(0.92f, 0.83f, 0.66f);
				dust = 1f;
				break;
			case StormType.DenseFog:
				tint = Color.white;
				fog = 1f;
				coverage = 0.85f;
				density = 0.8f;
				break;
			default:
				if (s.atmoClass == 1)
				{
					tint = new Color(1f, 0.93f, 0.75f);
				}
				else
				{
					tint = Color.white;
				}
				break;
		}
		if (s.type == StormType.MCS)
		{
			coverage = 0.65f;
		}
		if (s.type == StormType.Typhoon)
		{
			coverage = 0.62f;
		}

		if (_seed < 0f)
		{
			_seed = UnityEngine.Random.value * 100f;
		}

		// ---- uniform 上传 ----
		_mpb.SetFloat(PExtX, _extX);
		_mpb.SetFloat(PYMin, _yMin);
		_mpb.SetFloat(PYMax, _yMax);
		_mpb.SetFloat(PRmax, (float)R);
		_mpb.SetFloat(PEyeR, eyeR);
		_mpb.SetFloat(PSpiral, rotary ? 1f : 0f);
		_mpb.SetFloat(PLine, line ? 1f : 0f);
		_mpb.SetFloat(PAnvil, (s.type == StormType.MCS) ? 1f : 0f);
		_mpb.SetFloat(PWall, (s.type == StormType.Supercell) ? 1f : 0f);
		_mpb.SetFloat(PDust, dust);
		_mpb.SetFloat(PFog, fog);
		_mpb.SetFloat(PWindT, windT);
		_mpb.SetFloat(PWindR, windR);
		_mpb.SetFloat(PAdvT, (float)_advT);
		_mpb.SetFloat(PRotSpd, rotSpd);
		_mpb.SetFloat(PSeed, _seed);
		_mpb.SetColor(PTint, tint);
		_mpb.SetFloat(PAlpha, alpha);
		_mpb.SetFloat(PDensity, density);
		_mpb.SetFloat(PCoverage, coverage);
		_mpb.SetFloat(PSunUp, 0.5f);
		_mpb.SetFloat(PDetail, 0.7f);
		_mr.SetPropertyBlock(_mpb);

		// ---- 几何：顶点=风暴本地米（far 空间预乘 farAbsS），GO 锚定风暴中心 ----
		float k = farAbs ? farAbsS : 1f;
		float ex = _extX * k;
		float y0 = _yMin * k;
		float y1 = _yMax * k;
		_verts[0] = new Vector3(0f - ex, y0, 0f);
		_verts[1] = new Vector3(0f - ex, y1, 0f);
		_verts[2] = new Vector3(ex, y1, 0f);
		_verts[3] = new Vector3(ex, y0, 0f);
		_mesh.vertices = _verts;
		_mesh.bounds = BigBounds;

		_go.layer = layer;
		_go.transform.position = anchor;
		_go.transform.rotation = Quaternion.identity;
		_go.transform.localScale = Vector3.one;
		SetVisible(true);
	}

	private void Build()
	{
		_go = new GameObject("MWE_ShaderCloud");
		MeshFilter mf = _go.AddComponent<MeshFilter>();
		_mesh = new Mesh
		{
			name = "MWEStormCloud"
		};
		_mesh.MarkDynamic();
		mf.sharedMesh = _mesh;
		_mr = _go.AddComponent<MeshRenderer>();
		Material mat = new Material(_shader);
		mat.renderQueue = StormRenderer.TopQueueCloud;
		_mr.sharedMaterial = mat;
		_mr.shadowCastingMode = ShadowCastingMode.Off;
		_mr.receiveShadows = false;
		_mr.sortingOrder = StormRenderer.TopSortingBase + 21;
		_mpb = new MaterialPropertyBlock();
		Vector2[] uvs = new Vector2[4]
		{
			new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f)
		};
		int[] tris = new int[6] { 0, 1, 2, 0, 2, 3 };
		_mesh.vertices = _verts;
		_mesh.uv = uvs;
		_mesh.triangles = tris;
		_mesh.bounds = BigBounds;
	}

	private void OnDestroy()
	{
		if (_mesh != null)
		{
			UnityEngine.Object.Destroy(_mesh);
		}
		if (_mr != null && _mr.sharedMaterial != null)
		{
			UnityEngine.Object.Destroy(_mr.sharedMaterial);
		}
	}
}
