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
		// 窗口高度固定：左侧类型栏改为「固定可见行数 + 滚轮滚动」，17 类不再把整窗撑高。
		float railViewH = (30f * 9f + 6f) * s;        // 可见约 9 行（30s 行高），其余滚轮滚动
		float bh = 28f * s + Mathf.Max(railViewH, 300f * s) + 20f * s;
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

		// ---- 左：类型栏（固定可见行数 + 滚轮滚动 + 每类小图标）----
		float railW = 300f * s;
		float rowH = 30f * s;
		float railX = px, railY = py;
		UiTheme.Fill(new Rect(railX, railY, railW, railViewH), UiTheme.A(UiTheme.PanelDeep, 0.55f));
		// 滚轮滚动（仅悬停类型栏时）
		if (e.type == EventType.ScrollWheel && new Rect(railX, railY, railW, railViewH).Contains(e.mousePosition))
		{
			float contentH = rowH * WeatherSystem.Spec.Length + 8f * s;
			float maxScroll = Mathf.Max(0f, contentH - railViewH);
			listScroll = Mathf.Clamp(listScroll + e.delta.y * 22f * s, 0f, maxScroll);
			e.Use();
		}
		GUI.BeginGroup(new Rect(railX, railY, railW, railViewH));   // 裁剪视口
		for (int i = 0; i < WeatherSystem.Spec.Length; i++)
		{
			WeatherSystem.TypeSpec sp = WeatherSystem.Spec[i];
			float ry = 4f * s + rowH * i - listScroll;
			if (ry + (rowH - 3f * s) < 0f || ry > railViewH)   // 视口外跳过绘制
			{
				continue;
			}
			Rect row = new Rect(3f * s, ry, railW - 6f * s, rowH - 3f * s);
			bool sel = i == main.selectedType;
			if (sel)
			{
				UiTheme.Fill(row, UiTheme.A(UiTheme.Engine, 0.16f));
			}
			Color accent = UiTheme.IntensityOf(sp.defaultCat);
			UiTheme.Fill(new Rect(row.x, row.y + 3f * s, 3f * s, row.height - 6f * s), accent);
			// 类型小图标（图元绘制，无需外部图片）
			Rect ir = new Rect(row.x + 8f * s, row.y + (row.height - 18f * s) * 0.5f, 18f * s, 18f * s);
			DrawTypeIcon(ir, (StormType)i);
			float tx = row.x + 30f * s;
			UiTheme.DrawText(new Rect(tx, row.y + 1f * s, railW - 122f * s, 14f * s), sp.name, Mathf.RoundToInt(12f * s),
				sel ? UiTheme.TextHover : UiTheme.A(UiTheme.Text, 0.92f));
			// 类型特征小条（类型专属等级名 + 上限档）
			UiTheme.DrawText(new Rect(row.xMax - 92f * s, row.y, 84f * s, row.height),
				WeatherSystem.StrengthName((StormType)i, sp.defaultCat) + " ≤" + sp.maxCat, Mathf.RoundToInt(9.5f * s), UiTheme.A(UiTheme.Staging, 0.9f), TextAnchor.MiddleRight);
			if (GUI.Button(row, GUIContent.none, UiTheme.Hit()))
			{
				main.selectedType = i;
				SpawnAtView(main, (StormType)i);
			}
			UiTheme.DrawText(new Rect(tx, row.y + row.height - 12f * s, railW - 122f * s, 11f * s), sp.desc, Mathf.RoundToInt(9f * s), UiTheme.A(UiTheme.Text, 0.4f));
		}
		GUI.EndGroup();
		// 滚动条（内容超出时）
		float contentH2 = rowH * WeatherSystem.Spec.Length + 8f * s;
		if (contentH2 > railViewH + 0.5f)
		{
			float barX = railX + railW - 4f * s;
			float thumbH = Mathf.Max(18f * s, railViewH * railViewH / contentH2);
			float thumbY = railY + listScroll * (railViewH - thumbH) / (contentH2 - railViewH);
			UiTheme.Fill(new Rect(barX, thumbY, 3f * s, thumbH), UiTheme.A(UiTheme.Text, 0.25f));
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
				if (sel2.type == StormType.Typhoon && sel2.category >= 6)
				{
					sel2.MakeHypercane();   // 满档台风再 +1 → 超级飓风（指挥中心触发）
					TyphoonManager.Msg("超级飓风 Hypercane 已激活（峰值 ≈800 km/h）");
				}
				else
				{
					sel2.SetCategory((sel2.category + 1) % 7);
				}
				sel2.naturalProgress = 0.0;
				sel2.energy = System.Math.Max(sel2.energy, 80.0);
				TyphoonManager.Msg(WeatherSystem.TypeName(sel2.type) + " 强度 → " + WeatherSystem.StrengthName(sel2.type, sel2.category) + " (" + sel2.vmaxTarget.ToString("0") + " m/s)");
			}
		}

		// ③ 底部提示
		UiTheme.Fill(new Rect(px, w.yMax - 20f * s, w.width - 24f * s, 1f), UiTheme.A(UiTheme.Text, 0.12f));
		UiTheme.DrawText(new Rect(px, w.yMax - 17f * s, w.width - 24f * s, 13f * s),
			"F6 关闭菜单 · 滚轮滚动类型 · F9 底部活跃系统列表 · F7 解散选中 · F8 强度 +1 · 雷达与龙卷预报见右上",
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

	// ===== 类型小图标（纯图元绘制，无外部图片）=====
	private static float listScroll;                 // 类型栏滚动偏移
	private static Texture2D discTex;                // 一次性生成的软边圆（用于旋风眼/雪/云）
	private static Texture2D DiscTex
	{
		get
		{
			if (discTex == null)
			{
				discTex = MakeDisc();
			}
			return discTex;
		}
	}

	// 每类图标颜色（按族区分，便于一眼辨认）
	private static Color IconColor(StormType t)
	{
		switch (t)
		{
		case StormType.Typhoon: return new Color(0.30f, 0.80f, 0.95f, 1f);   // 青
		case StormType.Megastorm: return new Color(0.62f, 0.52f, 0.86f, 1f);  // 紫
		case StormType.PolarVortex: return new Color(0.66f, 0.86f, 1.0f, 1f); // 冰蓝
		case StormType.ExtratropicalCyclone: return new Color(0.56f, 0.66f, 0.82f, 1f);
		case StormType.Cell: case StormType.Multicell: case StormType.Supercell: case StormType.MCS:
			return new Color(0.72f, 0.56f, 0.90f, 1f);                         // 云紫
		case StormType.SquallLine: case StormType.AtmosphericRiver: case StormType.Derecho:
			return new Color(0.45f, 0.86f, 0.55f, 1f);                         // 绿（线状）
		case StormType.DustStorm: case StormType.DustDevil:
			return new Color(0.82f, 0.70f, 0.45f, 1f);                         // 沙
		case StormType.WinterStorm: case StormType.IceStorm:
			return new Color(0.86f, 0.93f, 1.0f, 1f);                          // 雪白
		case StormType.DenseFog: return new Color(0.70f, 0.72f, 0.75f, 1f);    // 灰
		case StormType.Firestorm: return new Color(0.98f, 0.45f, 0.18f, 1f);   // 火橙
		default: return new Color(0.7f, 0.7f, 0.7f, 1f);
		}
	}

	private static Color Tint(Color c, float m)
	{
		return new Color(Mathf.Clamp01(c.r * m), Mathf.Clamp01(c.g * m), Mathf.Clamp01(c.b * m), c.a);
	}

	// 在 ir 内绘制该类型的示意图标
	private static void DrawTypeIcon(Rect ir, StormType t)
	{
		Color prev = GUI.color;
		Color col = IconColor(t);
		bool cyclone = t == StormType.Typhoon || t == StormType.Megastorm || t == StormType.PolarVortex;
		bool cloud = t == StormType.Cell || t == StormType.Multicell || t == StormType.Supercell || t == StormType.MCS;
		bool line = t == StormType.SquallLine || t == StormType.AtmosphericRiver || t == StormType.Derecho;
		bool dust = t == StormType.DustStorm || t == StormType.DustDevil;
		bool winter = t == StormType.WinterStorm || t == StormType.IceStorm;
		bool fire = t == StormType.Firestorm;
		if (line || t == StormType.DenseFog)
		{
			// 横条（线状降雨带 / 贴地雾层）
			UiTheme.Fill(new Rect(ir.x, ir.y + ir.height * 0.35f, ir.width, ir.height * 0.30f), col);
			if (line)   // 弓形隆起（飑线/德雷科特征）
			{
				UiTheme.Fill(new Rect(ir.x + ir.width * 0.28f, ir.y + ir.height * 0.08f, ir.width * 0.44f, ir.height * 0.32f), col);
			}
		}
		else
		{
			// 圆形主体
			GUI.color = col;
			GUI.DrawTexture(ir, DiscTex, ScaleMode.StretchToFill, true);
			if (cyclone || t == StormType.ExtratropicalCyclone)
			{
				// 眼（深色挖空）+ 旋臂（竖条）
				GUI.color = new Color(0.08f, 0.10f, 0.14f, 1f);
				GUI.DrawTexture(new Rect(ir.x + ir.width * 0.34f, ir.y + ir.height * 0.34f, ir.width * 0.32f, ir.height * 0.32f), DiscTex, ScaleMode.StretchToFill, true);
				GUI.color = col;
				GUI.DrawTexture(new Rect(ir.x + ir.width * 0.45f, ir.y + ir.height * 0.10f, ir.width * 0.12f, ir.height * 0.80f), DiscTex, ScaleMode.StretchToFill, true);
			}
			else if (cloud)
			{
				// 上方小云突（浅色）
				GUI.color = Tint(col, 1.3f);
				GUI.DrawTexture(new Rect(ir.x + ir.width * 0.08f, ir.y, ir.width * 0.48f, ir.height * 0.46f), DiscTex, ScaleMode.StretchToFill, true);
			}
			else if (winter)
			{
				// 雪花：十字 + 斜叉
				Color w = new Color(0.92f, 0.96f, 1.0f, 1f);
				UiTheme.Fill(new Rect(ir.x + ir.width * 0.46f, ir.y + ir.height * 0.10f, ir.width * 0.08f, ir.height * 0.80f), w);
				UiTheme.Fill(new Rect(ir.x + ir.width * 0.10f, ir.y + ir.height * 0.46f, ir.width * 0.80f, ir.height * 0.08f), w);
				UiTheme.Fill(new Rect(ir.x + ir.width * 0.20f, ir.y + ir.height * 0.20f, ir.width * 0.60f, ir.height * 0.08f), w);
				UiTheme.Fill(new Rect(ir.x + ir.width * 0.20f, ir.y + ir.height * 0.72f, ir.width * 0.60f, ir.height * 0.08f), w);
			}
			else if (dust)
			{
				// 尘锥：上窄下宽（三条横条）
				for (int k = 0; k < 3; k++)
				{
					float f = k / 3f;
					UiTheme.Fill(new Rect(ir.x + ir.width * (0.30f + f * 0.16f), ir.y + ir.height * (0.16f + f * 0.26f), ir.width * (0.40f - f * 0.28f), ir.height * 0.16f), col);
				}
			}
			else if (fire)
			{
				// 火苗：底宽顶尖（三条横条收窄）
				for (int k = 0; k < 3; k++)
				{
					float f = k / 3f;
					UiTheme.Fill(new Rect(ir.x + ir.width * (0.22f + f * 0.20f), ir.y + ir.height * (0.56f - f * 0.18f), ir.width * (0.56f - f * 0.38f), ir.height * 0.18f), col);
				}
			}
		}
		GUI.color = prev;
	}

	// 生成 64×64 软边圆纹理（白色 + alpha），靠 GUI.color 着色
	private static Texture2D MakeDisc()
	{
		Texture2D t = new Texture2D(64, 64, TextureFormat.ARGB32, false);
		Color[] c = new Color[64 * 64];
		for (int y = 0; y < 64; y++)
		{
			for (int x = 0; x < 64; x++)
			{
				float dx = (x - 31.5f) / 31.5f;
				float dy = (y - 31.5f) / 31.5f;
				float d = Mathf.Sqrt(dx * dx + dy * dy);
				float a = Mathf.Clamp01(1f - (d - 0.84f) / 0.16f);
				c[y * 64 + x] = new Color(1f, 1f, 1f, a);
			}
		}
		t.SetPixels(c);
		t.Apply();
		return t;
	}
}
