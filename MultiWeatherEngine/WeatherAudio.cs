using UnityEngine;

namespace MultiWeatherEngine;

// ===== — 天气音效：风声/雷声（纯程序化合成，零资源文件） =====
// 用 Unity 原生 AudioSource + AudioClip.Create 合成波形：
// 风声 = 布朗噪声（积分白噪声，低频强）2s 循环 + 0.3Hz 呼吸起伏
// 雷声 = 白噪声 × 指数衰减包络 + 70Hz 低频轰 + 两次延迟回响脉冲（一次性触发）
// fix — 删雨声（用户：现实中不要似乎也行）；响度提升（布朗噪声 RMS 低听感轻，
// Normalize 峰值 1.2 + 整体 ×1.25 增益）；首次有声打印日志便于诊断音量链路。
// 音量由 TyphoonManager.UpdateWeatherAudio 每帧驱动（平滑插值防爆音）。
public class WeatherAudio : MonoBehaviour
{
	public static WeatherAudio main;

	private AudioSource windSrc;
	private AudioSource thunSrc;
	private AudioLowPassFilter thunLPF;

	public float targetWind;   // Manager 每帧设置 0-1

	private bool loggedActive;   // 首次有声打日志

	// ===== 龙卷预警曲（两首）=====
	// A 常规尺度（storm_chase.wav，提前 15 现实秒）/ B 大尺度备选（storm_chase_big.wav，
	// 提前 19 秒）。判据 = 核半径 ≥ tornadoThemeBigCoreM（默认 280m：楔形 304-334m 命中，
	// 标准/绳状 138-152m 走 A）。从 mod 目录读 16bit PCM WAV（自研解析 + AudioClip.Create，
	// 不依赖 Unity 解码器）；读盘走后台线程，clip 按需才建（没遇到大龙卷不占那 30MB）。
	private class ThemeTrack
	{
		public string file;
		public AudioClip clip;
		public float[] data;
		public int ch = 2;
		public int sr = 22050;
		public volatile bool ready;
		public bool tried;
	}

	private readonly ThemeTrack themeA = new ThemeTrack();
	private readonly ThemeTrack themeB = new ThemeTrack();
	private AudioSource themeSrc;
	private bool themeArmed = true;   // 重新武装：离开/消散后可再次触发（从头播）
	private bool themePlaying;        // 本轮遭遇是否处于"该放"状态（含已越过玩家、正在远离）
	private float themeStarted;       // 起播时刻（Time.unscaledTime，给最短播放时长兜底）
	private float themeTarget;        // 目标音量（0 或满），实际音量平滑逼近
	private float warnEta = -1f;      // Manager 每帧写入：最近"逼近中"龙卷的 ETA（秒），无则 -1
	private float warnDist = -1f;     // Manager 每帧写入：到最近龙卷的距离（米，不论朝向），无则 -1
	private float warnCoreM = -1f;    // Manager 每帧写入：该龙卷的核半径（米），无则 -1

	private void Awake()
	{
		main = this;
	}

	private void Start()
	{
		windSrc = gameObject.AddComponent<AudioSource>();
		thunSrc = gameObject.AddComponent<AudioSource>();
		windSrc.clip = SynthWind();
		thunSrc.clip = SynthThunder();
		thunLPF = gameObject.AddComponent<AudioLowPassFilter>();
		thunLPF.cutoffFrequency = 8000f;
		windSrc.loop = true;
		windSrc.volume = 0f;
		windSrc.Play();
		BeginLoad(themeA, TyphoonConfig.I.tornadoThemeFile);
		BeginLoad(themeB, TyphoonConfig.I.tornadoThemeBigFile);
		Debug.Log("[TyphoonAudio] synthesized wind " + windSrc.clip.length + "s @" + windSrc.clip.frequency + "Hz, thunder " + thunSrc.clip.length + "s");
	}

