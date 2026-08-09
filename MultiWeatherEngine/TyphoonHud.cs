using System;
using SFS.Variables;
using SFS.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiWeatherEngine;

// v2.0.10 — HUD 重构：底部"活跃天气系统"面板（F9/点击标题 展开收起、点击行选中）+
// 左上精简条（选中系统详情 + 玩家飞行数据 + 视距/键位）。
public class TyphoonHud : MonoBehaviour
{
	private GUIStyle box;

	private GUIStyle label;

	private GUIStyle small;

	private GUIStyle header;

	// v2.3.8 优化#2 — GUIStyle 缓存（原 OnGUI 每帧 new GUIStyle(GUI.skin.button/box) →
	// 面板开着就每帧 GC 分配。首次使用时创建，之后复用；样式值固定不变）。
	private GUIStyle hdrStyle;

	private GUIStyle rowSelStyle;

	private GUIStyle rowStyle;

	private Texture2D bg;

	private Texture2D bar;

	private Texture2D marker;

	// v2.3.9 — 左下角面板半透明（用户要求）：底部监控面板用半透明底（alpha 0.55，
	// 透出地表/风暴不遮挡视野），详情/待机面板保持 0.92（信息可读性优先）。
	private Texture2D bottomBg;

	private GUIStyle bottomBox;

	private bool init;

	private WeatherSystem S => TyphoonManager.main != null ? TyphoonManager.main.selected : null;

	private void Setup()
	{
		init = true;
		// v2.0.13 — 深空色系：深蓝黑底 + 星云蓝字
		bg = Solid(new Color(0.016f, 0.024f, 0.05f, 0.92f));
		bar = Solid(new Color(0.5f, 0.75f, 1f, 0.14f));
		marker = Solid(new Color(0.72f, 0.86f, 1f));
		box = new GUIStyle();
		box.normal.background = bg;
		box.padding = new RectOffset(12, 12, 10, 10);
		box.font = TyphoonManager.CnFont();   // v2.3.8.6 — 中文字体（原方块）
		// v2.3.9 — 底部面板半透明 style（独立于 box，不影响详情/待机）
		bottomBg = Solid(new Color(0.016f, 0.024f, 0.05f, 0.55f));
		bottomBox = new GUIStyle(box);
		bottomBox.normal.background = bottomBg;
		header = new GUIStyle();
		header.fontSize = 15;
		header.fontStyle = (FontStyle)1;
		header.font = TyphoonManager.CnFont();
		header.normal.textColor = new Color(0.74f, 0.87f, 1f);
		label = new GUIStyle();
		label.fontSize = 13;
		label.font = TyphoonManager.CnFont();
		label.normal.textColor = new Color(0.78f, 0.87f, 1f);
		small = new GUIStyle();
		small.fontSize = 11;
		small.font = TyphoonManager.CnFont();
		small.normal.textColor = new Color(0.52f, 0.64f, 0.8f);
	}

	private static Texture2D Solid(Color c)
	{
		Texture2D val = new Texture2D(1, 1);
		val.SetPixel(0, 0, c);
		val.Apply();
		return val;
	}

	private void OnGUI()
	{
		if (!init)
		{
			Setup();
		}
		TyphoonManager main = TyphoonManager.main;
		if (main == null)
		{
			return;
		}
		if (TyphoonManager.GetPlayerLocation() == null && TyphoonManager.systems.Count == 0)
		{
			return;
		}
		if (TyphoonConfig.I.hud)
		{
			DrawBottomPanel(main);
			DrawTopBar(main);
		}
		// v2.0.98 — F1 参数编辑面板已删除（用户要求：f1 参数编辑菜单删除）。
	}

	// v2.0.98 — F1 参数编辑面板已删除（用户要求）。原 DrawWindGainPanel 移除。

