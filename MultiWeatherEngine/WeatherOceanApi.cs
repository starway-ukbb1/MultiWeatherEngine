using System;
using System.Reflection;
using SFS;
using SFS.WorldBase;

namespace MultiWeatherEngine
{
    // ═══════════════════════════════════════════════════════════════════════
    //  跨 mod 通信契约 —— 委托类型
    //
    //  MultiWeatherEngine（本 mod，下称 MWE）与 OceanWaves 是两个**独立 DLL**，
    //  没有任何编译期引用关系（谁先装、装不装、版本各是多少，都不能假定）。
    //  所以两边各写一份**同名同签名**的契约文件，运行时靠反射互相发现、
    //  再用 `Delegate.CreateDelegate` 绑成**强类型委托**。
    //
    //  能这么干的前提（已核实）：
    //    · 两个 DLL 都引用同一份 Assembly-CSharp.dll（游戏本体），所以
    //      `SFS.WorldBase.Planet` 在进程里**是同一个 Type 对象** → 委托签名可精确匹配；
    //    · 方法签名只用 BCL + Assembly-CSharp 的类型，**绝不出现任何一方自己定义的类型**
    //      （那才是跨程序集身份不成立的东西）。
    //
    //  绑定成功后每次查询就是一次普通委托调用，**零反射、零装箱**。
    //  一方缺席 → 全部方法优雅返回 false/0，各自退回原行为。
    // ═══════════════════════════════════════════════════════════════════════

    internal delegate int Fn_Planet_Int(Planet p);
    internal delegate int Fn_Describe(Planet p, float[] buf, int stride);
    internal delegate int Fn_WindBatch(Planet p, double[] angles, double heightM, float[] outUV);
    internal delegate float Fn_Planet_Angle(Planet p, double angleRad);
    internal delegate int Fn_SeaBatch(Planet p, double[] angles, float[] outHs, float[] outTp);
    internal delegate string Fn_Planet_Str(Planet p);
    internal delegate string Fn_Str();
    internal delegate bool Fn_Bool();

    /// <summary>
    /// MWE 对外 API + 与 OceanWaves 的双向桥。
    ///
    /// **两个方向**：
    ///   ① 天气 → 浪（本 mod 提供）：`SampleWindBatch` / `DescribeStorms` / `SampleSst`
    ///      → OceanWaves 拿去造浪（风暴隆起 + 逐点风浪）。
    ///   ② 浪 → 天气（本 mod 拉取）：`SampleSeaStateBatch` / `SeaSurfaceOffset`
    ///      → 玩家所在处的海况，本 mod 用来显示，并在 OceanWaves 在线时**让出造浪权**。
    ///
    /// **第三方怎么用**：反射找 `MultiWeatherEngine.WeatherOceanApi`（或海洋侧的
    /// `OceanWaves.WeatherOceanApi`），两边方法集**完全一致**，任取其一即可。
    /// </summary>
    public static class WeatherOceanApi
    {
        /// <summary>契约版本。两边不一致时只降级、不崩。</summary>
        public const int ApiVersion = 1;

        public const string PeerTypeName = "OceanWaves.WeatherOceanApi";
        public const string SelfTypeName = "MultiWeatherEngine.WeatherOceanApi";

        // ── DescribeStorms 的槽位布局（float[StormStride] 一条风暴）──
        public const int StormStride = 8;
        public const int Slot_CenterAngleRad = 0;   // 风暴中心绝对角度（弧度）
        public const int Slot_RmaxM = 1;            // 最大风半径（米）
        public const int Slot_Vmax = 2;             // 峰值风速（m/s，平滑显示值）
        public const int Slot_TypeId = 3;           // StormType 枚举值
        public const int Slot_Stage = 4;            // 0 发展 / 1 成熟 / 2 消散
        public const int Slot_SstC = 5;             // 中心海温 °C
        public const int Slot_RouterM = 6;          // 外缘半径（米）
        public const int Slot_MoveSpeed = 7;        // 中心移动速度（m/s）

        // ═══════════════ 握手状态 ═══════════════

        static Type _peer;
        static float _nextProbe = -1f;
        static string _note = "尚未探测";

