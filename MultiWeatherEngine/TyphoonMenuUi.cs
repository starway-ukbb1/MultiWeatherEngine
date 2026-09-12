using System.Text;
using UnityEngine;
using SFS.World;

namespace MultiWeatherEngine;

// ===================== F6「星程气象 指挥中心」（全面革新版 · 与 SpaceXHUD 同色系） =====================
// 布局：左侧类型栏（一列 7 类，色条 = 强度色，一键召唤）+ 右侧操作栏（附属现象/全局操作）+
// 底部选中态信息条。整体可拖动（拖标题栏），位置自动避开 SpaceXHUD 顶条。
public static class TyphoonMenuUi
{
	private static Vector2 offset = Vector2.zero;
	private static bool dragging;
	private static Vector2 grabDelta;
	private static readonly StringBuilder sb = new StringBuilder(160);

	public static void Draw(TyphoonManager main)
	{
		if (!main.menuOpen || !TyphoonConfig.I.hud)
		{
			return;
		}
		float s = UiTheme.Scale;
		float bw = 620f * s;
		// 高度按内容定：标题 36 + 左栏 218（7 类）与右栏 207 取大 + 底部提示。原 432s 是
		// 右栏从类型栏底部才开始（右上留了 ~200×280s 的空白）硬撑出来的高度。
		float bh = 282f * s;
		float baseX = (Screen.width - bw) * 0.5f;
		float baseY = UiTheme.TopReserve + 6f * s;
		Rect w = new Rect(baseX + offset.x, baseY + offset.y, bw, bh);

		// 拖动（标题栏热区排除右侧关闭按钮 84s）；双击标题栏复位位置。
		Event e = Event.current;
		if (e.type == EventType.MouseDown && e.button == 0 && new Rect(w.x, w.y, bw - 84f * s, 30f * s).Contains(e.mousePosition))
		{
			if (e.clickCount >= 2)
			{
				offset = Vector2.zero;   // 双击复位：原实现无边界钳制，拖出屏幕后就再也找不回来
			}
			else
			{
				dragging = true;
				grabDelta = e.mousePosition - new Vector2(w.x, w.y);
			}
			e.Use();
		}
		else if (e.type == EventType.MouseDrag && dragging)
		{
			// 钳制在屏幕内（留 4s 边距，保证标题栏始终可抓）
			Vector2 want = e.mousePosition - grabDelta - new Vector2(baseX, baseY);
			want.x = Mathf.Clamp(want.x, -baseX + 4f, Screen.width - bw - baseX - 4f);
			want.y = Mathf.Clamp(want.y, -(baseY - 4f), Screen.height - bh - baseY - 4f);
			offset = want;
			e.Use();
		}
		else if (e.type == EventType.MouseUp && dragging)
		{
			dragging = false;
			e.Use();
		}

		UiTheme.PanelBg(w, 0.94f);
		UiTheme.Fill(new Rect(w.x, w.y, w.width, 3f * s), UiTheme.Engine);

		float px = w.x + 12f * s;
		float py = w.y + 8f * s;
		UiTheme.Fill(new Rect(px, py + 2f * s, 3f * s, 14f * s), UiTheme.Engine);   // 标题前色块
		UiTheme.TextBold(new Rect(px + 8f * s, py, 260f * s, 18f * s), "星程气象 · 指挥中心", Mathf.RoundToInt(14f * s), UiTheme.TextHover);
		UiTheme.DrawText(new Rect(px + 178f * s, py + 3f * s, 340f * s, 14f * s), "拖标题栏移动 · 双击复位 · 点类型即刻召唤", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.5f));
		if (GUI.Button(new Rect(w.xMax - 62f * s, py, 52f * s, 20f * s), "关闭", UiTheme.Button(Mathf.RoundToInt(11f * s))))
		{
			main.menuOpen = false;
			return;
		}
		py += 28f * s;
		float top = py;   // 左右两栏共同的顶（原实现右栏从类型栏底部才开始 → 右上空白）

