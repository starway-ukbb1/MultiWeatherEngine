using System;
using SFS.UI.ModGUI;
using ModGUIElement = SFS.UI.ModGUI.GUIElement;
using UITools;
using UnityEngine;

namespace MultiWeatherEngine;

// SFS 设置内嵌配置页（用户：学习 BuildSettings/MenuRestyler 在设置里加入
// 自己的设置项）：走 UITools 的 ConfigurationMenu（Settings -> Mods Settings -> 星程气象）。
// 结构参考 MenuRestyler.RestylerSettings：ConfigurationMenu.Add(title, tabs) + Builder 构建。
// 依赖：Mods/UITools（MenuRestyler 同款），未装或加载失败时 try-catch 静默降级（HUD/配置不变）。
public static class TyphoonSettings
{
	public static void Setup()
	{
		try
		{
			ConfigurationMenu.Add("星程气象", new (string, Func<Transform, GameObject>)[]
			{
				("通用", (Transform parent) => BuildTab(parent, GeneralTab)),
				("粒子", (Transform parent) => BuildTab(parent, ParticleTab)),
				("风场", (Transform parent) => BuildTab(parent, WindTab))
			});
			Debug.Log("[Typhoon] 设置页已注册 (Settings -> Mods Settings -> 星程气象)");
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[Typhoon] 设置页不可用（需 UITools mod）: " + ex.Message);
		}
	}

	private static GameObject BuildTab(Transform parent, Action<Box, int> body)
	{
		Vector2Int contentSize = ConfigurationMenu.ContentSize;
		Box box = Builder.CreateBox(parent, contentSize.x, contentSize.y, 0, 0, 0.3f);
		box.CreateLayoutGroup((SFS.UI.ModGUI.Type)0, (TextAnchor)1, 14f, new RectOffset(15, 15, 12, 12), true);
		int w = contentSize.x - 60;
		try
		{
			body(box, w);
		}
		catch (Exception ex)
		{
			Builder.CreateLabel(box, w, 40, 0, 0, "UI error: " + ex.Message);
		}
		return box.gameObject;
	}

	// -- 行控件：标签 + 数值输入 / 开关按钮 ----

	private static void Section(Box box, int w, string title)
	{
		Builder.CreateLabel(box, w, 26, 0, 0, title);
		Builder.CreateSeparator(box, w - 20, 0, 0);
	}

