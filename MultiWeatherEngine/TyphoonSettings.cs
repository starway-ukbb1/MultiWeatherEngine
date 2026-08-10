using System;
using SFS.UI.ModGUI;
using ModGUIElement = SFS.UI.ModGUI.GUIElement;
using UITools;
using UnityEngine;

namespace MultiWeatherEngine;

// v2.3.9 — SFS 设置内嵌配置页（用户：学习 BuildSettings/MenuRestyler 在设置里加入
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

	// ---- 行控件：标签 + 数值输入 / 开关按钮 ----

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

	// ---- Tab 内容 ----

	private static void GeneralTab(Box box, int w)
	{
		Section(box, w, "界面");
		ToggleRow(box, w, "HUD 显示", () => TyphoonConfig.I.hud, delegate (bool v) { TyphoonConfig.I.hud = v; });
		ToggleRow(box, w, "风暴视觉效果", () => TyphoonConfig.I.visuals, delegate (bool v) { TyphoonConfig.I.visuals = v; });
		ToggleRow(box, w, "闪电", () => TyphoonConfig.I.lightning, delegate (bool v) { TyphoonConfig.I.lightning = v; });
		Section(box, w, "相机");
		ToggleRow(box, w, "相机抖动", () => TyphoonConfig.I.cameraShake, delegate (bool v) { TyphoonConfig.I.cameraShake = v; });
		NumRow(box, w, "抖动强度 (0-3)", TyphoonConfig.I.cameraShakeScale, 0.1f, delegate (float v) { TyphoonConfig.I.cameraShakeScale = Mathf.Clamp(v, 0f, 3f); });
		Section(box, w, "召唤");
		NumRow(box, w, "召唤偏移 (米)", TyphoonConfig.I.spawnLeadDistanceMeters, 1000f, delegate (float v) { TyphoonConfig.I.spawnLeadDistanceMeters = Mathf.Max(1000f, v); });
		// v2.4.5 — 等级上限 + 消散产物开关（等级上限与消散产物讨论）：关 limitMaxCategory =
		// chaos 模式（所有系统可自然升到 6 级）；残余低压/泥雨/云底侵蚀默认开。运行时读取。
		Section(box, w, "等级与消散");
		ToggleRow(box, w, "等级上限（关=全 6 级）", () => TyphoonConfig.I.limitMaxCategory, delegate (bool v) { TyphoonConfig.I.limitMaxCategory = v; });
		ToggleRow(box, w, "台风残余低压", () => TyphoonConfig.I.residualLow, delegate (bool v) { TyphoonConfig.I.residualLow = v; });
		ToggleRow(box, w, "沙尘泥雨", () => TyphoonConfig.I.muddyRain, delegate (bool v) { TyphoonConfig.I.muddyRain = v; });
		ToggleRow(box, w, "云底侵蚀", () => TyphoonConfig.I.cloudBaseErosion, delegate (bool v) { TyphoonConfig.I.cloudBaseErosion = v; });
		// v2.2 — 自然生成（"随机刷新"参数）：开关 + 间隔 + 距离，免去玩家困惑"风暴从哪来"。
		Section(box, w, "自然生成（随机刷新）");
		ToggleRow(box, w, "自然生成", () => TyphoonConfig.I.naturalSpawn, delegate (bool v) { TyphoonConfig.I.naturalSpawn = v; });
		NumRow(box, w, "最小间隔 (秒)", TyphoonConfig.I.naturalSpawnMinSec, 5f, delegate (float v) { TyphoonConfig.I.naturalSpawnMinSec = Mathf.Clamp(v, 5f, 600f); });
		NumRow(box, w, "最大间隔 (秒)", TyphoonConfig.I.naturalSpawnMaxSec, 5f, delegate (float v) { TyphoonConfig.I.naturalSpawnMaxSec = Mathf.Clamp(v, 10f, 1200f); });
		NumRow(box, w, "生成距离 (km)", TyphoonConfig.I.naturalSpawnDistKm, 5f, delegate (float v) { TyphoonConfig.I.naturalSpawnDistKm = Mathf.Clamp(v, 15f, 500f); });
		Section(box, w, "影响");
		ToggleRow(box, w, "影响宇航员", () => TyphoonConfig.I.affectAstronauts, delegate (bool v) { TyphoonConfig.I.affectAstronauts = v; });
		// v2.2 — 快捷键说明（只读，玩家易忽略）
		Section(box, w, "快捷键");
		Builder.CreateLabel(box, w, 22, 0, 0, "F6 气象菜单  F7 解散选中  F8 强度+1");
		Builder.CreateLabel(box, w, 22, 0, 0, "F9 系统面板  Shift+F7 隐藏 HUD");
	}

	private static void ParticleTab(Box box, int w)
	{
		Section(box, w, "粒子数量（改后立即生效）");
		// v2.3.10 — 待办1：粒子数设置热更新——canopy/cloud 由 StormRenderer LateUpdate 每帧
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

	// v2.3.10 — 待办1：请求所有活跃风暴重建粒子（雨滴数变化后 drops 数组需重新分配）。
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