	// ===== 底部：活跃天气系统面板（v2.0.13 左下角 · 缩短 · 可展开收起，点击行选中） =====
	private void DrawBottomPanel(TyphoonManager main)
	{
		int n = TyphoonManager.systems.Count;
		bool exp = TyphoonManager.panelExpanded;
		// v2.3.8.7 — 排版：宽上限 560→640（行文字含 类型/强度/阶段/现象/峰风/半径/云顶/距离
		// 原 560px 裁掉末尾距离；加宽 + 行文本紧凑化后完整显示）
		float bw = Mathf.Min((float)Screen.width - 30f, 640f);
		float bh = exp ? (38f + (float)n * 24f + 8f) : 38f;
		if (n == 0)
		{
			bh = 38f;
		}
		float bx = 14f;
		float by = (float)Screen.height - bh - 14f;
		Rect panel = new Rect(bx, by, bw, bh);
		GUI.Box(panel, GUIContent.none, bottomBox);   // v2.3.9 — 半透明底（用户要求）
		float x = bx + 10f;
		float y = by + 6f;
		// v2.3.8 优化#2 — GUIStyle 缓存（首次创建，样式固定）
		if (hdrStyle == null)
		{
			hdrStyle = new GUIStyle(GUI.skin.button);
			hdrStyle.alignment = TextAnchor.MiddleLeft;
			hdrStyle.fontSize = 13;
			hdrStyle.fontStyle = FontStyle.Bold;
			hdrStyle.font = TyphoonManager.CnFont();
			hdrStyle.normal.textColor = new Color(0.72f, 0.86f, 1f);
		}
		GUIStyle hdr = hdrStyle;
		if (GUI.Button(new Rect(x, y, bw - 20f, 24f), "活跃天气系统 (" + n + ")   " + (exp ? "▼ 点击收起" : "▶ 点击展开"), hdr))
		{
			TyphoonManager.panelExpanded = !TyphoonManager.panelExpanded;
		}
		if (n == 0)
		{
			GUI.Label(new Rect(x, y + 28f, bw - 20f, 18f), "无活跃系统 — [F6] 打开气象菜单召唤", small);
			return;
		}
		if (!exp)
		{
			string sum = "";
			for (int i = 0; i < n; i++)
			{
				WeatherSystem s = TyphoonManager.systems[i];
				if (s == null || !s.active)
				{
					continue;
				}
				if (sum.Length > 0)
				{
					sum += "  ·  ";
				}
				sum += WeatherSystem.TypeName(s.type) + " " + WeatherSystem.StrengthName(s.type, s.category);
			}
			GUI.Label(new Rect(x, y + 28f, bw - 20f, 18f), sum, small);
			return;
		}
		y += 30f;
		for (int i = 0; i < n; i++)
		{
			WeatherSystem s = TyphoonManager.systems[i];
			if (s == null || !s.active)
			{
				continue;
			}
			bool isSel = main.selected == s;
			// v2.3.8 优化#2 — GUIStyle 两态缓存（选中/未选中各一，首次创建复用）
			if (rowSelStyle == null)
			{
				rowSelStyle = new GUIStyle(GUI.skin.box);
				rowSelStyle.fontSize = 12;
				rowSelStyle.alignment = TextAnchor.MiddleLeft;
				rowSelStyle.font = TyphoonManager.CnFont();
				rowSelStyle.normal.textColor = new Color(0.58f, 0.88f, 1f);
			}
			if (rowStyle == null)
			{
				rowStyle = new GUIStyle(GUI.skin.button);
				rowStyle.fontSize = 12;
				rowStyle.alignment = TextAnchor.MiddleLeft;
				rowStyle.font = TyphoonManager.CnFont();
				rowStyle.normal.textColor = new Color(0.82f, 0.9f, 1f);
			}
			GUIStyle row = isSel ? rowSelStyle : rowStyle;
			string stage = s.stage == 0 ? "发展" : (s.stage == 1 ? "成熟" : "消散");
			// v2.3.0 — 能量制显示（用户：不写死寿命，改能量制 + HUD 阶段名+能量）：
			// 能量百分比让生命周期永远可感知——下降 = 快消散了，任何时间倍率都看得见。
			// v2.2（专项 A）— 阶段内重定标（80 是成熟峰，全局 0-100 标尺误导玩家以为能
			// 充满）：发展 (e-55)/25、成熟 e/80（峰值正好 100%）、消散 e/30。
			string stageTxt;
			if (s.stage == 0)
			{
				stageTxt = "发展 能量 " + (WeatherSystem.Clamp01((s.energy - 55.0) / 25.0) * 100.0).ToString("0") + "%";
			}
			else if (s.stage == 1)
			{
				stageTxt = "成熟 能量 " + (WeatherSystem.Clamp01(s.energy / 80.0) * 100.0).ToString("0") + "%  升↗" + (s.naturalProgress * 100.0).ToString("0") + "%";
			}
			else
			{
				stageTxt = "消散 能量 " + (WeatherSystem.Clamp01(s.energy / 30.0) * 100.0).ToString("0") + "%";
			}
			string phen = "";
			if (s.tornadoes.Count > 0)
			{
				// v2.0.98 — 龙卷Lv 字样删除（用户要求）：只显示数量，强度跟母体。
				phen += " 龙卷×" + s.tornadoes.Count;
			}
			if (s.downbursts.Count > 0)
			{
				phen += " 下暴×" + s.downbursts.Count;
			}
			if (s.gustFronts.Count > 0)          // v2.1.2
			{
				phen += " 阵风锋×" + s.gustFronts.Count;
			}
			if (s.lightningBursts.Count > 0)     // v2.1.2
			{
				phen += " 闪电×" + s.lightningBursts.Count;
			}
			// v2.4.5 — F8 超限标记：category > 类型自然上限（maxCat）时标 ⚠超限（god mode
			// 实验自由不强制回落，但明示玩家"这等级不是自然的"）。
			string over = (s.category > WeatherSystem.Spec[(int)s.type].maxCat) ? "⚠超限 " : "";
			string txt = (isSel ? "▶ " : "  ") + WeatherSystem.TypeName(s.type) + " " + GradeName(s)
				+ over + " [" + stageTxt + "]" + phen
				+ "  峰风 " + s.vmaxDisplay.ToString("0") + "m/s 半径 " + (s.Rmax / 1000.0).ToString("0.##") + "km 云顶 " + (s.Htop / 1000.0).ToString("0.0") + "km"   // v2.3.8.7 — 紧凑格式省宽
				+ DistTo(s);
			if (GUI.Button(new Rect(x, y, bw - 20f, 22f), txt, row))
			{
				main.selected = s;
				main.selectedType = (int)s.type;
			}
			y += 24f;
		}
	}