	private static void NumRow(Box box, int w, string name, double value, float step, Action<float> set)
	{
		Builder.CreateLabel(box, w - 200, 30, 0, 0, name);
		NumberInput ni = UIToolsBuilder.CreateNumberInput(box, 200, 30, (float)value, step);
		ni.OnValueChangedEvent += delegate (float v)
		{
			try
			{
				set(v);
				TyphoonConfig.Save();
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Typhoon] 设置项写入失败 " + name + ": " + ex.Message);
			}
		};
	}

	// fix2 — 带 min/max 范围约束的 NumRow（NumberInput 无内置范围限制，
	// 用户可输入负数/超大值）。回写 ni.Value 会递归触发 OnValueChangedEvent（setter
	// 无条件调 OnValueChanged → 爆栈），用 updating 标志防重入：回写时跳过回调，
	// 回写完毕再手动 set + Save。
	private static void ClampedNumRow(Box box, int w, string name, double value, float step, float min, float max, Action<float> set)
	{
		Builder.CreateLabel(box, w - 200, 30, 0, 0, name);
		NumberInput ni = UIToolsBuilder.CreateNumberInput(box, 200, 30, Mathf.Clamp((float)value, min, max), step);
		bool updating = false;
		ni.OnValueChangedEvent += delegate (float v)
		{
			if (updating)
			{
				return;
			}
			try
			{
				float clamped = Mathf.Clamp(v, min, max);
				if (Mathf.Abs(clamped - v) > 0.0001f)
				{
					// 输入越界 → 回写显示为钳位后的值（用户看到 -5 → 自动变 0）
					updating = true;
					ni.Value = clamped;
					updating = false;
				}
				set(clamped);
				TyphoonConfig.Save();
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Typhoon] 设置项写入失败 " + name + ": " + ex.Message);
			}
		};
	}

	private static void ToggleRow(Box box, int w, string name, Func<bool> get, Action<bool> set)
	{
		Button b = Builder.CreateButton(box, w, 34, 0, 0, null, name + ": " + (get() ? "开" : "关"));
		Button btn = b;
		btn.OnClick += delegate
		{
			bool v = !get();
			set(v);
			btn.Text = name + ": " + (v ? "开" : "关");
			TyphoonConfig.Save();
		};
	}

	// -- Tab 内容 ----

	private static void GeneralTab(Box box, int w)
	{
		Section(box, w, "界面");
		ToggleRow(box, w, "HUD 显示", () => TyphoonConfig.I.hud, delegate (bool v) { TyphoonConfig.I.hud = v; });
		// 地图标记（M 地图视图显示风暴）
		ToggleRow(box, w, "地图标记风暴", () => TyphoonConfig.I.mapMarkers, delegate (bool v) { TyphoonConfig.I.mapMarkers = v; });
		ToggleRow(box, w, "风暴视觉效果", () => TyphoonConfig.I.visuals, delegate (bool v) { TyphoonConfig.I.visuals = v; });
		ToggleRow(box, w, "闪电", () => TyphoonConfig.I.lightning, delegate (bool v) { TyphoonConfig.I.lightning = v; });
		// 天气音效（程序化合成，无音频文件）
		Section(box, w, "音效");
		ToggleRow(box, w, "天气音效", () => TyphoonConfig.I.weatherAudio, delegate (bool v) { TyphoonConfig.I.weatherAudio = v; });
		// fix2 — ClampedNumRow 0-1：原 NumRow 无范围约束，可输入负数（内部 Clamp01
		// 到 0 → 静音，但 UI 仍显示负数 → 用户以为"音量没生效"）。ClampedNumRow 回写钳位值。
		ClampedNumRow(box, w, "音量 (0-1)", TyphoonConfig.I.weatherVolume, 0.05f, 0f, 1f, delegate (float v) { TyphoonConfig.I.weatherVolume = v; });
		Section(box, w, "相机");
		ToggleRow(box, w, "相机抖动", () => TyphoonConfig.I.cameraShake, delegate (bool v) { TyphoonConfig.I.cameraShake = v; });
		NumRow(box, w, "抖动强度 (0-3)", TyphoonConfig.I.cameraShakeScale, 0.1f, delegate (float v) { TyphoonConfig.I.cameraShakeScale = Mathf.Clamp(v, 0f, 3f); });
		Section(box, w, "召唤");
		NumRow(box, w, "召唤偏移 (米)", TyphoonConfig.I.spawnLeadDistanceMeters, 1000f, delegate (float v) { TyphoonConfig.I.spawnLeadDistanceMeters = Mathf.Max(1000f, v); });
		// 等级上限 + 消散产物开关（等级上限与消散产物讨论）：关 limitMaxCategory =
		// chaos 模式（所有系统可自然升到 6 级）；残余低压/泥雨/云底侵蚀默认开。运行时读取。
		Section(box, w, "等级与消散");
		ToggleRow(box, w, "等级上限（关=全 6 级）", () => TyphoonConfig.I.limitMaxCategory, delegate (bool v) { TyphoonConfig.I.limitMaxCategory = v; });
		ToggleRow(box, w, "台风残余低压", () => TyphoonConfig.I.residualLow, delegate (bool v) { TyphoonConfig.I.residualLow = v; });
		ToggleRow(box, w, "沙尘泥雨", () => TyphoonConfig.I.muddyRain, delegate (bool v) { TyphoonConfig.I.muddyRain = v; });
		ToggleRow(box, w, "云底侵蚀", () => TyphoonConfig.I.cloudBaseErosion, delegate (bool v) { TyphoonConfig.I.cloudBaseErosion = v; });
		// 附属现象现实配额（现实性审计）：关掉 = 旧的高频连续生成（龙卷恒满 4 个）。
		ToggleRow(box, w, "现象现实配额（龙卷/下暴按现实频率）", () => TyphoonConfig.I.phenomenaRealism, delegate (bool v) { TyphoonConfig.I.phenomenaRealism = v; });
		// 自然生成（"随机刷新"参数）：开关 + 间隔 + 距离，免去玩家困惑"风暴从哪来"。
		Section(box, w, "自然生成（随机刷新）");
		ToggleRow(box, w, "自然生成", () => TyphoonConfig.I.naturalSpawn, delegate (bool v) { TyphoonConfig.I.naturalSpawn = v; });
		// 间隔单位改为**游戏分钟**（内部字段仍是游戏秒）：时间加速倍率越高 → 游戏时间
		// 推进越快 → 生成同步快进。现实量级：一个 ~60km 半径区域内强对流生成一天数次。
		NumRow(box, w, "最小间隔 (游戏分钟)", TyphoonConfig.I.naturalSpawnMinSec / 60f, 5f, delegate (float v) { TyphoonConfig.I.naturalSpawnMinSec = Mathf.Clamp(v, 1f, 720f) * 60f; });
		NumRow(box, w, "最大间隔 (游戏分钟)", TyphoonConfig.I.naturalSpawnMaxSec / 60f, 5f, delegate (float v) { TyphoonConfig.I.naturalSpawnMaxSec = Mathf.Clamp(v, 1f, 1440f) * 60f; });
		NumRow(box, w, "生成距离 (km)", TyphoonConfig.I.naturalSpawnDistKm, 5f, delegate (float v) { TyphoonConfig.I.naturalSpawnDistKm = Mathf.Clamp(v, 15f, 500f); });
		// 预生成（进存档/换星球时给当前行星播种）
		Section(box, w, "预生成（进存档播种）");
		ToggleRow(box, w, "预生成", () => TyphoonConfig.I.preSpawnEnabled, delegate (bool v) { TyphoonConfig.I.preSpawnEnabled = v; });
		NumRow(box, w, "每行星数量", (float)TyphoonConfig.I.preSpawnCountPerPlanet, 1f, delegate (float v) { TyphoonConfig.I.preSpawnCountPerPlanet = Mathf.RoundToInt(Mathf.Clamp(v, 0f, 5f)); });
		NumRow(box, w, "台风概率 (%)", TyphoonConfig.I.preSpawnTyphoonChance * 100f, 5f, delegate (float v) { TyphoonConfig.I.preSpawnTyphoonChance = Mathf.Clamp(v / 100f, 0f, 1f); });
		Section(box, w, "影响");
		ToggleRow(box, w, "影响宇航员", () => TyphoonConfig.I.affectAstronauts, delegate (bool v) { TyphoonConfig.I.affectAstronauts = v; });
		// 风暴卷起的障碍物（树/石）：真实障碍物，可撞毁火箭（游戏原生 RocketCollision）
		ToggleRow(box, w, "卷起树木/石头（可撞击火箭）", () => TyphoonConfig.I.stormDebris, delegate (bool v) { TyphoonConfig.I.stormDebris = v; });
		NumRow(box, w, "障碍物上限/风暴", (float)TyphoonConfig.I.debrisMaxPerStorm, 2f, delegate (float v) { TyphoonConfig.I.debrisMaxPerStorm = Mathf.Clamp(Mathf.RoundToInt(v), 0, 40); });
		ToggleRow(box, w, "障碍物可摧毁火箭", () => TyphoonConfig.I.debrisHurtsRockets, delegate (bool v) { TyphoonConfig.I.debrisHurtsRockets = v; });
		ToggleRow(box, w, "龙卷内部能见度骤降", () => TyphoonConfig.I.tornadoObscure, delegate (bool v) { TyphoonConfig.I.tornadoObscure = v; });
		ToggleRow(box, w, "云/雨包裹能见度骤降（逐型对标现实）", () => TyphoonConfig.I.rainObscure, delegate (bool v) { TyphoonConfig.I.rainObscure = v; });
		// 快捷键说明（只读，玩家易忽略）
		Section(box, w, "快捷键");
		Builder.CreateLabel(box, w, 22, 0, 0, "F6 气象菜单  F7 解散选中  F8 强度+1");
		Builder.CreateLabel(box, w, 22, 0, 0, "F9 系统面板  Shift+F7 隐藏 HUD");
	}

	private static void ParticleTab(Box box, int w)
	{
		Section(box, w, "粒子数量（改后立即生效）");
		// 待办1：粒子数设置热更新——canopy/cloud 由 StormRenderer LateUpdate 每帧
		// 检测 puffs.Length 变化自动 Rebuild；rainDrops 只影响 drops 数组（Rebuild 内分配），
		// 需显式置 RebuildPuffsFlag 触发（StormRenderer LateUpdate 检测 flag 重建）。
		NumRow(box, w, "云顶薄云粒子", TyphoonConfig.I.canopyPuffs, 100f, delegate (float v) { TyphoonConfig.I.canopyPuffs = Mathf.Clamp((int)v, 0, 4000); });
		NumRow(box, w, "主体云粒子", TyphoonConfig.I.cloudPuffs, 100f, delegate (float v) { TyphoonConfig.I.cloudPuffs = Mathf.Clamp((int)v, 0, 4000); });
		NumRow(box, w, "雨滴数", TyphoonConfig.I.rainDrops, 100f, delegate (float v) { TyphoonConfig.I.rainDrops = Mathf.Clamp((int)v, 0, 12000); RequestRebuild(); });
		Section(box, w, "不透明度");
		NumRow(box, w, "云不透明度 (0-1)", TyphoonConfig.I.cloudOpacity, 0.05f, delegate (float v) { TyphoonConfig.I.cloudOpacity = Mathf.Clamp(v, 0f, 1f); });
		NumRow(box, w, "雨不透明度 (0-1)", TyphoonConfig.I.rainOpacity, 0.05f, delegate (float v) { TyphoonConfig.I.rainOpacity = Mathf.Clamp(v, 0f, 1f); });
		NumRow(box, w, "雨尺度", TyphoonConfig.I.rainScale, 0.1f, delegate (float v) { TyphoonConfig.I.rainScale = Mathf.Clamp(v, 0.1f, 5f); });
	}

	// 待办1：请求所有活跃风暴重建粒子（雨滴数变化后 drops 数组需重新分配）。
	private static void RequestRebuild()
	{
		try
		{
			if (TyphoonManager.main != null && TyphoonManager.systems != null)
			{
				foreach (WeatherSystem s in TyphoonManager.systems)
				{
					if (s != null)
					{
						s.RebuildPuffsFlag = true;
					}
				}
			}
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[Typhoon] rebuild request: " + ex.Message);
		}
	}

	private static void WindTab(Box box, int w)
	{
		Section(box, w, "风场");
		NumRow(box, w, "阵风幅度 (0-1)", TyphoonConfig.I.gustAmplitude, 0.05f, delegate (float v) { TyphoonConfig.I.gustAmplitude = Mathf.Clamp(v, 0f, 1f); });
		NumRow(box, w, "外流比例 (0-1)", TyphoonConfig.I.outflowFraction, 0.05f, delegate (float v) { TyphoonConfig.I.outflowFraction = Mathf.Clamp(v, 0f, 1f); });
		Section(box, w, "天空");
		NumRow(box, w, "天空覆盖度 (0-1)", TyphoonConfig.I.skyOpacity, 0.05f, delegate (float v) { TyphoonConfig.I.skyOpacity = Mathf.Clamp(v, 0f, 1f); });
	}
}
