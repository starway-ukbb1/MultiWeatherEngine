using System.Text;
using SFS.Variables;
using SFS.World;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiWeatherEngine;

// ===================== 飞行 HUD（全面革新版 · 与 SpaceXHUD 同色系同布局语言） =====================
// 三块面板（全部避让 SpaceXHUD 的顶条 80s / 底条 158s，见 UiTheme）：
//   ①左上「风暴详情卡」：数值 + 进度条 + 风圈刻度尺 + 本地风
//   ②左下「活跃系统条」：每行 = 强度条 + 能量条 + 距离条 + 现象标签（点击行选中）
//   ③右上「雷达带」：以玩家为中心的切向雷达，直观看"哪个风暴离你多近、多大"
// 设计原则：能画成条/尺/芯片的就不写成一长串文本；颜色 = UiTheme（SpaceX 色系）。
public class TyphoonHud : MonoBehaviour
{
	private readonly StringBuilder sb = new StringBuilder(160);

	// 现象标签复用缓冲（原 PhenText 每次调用 new StringBuilder(32)：每帧 1+系统数 次分配）
	private static readonly StringBuilder phenSb = new StringBuilder(48);

	private bool init;

	private WeatherSystem S => TyphoonManager.main != null ? TyphoonManager.main.selected : null;

	private void OnGUI()
	{
		if (!init)
		{
			init = true;
			UiTheme.CnFont();   // 预热中文字体（SFS 默认字体无中文字形）
		}
		TyphoonManager main = TyphoonManager.main;
		if (main == null || WorldView.main == null)
		{
			return;
		}
		if (!TyphoonConfig.I.hud)
		{
			return;
		}
		// 性能：本 HUD 全部是"显式矩形 + GUI.Button"的立即模式绘制（不使用 GUILayout），
		// Layout 通道不参与任何布局计算 → 直接跳过。OnGUI 每帧要跑 Layout + Repaint 两趟，
		// 跳过 Layout 等于绘制工作量腰斩（MouseDown/Up 等输入事件照常处理，按钮点击不受影响）。
		// 原 frameId/frameReady 字段是死代码（frameReady 从未被读），一并移除。
		if (Event.current != null && Event.current.type == EventType.Layout)
		{
			return;
		}
		if (TyphoonManager.GetPlayerLocation() == null && TyphoonManager.systems.Count == 0)
		{
			return;
		}
		float s = UiTheme.Scale;
		DrawDetailCard(main, s);
		DrawStormList(main, s);
		DrawRadar(main, s);
	}

	// ---------------------------------------------------------------- ① 详情卡
	private void DrawDetailCard(TyphoonManager main, float s)
	{
		float x = UiTheme.Edge * s;
		float y = UiTheme.TopReserve;
		float w = 306f * s;
		WeatherSystem st = S;
		bool has = st != null && st.active;

		// 行数决定高度（有风暴才有风圈尺/本地风块）
		float h = (has ? 292f : 74f) * s;
		UiTheme.PanelBg(new Rect(x, y, w, h), 0.90f);

		// 顶部色条 = 强度色（一眼看出选中的是几级）
		Color accent = has ? UiTheme.IntensityOf(st.category) : UiTheme.Staging;
		UiTheme.Fill(new Rect(x, y, w, 3f * s), accent);

		float px = x + 10f * s;
		float py = y + 9f * s;
		float iw = w - 20f * s;

		if (!has)
		{
			UiTheme.TextBold(new Rect(px, py, iw, 18f * s), "星程气象", Mathf.RoundToInt(13f * s), UiTheme.Text);
			UiTheme.DrawText(new Rect(px, py + 22f * s, iw, 16f * s), "未选中系统 · 在左下列表点选或按 [F6] 召唤", Mathf.RoundToInt(11f * s), UiTheme.A(UiTheme.Text, 0.6f));
			return;
		}

		// 标题行：类型 + 等级 + 阶段
		sb.Clear();
		sb.Append(WeatherSystem.TypeName(st.type)).Append("  ").Append(GradeName(st));
		UiTheme.TextBold(new Rect(px, py, iw - 62f * s, 20f * s), sb.ToString(), Mathf.RoundToInt(14f * s), accent);
		string stageTxt = (st.stage == 0) ? "发展" : ((st.stage == 1) ? "成熟" : "消散");
		UiTheme.Chip(new Rect(px + iw - 58f * s, py + 1f * s, 58f * s, 17f * s), stageTxt,
			st.stage == 0 ? UiTheme.Engine : (st.stage == 1 ? UiTheme.Fuel : UiTheme.Warn), Mathf.RoundToInt(11f * s));
		py += 26f * s;

		// 三条主指标：强度 / 能量 / 距离
		float dist = DistTo(st);
		DrawMetric(px, ref py, iw, s, "强度", UiTheme.IntensityOf(st.category), st.category / 6f,
			"CAT-" + st.category + " / 上限 " + WeatherSystem.Spec[(int)st.type].maxCat);
		DrawMetric(px, ref py, iw, s, "能量", UiTheme.Fuel, (float)(st.energy / 100.0), (st.energy).ToString("0") + "%");
		if (dist >= 0f)
		{
			float maxShow = (float)(st.Rmax * 5.0);
			DrawMetric(px, ref py, iw, s, "距离", UiTheme.Engine, 1f - Mathf.Clamp01(dist / maxShow), (dist / 1000f).ToString("0.0") + " km");
		}
		py += 4f * s;

		// 风圈刻度尺（7/10/12 级风圈）：尺长 = Router，主刻度 10 等分
		double cr7 = st.WindCircleRoCached(13.9);
		double cr10 = st.WindCircleRoCached(24.5);
		double cr12 = st.WindCircleRoCached(32.7);
		UiTheme.DrawText(new Rect(px, py, iw, 14f * s), "风圈（7/10/12 级 · 尺 = 外半径 " + (st.Router / 1000.0).ToString("0.#") + " km）", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.55f));
		py += 15f * s;
		Rect ruler = new Rect(px, py, iw, 12f * s);
		UiTheme.Ruler(ruler, 0f, (float)(st.Router / st.Rmax), (float)cr12, UiTheme.Intensity[6]);
		// 三个风圈标记（三角形做不了，用小方块 + 数值）
		Marker(ruler, (float)(st.Router / st.Rmax), (float)cr7, s, UiTheme.Fuel, (cr7 * st.Rmax / 1000.0).ToString("0") + "km");
		Marker(ruler, (float)(st.Router / st.Rmax), (float)cr10, s, UiTheme.Throttle, (cr10 * st.Rmax / 1000.0).ToString("0") + "km");
		Marker(ruler, (float)(st.Router / st.Rmax), (float)cr12, s, UiTheme.Warn, (cr12 * st.Rmax / 1000.0).ToString("0") + "km");
		py += 16f * s;