	// v2.4.5 — 等级名（含残余低压）：台风消散期逗点化明显（commaK>0.3，地球类）时
	// 等级名改"残余低压"（现实：台风消散=变性/残余低压，新闻"残余低压持续降雨"）；
	// 其余用类型分级名。
	private static string GradeName(WeatherSystem s)
	{
		if (s.type == StormType.Typhoon && s.commaK > 0.3 && s.atmoClass == 0)
		{
			return "残余低压";
		}
		return WeatherSystem.StrengthName(s.type, s.category);
	}

	private static string DistTo(WeatherSystem s)
	{
		try
		{			Location pl = TyphoonManager.GetPlayerLocation();
			if (pl == null || pl.planet == null || (Object)pl.planet != (Object)s.planet)
			{
				return "  其他星球";
			}
			double d = Math.Abs(WrapAngle(s.centerAngle - pl.position.AngleRadians)) * s.planet.Radius;
			return "  距 " + (d / 1000.0).ToString("0.0") + " km";
		}
		catch
		{
			return "";
		}
	}

	private static double WrapAngle(double a)
	{
		while (a > Math.PI)
		{
			a -= Math.PI * 2.0;
		}
		while (a < -Math.PI)
		{
			a += Math.PI * 2.0;
		}
		return a;
	}