		// ---- 左：类型栏 ----
		float railW = 300f * s;
		float rowH = 30f * s;
		UiTheme.Fill(new Rect(px, py, railW, rowH * WeatherSystem.Spec.Length + 8f * s), UiTheme.A(UiTheme.PanelDeep, 0.55f));
		for (int i = 0; i < WeatherSystem.Spec.Length; i++)
		{
			WeatherSystem.TypeSpec sp = WeatherSystem.Spec[i];
			Rect row = new Rect(px + 3f * s, py + 4f * s + rowH * i, railW - 6f * s, rowH - 3f * s);
			bool sel = i == main.selectedType;
			if (sel)
			{
				UiTheme.Fill(row, UiTheme.A(UiTheme.Engine, 0.16f));
			}
			Color accent = UiTheme.IntensityOf(sp.defaultCat);
			UiTheme.Fill(new Rect(row.x, row.y + 3f * s, 3f * s, row.height - 6f * s), accent);
			// 名称固定占上半个行高（原实现用整行高 + MiddleLeft 居中，和贴底的类型描述
			// 在 15-19s 区间是重叠的 —— 两行字糊在一起）
			UiTheme.DrawText(new Rect(row.x + 9f * s, row.y + 1f * s, railW - 100f * s, 14f * s), sp.name, Mathf.RoundToInt(12f * s),
				sel ? UiTheme.TextHover : UiTheme.A(UiTheme.Text, 0.92f));
			// 类型特征小条（默认等级/上限）
			UiTheme.DrawText(new Rect(row.xMax - 92f * s, row.y, 84f * s, row.height),
				"C" + sp.defaultCat + " ≤C" + sp.maxCat, Mathf.RoundToInt(9.5f * s), UiTheme.A(UiTheme.Staging, 0.9f), TextAnchor.MiddleRight);
			if (GUI.Button(row, GUIContent.none, UiTheme.Hit()))
			{
				main.selectedType = i;
				SpawnAtView(main, (StormType)i);
			}
			UiTheme.DrawText(new Rect(row.x + 9f * s, row.y + row.height - 12f * s, railW - 100f * s, 11f * s), sp.desc, Mathf.RoundToInt(9f * s), UiTheme.A(UiTheme.Text, 0.4f));
		}
		// ---- 右：选中系统快照（顶部）+ 附加现象 + 全局操作 ----
		float ox = px + railW + 14f * s;
		float ow = w.width - (ox - w.x) - 12f * s;
		WeatherSystem sel2 = main.selected;
		bool hasSel = sel2 != null && sel2.active;
		float by = top;