	// Manager 每帧调用：eta = 最近"逼近中"龙卷的剩余抵达时间（秒，无则 -1）；
	// dist = 到最近龙卷的距离（米，不论朝向，无则 -1）；coreM = 该龙卷的核半径（米，
	// 决定用 A 还是大尺度 B 曲目）。eta 负责"起播"，dist 负责"抵达后继续播"
	// （龙卷越过玩家后 eta 恒为 -1）。
	public void SetTornadoAlert(float eta, float dist, float coreM)
	{
		warnEta = eta;
		warnDist = dist;
		warnCoreM = coreM;
	}

	// 后台线程读盘 + 解析 WAV（AudioClip.Create 必须在主线程，见 UpdateTheme）。
	private void BeginLoad(ThemeTrack t, string file)
	{
		if (t.tried)
		{
			return;
		}
		t.tried = true;
		try
		{
			if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(TyphoonConfig.Folder))
			{
				return;
			}
			string full = System.IO.Path.Combine(TyphoonConfig.Folder, file);
			if (!System.IO.File.Exists(full))
			{
				Debug.LogWarning("[TyphoonAudio] theme file not found: " + full);
				return;
			}
			t.file = full;
			System.Threading.Thread th = new System.Threading.Thread(delegate()
			{
				try
				{
					float[] data;
					int ch;
					int sr;
					if (LoadWav(full, out data, out ch, out sr))
					{
						t.data = data;
						t.ch = ch;
						t.sr = sr;
						t.ready = true;
					}
					else
					{
						Debug.LogWarning("[TyphoonAudio] theme wav unsupported (need 16bit PCM): " + full);
					}
				}
				catch (System.Exception ex)
				{
					Debug.LogWarning("[TyphoonAudio] theme load failed: " + ex.Message);
				}
			});
			th.IsBackground = true;
			th.Start();
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[TyphoonAudio] theme thread: " + ex.Message);
		}
	}

	// 按需建 AudioClip（主线程）：没遇到大龙卷就不建 B，省下 ~30MB 托管 + 显存。
	private void MakeClip(ThemeTrack t)
	{
		if (!t.ready || t.clip != null || t.data == null)
		{
			return;
		}
		try
		{
			t.clip = AudioClip.Create("mwe_theme_" + t.file, t.data.Length / t.ch, t.ch, t.sr, false);
			t.clip.SetData(t.data, 0);
			t.data = null;   // 数据已交给 AudioClip，释放托管侧引用
			if (themeSrc == null)
			{
				themeSrc = gameObject.AddComponent<AudioSource>();
				themeSrc.loop = false;
				themeSrc.spatialBlend = 0f;
				themeSrc.volume = 0f;
			}
			Debug.Log("[TyphoonAudio] theme ready " + t.file + " " + t.clip.length.ToString("0.0") + "s");
		}
		catch (System.Exception ex)
		{
			t.ready = false;
			Debug.LogWarning("[TyphoonAudio] theme clip failed: " + ex.Message);
		}
	}

	// 自研 16bit PCM WAV 解析（RIFF 块扫描）：输出交错 float 数据。
	private static bool LoadWav(string path, out float[] data, out int channels, out int sampleRate)
	{
		data = null;
		channels = 2;
		sampleRate = 22050;
		byte[] b = System.IO.File.ReadAllBytes(path);
		if (b.Length < 44 || b[0] != (byte)'R' || b[1] != (byte)'I' || b[2] != (byte)'F' || b[3] != (byte)'F')
		{
			return false;
		}
		int p = 12;
		int bits = 16;
		int fmtCode = 1;
		int dataOff = -1;
		int dataLen = 0;
		while (p + 8 <= b.Length)
		{
			int c0 = b[p];
			int c1 = b[p + 1];
			int c2 = b[p + 2];
			int c3 = b[p + 3];
			int sz = b[p + 4] | (b[p + 5] << 8) | (b[p + 6] << 16) | (b[p + 7] << 24);
			int body = p + 8;
			if (c0 == 'f' && c1 == 'm' && c2 == 't' && c3 == ' ')
			{
				fmtCode = b[body] | (b[body + 1] << 8);
				channels = b[body + 2] | (b[body + 3] << 8);
				sampleRate = b[body + 4] | (b[body + 5] << 8) | (b[body + 6] << 16) | (b[body + 7] << 24);
				bits = b[body + 14] | (b[body + 15] << 8);
			}
			else if (c0 == 'd' && c1 == 'a' && c2 == 't' && c3 == 'a')
			{
				dataOff = body;
				dataLen = (sz < b.Length - body) ? sz : (b.Length - body);
				break;
			}
			p = body + sz + (sz & 1);
		}
		if (dataOff < 0 || fmtCode != 1 || bits != 16 || channels < 1)
		{
			return false;
		}
		int n = dataLen / 2;
		float[] d = new float[n];
		for (int i = 0; i < n; i++)
		{
			short s16 = (short)(b[dataOff + i * 2] | (b[dataOff + i * 2 + 1] << 8));
			d[i] = s16 / 32768f;
		}
		data = d;
		return true;
	}

	// 预警曲状态机：ETA ≤ 提前量 → 起播（每次遭遇只触发一次）；离开/消散 → 淡出并重新武装。
	private void UpdateTheme(float masterVol)
	{
		// 轨道选型：核半径 ≥ 阈值 = 大尺度龙卷（楔形宽漏斗 304-334m）→ 备选曲 B + 19 秒
		// 提前量；常规尺度（标准/绳状/陆龙卷 138-152m）→ 曲 A + 15 秒。判据用核半径
		// （= 看得见的漏斗宽度），不用 EF 强度 —— 用户要的是"尺度大时换曲"。
		float bigCore = Mathf.Max(50f, TyphoonConfig.I.tornadoThemeBigCoreM);
		bool bigWant = warnCoreM >= bigCore;
		float lead = Mathf.Max(2f, bigWant ? TyphoonConfig.I.tornadoThemeBigLeadSec : TyphoonConfig.I.tornadoThemeLeadSec);
		// 维持半径至少 3×核半径：超巨型（核可达 ~1km）的风暴不能被"1500m 维持圈"提前放开。
		float keepDist = Mathf.Max(Mathf.Max(500f, TyphoonConfig.I.tornadoThemeKeepM), warnCoreM * 3f);
		// 时间加速保护：warnEta 已是现实秒，但时间加速下整段遭遇会被压缩到几秒内，
		// 快进时连着几个风暴都触发会一直响 → 倍率过高直接不放。
		bool warpOk = TyphoonManager.timeScaleReal <= Mathf.Max(1f, TyphoonConfig.I.tornadoThemeMaxWarp);
		bool incoming = warpOk && warnEta >= 0f && warnEta <= lead;   // 逼近中：漏斗边缘 ≤ lead 秒
		if (TyphoonConfig.I.tornadoTheme && incoming && themeArmed)
		{
			// 只在真正要播时才建 clip（没遇到大龙卷就不占那份 ~30MB 内存）；
			// 目标曲目缺失（文件没放）就退回另一首，避免"该响的时候静音"。
			MakeClip(bigWant ? themeB : themeA);
			MakeClip(bigWant ? themeA : themeB);
			AudioClip want = (bigWant ? themeB : themeA).clip;
			if (want == null)
			{
				want = (bigWant ? themeA : themeB).clip;
				bigWant = !bigWant;
			}
			if (want != null)
			{
				// 曲长 vs 遭遇时长：遭遇 ≈ 抵达剩余 + 过境后离开维持半径（都是现实秒）。
				// 曲子比遭遇长 → 按比例提速，让它跟遭遇一起收尾；上限 maxPitch 防变调过头
				// （时间加速下遭遇会被压到几秒，这里就只能靠上限兜住）。
				float encSec = warnEta + keepDist / Mathf.Max(TyphoonManager.nearestTornadoSpeedReal, 0.5f);
				float maxPitch = Mathf.Max(1f, TyphoonConfig.I.tornadoThemeMaxPitch);
				float pitch = Mathf.Clamp(want.length / Mathf.Max(encSec, 1f), 1f, maxPitch);
				themeArmed = false;
				themePlaying = true;
				themeStarted = Time.unscaledTime;
				themeSrc.clip = want;
				themeSrc.pitch = pitch;
				themeSrc.time = 0f;
				themeSrc.Play();
				Debug.Log("[TyphoonAudio] tornado theme start (" + (bigWant ? "大尺度" : "常规") + "), ETA " + warnEta.ToString("0.0") + "s, dist " + warnDist.ToString("0") + "m, core " + warnCoreM.ToString("0") + "m, enc " + encSec.ToString("0") + "s, pitch " + pitch.ToString("0.00"));
			}
		}
		if (themeSrc == null || themeSrc.clip == null)
		{
			return;
		}
		// 维持（本次修复）：① 龙卷还在 keepDist 米内 —— 含"已经越过玩家、正在远离"，
		// 原来只看"逼近中"的 ETA，龙卷一到 0m（越过你）ETA 就变 -1 → 音乐在最刺激的
		// 抵达瞬间被掐掉，用户实测"一旦为 0m 就不放了"；② 起播不足 minHoldSec（音乐
		// 不在遭遇中途被切）。两者都不满足才淡出，并重新武装等下一场龙卷。
		if (themePlaying)
		{
			bool hold = warnDist >= 0f && warnDist <= keepDist;
			bool minHold = (Time.unscaledTime - themeStarted) < Mathf.Max(0f, TyphoonConfig.I.tornadoThemeMinHoldSec);
			if (!hold && !minHold)
			{
				themePlaying = false;
				themeArmed = true;
			}
		}
		themeTarget = (TyphoonConfig.I.tornadoTheme && themePlaying) ? Mathf.Clamp01(TyphoonConfig.I.tornadoThemeVolume) : 0f;
		// 淡入淡出：按秒数线性推进（原 MoveTowards 0.8/s ≈ 1.25 秒，起播和收尾都太硬）。
		float fadeSec = Mathf.Max(0.2f, TyphoonConfig.I.tornadoThemeFadeSec);
		float vol = themeTarget * masterVol;
		themeSrc.volume = Mathf.MoveTowards(themeSrc.volume, vol, Time.deltaTime / fadeSec);
		if (themeTarget <= 0f && themeSrc.volume <= 0.01f && themeSrc.isPlaying)
		{
			themeSrc.Stop();   // 音量归零后复位，下一场从头播
		}
	}

	private void Update()
	{
		if (windSrc == null)
		{
			return;
		}
		// fix2 — 防御性 Clamp01（设置页 NumberInput 可输入负数/超 1，
		// 回调虽 Clamp 但内部值可能残留异常 → 这里兜底）。
		float vol = TyphoonConfig.I.weatherAudio ? Mathf.Clamp01(TyphoonConfig.I.weatherVolume) : 0f;
		windSrc.volume = Mathf.MoveTowards(windSrc.volume, targetWind * vol, Time.deltaTime * 2f);
		if (!loggedActive && windSrc.volume > 0.05f)
		{
			loggedActive = true;
			Debug.Log("[TyphoonAudio] wind active, volume=" + windSrc.volume.ToString("0.00") + " (target " + targetWind.ToString("0.00") + ", vol " + vol.ToString("0.00") + ")");
		}
		UpdateTheme(vol);
	}

	// 雷声触发（闪电产生时调用；vol 0-1 按风暴距离衰减）
	// fix2 — 雷声也乘 weatherVolume（原完全没走音量设置，只受距离衰减控制）。
	// fix — 雷声按距离低通（近亮远闷）+ 按风暴方位立体声平移，更真实
	public void PlayThunder(float vol, float pan = 0f)
	{
		if (thunSrc == null || vol <= 0.01f)
		{
			return;
		}
		float scale = TyphoonConfig.I.weatherAudio ? Mathf.Clamp01(TyphoonConfig.I.weatherVolume) : 0f;
		if (thunLPF != null)
		{
			thunLPF.cutoffFrequency = Mathf.Lerp(600f, 16000f, Mathf.Clamp01(vol));
		}
		thunSrc.panStereo = Mathf.Clamp(pan, -1f, 1f);
		thunSrc.volume = Mathf.Clamp01(vol * scale);
		thunSrc.Play();
	}

	// ===== 合成 =====

	// 风声：布朗噪声（随机游走积分 → -6dB/oct 低频强）+ 0.3Hz 幅度呼吸。
	private static AudioClip SynthWind()
	{
		int sr = 44100;
		int len = sr * 2;   // 2s 循环
		float[] d = new float[len];
		double brown = 0.0;
		for (int i = 0; i < len; i++)
		{
			double white = UnityEngine.Random.value * 2.0 - 1.0;
			brown = (brown + white * 0.02) / 1.02;
			// 呼吸：0.25Hz（周期 4s，但 2s 循环处 sin(π)=0 与起点同值→接缝无缝）；
			// 幅度 ±15% 改原 ±25%，消除"一直弱强弱"的明显周期起伏。
			double breath = 0.85 + 0.15 * Mathf.Sin(6.2831853f * (float)i / (float)sr * 0.25f);
			d[i] = (float)(brown * 6.0 * breath);
		}
		// 无缝循环：头尾 50ms 等幂交叉淡化，消除 2s 循环接缝的咔哒声
		int cross = sr / 20;
		for (int i = 0; i < cross && i < len / 2; i++)
		{
			float w = (float)i / (float)cross;
			float a = d[i];
			float b = d[len - cross + i];
			d[i] = a * (1f - w) + b * w;
			d[len - cross + i] = b * (1f - w) + a * w;
		}
		Normalize(d, 1.2f);   // 峰值 1.2（响度基准提高）
		// 布朗噪声 RMS 低（能量集中低频），整体 ×1.25 增益补偿听感
		for (int i = 0; i < d.Length; i++)
		{
			d[i] = Mathf.Clamp(d[i] * 1.25f, -1f, 1f);
		}
		return MakeClip("mwe_wind", d, sr);
	}

	// 雷声：3s 一次性。主隆隆 = 白噪声 × exp(-t/0.35)；70Hz 低频轰 × exp(-t/0.5)；
	// 0.7s/1.4s 两个延迟回响脉冲（近地雷声在山谷/云层的二次回声）。
	private static AudioClip SynthThunder()
	{
		int sr = 44100;
		int len = sr * 3;
		float[] d = new float[len];
		for (int i = 0; i < len; i++)
		{
			float t = (float)i / (float)sr;
			float white = UnityEngine.Random.value * 2f - 1f;
			float main = white * Mathf.Exp(0f - t / 0.35f) * 0.9f;
			float boom = Mathf.Sin(6.2831853f * 70f * t) * Mathf.Exp(0f - t / 0.5f) * 0.5f;
			float echo1 = (t > 0.7f) ? (white * Mathf.Exp(0f - (t - 0.7f) / 0.2f) * 0.45f) : 0f;
			float echo2 = (t > 1.4f) ? (white * Mathf.Exp(0f - (t - 1.4f) / 0.25f) * 0.3f) : 0f;
			d[i] = main + boom + echo1 + echo2;
		}
		Normalize(d, 1.0f);
		return MakeClip("mwe_thunder", d, sr);
	}

	private static void Normalize(float[] d, float peakTarget)
	{
		float peak = 0.001f;
		for (int i = 0; i < d.Length; i++)
		{
			float a = Mathf.Abs(d[i]);
			if (a > peak)
			{
				peak = a;
			}
		}
		if (peak > 0.001f)
		{
			float g = peakTarget / peak;
			for (int i = 0; i < d.Length; i++)
			{
				d[i] *= g;
			}
		}
	}

	private static AudioClip MakeClip(string name, float[] data, int sr)
	{
		AudioClip clip = AudioClip.Create(name, data.Length, 1, sr, false);
		clip.SetData(data, 0);
		return clip;
	}
}