        static Fn_Bool _pOceanOnline;
        static Fn_Str _pStatus;
        static Fn_SeaBatch _pSampleSeaStateBatch;
        static Fn_Planet_Angle _pSeaSurfaceOffset;
        static Fn_Planet_Str _pSeaStateSummary;

        /// <summary>本 mod 自己是天气侧 —— 恒 true</summary>
        public static bool WeatherOnline() { return TyphoonManager.main != null || TyphoonManager.systems != null; }

        /// <summary>OceanWaves 是否在线（已握手且它自称可用）</summary>
        public static bool OceanOnline()
        {
            try { return _peer != null && _pOceanOnline != null && _pOceanOnline(); }
            catch { DropPeer("调用海洋侧抛异常"); return false; }
        }

        public static int GetApiVersion() { return ApiVersion; }
        public static string ApiName() { return "MultiWeatherEngine"; }

        public static string Status()
        {
            if (_peer == null) return "WeatherOceanBridge v" + ApiVersion + " [MWE] peer=未发现(" + _note + ")";
            return "WeatherOceanBridge v" + ApiVersion + " [MWE] peer=OceanWaves 已连接";
        }

        /// <summary>海洋侧连接状态（面板/日志显示用）</summary>
        public static string PeerWatch()
        {
            return _peer == null ? "未发现 OceanWaves（" + _note + "）" : "已连接 OceanWaves";
        }

        /// <summary>
        /// 每帧调一次（TyphoonManager.LateUpdate）。加载顺序不定 —— 必须容错重试，
        /// 不能在 Load 里一次性放弃。重试带 2 秒节流，连上后只剩一次 null 判断。
        /// </summary>
        public static void Tick()
        {
            if (_peer != null) return;
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextProbe) return;
            _nextProbe = now + 2f;
            Probe();
        }

        static void Probe()
        {
            Type t = null;
            try
            {
                Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < asms.Length; i++)
                {
                    Assembly a = asms[i];
                    if (a == null) continue;
                    try
                    {
                        t = a.GetType(PeerTypeName, false);
                        if (t != null) break;
                    }
                    catch { }
                }
            }
            catch (Exception e) { _note = "枚举程序集失败: " + e.Message; return; }

            if (t == null) { _note = "OceanWaves 未加载"; return; }

