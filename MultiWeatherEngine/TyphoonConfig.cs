using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace MultiWeatherEngine;

public class TyphoonConfig
{
	public static TyphoonConfig I = new TyphoonConfig();

	private static string path;

	// 结构整理（现实性审计）：以下旧版尺度参数已成死字段，全部删除——
	// eyewallRadiusAsPlanetFraction / eyewallRadiusMinMeters / eyewallRadiusMaxMeters
	// （被 WeatherSystem.TyphoonEyeR(category) 与 TypeSpec.rmaxFrac 取代）、
	// outerRadiusMultiplier（Router = Rmax×9 硬编码）、topHeightMin/Mid/MaxMeters
	// （被 TypeSpec.htopFrac + 强度映射取代，Load() 里还专门强行覆写它们以中和旧 json）、
	// updraftFraction（被 TypeSpec.updraft 取代）、driftSpeed（被 TypeSpec.vmaxMs 派生取代）。
	// 旧 json 里残留这些键会被 Newtonsoft 忽略，不影响读取。

	public double outflowFraction = 0.75;

	public double gustAmplitude = 0.35;

	public double spawnLeadDistanceMeters = 60000.0;

	public bool cameraShake = true;

	public double cameraShakeScale = 1.0;

	public bool affectAstronauts = true;

	public bool visuals = true;

	// 地图标记（M 地图视图显示风暴点+标签）：
	public bool mapMarkers = true;

	// 天气音效（程序化合成雷声/风声/雨声，无需音频文件）：
	public bool weatherAudio = true;        // 音效总开关
	public float weatherVolume = 0.8f;      // 音效音量 0-1

	public int canopyPuffs = 1500;

	public int cloudPuffs = 1100;

	public int rainDrops = 1700;

	public double cloudOpacity = 0.95;

	public double rainOpacity = 0.85;

	public double rainScale = 1.0;

	public double skyOpacity = 0.78;

	public bool lightning = true;

	public bool hud = true;

	// 等级上限 + 消散产物开关（等级上限与消散产物讨论）：limitMaxCategory 关 =
	// chaos 模式（所有系统可自然升到 6 级）；residualLow 台风残余低压（逗点化+延长）；
	// muddyRain 泥雨（沙尘与雨混合染色）；cloudBaseErosion 云底侵蚀（消散从下往上散）。
	// 运行时读取免重启（逻辑/渲染每帧读字段）。
	public bool limitMaxCategory = true;

	public bool residualLow = true;

	public bool muddyRain = true;

	public bool cloudBaseErosion = true;

	// 附属现象现实配额（现实性审计）：开 = 按现实统计生成（仅约 1/3 风暴产龙卷、
	// 一生 1-3 个，下暴/阵风锋各 1-2 个，同时最多 2 个）；关 = 旧的高频连续生成
	// （成熟期几乎恒定挂满 4 龙卷，观赏性强但与现实差两个数量级）。
	public bool phenomenaRealism = true;

	// 风暴卷起的障碍物（树木 / 石头）：被风刮起后成为真实障碍物，撞击火箭时用游戏原生
	// DestructionReason.RocketCollision 摧毁它（失败菜单照常弹出）。树木只在绿地生成、
	// 石头任意陆地；海上无源。数量上限按风暴计（性能：每个最多 4 个 quad）。
	public bool stormDebris = true;
	public int debrisMaxPerStorm = 14;
	// 障碍物是否摧毁火箭（关 = 只做物理撞击的视觉与推挤，不触发解体）。
	public bool debrisHurtsRockets = true;
	// 龙卷内部能见度骤降（屏幕级沙尘遮蔽 + 后处理变暗偏褐）。
	public bool tornadoObscure = true;
	// 被云/雨包裹的逐型能见度骤降（开雾+灰化，按真实能见度距离：暴雨内<1km）。
	public bool rainObscure = true;

	// 自然生成（玩家易忽略的"随机刷新"参数，设置页可调）：
	// 间隔单位 = **游戏秒**（世界时间）：时间加速倍率越高 → 游戏时间推进越快 →
	// 生成同步快进（与生命周期/移动/升级同一套"时间加速=快进"语义）。
	// 默认 3600-10800（1-3 游戏小时一个对流系统）：现实一个 ~60km 半径区域内强对流
	// 生成事件就是一天数次这个量级。
	public bool naturalSpawn = true;           // 自然生成开关（关 = 只手动召唤）
	public float naturalSpawnMinSec = 3600f;   // 生成最小间隔（游戏秒）
	public float naturalSpawnMaxSec = 10800f;  // 生成最大间隔（游戏秒）
	public float naturalSpawnDistKm = 62f;     // 生成距离范围（km，距玩家 12km~此值）
	// 配置版本（一次性迁移用，见 Load）：3 = 生成间隔改按游戏时间计（分钟级默认值）。
	public int cfgVersion = 3;