		// ① 选中系统快照卡（原实现放在右栏最底部、右栏又从类型栏底部才开始 → 右上大片空白）
		UiTheme.DrawText(new Rect(ox, by, ow, 13f * s), "选中系统", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.5f));
		by += 14f * s;
		float cardH = 66f * s;
		UiTheme.Fill(new Rect(ox, by, ow, cardH), UiTheme.A(UiTheme.PanelDeep, 0.5f));
		UiTheme.Fill(new Rect(ox, by, 3f * s, cardH), hasSel ? UiTheme.IntensityOf(sel2.category) : UiTheme.Staging);
		float cx2 = ox + 9f * s;
		float cw = ow - 18f * s;
		sb.Clear();
		if (hasSel)
		{
			UiTheme.TextBold(new Rect(cx2, by + 4f * s, cw, 16f * s),
				WeatherSystem.TypeName(sel2.type) + "  " + WeatherSystem.StrengthName(sel2.type, sel2.category),
				Mathf.RoundToInt(12f * s), UiTheme.IntensityOf(sel2.category));
			UiTheme.DrawText(new Rect(cx2, by + 22f * s, cw, 13f * s),
				"阶段 " + (sel2.stage == 0 ? "发展" : (sel2.stage == 1 ? "成熟" : "消散")) + " · 能量 " + sel2.energy.ToString("0") + "% · 峰风 " + sel2.EyewallWind.ToString("0") + " m/s",
				Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.8f));
			UiTheme.DrawText(new Rect(cx2, by + 36f * s, cw, 13f * s),
				"半径 " + (sel2.Rmax / 1000.0).ToString("0.##") + " km · 云顶 " + (sel2.Htop / 1000.0).ToString("0.0") + " km · " + WeatherSystem.TerrainName(sel2.terrainKind),
				Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.8f));
			// 龙卷现役/潜势（与雷达的「龙卷预报」同源，召唤前就能知道这个系统产不产龙卷）
			if (sel2.tornadoes.Count > 0)
			{
				sb.Append("龙卷 现役 ").Append(sel2.tornadoes.Count).Append(" 个 · EF").Append(sel2.StrongestTornadoEf());
			}
			else if (sel2.tornadoQuota < 0)
			{
				sb.Append("龙卷潜势 不限额（现实性配额已关）");
			}
			else if (sel2.tornadoQuota == 0)
			{
				sb.Append("龙卷潜势 无（种子未配上）");
			}
			else
			{
				sb.Append("龙卷潜势 余 ").Append(sel2.tornadoQuota).Append(" 个 · ").Append(sel2.stage == 1 ? "成熟期触发" : "待成熟");
			}
			UiTheme.DrawText(new Rect(cx2, by + 50f * s, cw, 13f * s), sb.ToString(), Mathf.RoundToInt(10f * s),
				sel2.tornadoes.Count > 0 ? UiTheme.Warn : UiTheme.A(UiTheme.Throttle, 0.9f));
		}
		else
		{
			UiTheme.TextBold(new Rect(cx2, by + 4f * s, cw, 16f * s), "未选中系统", Mathf.RoundToInt(12f * s), UiTheme.A(UiTheme.Text, 0.6f));
			UiTheme.DrawText(new Rect(cx2, by + 22f * s, cw, 13f * s), "点左侧类型即刻召唤；", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.55f));
			UiTheme.DrawText(new Rect(cx2, by + 36f * s, cw, 13f * s), "左下列表点行可选中已有系统。", Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.55f));
		}
		by += cardH + 9f * s;

		// ② 附加现象 + 全局操作
		UiTheme.DrawText(new Rect(ox, by, ow, 13f * s), hasSel ? "附加现象（作用于选中系统）" : "附加现象：先在左下列表点选一个系统",
			Mathf.RoundToInt(10f * s), UiTheme.A(UiTheme.Text, 0.55f));
		by += 15f * s;

		float bx = ox;
		float bwid = (ow - 8f * s) * 0.5f;
		if (Op(bx, by, bwid, s, "加龙卷", UiTheme.Throttle)) main.AddPhenomenon(1);
		if (Op(bx + bwid + 8f * s, by, bwid, s, "加下击暴流", UiTheme.Throttle)) main.AddPhenomenon(2);
		by += 26f * s;
		if (Op(bx, by, bwid, s, "加阵风锋", UiTheme.Engine)) main.AddPhenomenon(3);
		if (Op(bx + bwid + 8f * s, by, bwid, s, "加闪电风暴", UiTheme.Engine)) main.AddPhenomenon(4);
		by += 26f * s;
		if (Op(bx, by, bwid, s, "清除附属", UiTheme.Staging)) main.AddPhenomenon(5);
		if (Op(bx + bwid + 8f * s, by, bwid, s, "全部分散", UiTheme.Warn)) main.DespawnAll();
		by += 26f * s;
		if (Op(bx, by, bwid, s, "风眼对准玩家", UiTheme.Fuel))
		{
			if (hasSel)
			{
				Location loc = TyphoonManager.GetPlayerLocation();
				if (loc != null)
				{
					sel2.centerAngle = loc.position.AngleRadians;
					TyphoonManager.Msg("风眼已对准当前位置");
				}
			}
		}
		if (Op(bx + bwid + 8f * s, by, bwid, s, hasSel ? "强度 +1 (F8)" : "强度 +1", UiTheme.Fuel))
		{
			if (hasSel)
			{
				sel2.SetCategory((sel2.category + 1) % 7);
				sel2.naturalProgress = 0.0;
				sel2.energy = System.Math.Max(sel2.energy, 80.0);
				TyphoonManager.Msg(WeatherSystem.TypeName(sel2.type) + " 强度 → " + WeatherSystem.StrengthName(sel2.type, sel2.category) + " (" + sel2.vmaxTarget.ToString("0") + " m/s)");
			}
		}

		// ③ 底部提示
		UiTheme.Fill(new Rect(px, w.yMax - 20f * s, w.width - 24f * s, 1f), UiTheme.A(UiTheme.Text, 0.12f));
		UiTheme.DrawText(new Rect(px, w.yMax - 17f * s, w.width - 24f * s, 13f * s),
			"F6 关闭菜单 · F9 底部活跃系统列表 · F7 解散选中 · F8 强度 +1 · 雷达与龙卷预报见右上",
			Mathf.RoundToInt(9.5f * s), UiTheme.A(UiTheme.Text, 0.45f));
	}

	// 召唤：落点自动落在相机可视范围内（原实现保留）
	private static void SpawnAtView(TyphoonManager main, StormType t)
	{
		Location loc = TyphoonManager.GetPlayerLocation();
		if (loc == null || loc.planet == null)
		{
			return;
		}
		double lead = TyphoonConfig.I.spawnLeadDistanceMeters;
		try
		{
			double vd = ((SFS.Variables.Obs<float>)(object)WorldView.main.viewDistance).Value;
			lead = System.Math.Min(lead, System.Math.Max(4000.0, vd * 0.55));
		}
		catch
		{
		}
		main.SpawnSystem(t, loc, lead, 0, false, true);   // 指挥中心召唤：直接成熟
	}

	private static bool Op(float x, float y, float w, float s, string label, Color accent)
	{
		Rect r = new Rect(x, y, w, 22f * s);
		bool down = GUI.Button(r, label, UiTheme.Button(Mathf.RoundToInt(11f * s)));
		UiTheme.Fill(new Rect(r.x, r.yMax - 2f * s, r.width, 2f * s), UiTheme.A(accent, down ? 0.9f : 0.5f));
		return down;
	}
}