            try
            {
                _pOceanOnline = Bind<Fn_Bool>(t, "OceanOnline");
                _pStatus = Bind<Fn_Str>(t, "Status");
                _pSampleSeaStateBatch = Bind<Fn_SeaBatch>(t, "SampleSeaStateBatch");
                _pSeaSurfaceOffset = Bind<Fn_Planet_Angle>(t, "SeaSurfaceOffset");
                _pSeaStateSummary = Bind<Fn_Planet_Str>(t, "SeaStateSummary");

                if (_pSeaSurfaceOffset == null && _pSampleSeaStateBatch == null)
                {
                    _note = "找到类型但方法签名不匹配（版本差异？）";
                    return;
                }

                int ver = -1;
                try
                {
                    MethodInfo mv = t.GetMethod("GetApiVersion", BindingFlags.Public | BindingFlags.Static);
                    if (mv != null) ver = (int)mv.Invoke(null, null);
                }
                catch { }

                _peer = t;
                _note = "已连接 v" + (ver < 0 ? "?" : ver.ToString());
                UnityEngine.Debug.Log("[WeatherOcean] 已连接 OceanWaves " + _note
                    + "（造浪权交给海洋侧，本 mod 不再直接改写水面 shader）");
            }
            catch (Exception e)
            {
                _note = "绑定失败: " + e.Message;
                _peer = null;
            }
        }

        static T Bind<T>(Type t, string name) where T : class
        {
            try
            {
                MethodInfo m = t.GetMethod(name, BindingFlags.Public | BindingFlags.Static);
                if (m == null) return null;
                return Delegate.CreateDelegate(typeof(T), m, false) as T;
            }
            catch { return null; }
        }

        static void DropPeer(string why)
        {
            _peer = null;
            _pOceanOnline = null; _pStatus = null;
            _pSampleSeaStateBatch = null; _pSeaSurfaceOffset = null; _pSeaStateSummary = null;
            _note = why;
            _nextProbe = UnityEngine.Time.realtimeSinceStartup + 2f;
            UnityEngine.Debug.Log("[WeatherOcean] 断开 OceanWaves：" + why);
        }

        // ═══════════════ 天气侧（本 mod 原生提供） ═══════════════

        /// <summary>该行星上活跃天气系统数量（p 为 null = 全部行星）</summary>
        public static int StormCount(Planet p)
        {
            try
            {
                var list = TyphoonManager.systems;
                if (list == null) return 0;
                int n = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    WeatherSystem s = list[i];
                    if (s == null || !s.active) continue;
                    if (p != null && !ReferenceEquals(s.planet, p)) continue;
                    n++;
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// 把该行星上的风暴快照写进 buf（每风暴 <see cref="StormStride"/> 个 float，见 Slot_*）。
        /// 返回写入的风暴条数。
        /// </summary>
        public static int DescribeStorms(Planet p, float[] buf, int stride)
        {
            try
            {
                var list = TyphoonManager.systems;
                if (list == null || buf == null) return 0;
                if (stride <= 0) stride = StormStride;
                if (stride < StormStride) return 0;         // 槽位不够，宁可返回 0 也不越界写

                int n = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    WeatherSystem s = list[i];
                    if (s == null || !s.active) continue;
                    if (p != null && !ReferenceEquals(s.planet, p)) continue;

                    int o = n * stride;
                    if (o + StormStride > buf.Length) break;

                    buf[o + Slot_CenterAngleRad] = (float)s.centerAngle;
                    buf[o + Slot_RmaxM] = (float)s.Rmax;
                    buf[o + Slot_Vmax] = (float)s.vmaxDisplay;
                    buf[o + Slot_TypeId] = (float)(int)s.type;
                    buf[o + Slot_Stage] = (float)s.stage;
                    buf[o + Slot_SstC] = s.sstDisplay;
                    buf[o + Slot_RouterM] = (float)s.Router;
                    buf[o + Slot_MoveSpeed] = (float)s.moveSpeed;
                    n++;
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>
        /// **风驱动浪的主入口**：批量取 angles 处、heightM 高度上的风矢量（各风暴叠加）。
        /// outUV[2i] = 行星全局风矢量 x（m/s），outUV[2i+1] = y。返回写入的采样点数。
        ///
        /// 传入角度处的采样点位置 = (cos·（R+heightM）, sin·（R+heightM）)，
        /// 即 heightM = 0 就是海面风。MWE 的风廓线在海面最强（hDecay 在 h=0 处为 1），
        /// 所以这就是"驱动海浪的那个 U"。
        /// </summary>
        public static int SampleWindBatch(Planet p, double[] angles, double heightM, float[] outUV)
        {
            try
            {
                var list = TyphoonManager.systems;
                if (p == null || angles == null || outUV == null) return 0;
                int n = angles.Length;
                if (outUV.Length < n * 2) return 0;

                double r = p.Radius + heightM;
                if (r < 1.0) r = 1.0;

                int active = (list == null) ? 0 : list.Count;
                for (int i = 0; i < n; i++)
                {
                    double ang = angles[i];
                    Double2 pos = new Double2(Math.Cos(ang) * r, Math.Sin(ang) * r);

                    double wx = 0.0, wy = 0.0;
                    for (int k = 0; k < active; k++)
                    {
                        WeatherSystem s = list[k];
                        if (s == null || !s.active) continue;
                        if (!ReferenceEquals(s.planet, p)) continue;
                        Double2 w = s.SampleWind(pos);
                        wx += w.x;
                        wy += w.y;
                    }

                    outUV[i * 2] = (float)wx;
                    outUV[i * 2 + 1] = (float)wy;
                }
                return n;
            }
            catch { return 0; }
        }

        /// <summary>该角度处海温 °C（MWE 的海温场 + 台风冷尾流）。</summary>
        public static float SampleSst(Planet p, double angleRad)
        {
            try
            {
                if (p == null) return 27f;
                return WeatherSystem.SampleSst(p, angleRad, AtmoClassOf(p));
            }
            catch { return 27f; }
        }

        /// <summary>行星大气分级（与 WeatherSystem.Configure 里同一套判据，任何行星 mod 自动生效）</summary>
        static int AtmoClassOf(Planet p)
        {
            double atmTop = 60000.0;
            try
            {
                if (p.HasAtmospherePhysics && p.AtmosphereHeightPhysics > 1000.0)
                    atmTop = p.AtmosphereHeightPhysics;
            }
            catch { }
            if (atmTop > 120000.0) return 2;
            if (atmTop > 70000.0) return 1;
            return 0;
        }

        // ═══════════════ 海洋侧（转发给 OceanWaves） ═══════════════

        /// <summary>
        /// 批量海况：outHs[i] = 有效波高 Hs（米），outTp[i] = 峰周期（秒）。
        /// OceanWaves 不在线返回 0。
        /// </summary>
        public static int SampleSeaStateBatch(Planet p, double[] angles, float[] outHs, float[] outTp)
        {
            if (_peer == null || _pSampleSeaStateBatch == null) return 0;
            try { return _pSampleSeaStateBatch(p, angles, outHs, outTp); }
            catch { DropPeer("SampleSeaStateBatch 抛异常"); return 0; }
        }

        /// <summary>该角度的水面相对海平面位移（米，正=波峰）。OceanWaves 不在线返回 0。</summary>
        public static float SeaSurfaceOffset(Planet p, double angleRad)
        {
            if (_peer == null || _pSeaSurfaceOffset == null) return 0f;
            try { return _pSeaSurfaceOffset(p, angleRad); }
            catch { DropPeer("SeaSurfaceOffset 抛异常"); return 0f; }
        }

        /// <summary>海洋侧的一行海况摘要。不在线返回提示串。</summary>
        public static string SeaStateSummary(Planet p)
        {
            if (_peer == null || _pSeaStateSummary == null) return "OceanWaves 未在线";
            try { return _pSeaStateSummary(p); }
            catch { DropPeer("SeaStateSummary 抛异常"); return "海况读取失败"; }
        }

        // ═══════════════ 本 mod 对海况的消费（浪 → 天气方向） ═══════════════

        /// <summary>玩家处最近一次采到的水面位移（米，正=波峰）</summary>
        public static float LastSeaOffset { get; private set; }
        /// <summary>玩家处最近一次采到的有效波高（米）</summary>
        public static float LastSeaHs { get; private set; }
        /// <summary>玩家处最近一次采到的峰周期（秒）</summary>
        public static float LastSeaTp { get; private set; }
        /// <summary>是否采到过有效海况</summary>
        public static bool SeaStateValid { get; private set; }

        static readonly double[] _probeAng = new double[1];
        static readonly float[] _probeHs = new float[1];
        static readonly float[] _probeTp = new float[1];

        /// <summary>
        /// 采一次玩家脚下的海况（TyphoonManager.LateUpdate 每帧调）。
        /// 用批量接口传 1 个点，避免为单点再造一套签名 —— 复用率高，开销一样。
        /// </summary>
        public static void ProbeSeaState(Planet p, double angleRad)
        {
            if (_peer == null) { SeaStateValid = false; return; }
            try
            {
                _probeAng[0] = angleRad;
                LastSeaOffset = SeaSurfaceOffset(p, angleRad);
                int got = SampleSeaStateBatch(p, _probeAng, _probeHs, _probeTp);
                if (got > 0)
                {
                    LastSeaHs = _probeHs[0];
                    LastSeaTp = _probeTp[0];
                    SeaStateValid = true;
                }
                else SeaStateValid = false;
            }
            catch { SeaStateValid = false; }
        }

        /// <summary>一行诊断（HUD / 日志）：海况 + 桥状态。</summary>
        public static string ProbeLine()
        {
            if (!SeaStateValid) return PeerWatch();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "海况 Hs {0:0.0} m / 周期 {1:0.0} s / 水面 {2:+0.0;-0.0;0.0} m  |  {3}",
                LastSeaHs, LastSeaTp, LastSeaOffset, PeerWatch());
        }
    }
}