	// 预生成（进存档/换星球时给当前行星播种，世界一进去就是活的）：
	public bool preSpawnEnabled = true;        // 预生成开关
	public int preSpawnCountPerPlanet = 2;     // 当前行星预生成数量（0 = 关）
	public float preSpawnTyphoonChance = 0.15f; // 台风占比（低，台风是大事件）

	// 龙卷预警音乐（风暴拦截者曲目）：龙卷抵达前 tornadoThemeLeadSec 现实秒开始播放，
	// 音频文件放在 mod 目录（16bit PCM WAV，默认 storm_chase.wav）。
	public bool tornadoTheme = true;             // 开关
	public float tornadoThemeLeadSec = 15f;      // 提前量（现实秒）
	public float tornadoThemeVolume = 0.9f;      // 相对音量（再乘 weatherVolume 主音量）
	public string tornadoThemeFile = "storm_chase.wav";
	// 大尺度龙卷备选曲：龙卷核半径 ≥ tornadoThemeBigCoreM（默认 280m —— 楔形宽漏斗
	// 304-334m 命中；标准/绳状/陆龙卷 138-152m 走上面那首）时换成这首，提前量也改用
	// tornadoThemeBigLeadSec。
	public string tornadoThemeBigFile = "storm_chase_big.wav";
	public float tornadoThemeBigLeadSec = 19f;
	public float tornadoThemeBigCoreM = 280f;
	// 时间加速保护：倍率高于该值不放预警曲（快进时一个接一个触发会很吵，而且那时
	// 玩家并没在看龙卷）；提前量本身已按现实秒换算，不受加速影响。
	public float tornadoThemeMaxWarp = 10f;
	// 曲长 > 遭遇时长时的提速上限（pitch，1.35 ≈ 快 35%，再高就明显变调了）。
	public float tornadoThemeMaxPitch = 1.35f;
	// 起播后的维持条件（原实现只看"逼近中"的 ETA，龙卷一越过玩家角度 ETA 变 -1 →
	// 音乐在最刺激的抵达瞬间被掐掉）：龙卷还在 keepM 米内 或 起播不足 minHoldSec
	// 就继续放；两者都不满足才淡出。fadeSec = 淡入淡出时长。
	public float tornadoThemeKeepM = 1500f;      // 维持半径（米）
	public float tornadoThemeMinHoldSec = 20f;   // 最短播放时长（秒，防遭遇中途被切）
	public float tornadoThemeFadeSec = 2.5f;     // 淡入/淡出时长（秒）

	// mod 目录（Load 时记下）：音频等资源文件按它定位（TyphoonConfig.Folder + 文件名）。
	public static string Folder;

	public static void Load(string modFolder)
	{
		if (string.IsNullOrEmpty(modFolder))
		{
			return;
		}
		Folder = modFolder;
		path = Path.Combine(modFolder, "typhoon_config.json");
		try
		{
			if (File.Exists(path))
			{
				TyphoonConfig typhoonConfig = JsonConvert.DeserializeObject<TyphoonConfig>(File.ReadAllText(path));
				if (typhoonConfig != null)
				{
					// 结构整理：原在此处强行覆写 topHeight* 三个死字段以中和旧 json 的
					// 云顶模型参数；字段已删除（云顶模型现在完全由 TypeSpec.htopFrac +
					// 强度映射决定），旧 json 键被 Newtonsoft 直接忽略。
					// 一次性迁移（cfgVersion<3）：自然生成间隔由"现实秒"改为"游戏秒"口径，
					// 旧 json 的 30/70 秒在新口径下等于每 30-70 游戏秒生成一个风暴（过密）
					// → 重置为新默认 1-3 游戏小时。
					if (typhoonConfig.cfgVersion < 3)
					{
						typhoonConfig.naturalSpawnMinSec = 3600f;
						typhoonConfig.naturalSpawnMaxSec = 10800f;
						typhoonConfig.cfgVersion = 3;
					}
					I = typhoonConfig;
				}
			}
			File.WriteAllText(path, JsonConvert.SerializeObject((object)I, (Formatting)1));
		}
		catch (Exception ex)
		{
			Debug.LogWarning((object)("[Typhoon] config: " + ex.Message));
		}
	}

	// 设置页保存（用户：在设置里加入自己的设置项）：设置页改动字段后写回 json。
	public static void Save()
	{
		try
		{
			if (!string.IsNullOrEmpty(path))
			{
				File.WriteAllText(path, JsonConvert.SerializeObject((object)I, (Formatting)1));
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning((object)("[Typhoon] config save: " + ex.Message));
		}
	}
}
