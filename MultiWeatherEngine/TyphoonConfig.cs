using System;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace MultiWeatherEngine;

public class TyphoonConfig
{
	public static TyphoonConfig I = new TyphoonConfig();

	private static string path;

	public double eyewallRadiusAsPlanetFraction = 0.005;

	public double eyewallRadiusMinMeters = 2500.0;

	public double eyewallRadiusMaxMeters = 60000.0;

	public double outerRadiusMultiplier = 9.0;

	// v1.0.25 — 台风云顶高度模型（SFS 大气 ≈ 现实 × 3/10）：
	//   现实台风 15-20 km → SFS 4.5-6 km（一般）
	//   极端台风 25-30 km → SFS 7.5-9 km（极端，与强度相关）
	// 强度映射：cat 0-4 → min~mid（一般），cat 5-6 → mid~max（极端）。
	public double topHeightMinMeters = 4500.0;

	public double topHeightMidMeters = 6000.0;

	public double topHeightMaxMeters = 9000.0;

	public double updraftFraction = 0.32;

	public double outflowFraction = 0.75;

	public double gustAmplitude = 0.35;

	public double driftSpeed = 9.0;

	public double spawnLeadDistanceMeters = 60000.0;

	public bool cameraShake = true;

	public double cameraShakeScale = 1.0;

	public bool affectAstronauts = true;

	public bool visuals = true;

	public int canopyPuffs = 1500;

	public int cloudPuffs = 1100;

	public int rainDrops = 1700;

	public double cloudOpacity = 0.95;

	public double rainOpacity = 0.85;

	public double rainScale = 1.0;

	public double skyOpacity = 0.78;

	public bool lightning = true;

	public bool hud = true;

	// v2.4.5 — 等级上限 + 消散产物开关（等级上限与消散产物讨论）：limitMaxCategory 关 =
	// chaos 模式（所有系统可自然升到 6 级）；residualLow 台风残余低压（逗点化+延长）；
	// muddyRain 泥雨（沙尘与雨混合染色）；cloudBaseErosion 云底侵蚀（消散从下往上散）。
	// 运行时读取免重启（逻辑/渲染每帧读字段）。
	public bool limitMaxCategory = true;

	public bool residualLow = true;

	public bool muddyRain = true;

	public bool cloudBaseErosion = true;

	public static void Load(string modFolder)
	{
		if (string.IsNullOrEmpty(modFolder))
		{
			return;
		}
		path = Path.Combine(modFolder, "typhoon_config.json");
		try
		{
			if (File.Exists(path))
			{
				TyphoonConfig typhoonConfig = JsonConvert.DeserializeObject<TyphoonConfig>(File.ReadAllText(path));
				if (typhoonConfig != null)
				{
					// v1.0.25 — 云顶高度模型改为内置强度映射，旧 json(3000/20000/0.15 大气分数)会破坏映射，强制用新默认。
					typhoonConfig.topHeightMinMeters = 4500.0;
					typhoonConfig.topHeightMidMeters = 6000.0;
					typhoonConfig.topHeightMaxMeters = 9000.0;
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

	// v2.3.9 — 设置页保存（用户：在设置里加入自己的设置项）：设置页改动字段后写回 json。
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