	// ===== 顶部：选中系统详情 + 玩家飞行数据 + 视距/键位 =====
	private void DrawTopBar(TyphoonManager main)
	{
		WeatherSystem s = S;
		if (s == null || !s.active)
		{
			// v2.0.98 — HUD 下移（用户要求）+ 删视距/缩放空间字样。v2.3.8.7 — 宽度对齐详情面板 430。
			Rect v0 = new Rect(14f, 58f, 430f, 48f);
			GUI.Box(v0, GUIContent.none, box);
			GUI.Label(new Rect(28f, 64f, 300f, 20f), "TYPHOON — 待机  系统 " + TyphoonManager.systems.Count, header);
			GUI.Label(new Rect(28f, 86f, 400f, 18f), "[F6] 菜单  [Shift+F7] 隐藏", small);
			return;
		}
		float num = 370f;   // v2.3.8.7 — 排版修复：宽 322→430、高 330→370（雅黑字体行高比 Futura 大 + 峰值风拆两行）
		// v2.0.98 — HUD 下移一些（用户要求）：y 14 → 58，避开 SFS 原版左上 UI。
		Rect val = new Rect(14f, 58f, 430f, num);
		GUI.Box(val, GUIContent.none, box);
		float num2 = val.x + 14f;
		float num3 = val.y + 10f;
		float rowW = 402f;   // v2.3.8.7 — 行宽统一（面板 430 - 左右 padding 28）
		Color val3 = (GUI.color = Category.Tint[s.category]);
		// v2.0.85 — 多实例：显示数量（龙卷×N 下暴×M）。
		string phen = (s.tornadoes.Count > 0 ? "  龙卷×" + s.tornadoes.Count : "") + (s.downbursts.Count > 0 ? "  下暴×" + s.downbursts.Count : "");
		string over2 = (s.category > WeatherSystem.Spec[(int)s.type].maxCat) ? "⚠超限 " : "";   // v2.4.5 F8 超限标记
		GUI.Label(new Rect(num2, num3, rowW, 20f), "◎ " + WeatherSystem.TypeName(s.type) + "  " + GradeName(s) + over2 + "  [" + (s.stage == 0 ? "发展" : (s.stage == 1 ? "成熟" : "消散")) + "]" + phen, header);
		GUI.color = Color.white;
		num3 += 22f;
		// v2.3.8.7 — 排版：峰值风行拆两行（原一行 322px 塞 峰值风+km/h+云底+云顶+移速 ~400px 溢出裁切）
		GUI.Label(new Rect(num2, num3, rowW, 18f), "峰值风 " + s.EyewallWind.ToString("0") + " m/s (" + (s.EyewallWind * 3.6).ToString("0") + " km/h)    移速 " + s.drift.ToString("0.#") + " m/s", small);   // v2.4.3 待办5 🟡-8：口径统一为 EyewallWind（基准 PeakWind × 眼壁 1.2）
		num3 += 20f;
		GUI.Label(new Rect(num2, num3, rowW, 18f), "云底 " + (s.Hbase / 1000.0).ToString("0.00") + " km    云顶 " + (s.Htop / 1000.0).ToString("0.0") + " km", small);
		num3 += 20f;
		// v2.1.4 — 地形行移出 pValid 分支（用户：HUD 没显示地形）：地形是风暴自身属性，
		// 与玩家位置无关——只要选中系统就显示（原嵌在 pValid 分支，玩家不在风暴范围
		// 走 else 分支整行不渲染）。v2.1.5 — 所有风暴都显示地形（用户：地形对所有风暴
		// 生效），"已登陆·衰减中"后缀仅台风（雷暴无登陆衰减概念）。
		GUI.Label(new Rect(num2, num3, rowW, 18f), "地形 " + WeatherSystem.TerrainName(s.terrainKind) + ((s.type == StormType.Typhoon && s.overLand) ? "（已登陆·衰减中）" : ""), label);
		num3 += 20f;
		// v2.3.1 — 台风海温显示（用户：冷水会冷死台风）：海上显示中心海温 + 冷暖状态，
		// 冷海水（<24°C）台风能量快速枯竭。
		// v2.3.7 — 审查🟡-12/🟡-4：阈值 26.5→26.8 对齐 sstF≥1.0（26.5 处 sstF=0.96 实为
		// 轻微衰减，"暖水增强"名不副实）；巨行星无"海"，标签改"供能"（内部热通量等效温度）。
		if (s.type == StormType.Typhoon)
		{
			string sstLabel = (s.atmoClass == 2) ? "供能" : "海温";
			string sstTxt = (s.terrainKind == TerrainKind.Ocean)
				? sstLabel + " " + s.sstDisplay.ToString("0.0") + "°C " + (s.sstDisplay >= 26.8 ? "（暖水·增强）" : ((s.sstDisplay >= 24.0) ? "（临界）" : "（冷水·快速衰减）"))
				: sstLabel + " 无（已登陆）";
			GUI.Label(new Rect(num2, num3, rowW, 18f), sstTxt, label);
			num3 += 20f;
		}
		if ((Object)main != (Object)null && main.pValid)
		{
			double num4 = Math.Abs(main.pS);
			double rho = num4 / s.Rmax;
			double pU = main.pU;
			double pW = main.pW;
			double num5 = Math.Sqrt(pU * pU + pW * pW);
			GUI.Label(new Rect(num2, num3, rowW, 18f), "本地风  " + num5.ToString("0.0") + " m/s   (" + (num5 * 3.6).ToString("0") + " km/h)  = " + WeatherSystem.Beaufort(num5) + "级", label);
			num3 += 20f;
			GUI.Label(new Rect(num2, num3, rowW, 18f), "  水平 " + ((pU >= 0.0) ? "→ " : "← ") + Math.Abs(pU).ToString("0.0") + "    垂直 " + ((pW >= 0.0) ? "↑ " : "↓ ") + Math.Abs(pW).ToString("0.0") + " m/s", label);
			num3 += 20f;
			GUI.Label(new Rect(num2, num3, rowW, 18f), "真空速 " + main.pAirspeed.ToString("0.0") + " m/s    高度 " + (main.pH / 1000.0).ToString("0.00") + " km", label);
			num3 += 20f;
			// v2.0.93 — HUD 不再对非台风无差别套用台风结构（用户发现）：台风显示 距风眼+11区名+
			// 风圈；非台风只显示 距中心+ρ（风眼/风圈是台风专属概念）。
			if (s.type == StormType.Typhoon)
			{
				GUI.Label(new Rect(num2, num3, rowW, 18f), "距风眼 " + (num4 / 1000.0).ToString("0.0") + " km   (ρ=" + rho.ToString("0.00") + ")  " + Zone(rho, s, main.pS), label);
				num3 += 20f;
				// v2.0.83 — 风圈半径（用户：7/10/12 级风圈概念）：当前风场配置下各等级风圈最远半径。
				double cr7 = s.WindCircleRoCached(13.9);   // v2.1.1 — 缓存版
				double cr10 = s.WindCircleRoCached(24.5);
				double cr12 = s.WindCircleRoCached(32.7);
				GUI.Label(new Rect(num2, num3, rowW, 18f), "风圈 7级≈" + (cr7 * s.Rmax / 1000.0).ToString("0") + "km  10级≈" + (cr10 * s.Rmax / 1000.0).ToString("0") + "km  12级≈" + (cr12 * s.Rmax / 1000.0).ToString("0") + "km", label);
				num3 += 20f;
			}
			else
			{
				GUI.Label(new Rect(num2, num3, rowW, 18f), "距中心 " + (num4 / 1000.0).ToString("0.0") + " km   (ρ=" + rho.ToString("0.00") + ")  风级 " + WeatherSystem.Beaufort(num5) + "级", label);
				num3 += 20f;
			}
			num3 += 20f;
			// v2.0.98 — bar 缩短（用户：活跃系统左边缘缩减为原先一半、右边缘位置固定）：
			// 宽 294→150。v2.3.8.7 — 面板加宽后居中（原右端固定在 num2+294 会偏左）。
			float num6 = 150f;
			float num7 = 12f;
			Rect val5 = new Rect(num2 + (rowW - num6) * 0.5f, num3, num6, num7);
			GUI.DrawTexture(val5, (Texture)bar);
			// v2.0.96 — 眼壁 marker 台风专属（用户：找 HUD 中疑似显示台风结构的那个——
			// 就是这里：±Rmax 位置的两条竖线是台风眼壁标记，原对非台风也画 = 误套台风结构）。
			if (s.type == StormType.Typhoon)
			{
				for (int i = -1; i <= 1; i += 2)
				{
					float num8 = val5.x + num6 * 0.5f + (float)i * (float)(s.Rmax / (s.Router * 1.6) * (double)num6 * 0.5);
					GUI.color = new Color(val3.r, val3.g, val3.b, 0.85f);
					GUI.DrawTexture(new Rect(num8 - 1f, val5.y, 2f, num7), (Texture)marker);
				}
			}
			GUI.color = new Color(1f, 1f, 1f, 0.35f);
			GUI.DrawTexture(new Rect(val5.x + num6 * 0.5f - 1f, val5.y, 2f, num7), (Texture)marker);
			float num11 = Mathf.Clamp((float)(main.pS / (s.Router * 1.6)), -1f, 1f);
			GUI.color = Color.white;
			GUI.DrawTexture(new Rect(val5.x + num6 * 0.5f + num11 * num6 * 0.5f - 2f, val5.y - 3f, 4f, num7 + 6f), (Texture)marker);
			GUI.color = Color.white;
			num3 += num7 + 6f;
		}
		else
		{
			GUI.Label(new Rect(num2, num3, rowW, 18f), "（不在该星球 / 风暴范围外）", label);
			num3 += 60f;
		}
		// v2.2.5 — 视距数据面板显示（用户：把视距数据在面板显示）
		GUI.Label(new Rect(num2, num3, rowW, 18f), "视距 " + ViewDist().ToString("0") + " m", small);
		num3 += 20f;
		// v2.0.98 — 删视距/缩放空间字样（用户要求）；键位提示精简（F1/Shift+F8 已删）。
		GUI.Label(new Rect(num2, num3, rowW, 18f), "[F6] 菜单  [F7] 解散  [F8] 强度  [F9] 面板  [Shift+F7] 隐藏", small);
		num3 += 20f;
	}

	private static double ViewDist()
	{
		try
		{
			return ((Obs<float>)(object)WorldView.main.viewDistance).Value;
		}
		catch
		{
			return 0.0;
		}
	}

	private static string Zone(double rho, WeatherSystem s, double pS)
	{
		// v2.0.81 — HUD 区域标注与 11 区风场统一（用户：HUD 区跟实际台风统一吗——旧 Zone 是
		// v2.0.13 随手 5 段 0.5/0.85/1.25/2.4/3.6，与 v2.0.77-79 的 11 区分段 0.7/1.2/1.6/2.2/3.0
		// 完全错位：HUD 说“眼壁内缘”实际风已在“强·眼壁”，说“内雨带”实际在“较强+中”）。
		// 现在直接调 WindZoneIndex（含 windZoneOffA/B 偏移）→ 显示 11 区名 + 区号，与 F1 面板一致。
		double sWind = pS - (pS >= 0.0 ? (double)StormRenderer.windZoneOffA : (double)StormRenderer.windZoneOffB) * s.Rmax;
		int zi = WeatherSystem.WindZoneIndex(sWind / s.Rmax);
		return "[" + zi + "]" + StormRenderer.windZoneNames[zi];
	}
}
