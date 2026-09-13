using System;
using System.Reflection;
using HarmonyLib;
using ModLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiWeatherEngine;

public class Main : Mod
{
	public static Main main;

	public static Harmony harmony;

	public override string ModNameID => "MultiWeatherEngine";

	public override string DisplayName => "多天气系统引擎";

	public override string Author => "星程（AI 制作）";

	public override string MinimumGameVersionNecessary => "1.6";

	public override string ModVersion => "v2.3.0";

	// 音效修复（音量设置真正生效+雷声跟随音量+范围衰减 2.5Rmax 平方）、
	// 地图标记改矩形（尺寸对应 Rmax）、HUD/菜单只在飞行场景显示（退出世界自动清场）、
	// 性能优化（风圈脏标记/静态数组/StringBuilder/去装箱/FindAnyObjectByType）。
	public override string Description => "多天气系统引擎（星程气象）：SFS 17 类天气系统（台风/超级单体/飑线/MCS/多单体/雷暴/沙尘暴 + 温带气旋/冬季风暴/冰暴/浓雾/大气河/尘卷风 + 火焰风暴/德雷科/极地涡旋/超级风暴）自然生成、演化、移动、合并与类型转变，真实风场/降雨/龙卷/下击暴流/沙尘暴，全程可交互。能量制生命周期按现实气象尺度（台风 5 天/单体 45min，时间加速快进）、海温场+台风冷尾流（挖冷水自我削弱）、眼壁置换 EWRC、龙卷 8 类型、消散残余低压/云底侵蚀/沙尘泥雨、SFS 设置页可调。v2.2.2 新增：进存档预生成当前行星风暴 + 地图视图（M）矩形轮廓标记（颜色=强度）+ 天气音效（程序化合成风声/雷声，音量可调）+ 面板仅飞行场景显示 + 全面性能优化。v2.2.3：性能（不可见风暴整体跳过、雨层隐藏不再清零上传、UV 一次性上传、back 网格动态预留、相机/材质去每帧重设、风采样热路径外提）+ 现实性审计修正（登陆风速衰减累积 bug、沙尘暴云顶 4km、单体/多单体峰值风对齐现实阵风、微下击暴流强度、EWRC 冷却 2 天、龙卷/下暴/阵风锋改现实配额）+ 结构整理（三阶段时长入类型表、删除 12 个死字段与粒子诊断日志）。v2.2.16 起：风暴雷达重做（玩家居中/发光风暴团/龙卷红点）+ 龙卷预报并入雷达（类型/EF/距离/方位/现实秒 ETA/潜势）+ 指挥中心排版修复 + UI 性能优化；修复自然生成概率门恒不成立（自然龙卷/下暴/阵风锋/闪电与类型转变从未触发）；龙卷风场尺度改为漏斗核半径（修隔很远也被风吸、判定范围与实际风场错位），并给龙卷尺度加 4km 基准钳制（修中尺度母体把龙卷吹/画成 1km 宽的怪物）；EF 改按龙卷自身风速；类型转变修复默认值退化（MCS→单体）并加淡出-中点换型-淡入动画；下击暴流视觉提升（下冲锥/亮柱/三层出流锋/触地尘爆）；龙卷预警曲（社区公用 Storm Chaser 风格曲目，15 现实秒起播、单曲循环撑满遭遇，时间加速过载保护）。v2.3.0 新增：6 类现实天气系统（温带气旋/冬季风暴/冰暴/浓雾/大气河/尘卷风），含固态降水（雪/冰粒）渲染、按地形/相态驱动的自然生成与预生成；等级显示规范化（HUD/菜单改用各类型专属分级名，不再全部显示 CAT-/C）；龙卷刷新现实性修正（避开母体正中心、刷新频率随母体强度、自然龙卷不再被切档续命）；龙卷预警曲改为随仓库附带的社区公用「风暴拦截者」风格单曲并循环播放，ETA 计入飞船自身移动、加 12km 高度门；渲染对齐（温带气旋逗点云盾、大气河线状剖面、浓雾屏幕能见度骤降）、物理对齐（龙卷独立平流）、新增 4 类理论/极端风暴（火焰风暴/德雷科/极地涡旋/超级风暴）、超级飓风 Hypercane（理论严格 SST≈48°C + 指挥中心手动：满档台风 F8 再强化，峰值 ×2.85 ≈ 800 km/h）。本 mod 由 AI 辅助设计制作。";

	public override void Early_Load()
	{
		// IL_000b: Unknown result type (might be due to invalid IL or missing references)
		// IL_0015: Expected O, but got Unknown
		main = this;
		try
		{
			harmony = new Harmony("workbuddy.multiweatherengine");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
			Debug.Log((object)"[Typhoon] Harmony patches applied.");
		}
		catch (Exception ex)
		{
			Debug.LogError((object)("[Typhoon] Harmony patch failed: " + ex));
		}
	}

	public override void Load()
	{
		// IL_0031: Unknown result type (might be due to invalid IL or missing references)
		// IL_0036: Unknown result type (might be due to invalid IL or missing references)
		// IL_0038: Expected O, but got Unknown
		// IL_0037: Unknown result type (might be due to invalid IL or missing references)
		// IL_0041: Expected O, but got Unknown
		try
		{
			TyphoonConfig.Load(((Mod)this).ModFolder);
		}
		catch (Exception ex)
		{
			Debug.LogError((object)("[Typhoon] config load failed: " + ex));
		}
		try
		{
			// 注册 SFS 设置页（Settings -> Mods Settings -> 星程气象，需 UITools）。
			TyphoonSettings.Setup();
		}
		catch (Exception ex)
		{
			Debug.LogWarning((object)("[Typhoon] settings page: " + ex.Message));
		}
		try
	{
		StormShaderCloud.TryLoad(((Mod)this).ModFolder);
	}
	catch (Exception ex)
	{
		Debug.LogError((object)("[Typhoon] shader bundle load failed: " + ex));
	}
	GameObject val = new GameObject("Typhoon Manager");
		Object.DontDestroyOnLoad((Object)val);
		val.AddComponent<TyphoonManager>();
		Debug.Log((object)("[Typhoon] " + ((Mod)this).ModVersion + " loaded. Press F6 in a world to open the weather menu."));
	}
}