		// 数据两列
		sb.Clear();
		sb.Append("峰风 ").Append(st.EyewallWind.ToString("0")).Append(" m/s · 移速 ").Append(st.drift.ToString("0.#")).Append(" m/s");
		UiTheme.DrawText(new Rect(px, py, iw, 14f * s), sb.ToString(), Mathf.RoundToInt(11f * s), UiTheme.A(UiTheme.Text, 0.85f));
		py += 14f * s;
		sb.Clear();
		sb.Append("云底 ").Append((st.Hbase / 1000.0).ToString("0.00")).Append(" km · 云顶 ").Append((st.Htop / 1000.0).ToString("0.0")).Append(" km · ").Append(WeatherSystem.TerrainName(st.terrainKind));
		// 登陆提示移入下方海温行，避免本行过长被裁剪
		UiTheme.DrawText(new Rect(px, py, iw, 14f * s), sb.ToString(), Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.85f));
		py += 14f * s;
		if (st.type == StormType.Typhoon)
		{
			sb.Clear();
			string sstLabel = (st.atmoClass == 2) ? "供能" : "海温";
			if (st.terrainKind == TerrainKind.Ocean)
			{
				sb.Append(sstLabel).Append(' ').Append(st.sstDisplay.ToString("0.0")).Append("°C ");
				sb.Append(st.sstDisplay >= 26.8 ? "暖水·增强" : (st.sstDisplay >= 24.0 ? "临界" : "冷水·衰减"));
			}
			else
			{
				sb.Append(sstLabel).Append(" 无（已登陆·衰减中）");
			}
			UiTheme.DrawText(new Rect(px, py, iw, 14f * s), sb.ToString(), Mathf.RoundToInt(11f * s),
				st.sstDisplay >= 26.8 ? UiTheme.Fuel : (st.sstDisplay >= 24.0 ? UiTheme.Altitude : UiTheme.Warn));
			py += 14f * s;
		}
		// 现象标签
		string phen = PhenText(st);
		if (!string.IsNullOrEmpty(phen))
		{
			UiTheme.Chip(new Rect(px, py, Mathf.Min(iw, phen.Length * 11f * s + 12f * s), 15f * s), phen, UiTheme.Throttle, Mathf.RoundToInt(10f * s));
			py += 17f * s;
		}

		// 本地风块（玩家所在处）
		if (main.pValid)
		{
			double windSpeed = System.Math.Sqrt(main.pU * main.pU + main.pW * main.pW);
			UiTheme.Fill(new Rect(x + 1f, py, w - 2f, 1f), UiTheme.A(UiTheme.Text, 0.14f));
			py += 5f * s;
			UiTheme.DrawText(new Rect(px, py, iw, 14f * s), "本地风", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.55f));
			UiTheme.DrawText(new Rect(px + 40f * s, py, iw - 40f * s, 14f * s),
				windSpeed.ToString("0.0") + " m/s (" + (windSpeed * 3.6).ToString("0") + " km/h) · " + WeatherSystem.Beaufort(windSpeed) + " 级",
				Mathf.RoundToInt(11f * s), UiTheme.Fuel);
			py += 15f * s;
			// 能见度（逐型对标现实：暴雨内 <1km；风眼/云顶之上则清晰）
			if (main.rainVisM < 12000f)
			{
				string visTxt = (main.rainVisM >= 1000f)
					? (main.rainVisM / 1000f).ToString("0.0") + " km"
					: main.rainVisM.ToString("0") + " m";
				string visTag = main.rainVisTornado ? "龙卷沙幕" : "云/雨包裹";
				UiTheme.DrawText(new Rect(px, py, iw, 14f * s),
					"能见度 ≈ " + visTxt + "（" + visTag + "）",
					Mathf.RoundToInt(10f * s), UiTheme.Warn);
				py += 15f * s;
			}
			// 水平/垂直分量条（中心为 0，向两侧生长）
			DrawBipolar(px, py, iw, s, "水平", main.pU, 60f, UiTheme.Engine, main.pU >= 0.0 ? "→" : "←");
			py += 13f * s;
			DrawBipolar(px, py, iw, s, "垂直", main.pW, 40f, UiTheme.Throttle, main.pW >= 0.0 ? "↑" : "↓");
			py += 14f * s;
			UiTheme.DrawText(new Rect(px, py, iw, 14f * s),
				"真空速 " + main.pAirspeed.ToString("0.0") + " m/s · 高度 " + (main.pH / 1000.0).ToString("0.00") + " km",
				Mathf.RoundToInt(11f * s), UiTheme.A(UiTheme.Text, 0.8f));
		}

		// 底栏键位提示
		UiTheme.DrawText(new Rect(px, y + h - 15f * s, iw, 13f * s), "[F6] 菜单  [F7] 解散  [F8] 强度  [F9] 列表  [Shift+F7] 隐藏 HUD",
			Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.45f));
	}

	// 指标行：标签 + 条 + 数值
	private void DrawMetric(float px, ref float py, float iw, float s, string label, Color col, float pct, string val)
	{
		UiTheme.DrawText(new Rect(px, py, 34f * s, 13f * s), label, Mathf.RoundToInt(11f * s), UiTheme.A(UiTheme.Text, 0.7f));
		Rect bar = new Rect(px + 36f * s, py + 2f * s, iw - 36f * s - 74f * s, 9f * s);
		UiTheme.Bar(bar, pct, col, true);
		UiTheme.DrawText(new Rect(bar.xMax + 6f * s, py, 68f * s, 13f * s), val, Mathf.RoundToInt(11f * s), UiTheme.A(UiTheme.Text, 0.95f), TextAnchor.MiddleRight);
		py += 15f * s;
	}

	// 双极条（水平/垂直风分量：0 在中间）
	private void DrawBipolar(float px, float py, float iw, float s, string label, double v, float full, Color col, string arrow)
	{
		UiTheme.DrawText(new Rect(px, py, 34f * s, 12f * s), label, Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.6f));
		Rect bar = new Rect(px + 36f * s, py + 2f * s, iw - 36f * s - 74f * s, 8f * s);
		UiTheme.Fill(bar, UiTheme.A(UiTheme.PanelDeep, 0.85f));
		float mid = bar.x + bar.width * 0.5f;
		UiTheme.Fill(new Rect(mid, bar.y, 1f, bar.height), UiTheme.A(UiTheme.Text, 0.25f));
		float t = Mathf.Clamp((float)(v / full), -1f, 1f);
		float w = bar.width * 0.5f * Mathf.Abs(t);
		UiTheme.Fill(t >= 0f ? new Rect(mid, bar.y, w, bar.height) : new Rect(mid - w, bar.y, w, bar.height), col);
		UiTheme.Frame(bar, UiTheme.A(UiTheme.Text, 0.18f));
		UiTheme.DrawText(new Rect(bar.xMax + 6f * s, py, 68f * s, 12f * s), arrow + " " + System.Math.Abs(v).ToString("0.0"), Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.85f), TextAnchor.MiddleRight);
	}

	private static void Marker(Rect ruler, float rulerMax, float v, float s, Color col, string txt)
	{
		float t = Mathf.Clamp01(v / Mathf.Max(rulerMax, 0.001f));
		float x = ruler.x + ruler.width * t;
		UiTheme.Fill(new Rect(x - 1f, ruler.y + ruler.height * 0.72f, 3f, ruler.height * 0.5f), col);
		UiTheme.DrawText(new Rect(x - 16f * s, ruler.y + ruler.height + 1f * s, 32f * s, 11f * s), txt, Mathf.RoundToInt(9f * s), UiTheme.A(col, 0.9f), TextAnchor.MiddleCenter);
	}

	// ---------------------------------------------------------------- ② 系统列表
	private void DrawStormList(TyphoonManager main, float s)
	{
		int n = TyphoonManager.systems.Count;
		bool exp = TyphoonManager.panelExpanded;
		float w = 372f * s;
		float x = UiTheme.Edge * s;
		float headerH = 24f * s;
		float rowH = 26f * s;
		float body = exp ? (n * rowH + (n == 0 ? 20f * s : 0f)) : 0f;
		float h = headerH + body + 8f * s;
		float y = Screen.height - UiTheme.BottomReserve - h;
		UiTheme.PanelBg(new Rect(x, y, w, h), 0.82f);

		Color sel = UiTheme.Engine;
		UiTheme.Fill(new Rect(x, y, 3f * s, h), UiTheme.A(sel, 0.85f));

		// 表头（可点：展开/收起）
		sb.Clear();
		sb.Append("活跃天气系统  ").Append(n).Append(exp ? "   ▼" : "   ▶");
		Rect hdr = new Rect(x + 8f * s, y + 2f * s, w - 16f * s, headerH - 4f * s);
		if (GUI.Button(hdr, sb.ToString(), UiTheme.Button(Mathf.RoundToInt(12f * s))))
		{
			TyphoonManager.panelExpanded = !exp;
		}
		UiTheme.DrawText(new Rect(x + w - 96f * s, y + 4f * s, 88f * s, 16f * s), "[F9] 展开/收起", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.45f), TextAnchor.MiddleRight);
		if (!exp)
		{
			return;
		}
		float ry = y + headerH + 2f * s;
		if (n == 0)
		{
			UiTheme.DrawText(new Rect(x + 12f * s, ry, w - 24f * s, 18f * s), "无活跃系统 · [F6] 打开气象菜单召唤", Mathf.RoundToInt(11f * s), UiTheme.A(UiTheme.Text, 0.5f));
			return;
		}
		for (int i = 0; i < n; i++)
		{
			WeatherSystem st = TyphoonManager.systems[i];
			if (st == null || !st.active)
			{
				continue;
			}
			Rect row = new Rect(x + 4f * s, ry, w - 8f * s, rowH - 2f * s);
			bool isSel = main.selected == st;
			if (isSel)
			{
				UiTheme.Fill(row, UiTheme.A(sel, 0.16f));
			}
			Color accent = UiTheme.IntensityOf(st.category);
			UiTheme.Fill(new Rect(row.x, row.y + 3f * s, 3f * s, row.height - 6f * s), accent);

			// 类型 + 等级
			sb.Clear();
			sb.Append(WeatherSystem.TypeName(st.type)).Append(' ').Append(GradeName(st));
			UiTheme.DrawText(new Rect(row.x + 8f * s, row.y, 118f * s, row.height), sb.ToString(), Mathf.RoundToInt(11f * s),
				isSel ? UiTheme.TextHover : UiTheme.A(UiTheme.Text, 0.9f));

			// 三条迷你条：强度（分类）/能量/距离
			float bx = row.x + 128f * s;
			float bw = 42f * s;
			UiTheme.Bar(new Rect(bx, row.y + 5f * s, bw, 6f * s), st.category / 6f, accent);
			UiTheme.Bar(new Rect(bx + bw + 4f * s, row.y + 5f * s, bw, 6f * s), (float)(st.energy / 100.0), UiTheme.Fuel);
			float d = DistTo(st);
			UiTheme.Bar(new Rect(bx + (bw + 4f * s) * 2f, row.y + 5f * s, bw, 6f * s),
				d < 0f ? 0f : 1f - Mathf.Clamp01((float)(d / (st.Rmax * 6.0))), UiTheme.Engine);
			sb.Clear();
			sb.Append(d < 0f ? "其他星球" : (d / 1000.0).ToString("0.0") + "km");
			UiTheme.DrawText(new Rect(bx, row.y + 12f * s, bw * 3f + 8f * s, 11f * s), sb.ToString(), Mathf.RoundToInt(9f * s), UiTheme.A(UiTheme.Text, 0.5f), TextAnchor.MiddleCenter);

			// 现象标签
			string phen = PhenText(st);
			if (!string.IsNullOrEmpty(phen))
			{
				UiTheme.DrawText(new Rect(row.xMax - 86f * s, row.y, 84f * s, row.height), phen, Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Throttle, 0.9f), TextAnchor.MiddleRight);
			}
			if (GUI.Button(row, GUIContent.none, UiTheme.Hit()))
			{
				main.selected = st;
				main.selectedType = (int)st.type;
			}
			ry += rowH;
		}
	}

	// ---------------------------------------------------------------- ③ 雷达带（风暴 + 龙卷预报）
	// 玩家居中、±range 对称；风暴 = 发光团 + 云塔 + 地面投影（尺寸 ∝ Rmax、色 = 强度）；
	// 龙卷 = 垂到地面的红色漏斗点（脉冲）；下方并入「龙卷预报」：最近龙卷的类型/EF/距离/
	// 方位/现实秒 ETA + 本系统剩余潜势 + 伴生现象。
	private void DrawRadar(TyphoonManager main, float s)
	{
		if (TyphoonManager.systems.Count == 0)
		{
			return;
		}
		float w = 356f * s;
		float x = Screen.width - UiTheme.Edge * s - w;
		float y = UiTheme.TopReserve;
		float px = x + 10f * s;
		float iw = w - 20f * s;
		Location pl = TyphoonManager.GetPlayerLocation();
		if (pl == null || (Object)pl.planet == (Object)null)
		{
			float hh = 48f * s;
			UiTheme.PanelBg(new Rect(x, y, w, hh), 0.86f);
			UiTheme.Fill(new Rect(x, y, w, 3f * s), UiTheme.A(UiTheme.Staging, 0.7f));
			UiTheme.TextBold(new Rect(px, y + 4f * s, iw, 17f * s), "风暴雷达", Mathf.RoundToInt(12f * s), UiTheme.TextHover);
			UiTheme.DrawText(new Rect(px, y + 24f * s, iw, 16f * s), "不在星球上 / 雷达不可用", Mathf.RoundToInt(10.5f * s), UiTheme.A(UiTheme.Text, 0.5f));
			return;
		}

		// ---- 数据：预报宿主 + 最近龙卷 + 潜势 ----
		WeatherSystem host = PickForecastHost(main, pl);
		int torN = (host != null) ? host.tornadoes.Count : 0;
		bool potential = host != null && host.stage == 1 && (host.tornadoQuota > 0 || host.tornadoQuota < 0);
		bool companion = host != null && (host.downbursts.Count > 0 || host.gustFronts.Count > 0 || host.lightningBursts.Count > 0);

		WeatherSystem.FxInst nearFx = null;
		double nearD = 0.0;
		bool nearEast = false;
		float nearEta = -1f;
		int nearEf = 0;
		if (host != null && torN > 0)
		{
			double pa = pl.position.AngleRadians;
			double pr = pl.planet.Radius;
			double best = 1e18;
			for (int i = 0; i < host.tornadoes.Count; i++)
			{
				WeatherSystem.FxInst fx = host.tornadoes[i];
				double ta = host.centerAngle + fx.sOff * host.Rmax / pr;
				double off = WeatherSystem.WrapPi(ta - pa) * pr;   // 有符号切向距离（+ = 玩家前方/东）
				double d = System.Math.Abs(off);
				if (d < best)
				{
					best = d;
					nearFx = fx;
					nearD = d;
					nearEast = off > 0.0;
				}
			}
			if (nearFx != null)
			{
				nearEf = WeatherSystem.EfFromWind(host.TornadoPeakWind(nearFx));
				if (!nearEast && host.moveSpeed > 0.5)
				{
					// /时间加速倍率 → 现实秒（风暴向 +角 移动，玩家在西侧才是逼近中）
					nearEta = (float)(nearD / host.moveSpeed / Mathf.Max(TyphoonManager.timeScaleReal, 0.01f));
				}
			}
		}

		// 预警色：红 = 龙卷已生成且 <5km；橙 = 有龙卷；黄 = 有潜势；灰 = 无
		Color warnCol = UiTheme.Staging;
		string warnTxt = "无";
		if (torN > 0)
		{
			warnCol = UiTheme.Throttle;
			warnTxt = "龙卷 ×" + torN;
			if (nearFx != null && nearD < 5000.0)
			{
				warnCol = UiTheme.Warn;
				warnTxt = "龙卷逼近";
			}
		}
		else if (potential)
		{
			warnCol = UiTheme.Intensity[3];
			warnTxt = "有潜势";
		}

		// ---- 面板几何（高度随预报行数）----
		float headerH = 21f * s;
		float scopeH = 74f * s;
		float tickH = 11f * s;
		float rowH = 15f * s;
		int rows = 1 + ((torN > 0) ? 1 : 0) + ((torN == 0) ? 1 : 0) + (companion ? 1 : 0);
		float h = headerH + 6f * s + scopeH + tickH + 5f * s + rows * rowH + 6f * s;
		UiTheme.PanelBg(new Rect(x, y, w, h), 0.86f);
		UiTheme.Fill(new Rect(x, y, w, 3f * s), UiTheme.A(warnCol, 0.9f));

		// ---- 标题行（雷达 + 预警芯片）----
		UiTheme.TextBold(new Rect(px, y + 3f * s, iw - 92f * s, 17f * s), "风暴雷达", Mathf.RoundToInt(12f * s), UiTheme.TextHover);
		UiTheme.Chip(new Rect(x + w - 10f * s - 84f * s, y + 4f * s, 84f * s, 15f * s), warnTxt, warnCol, Mathf.RoundToInt(10f * s));

		// ---- 范围：最远风暴外缘（下限 20km），半幅 = range ----
		double maxRange = 20000.0;
		for (int i = 0; i < TyphoonManager.systems.Count; i++)
		{
			WeatherSystem sys = TyphoonManager.systems[i];
			if (sys == null || !sys.active || (Object)sys.planet != (Object)pl.planet)
			{
				continue;
			}
			double off = System.Math.Abs(WeatherSystem.WrapPi(sys.centerAngle - pl.position.AngleRadians)) * pl.planet.Radius;
			maxRange = System.Math.Max(maxRange, off + sys.Rmax);
		}
		maxRange *= 1.12;

		float sy0 = y + headerH + 6f * s;
		Rect scope = new Rect(px, sy0, iw, scopeH);
		UiTheme.Scope(scope);
		float horizon = scope.yMax - 8f * s;                 // 地面线
		float cxMid = scope.x + scope.width * 0.5f;          // 玩家在正中
		float pxPerM = (float)(scope.width * 0.5 / maxRange);   // 米 → 像素

		// 距离网格 + 刻度（−range … 0 … +range）
		for (int i = 0; i <= 4; i++)
		{
			float gx = scope.x + scope.width * i / 4f;
			UiTheme.Fill(new Rect(gx, scope.y + 3f * s, 1f, scope.height - 11f * s), UiTheme.A(UiTheme.Text, 0.09f));
			double km = (i - 2) * 0.5 * maxRange / 1000.0;
			string lab = (i == 2) ? "0" : (((km > 0.0) ? "+" : "") + km.ToString("0"));
			UiTheme.DrawText(new Rect(gx - 22f * s, horizon + 1f * s, 44f * s, 10f * s), lab, Mathf.RoundToInt(8.5f * s), UiTheme.A(UiTheme.Text, 0.42f), TextAnchor.MiddleCenter);
		}

		// 地面线 + 扫描线（时间相位 → 雷达屏质感）
		UiTheme.Fill(new Rect(scope.x + 2f * s, horizon, scope.width - 4f * s, 1f), UiTheme.A(UiTheme.Text, 0.26f));
		float sweep = Mathf.Repeat(Time.time * 0.16f, 1f);
		float swx = scope.x + scope.width * sweep;
		UiTheme.Fill(new Rect(swx - 4f * s, scope.y + 3f * s, 8f * s, horizon - scope.y - 3f * s), UiTheme.A(UiTheme.Engine, 0.05f));
		UiTheme.Fill(new Rect(swx, scope.y + 3f * s, 1f, horizon - scope.y - 3f * s), UiTheme.A(UiTheme.Engine, 0.22f));

		// 玩家（正中）
		UiTheme.Fill(new Rect(cxMid - 3.5f * s, horizon - 2f * s, 7f * s, 2f * s), UiTheme.TextHover);
		UiTheme.Fill(new Rect(cxMid - 1.5f * s, horizon - 6f * s, 3f * s, 5f * s), UiTheme.TextHover);
		UiTheme.DrawText(new Rect(cxMid - 16f * s, horizon - 18f * s, 32f * s, 10f * s), "你", Mathf.RoundToInt(9f * s), UiTheme.TextHover, TextAnchor.MiddleCenter);

		// ---- 逐系统：发光团 + 云塔 + 地面投影 + 龙卷漏斗点 ----
		float maxTower = scope.height - 20f * s;
		for (int i = 0; i < TyphoonManager.systems.Count; i++)
		{
			WeatherSystem sys = TyphoonManager.systems[i];
			if (sys == null || !sys.active || (Object)sys.planet != (Object)pl.planet)
			{
				continue;
			}
			double angOff = WeatherSystem.WrapPi(sys.centerAngle - pl.position.AngleRadians);
			float sx = cxMid + (float)(angOff * pl.planet.Radius) * pxPerM;
			Color col = UiTheme.IntensityOf(sys.category);
			if (sx < scope.x + 3f * s || sx > scope.xMax - 3f * s)
			{
				// 超出雷达量程：贴边画一个小方块，提示"那边还有更远的"
				float ex = Mathf.Clamp(sx, scope.x + 4f * s, scope.xMax - 6f * s);
				UiTheme.Fill(new Rect(ex, horizon - 4f * s, 3f * s, 7f * s), UiTheme.A(col, 0.45f));
				continue;
			}
			float rPx = Mathf.Clamp((float)sys.Rmax * pxPerM, 3f * s, 26f * s);
			float tower = Mathf.Clamp((float)sys.Htop * pxPerM * 1.6f, 9f * s, maxTower);
			float cy = horizon - 4f * s - tower * 0.5f;
			bool sel = main.selected == sys;

			UiTheme.Glow(sx, cy, Mathf.Max(rPx, 4.5f * s), col, sel ? 0.95f : 0.78f);
			UiTheme.Fill(new Rect(sx - rPx * 0.22f, cy - tower * 0.5f, rPx * 0.44f, tower), UiTheme.A(col, 0.32f));       // 云塔
			UiTheme.Fill(new Rect(sx - rPx * 0.78f, cy - tower * 0.62f, rPx * 1.56f, rPx * 0.3f), UiTheme.A(col, 0.24f));  // 云砧
			UiTheme.Fill(new Rect(sx - rPx * 0.6f, horizon - 1.5f * s, rPx * 1.2f, 3f * s), UiTheme.A(col, 0.55f));        // 地面投影
			if (sel)
			{
				UiTheme.Frame(new Rect(sx - rPx, cy - tower * 0.68f, rPx * 2f, tower * 0.72f + 7f * s), UiTheme.A(UiTheme.TextHover, 0.5f));
				sb.Clear();
				sb.Append(WeatherSystem.TypeName(sys.type)).Append(' ').Append(GradeName(sys));
				UiTheme.DrawText(new Rect(sx - 50f * s, scope.y + 2f * s, 100f * s, 11f * s), sb.ToString(), Mathf.RoundToInt(9f * s), UiTheme.TextHover, TextAnchor.MiddleCenter);
			}
			// 龙卷：垂到地面的红色漏斗（脉冲，按实例 seed 错相）
			for (int ti = 0; ti < sys.tornadoes.Count; ti++)
			{
				WeatherSystem.FxInst fx = sys.tornadoes[ti];
				float tx = sx + (float)(fx.sOff * sys.Rmax) * pxPerM;
				if (tx < scope.x + 2f * s || tx > scope.xMax - 2f * s)
				{
					continue;
				}
				float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 5.5f + (float)fx.seed);
				float fh = Mathf.Clamp(tower * 0.42f, 6f * s, 26f * s);
				UiTheme.Fill(new Rect(tx - 0.5f, horizon - fh, 1f, fh), UiTheme.A(UiTheme.Warn, 0.4f + 0.35f * pulse));
				UiTheme.Fill(new Rect(tx - 4f * s, horizon - 5f * s, 8f * s, 5f * s), UiTheme.A(UiTheme.Warn, 0.3f * pulse));
				UiTheme.Fill(new Rect(tx - 2f * s, horizon - 4f * s, 4f * s, 4f * s), UiTheme.A(UiTheme.Warn, 0.7f + 0.3f * pulse));
			}
		}

		// ---- 龙卷预报（并入雷达下方）----
		float fy = sy0 + scopeH + tickH + 3f * s;
		UiTheme.Fill(new Rect(px, fy - 3f * s, iw, 1f), UiTheme.A(UiTheme.Text, 0.12f));
		float ry = fy;
		UiTheme.TextBold(new Rect(px, ry, 58f * s, rowH), "龙卷预报", Mathf.RoundToInt(10.5f * s), UiTheme.A(UiTheme.Text, 0.9f));
		sb.Clear();
		if (host == null)
		{
			sb.Append("本星球暂无系统");
		}
		else if (torN > 0)
		{
			sb.Append("现役 ").Append(torN).Append(" 个");
		}
		else if (potential)
		{
			sb.Append("潜势 · 待触发");
		}
		else
		{
			sb.Append("无潜势");
		}
		UiTheme.DrawText(new Rect(px + 62f * s, ry, iw - 62f * s, rowH), sb.ToString(), Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.7f), TextAnchor.MiddleRight);
		ry += rowH;

		if (torN > 0 && nearFx != null)
		{
			sb.Clear();
			sb.Append("● ").Append(WeatherSystem.TornadoSizeName(host.TornadoCoreR(nearFx)))
				.Append(WeatherSystem.TornadoVariantName(nearFx.variant)).Append("龙卷 EF").Append(nearEf);
			sb.Append(" · ").Append((nearD >= 1000.0) ? ((nearD / 1000.0).ToString("0.0") + " km") : (nearD.ToString("0") + " m"));
			sb.Append(nearEast ? " →" : " ←");
			if (nearEta >= 0f)
			{
				sb.Append(" · ").Append((nearEta >= 60f) ? ((nearEta / 60f).ToString("0.0") + " 分钟后抵达") : (nearEta.ToString("0") + " 秒后抵达"));
			}
			else
			{
				sb.Append(" · 正在远离");
			}
			UiTheme.DrawText(new Rect(px + 4f * s, ry, iw - 4f * s, rowH), sb.ToString(), Mathf.RoundToInt(10f * s), warnCol);
			ry += rowH;
		}
		if (torN == 0)
		{
			string txt;
			if (host == null)
			{
				txt = "当前星球没有可预报的天气系统";
			}
			else if (host.tornadoQuota < 0)
			{
				txt = "龙卷潜势：不限额（现实性配额已关闭）";
			}
			else if (host.tornadoQuota == 0)
			{
				txt = WeatherSystem.TypeName(host.type) + " 未配上龙卷潜势（约 2/3 强风暴一生不产龙卷）";
			}
			else if (host.stage != 1)
			{
				txt = "余 " + host.tornadoQuota + " 个 · 成熟期才触发（当前" + ((host.stage == 0) ? "发展" : "消散") + "）";
			}
			else
			{
				txt = "余 " + host.tornadoQuota + " 个 · 成熟期随机触发（约 10-30 分钟）";
			}
			UiTheme.DrawText(new Rect(px + 4f * s, ry, iw - 4f * s, rowH), txt, Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.6f));
			ry += rowH;
		}
		if (companion)
		{
			sb.Clear();
			sb.Append("伴生");
			if (host.downbursts.Count > 0) sb.Append(" 下击暴流×").Append(host.downbursts.Count);
			if (host.gustFronts.Count > 0) sb.Append(" 阵风锋×").Append(host.gustFronts.Count);
			if (host.lightningBursts.Count > 0) sb.Append(" 闪电×").Append(host.lightningBursts.Count);
			UiTheme.DrawText(new Rect(px + 4f * s, ry, iw - 4f * s, rowH), sb.ToString(), Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Throttle, 0.85f));
		}
	}

	// 预报宿主：①选中的系统（同星球）优先 → ②有现役龙卷的 → ③有龙卷潜势的 → ④最近的。
	private static WeatherSystem PickForecastHost(TyphoonManager main, Location pl)
	{
		WeatherSystem sel = main.selected;
		if (sel != null && sel.active && (Object)sel.planet == (Object)pl.planet)
		{
			return sel;
		}
		WeatherSystem best = null;
		double bestScore = double.MinValue;
		for (int i = 0; i < TyphoonManager.systems.Count; i++)
		{
			WeatherSystem sys = TyphoonManager.systems[i];
			if (sys == null || !sys.active || (Object)sys.planet != (Object)pl.planet)
			{
				continue;
			}
			double score = 0.0;
			if (sys.tornadoes.Count > 0) score += 1e9;
			if (sys.stage == 1 && sys.tornadoQuota != 0) score += 1e8;
			score -= System.Math.Abs(WeatherSystem.WrapPi(sys.centerAngle - pl.position.AngleRadians)) * pl.planet.Radius;
			if (score > bestScore)
			{
				bestScore = score;
				best = sys;
			}
		}
		return best;
	}

	// ---------------------------------------------------------------- 工具
	private static string PhenText(WeatherSystem st)
	{
		if (st.tornadoes.Count == 0 && st.downbursts.Count == 0 && st.gustFronts.Count == 0 && st.lightningBursts.Count == 0)
		{
			return null;
		}
		StringBuilder b = phenSb;
		b.Clear();
		if (st.tornadoes.Count > 0) b.Append("龙卷×").Append(st.tornadoes.Count);
		if (st.downbursts.Count > 0) b.Append(b.Length > 0 ? " " : "").Append("下暴×").Append(st.downbursts.Count);
		if (st.gustFronts.Count > 0) b.Append(b.Length > 0 ? " " : "").Append("阵风锋×").Append(st.gustFronts.Count);
		if (st.lightningBursts.Count > 0) b.Append(b.Length > 0 ? " " : "").Append("闪电×").Append(st.lightningBursts.Count);
		return b.ToString();
	}

	private static string GradeName(WeatherSystem s)
	{
		if (s.type == StormType.Typhoon && s.commaK > 0.3 && s.atmoClass == 0)
		{
			return "残余低压";
		}
		return WeatherSystem.StrengthName(s.type, s.category);
	}

	// 玩家到风暴中心的切向距离（米）；不同星球返回 -1
	private static float DistTo(WeatherSystem s)
	{
		try
		{
			Location pl = TyphoonManager.GetPlayerLocation();
			if (pl == null || (Object)pl.planet == (Object)null || (Object)pl.planet != (Object)s.planet)
			{
				return -1f;
			}
			return (float)(System.Math.Abs(WeatherSystem.WrapPi(s.centerAngle - pl.position.AngleRadians)) * s.planet.Radius);
		}
		catch
		{
			return -1f;
		}
	}
}
