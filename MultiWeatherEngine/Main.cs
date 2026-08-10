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

	public override string ModVersion => "v2.2.1";

	// v2.2.1 — 预生成 + 地图标记（研究：反编译 SFS.World.Maps 确认 ElementDrawer 通路）。
	public override string Description => "多天气系统引擎（星程气象）：SFS 7 类天气系统（台风/超级单体/飑线/MCS/多单体/雷暴/沙尘暴）自然生成、演化、移动、合并与类型转变，真实风场/降雨/龙卷/下击暴流/沙尘暴，全程可交互。能量制生命周期按现实气象尺度（台风 5 天/单体 45min，时间加速快进）、海温场+台风冷尾流（挖冷水自我削弱）、眼壁置换 EWRC、龙卷 8 类型、消散残余低压/云底侵蚀/沙尘泥雨、SFS 设置页可调。v2.2.1 新增：进存档预生成当前行星风暴（世界一进去就是活的，数量/台风概率可调）+ 地图视图（M）风暴标记（点+文字，颜色=强度）。本 mod 由 AI 辅助设计制作。";

	public override void Early_Load()
	{
		//IL_000b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Expected O, but got Unknown
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
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Expected O, but got Unknown
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Expected O, but got Unknown
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
			// v2.3.9 — 注册 SFS 设置页（Settings -> Mods Settings -> 星程气象，需 UITools）。
			TyphoonSettings.Setup();
		}
		catch (Exception ex)
		{
			Debug.LogWarning((object)("[Typhoon] settings page: " + ex.Message));
		}
		GameObject val = new GameObject("Typhoon Manager");
		Object.DontDestroyOnLoad((Object)val);
		val.AddComponent<TyphoonManager>();
		Debug.Log((object)("[Typhoon] " + ((Mod)this).ModVersion + " loaded. Press F6 in a world to open the weather menu."));
	}
}
