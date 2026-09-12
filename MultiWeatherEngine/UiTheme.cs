using System;
using System.Collections.Generic;
using UnityEngine;

namespace MultiWeatherEngine;

// ===================== UI 主题层（与 SpaceXHUD 统一色系 + 避让布局） =====================
// 目标（用户要求）：
//  ①颜色统一系：色板直接照抄 SpaceXHUD/Palette.cs（其源头是 Mods/MenuRestyler/colors.json），
//    风暴强度色阶也在同一饱和度/亮度带上重排 —— 两个 mod 同屏时是同一套视觉语言；
//  ②布局适配：SpaceXHUD 占满"顶部 80×s 条"与"底部 158×s 条"（s = min(h/1080, w/1920)），
//    天气 HUD 的所有面板都排在两条之间，不与它的油表/分级/时间控件打架；未装 SpaceXHUD 时
//    预留量自动收窄（不会平白留一大块空白）；
//  ③零美术资源：所有面板/进度条/芯片/刻度都由 1px 程序化贴图拼出来。
public static class UiTheme
{
	// ---- 基准尺寸（与 SpaceXHUD 相同）----
	public const float RefW = 1920f;
	public const float RefH = 1080f;
	public const float TopBarH = 80f;
	public const float BottomBarH = 158f;
	public const float Edge = 20f;

	public static float Scale => Mathf.Min((float)Screen.height / RefH, (float)Screen.width / RefW);

