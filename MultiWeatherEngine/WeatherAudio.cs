using UnityEngine;

namespace MultiWeatherEngine;

// ===== v2.2.1 — 天气音效：风声/雷声（纯程序化合成，零资源文件） =====
// 用 Unity 原生 AudioSource + AudioClip.Create 合成波形：
//   风声 = 布朗噪声（积分白噪声，低频强）2s 循环 + 0.3Hz 呼吸起伏
//   雷声 = 白噪声 × 指数衰减包络 + 70Hz 低频轰 + 两次延迟回响脉冲（一次性触发）
// v2.2.1 fix — 删雨声（用户：现实中不要似乎也行）；响度提升（布朗噪声 RMS 低听感轻，
// Normalize 峰值 1.2 + 整体 ×1.25 增益）；首次有声打印日志便于诊断音量链路。
// 音量由 TyphoonManager.UpdateWeatherAudio 每帧驱动（平滑插值防爆音）。
public class WeatherAudio : MonoBehaviour
{
	public static WeatherAudio main;

	private AudioSource windSrc;
	private AudioSource thunSrc;

	public float targetWind;   // Manager 每帧设置 0-1

	private bool loggedActive;   // 首次有声打日志

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
		windSrc.loop = true;
		windSrc.volume = 0f;
		windSrc.Play();
		Debug.Log("[TyphoonAudio] synthesized wind " + windSrc.clip.length + "s @" + windSrc.clip.frequency + "Hz, thunder " + thunSrc.clip.length + "s");
	}

	private void Update()
	{
		if (windSrc == null)
		{
			return;
		}
		// v2.2.1 fix2 — 防御性 Clamp01（设置页 NumberInput 可输入负数/超 1，
		// 回调虽 Clamp 但内部值可能残留异常 → 这里兜底）。
		float vol = TyphoonConfig.I.weatherAudio ? Mathf.Clamp01(TyphoonConfig.I.weatherVolume) : 0f;
		windSrc.volume = Mathf.MoveTowards(windSrc.volume, targetWind * vol, Time.deltaTime * 2f);
		if (!loggedActive && windSrc.volume > 0.05f)
		{
			loggedActive = true;
			Debug.Log("[TyphoonAudio] wind active, volume=" + windSrc.volume.ToString("0.00") + " (target " + targetWind.ToString("0.00") + ", vol " + vol.ToString("0.00") + ")");
		}
	}

	// 雷声触发（闪电产生时调用；vol 0-1 按风暴距离衰减）
	// v2.2.1 fix2 — 雷声也乘 weatherVolume（原完全没走音量设置，只受距离衰减控制）。
	public void PlayThunder(float vol)
	{
		if (thunSrc == null || vol <= 0.01f)
		{
			return;
		}
		float scale = TyphoonConfig.I.weatherAudio ? Mathf.Clamp01(TyphoonConfig.I.weatherVolume) : 0f;
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
			double breath = 0.75 + 0.25 * Mathf.Sin(6.2831853f * (float)i / (float)sr * 0.3f);   // 0.3Hz 呼吸
			d[i] = (float)(brown * 6.0 * breath);
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