	// SpaceXHUD 是否在场（反射探测一次）：决定上下预留量
	private static bool? spaceX;
	public static bool SpaceXPresent
	{
		get
		{
			if (!spaceX.HasValue)
			{
				spaceX = false;
				try
				{
					System.Reflection.Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
					for (int i = 0; i < asms.Length; i++)
					{
						string n = asms[i].GetName().Name;
						if (n != null && n.IndexOf("SpaceXHUD", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							spaceX = true;
							break;
						}
					}
				}
				catch
				{
					spaceX = false;
				}
			}
			return spaceX.Value;
		}
	}

	// 上下安全边距：装了 SpaceXHUD 就让开它的全宽条，否则只留小边距
	public static float TopReserve => SpaceXPresent ? (TopBarH * Scale + 8f) : 8f;
	public static float BottomReserve => SpaceXPresent ? (BottomBarH * Scale + 8f) : 8f;

	// ---- 色板（照抄 SpaceXHUD/Palette.cs）----
	public static readonly Color Panel = Hex("1c1c1c");
	public static readonly Color PanelDeep = Hex("111317");
	public static readonly Color PanelHover = Hex("2b2b2b");
	public static readonly Color Text = Hex("DCE7F4");
	public static readonly Color TextHover = Hex("EAF4FF");
	public static readonly Color Fuel = Hex("4FE3C1");       // 青绿
	public static readonly Color Throttle = Hex("FF8A5C");   // 橙
	public static readonly Color Staging = Hex("8A93A6");    // 中性
	public static readonly Color Engine = Hex("5FB8F0");     // 蓝
	public static readonly Color Altitude = Hex("AECBFF");   // 浅蓝
	public static readonly Color Warn = Hex("FF6B6B");       // 红
	public static readonly Color DeepBlue = Hex("1E5FD0");

	// 风暴强度色阶（cat 0-6）：同一饱和度/亮度带上重排，末端取 SpaceXHUD 的橙/红锚点，
	// 中间段保留"蓝→青→绿→黄→橙→红"的强度直觉（机场/气象常用色序），两套语言兼容。
	public static readonly Color[] Intensity = new Color[7]
	{
		Hex("AECBFF"),   // TD   浅蓝（= SpaceX Altitude）
		Hex("4FE3C1"),   // TS   青绿（= SpaceX Fuel）
		Hex("9FE06A"),   // CAT1 黄绿
		Hex("F2DC6B"),   // CAT2 黄
		Hex("FFB05C"),   // CAT3 亮橙
		Hex("FF8A5C"),   // CAT4 橙（= SpaceX Throttle）
		Hex("FF6B6B")    // CAT5 红（= SpaceX Warn）
	};

	public static Color IntensityOf(int cat)
	{
		if (cat < 0) cat = 0;
		if (cat > 6) cat = 6;
		return Intensity[cat];
	}

	// ---- 颜色工具 ----
	public static Color A(Color c, float a) => new Color(c.r, c.g, c.b, a);
	public static Color Mix(Color a, Color b, float t) => Color.Lerp(a, b, Mathf.Clamp01(t));

	public static Color Hex(string s)
	{
		s = s.TrimStart('#');
		float r = 1f, g = 1f, b = 1f, a = 1f;
		if (s.Length >= 6)
		{
			r = Byte(s, 0) / 255f;
			g = Byte(s, 2) / 255f;
			b = Byte(s, 4) / 255f;
		}
		if (s.Length >= 8) a = Byte(s, 6) / 255f;
		return new Color(r, g, b, a);
	}

	private static int Byte(string s, int i) => HexVal(s[i]) * 16 + HexVal(s[i + 1]);

	private static int HexVal(char c)
	{
		if (c >= '0' && c <= '9') return c - '0';
		if (c >= 'a' && c <= 'f') return c - 'a' + 10;
		if (c >= 'A' && c <= 'F') return c - 'A' + 10;
		return 0;
	}

	// ---- 程序化贴图（全部 1px，零资源）----
	// 结构整理：原 Solid(按色缓存)/VerticalGrad(渐变条) 是"每色一张贴图"的旧方案，
	// 面板改扁平化后已无调用者；配色统一走 GUI.color 染色白贴图（见 White()），
	// 既省贴图也免掉每次 Fill 的颜色字符串拼接。此处只留白贴图 + 扫描线。

	// ---- 字体与样式（按字号缓存，避免每帧 new GUIStyle 产生 GC —— 原实现的坑）----
	private static Font cnFont;
	private static bool cnFontTried;

	public static Font CnFont()
	{
		if (!cnFontTried)
		{
			cnFontTried = true;
			try
			{
				cnFont = Font.CreateDynamicFontFromOSFont(new string[] { "Microsoft YaHei", "微软雅黑", "SimHei", "黑体", "Noto Sans CJK SC" }, 14);
			}
			catch
			{
				cnFont = null;
			}
		}
		return cnFont;
	}

	private static readonly Dictionary<int, GUIStyle> labelStyles = new Dictionary<int, GUIStyle>();
	private static readonly Dictionary<int, GUIStyle> labelStylesBold = new Dictionary<int, GUIStyle>();

	public static GUIStyle Label(int size, bool bold = false, TextAnchor anchor = TextAnchor.MiddleLeft)
	{
		Dictionary<int, GUIStyle> map = bold ? labelStylesBold : labelStyles;
		int key = size * 10 + (int)anchor;
		if (map.TryGetValue(key, out GUIStyle st) && st != null)
		{
			return st;
		}
		st = new GUIStyle(GUI.skin.label);
		st.fontSize = size;
		st.font = CnFont();
		st.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
		st.alignment = anchor;
		st.clipping = TextClipping.Clip;
		st.padding = new RectOffset(0, 0, 0, 0);
		st.margin = new RectOffset(0, 0, 0, 0);
		st.richText = false;
		map[key] = st;
		return st;
	}

	private static readonly Dictionary<int, GUIStyle> buttonStyles = new Dictionary<int, GUIStyle>();

	public static GUIStyle Button(int size)
	{
		if (buttonStyles.TryGetValue(size, out GUIStyle st) && st != null)
		{
			return st;
		}
		st = new GUIStyle(GUI.skin.button);
		st.fontSize = size;
		st.font = CnFont();
		st.alignment = TextAnchor.MiddleCenter;
		st.clipping = TextClipping.Clip;
		st.normal.textColor = Text;
		st.hover.textColor = TextHover;
		st.active.textColor = TextHover;
		buttonStyles[size] = st;
		return st;
	}

	// ---- 绘制原语 ----

	// 面板底：纯色平板（去掉卡片渐变，更扁平干净）+ 1px 细描边做层次
	public static void PanelBg(Rect r, float alpha = 0.90f, bool border = true)
	{
		Fill(r, A(Panel, alpha));
		if (border)
		{
			Frame(r, A(Text, 0.12f));
		}
	}

	// 1×1 白贴图（Fill/Frame 全靠 GUI.color 染色）：原实现每次 Fill 都调
	// Solid(Color.white) → 每帧数百次 "s0.0000.0000.0001.000" 字符串拼接 + 字典查找
	// （HUD 每帧几百次 Fill = 纯 GC 垃圾，且是 Layout/Repaint 两趟翻倍）。白贴图是
	// 常量，缓存一次即可；这是本 mod UI 里最大的一处每帧托管分配。
	private static Texture2D solidWhite;

	private static Texture2D White()
	{
		if (solidWhite == null)
		{
			solidWhite = new Texture2D(1, 1);
			solidWhite.SetPixel(0, 0, Color.white);
			solidWhite.Apply();
		}
		return solidWhite;
	}

	public static void Fill(Rect r, Color c)
	{
		Color old = GUI.color;
		GUI.color = c;
		GUI.DrawTexture(r, White());
		GUI.color = old;
	}

	// 发光团：同心嵌套方块（外圈低透明度 → 内圈实心），用来画雷达上的"风暴体"。
	// 只用 4 次 Fill，比按像素画圆便宜得多。
	public static void Glow(float cx, float cy, float r, Color col, float coreA = 0.92f)
	{
		Fill(new Rect(cx - r, cy - r, r * 2f, r * 2f), A(col, 0.07f));
		Fill(new Rect(cx - r * 0.78f, cy - r * 0.78f, r * 1.56f, r * 1.56f), A(col, 0.14f));
		Fill(new Rect(cx - r * 0.54f, cy - r * 0.54f, r * 1.08f, r * 1.08f), A(col, 0.26f));
		Fill(new Rect(cx - r * 0.28f, cy - r * 0.28f, r * 0.56f, r * 0.56f), A(col, coreA));
	}

	// 雷达屏底板：深底 + 纵向扫描线平铺（1×4 贴图，一次 DrawTextureWithTexCoords）+ 边框 + 四角刻度。
	private static Texture2D scanTex;

	private static Texture2D ScanTex()
	{
		if (scanTex == null)
		{
			scanTex = new Texture2D(1, 4, TextureFormat.RGBA32, false);
			for (int i = 0; i < 4; i++)
			{
				scanTex.SetPixel(0, i, new Color(1f, 1f, 1f, (i == 0) ? 0.045f : 0.014f));
			}
			scanTex.wrapMode = TextureWrapMode.Repeat;
			scanTex.Apply();
		}
		return scanTex;
	}

	public static void Scope(Rect r)
	{
		Fill(r, A(PanelDeep, 0.95f));
		Color old = GUI.color;
		GUI.color = Color.white;
		GUI.DrawTextureWithTexCoords(r, ScanTex(), new Rect(0f, 0f, 1f, Mathf.Max(1f, r.height / 4f)));
		GUI.color = old;
		Frame(r, A(Text, 0.16f));
		float k = Mathf.Min(7f, r.height * 0.16f);
		Color c = A(Text, 0.32f);
		Fill(new Rect(r.x + 2f, r.y + 2f, k, 1f), c);
		Fill(new Rect(r.x + 2f, r.y + 2f, 1f, k), c);
		Fill(new Rect(r.xMax - 2f - k, r.y + 2f, k, 1f), c);
		Fill(new Rect(r.xMax - 3f, r.y + 2f, 1f, k), c);
		Fill(new Rect(r.x + 2f, r.yMax - 3f, k, 1f), c);
		Fill(new Rect(r.x + 2f, r.yMax - 2f - k, 1f, k), c);
		Fill(new Rect(r.xMax - 2f - k, r.yMax - 3f, k, 1f), c);
		Fill(new Rect(r.xMax - 3f, r.yMax - 2f - k, 1f, k), c);
	}

	public static void Frame(Rect r, Color c, float w = 1f)
	{
		Fill(new Rect(r.x, r.y, r.width, w), c);
		Fill(new Rect(r.x, r.yMax - w, r.width, w), c);
		Fill(new Rect(r.x, r.y, w, r.height), c);
		Fill(new Rect(r.xMax - w, r.y, w, r.height), c);
	}

	// 进度条：底槽 + 填充 + 可选刻度
	public static void Bar(Rect r, float pct, Color fill, bool ticks = false)
	{
		pct = Mathf.Clamp01(pct);
		Fill(r, A(PanelDeep, 0.85f));
		if (pct > 0.001f)
		{
			Fill(new Rect(r.x, r.y, r.width * pct, r.height), fill);
		}
		if (ticks)
		{
			int n = 4;
			for (int i = 1; i < n; i++)
			{
				float x = r.x + r.width * i / n;
				Fill(new Rect(x, r.y, 1f, r.height), A(Text, 0.14f));
			}
		}
		Frame(r, A(Text, 0.18f));
	}

	// 胶囊标签（类型/状态）
	public static void Chip(Rect r, string txt, Color accent, int fontSize = 11, bool filled = true)
	{
		Fill(r, filled ? A(accent, 0.18f) : A(PanelDeep, 0.8f));
		Fill(new Rect(r.x, r.y, 2f, r.height), accent);
		Frame(r, A(accent, 0.45f));
		DrawText(r, txt, fontSize, A(Text, 0.95f), TextAnchor.MiddleLeft, 5f);
	}

	public static void DrawText(Rect r, string s, int size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft, float padLeft = 0f)
	{
		if (string.IsNullOrEmpty(s))
		{
			return;
		}
		GUIStyle st = Label(size, false, anchor);
		Color old = GUI.contentColor;
		GUI.contentColor = c;
		GUI.Label(new Rect(r.x + padLeft, r.y, r.width - padLeft, r.height), s, st);
		GUI.contentColor = old;
	}

	public static void TextBold(Rect r, string s, int size, Color c, TextAnchor anchor = TextAnchor.MiddleLeft)
	{
		if (string.IsNullOrEmpty(s))
		{
			return;
		}
		GUIStyle st = Label(size, true, anchor);
		Color old = GUI.contentColor;
		GUI.contentColor = c;
		GUI.Label(r, s, st);
		GUI.contentColor = old;
	}

	// 透明点击层（不画背景也不画字）：用于"先手绘行内容、再叠一个整行热区"的列表行。
	// 【必须】用空白 GUIStyle 而不是 GUI.skin.button —— 皮肤自带底图，叠在刚画好的
	// 文字上会把文字整行盖掉（指挥中心的类型栏/左下列表行就是这种"手绘 + 热区"结构）。
	private static GUIStyle hitStyle;

	public static GUIStyle Hit()
	{
		if (hitStyle == null)
		{
			hitStyle = new GUIStyle();
			hitStyle.clipping = TextClipping.Clip;
		}
		return hitStyle;
	}

	// 旋钮/圆点（用 1px 贴图叠出方形点，够用且零资源）
	public static void Dot(Rect r, Color c)
	{
		Fill(r, c);
	}

	// 刻度尺：把一段区间画成带主/次刻度的尺子，返回某值对应的 x
	public static float Ruler(Rect r, float vMin, float vMax, float value, Color accent)
	{
		Fill(new Rect(r.x, r.y + r.height * 0.5f - 1f, r.width, 2f), A(Text, 0.25f));
		for (int i = 0; i <= 10; i++)
		{
			float x = r.x + r.width * i / 10f;
			bool major = (i % 5 == 0);
			Fill(new Rect(x, r.y + (major ? 0f : r.height * 0.35f), 1f, r.height * (major ? 1f : 0.3f)), A(Text, major ? 0.5f : 0.25f));
		}
		float t = Mathf.InverseLerp(vMin, vMax, Mathf.Clamp(value, vMin, vMax));
		float px = r.x + r.width * t;
		Fill(new Rect(px - 1f, r.y - 3f, 3f, r.height + 6f), accent);
		return px;
	}
}
