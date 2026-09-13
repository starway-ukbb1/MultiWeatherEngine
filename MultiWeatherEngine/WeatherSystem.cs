using System;
using System.Collections.Generic;
using SFS.WorldBase;
using UnityEngine;

namespace MultiWeatherEngine;

// ===================== 天气系统类型 =====================
public enum StormType
{
	Typhoon,      // 热带气旋（台风，原 Storm）
	Cell,         // 普通单体雷暴
	Multicell,    // 多单体风暴
	Supercell,    // 超级单体（深厚中气旋）
	SquallLine,   // 飑线（线状 MCS，弓形+阵风锋）
	MCS,          // 中尺度对流系统（大尺度，含涡旋）
	// 沙尘暴独立天气系统（用户：沙尘暴应该是独立的天气系统才对）：
	// 不依赖雷暴——蒙古气旋/冷锋驱动的干旱区沙尘暴是独立事件。沙漠地形自然生成，
	// 无降雨无闪电（干燥系统），沙尘层贴地 + 强水平风，可挂阵风锋（Haboob 沙墙前沿）。
	DustStorm,    // 沙尘暴（干旱区强风卷沙，沙墙推进）
	// ===== 扩展天气系统（现实气象普查新增，2026-09-12）=====
	// 原 7 类集中在对流风暴族 + 热带气旋 + 沙尘暴；补齐现实里另外几类大缺失系统：
	// 按尺度覆盖"天气尺度（温带气旋/大气河）→ 中尺度（冬季风暴/冰暴）→ 静稳（浓雾）
	// → 小尺度（尘卷风）"，并按"相态"补齐固态降水（雪/冰粒）与零降水（雾/尘卷）。
	ExtratropicalCyclone,  // 温带气旋（锋面气旋：冷心、冷锋/暖锋、逗点云盾，中纬度主流）
	WinterStorm,           // 冬季风暴（暴雪：固态降水 + 强风 + 低能见度）
	IceStorm,              // 冰暴（冻雨/冰粒：逆温层过冷雨滴，落地结冰）
	DenseFog,              // 浓雾（辐射雾/海雾：静稳贴地雾层，能见度极低、无降水）
	AtmosphericRiver,      // 大气河（数千公里水汽输送带：长带暴雨）
	DustDevil,             // 尘卷风（晴空干燥热对流涡旋：贴地尘柱，数分钟）
	// ===== 理论/极端风暴（2026-09-13 新增 4 类）=====
	Firestorm,             // 火焰风暴（火积雨云 + 火旋风：大火自维持的超强上升气流）
	Derecho,               // 德雷科（长生命直线风风暴：路径数百公里，飑线升级版）
	PolarVortex,           // 极地涡旋（极地高空冷性大涡旋南下：大范围严寒风暴）
	Megastorm              // 超级风暴（假想全球尺度超强气旋：《后天》式）
	// 龙卷/下击暴流不再是独立系统，而是宿主系统的附属现象
	// （龙卷产自超级单体中气旋/飑线涡旋，下击暴流产自成熟雷暴）。
}

// 地形类型（地理位置可行性扩展：沙漠 vs 绿地判定）。
// 研究结论（反编译 Assembly-CSharp.dll 验证）：SFS 是 2D 行星、无经纬度/biome 标签，
// 行星表面类型 = 纹理（TerrainModule.TerrainTexture 只有纹理层配置）。
// 正相判定：Planet.GetTerrainColor(position) 采样行星地表纹理像素色——
// 绿=植被、黄橙=沙漠/旱地、近白高亮低饱和=冰/雪、灰=岩石；
// 变相判定：GetTerrainHeightAtAngle 高度场分海陆（>0.5m=陆地），比颜色判海更准。
public enum TerrainKind
{
	Ocean,    // 海洋/水下（高度场判定）
	Green,    // 绿地/植被
	Desert,   // 沙漠/旱地（暖色主导）
	Ice,      // 冰盖/雪原（高亮低饱和）
	Rock      // 岩石/山地（中性灰）
}

// ===================== 天气系统（ 多天气引擎核心） =====================
// 一切对流系统/台风共用一个模型：行星锚定 + 生命周期 + 参数化风场。
// 尺度参数来自气象资料调研（2026-08-04）：单体 1-10km/25-45min、多单体 2-6h、
// 超级单体 20-40km/2-6h/中气旋、飑线 >50km 线状/弓形、MCS 100-400km/4-8h。
public class WeatherSystem
{
	// ===== 类型静态参数表 =====
	// 尺度全部按比例（SFS 行星=现实 5%、大气=现实 30%）：
	// rmaxFrac = 相对行星半径的比例（现实 Rmax / 6371km）
	// htopFrac = 相对大气高度的比例（现实 Htop / 100km）
	public class TypeSpec
	{
		public string name;        // 中文显示名
		public double rmaxFrac;    // 核心半径 ÷ 行星半径（现实比例）
		public double htopFrac;    // 云顶高度 ÷ 大气高度（现实比例）
		public double hbaseM;      // 云底高度（现实米 AGL，LCL 凝结层；SFS = ×0.3）
		public double aspectMin;   // 高宽比下限（Rmax ≥ Htop×aspectMin，防 SFS 大气厚导致细长柱）
		public double vmaxMs;      // 基准峰值风速（m/s，非台风 category=3 时；台风仅作归一化基准 = PeakWind[5] CAT-4）— （终审🟡-15）注释修正：62 在 PeakWind 表是索引 5（CAT-4）非 category 3
		public double updraft;     // 垂直速度系数（Wmax = Vmax × updraft）
		// 三阶段时长（游戏秒，现实口径）——结构整理：原实现把这三组数（发育 25 能量 /
		// 成熟 50 能量 / 消散 30 能量各自的速率）以三条 if-else 链硬编码在 Advance 内，
		// 与 Spec 表重复且容易失配。现在表是唯一真源，Advance 直接读（数值完全等价）。
		// developSec：energy 55→80；matureSec：80→30（净耗散）；dissolveSec：30→0。
		public double developSec;
		public double matureSec;
		public double dissolveSec;
		public double rotate;      // 旋转强度 0-1（超级单体中气旋）
		public double downburst;   // 下沉/出流强度 0-1（成熟期下击暴流倾向）
		public double line;        // 线状强度 0-1（飑线）
		public double gustFront;   // 阵风锋/出流强度 0-1
		public bool tornadoHost;   // 成熟期可产生龙卷（超级单体/飑线/MCS）
		public int defaultCat;     // 默认强度级 0-6
		// 等级上限科学性（用户：似乎所有天气系统都能到最高等级）：maxCat 按类型
		// 差异化——台风 6（Category 0-6 恰好=萨菲尔全表 TD/TS/CAT-1~5）、沙尘暴 4（GB/T
		// 国标 5 档）、超单/飑线/MCS 4（组织化描述无统一 6 级）、多单体 2、普通单体 1
		// （现实无等级概念，永不升级）。naturalUpTimeSec：升 1 级需要的游戏秒（×cL 大气
		// 分级，现实化：升 1 档=成熟期寿命 10-20%）；maxCat==defaultCat 的类型填 0 用不上。
		public int maxCat;
		public double naturalUpTimeSec;
		// 降水相态（渲染分流，2026-09-12 扩展）：0=液态雨、1=固态雪/冰粒、2=无降水。
		// 原实现"有无降雨"是 BuildRain 里散落的硬编码判断（DustStorm 特例），扩展后
		// 统一由本字段驱动——避免每加一个干燥/固态系统就要多改一处 if。
		public int precipKind;     // 0 雨 / 1 雪 / 2 无
		public string desc;        // 一句话说明
	}

	// 修复历史错位：Spec 数组元素重排对齐 StormType 枚举（反编译确认 [1..5] 曾错位：
	// 旧顺序 [台风, MCS, 超单, 飑线, 多单体, 单体] vs 枚举 [台风, Cell, Multicell, Supercell,
	// SquallLine, MCS]——界面自洽但 type 字段逻辑分支（下暴白名单/转变链/雨轮廓）全对错类型）。
	// 现在索引 = (int)StormType 严格一致：Typhoon=0 Cell=1 Multicell=2 Supercell=3 SquallLine=4 MCS=5 DustStorm=6。
	public static readonly TypeSpec[] Spec = new TypeSpec[17]   // 7 原有 + 6 扩展 + 4 理论/极端（火焰风暴/德雷科/极地涡旋/超级风暴）
	{
		// 现实标定速查（三阶段时长 = energy 55→80 / 80→30 / 30→0 的游戏秒）：
		// 台风 2.5d/5d/12h（现实生命史 5-9 天 ✓）、单体 20min/45min/20min（现实 30-60min ✓）、
		// 多单体 1.5h/8h/3h、超级单体 1.5h/8h/2h（现实 2-6h，8h 取上界）、
		// 飑线 3h/18h/6h（现实 6-12h，长寿命线状对流可达 24h）、MCS 3h/18h/6h、
		// 沙尘暴 4h/24h/8h（局地 Haboob 数十分钟、大范围沙尘暴 1-3 天）。
		new TypeSpec
		{
			name = "台风", rmaxFrac = 0.0079, htopFrac = 0.15, hbaseM = 600, aspectMin = 0.45,
			vmaxMs = 62, updraft = 0.32, developSec = 216000, matureSec = 432000, dissolveSec = 43200,
			rotate = 1.0, downburst = 0.0, line = 0.0, gustFront = 0.0,
			tornadoHost = false, defaultCat = 4, maxCat = 6, naturalUpTimeSec = 43200,
			desc = "热带气旋，最大天气系统"
		},   // maxCat 6（萨菲尔全表 0-6）；升级 12h/级（现实）；Rmax 现实 50km、云顶 15km
		// 风速口径修正（现实性审计）：非台风类型的实际峰值 = vmaxMs×(0.5+cat/6)，普通单体
		// maxCat=1 → 只有 0.667 档。原 vmaxMs=18 → 实际阵风仅 12 m/s（6 级），而现实普通
		// 雷暴地面阵风 15-25 m/s；多单体 25 → 实际 20.8 m/s，现实 25-35 m/s。把 vmaxMs
		// 抬到"典型档落进现实区间"：单体 27→实际 18 m/s、多单体 36→实际 30 m/s。
		new TypeSpec
		{
			name = "单体", rmaxFrac = 0.0006, htopFrac = 0.10, hbaseM = 1200, aspectMin = 0.40,
			vmaxMs = 27, updraft = 0.50, developSec = 1200, matureSec = 2700, dissolveSec = 1200,
			rotate = 0.0, downburst = 0.0, line = 0.0, gustFront = 0.20,
			tornadoHost = false, defaultCat = 1, maxCat = 1, naturalUpTimeSec = 0,
			desc = "普通单体雷暴：发育20+成熟45+消散20≈85min（能量制实际）"
		},   // maxCat 1（现实无等级概念，永不升级）
		new TypeSpec
		{
			name = "多单体", rmaxFrac = 0.0013, htopFrac = 0.11, hbaseM = 1200, aspectMin = 0.40,
			vmaxMs = 36, updraft = 0.50, developSec = 5400, matureSec = 28800, dissolveSec = 10800,
			rotate = 0.10, downburst = 0.0, line = 0.0, gustFront = 0.30,
			tornadoHost = false, defaultCat = 2, maxCat = 2, naturalUpTimeSec = 0,
			desc = "多单体风暴：团状，新生单体更替，2-6h"
		},   // maxCat 2
		new TypeSpec
		{
			name = "超级单体", rmaxFrac = 0.0019, htopFrac = 0.12, hbaseM = 1000, aspectMin = 0.50,
			vmaxMs = 38, updraft = 0.55, developSec = 5400, matureSec = 28800, dissolveSec = 7200,
			rotate = 0.85, downburst = 0.30, line = 0.0, gustFront = 0.40,
			tornadoHost = true, defaultCat = 4, maxCat = 4, naturalUpTimeSec = 0,
			desc = "超级单体：深厚中气旋，可产龙卷"
		},   // maxCat 4
		new TypeSpec
		{
			name = "飑线", rmaxFrac = 0.0031, htopFrac = 0.12, hbaseM = 1200, aspectMin = 0.40,
			vmaxMs = 32, updraft = 0.50, developSec = 10800, matureSec = 64800, dissolveSec = 21600,
			rotate = 0.10, downburst = 0.40, line = 0.85, gustFront = 0.95,
			tornadoHost = true, defaultCat = 3, maxCat = 4, naturalUpTimeSec = 12960,
			desc = "飑线：线状+弓形+阵风锋，可产龙卷"
		},   // maxCat 4；升级 3.6h/级
		new TypeSpec
		{
			name = "中尺度对流系统", rmaxFrac = 0.0065, htopFrac = 0.13, hbaseM = 1200, aspectMin = 0.50,
			vmaxMs = 30, updraft = 0.50, developSec = 10800, matureSec = 64800, dissolveSec = 21600,
			rotate = 0.30, downburst = 0.30, line = 0.50, gustFront = 0.75,
			tornadoHost = true, defaultCat = 4, maxCat = 4, naturalUpTimeSec = 0,
			desc = "MCS：大尺度，含弓形飑线+多涡旋"
		},   // maxCat 4
		// 沙尘暴（独立天气系统，用户：沙尘暴应该是独立系统才对）：蒙古气旋/冷锋
		// 驱动的干旱区沙尘暴（2021 亚洲大范围沙尘暴等）不依赖雷暴。参数基于气象调研：
		// 局地 Haboob 10-100km、大尺度 100-1000km；沙尘顶 1-5km、强水平风 20-40 m/s、
		// 贴地沙层（无云底概念）、可带弱涡旋（蒙古气旋）。无降雨/闪电/龙卷。
		// 尺度 0.02→0.006（用户：沙尘暴似乎太大）：原 38km 贴地低层铺天盖地，
		// 砍到 ~11.5km（介于 MCS 8.4km 与台风 15km 之间，符合常见 Haboob 规模）。
		// htopFrac 0.08→0.04（现实性审计）：原 8km 云顶远超自身调研的"沙尘顶 1-5km"
		// （8km 已是深对流砧云高度，沙尘无法被带到那里）→ 4km 现实上限。
		new TypeSpec
		{
			name = "沙尘暴", rmaxFrac = 0.006, htopFrac = 0.04, hbaseM = 100, aspectMin = 2.0,
			vmaxMs = 28, updraft = 0.15, developSec = 14400, matureSec = 86400, dissolveSec = 28800,
			rotate = 0.25, downburst = 0.0, line = 0.4, gustFront = 0.80,
			tornadoHost = false, defaultCat = 2, maxCat = 4, naturalUpTimeSec = 17280,
			precipKind = 2,
			desc = "沙墙推进（Haboob），无降雨"
		},   // maxCat 4（GB/T 国标 5 档）；升级 4.8h/级
		// ===================== 扩展天气系统（2026-09-12 现实气象普查新增 6 类） =====================
		// 【7 温带气旋 ExtratropicalCyclone】锋面气旋——温带中高纬地区最主流的天气系统，
		// 现实直径数百-3000km（平均 ~1000km，比热带气旋大）、生命史 1-7 天（典型 3-5 天）、
		// 地面风速 15-35 m/s（强气旋可达 40+）。**冷心**（与暖心台风相反）、有冷锋/暖锋/锢囚锋、
		// 逗点云系 + 盾状卷云罩、大范围层状降水（非深对流）。可挂阵风锋（锋面强阵风），
		// 一般不利龙卷（不设 tornadoHost）。SFS 换算：Rmax=6371km×0.0105×0.3≈20km（大于台风 15km ✓）。
		new TypeSpec
		{
			name = "温带气旋", rmaxFrac = 0.0105, htopFrac = 0.14, hbaseM = 900, aspectMin = 0.45,
			vmaxMs = 34, updraft = 0.35, developSec = 259200, matureSec = 432000, dissolveSec = 172800,
			rotate = 0.45, downburst = 0.10, line = 0.55, gustFront = 0.85,
			tornadoHost = false, defaultCat = 3, maxCat = 4, naturalUpTimeSec = 64800,
			precipKind = 0,
			desc = "锋面气旋：冷心大系统，冷锋+暖锋+逗点云盾，中纬度"
		},   // 三阶段 3天/5天/2天（现实生命史 1-7 天）；updraft 低（层状为主，非深对流）
		// 【8 冬季风暴 WinterStorm】暴雪系统——中尺度降雪带 50-200km、生命 6-24h，
		// 暴风雪判据（NWS）：持续风或阵风 ≥14 m/s 且能见度 <400m。固态降水（雪），
		// 低云底（降雪云系低）、强风、能见度骤降。冰原/绿地高纬常发。
		new TypeSpec
		{
			name = "冬季风暴", rmaxFrac = 0.006, htopFrac = 0.10, hbaseM = 600, aspectMin = 0.40,
			vmaxMs = 26, updraft = 0.40, developSec = 14400, matureSec = 57600, dissolveSec = 21600,
			rotate = 0.10, downburst = 0.10, line = 0.25, gustFront = 0.55,
			tornadoHost = false, defaultCat = 2, maxCat = 3, naturalUpTimeSec = 14400,
			precipKind = 1,
			desc = "冬季风暴：暴雪+强风，能见度骤降（冰原/高纬）"
		},   // Rmax≈11.5km（降雪带 50-200km 的 30% 折算）；三阶段 4h/16h/6h（现实 6-24h）
		// 【9 冰暴 IceStorm】冻雨/冰暴——浅层逆温（暖层在冷层之上），过冷雨滴落到
		// ≤0°C 地表结冰；冻雨带数十-数百 km、生命 6-24h、风速不高（10-20 m/s）、
		// 低云底（冻雨云系浅薄）。相态同样是固态（冰粒/冻雨），但比暴雪弱、更通透。
		new TypeSpec
		{
			name = "冰暴", rmaxFrac = 0.005, htopFrac = 0.08, hbaseM = 300, aspectMin = 0.40,
			vmaxMs = 18, updraft = 0.30, developSec = 10800, matureSec = 43200, dissolveSec = 14400,
			rotate = 0.05, downburst = 0.05, line = 0.15, gustFront = 0.40,
			tornadoHost = false, defaultCat = 2, maxCat = 3, naturalUpTimeSec = 10800,
			precipKind = 1,
			desc = "冻雨/冰暴：逆温层过冷雨滴，落地结冰，低云"
		},   // 低云底（300m 现实 ×0.3）、弱风、浅薄云系（htopFrac 0.08 最低档之一）
		// 【10 浓雾 DenseFog】辐射雾/平流雾/海雾——静稳天气下的贴地凝结层，雾层厚
		// 50-500m、生命数小时-2 天；**风速 0-5 m/s（静稳）**、能见度 50-200m（浓雾 <500m、
		// 强浓雾 <50m）、**无降水无闪电**。极扁（aspectMin 3.0 强制云体变成贴地薄层）、
		// 几乎无上升气流。与全部风暴形成最大反差（"没有风也能是最糟天气"）。
		new TypeSpec
		{
			name = "浓雾", rmaxFrac = 0.008, htopFrac = 0.015, hbaseM = 0, aspectMin = 3.0,
			vmaxMs = 4, updraft = 0.02, developSec = 7200, matureSec = 86400, dissolveSec = 21600,
			rotate = 0.0, downburst = 0.0, line = 0.0, gustFront = 0.0,
			tornadoHost = false, defaultCat = 2, maxCat = 3, naturalUpTimeSec = 0,
			precipKind = 2,
			desc = "浓雾：静稳贴地雾层，能见度极低，无降水"
		},   // Htop≈450m（雾层 50-500m）、Vmax 4 m/s（静稳）；无降水/无闪电/无阵风
		// 【11 大气河 AtmosphericRiver】大气河——中纬度"水汽输送带"，宽 300-500km、
		// 长可达 2000km+（强线状）、生命 1-3 天、低空急流风速 20-30 m/s；带来持续
		// 暴雨与洪水（层状为主、非强对流）。**line 0.95**（最强线状，复用飑线云带形态）。
		new TypeSpec
		{
			name = "大气河", rmaxFrac = 0.007, htopFrac = 0.10, hbaseM = 800, aspectMin = 0.40,
			vmaxMs = 28, updraft = 0.25, developSec = 21600, matureSec = 129600, dissolveSec = 43200,
			rotate = 0.05, downburst = 0.05, line = 0.95, gustFront = 0.50,
			tornadoHost = false, defaultCat = 3, maxCat = 4, naturalUpTimeSec = 21600,
			precipKind = 0,
			desc = "大气河：数千公里水汽输送带，长带暴雨"
		},   // line 0.95 最强线状；三阶段 6h/36h/12h（现实 1-3 天）
		// 【12 尘卷风 DustDevil】尘卷风——晴朗干燥白天的热对流涡旋，直径 10-100m、
		// 高 10-1000m、生命仅数分钟，内部风速 10-20 m/s（可达 25）。**晴空无云**、
		// 贴地尘柱、强旋。SFS 换算：Rmax≈670m（现实太小看不见，适度放大）；沙漠专属
		// （非沙漠发育失败，同沙尘暴的沙源逻辑）。
		new TypeSpec
		{
			name = "尘卷风", rmaxFrac = 0.00035, htopFrac = 0.012, hbaseM = 0, aspectMin = 0.35,
			vmaxMs = 18, updraft = 0.30, developSec = 60, matureSec = 300, dissolveSec = 120,
			rotate = 0.95, downburst = 0.0, line = 0.0, gustFront = 0.0,
			tornadoHost = false, defaultCat = 1, maxCat = 2, naturalUpTimeSec = 0,
			precipKind = 2,
			desc = "尘卷风：晴空干燥热对流涡旋，贴地尘柱，数分钟"
		},   // 三阶段 60s/300s/120s（现实生命数分钟）；rotate 0.95 强旋、无降水无闪电
		// ===================== 理论/极端风暴（2026-09-13 新增 4 类） =====================
		// 【13 火焰风暴 Firestorm】火积雨云（pyrocumulonimbus）+ 火旋风：大规模火灾自身
		// 创造并维持的强对流风暴，上升气流极强（火柱可冲对流层顶）、伴烟尘与火旋风，
		// 多为干性。风速可 >40 m/s。仅手动召唤（需极端火情）。
		new TypeSpec
		{
			name = "火焰风暴", rmaxFrac = 0.0035, htopFrac = 0.16, hbaseM = 500, aspectMin = 0.50,
			vmaxMs = 40, updraft = 0.90, developSec = 1800, matureSec = 10800, dissolveSec = 3600,
			rotate = 0.70, downburst = 0.50, line = 0.20, gustFront = 0.60,
			tornadoHost = false, defaultCat = 2, maxCat = 3, naturalUpTimeSec = 0,
			precipKind = 2,
			desc = "焰风暴：火积雨云+火旋风，大火自维持的超强对流"
		},   // updraft 0.90 最强（火柱）；无降水（干性）；仅手动召唤
		// 【14 德雷科 Derecho】长生命、快速移动的直线风风暴（bow echo 演化），路径数百公里、
		// 持续数小时，阵风 30-40 m/s（可 >50），= 大范围下击暴流串；线状 + 最强阵风锋。
		new TypeSpec
		{
			name = "德雷科", rmaxFrac = 0.004, htopFrac = 0.12, hbaseM = 1100, aspectMin = 0.40,
			vmaxMs = 38, updraft = 0.45, developSec = 10800, matureSec = 86400, dissolveSec = 21600,
			rotate = 0.15, downburst = 0.60, line = 0.95, gustFront = 1.0,
			tornadoHost = false, defaultCat = 3, maxCat = 4, naturalUpTimeSec = 25920,
			precipKind = 0,
			desc = "德雷科：长生命直线风风暴，路径数百公里"
		},   // line 0.95 线状、gustFront 1.0 最强出流；三阶段 3h/24h/6h
		// 【15 极地涡旋 PolarVortex】极地高空冷性大涡旋（绕极涡）南下时带来大范围严寒/暴雪：
		// 冷心、尺度大（上千 km）、生命数天-数周、风速中等但持续，降水以雪为主。
		new TypeSpec
		{
			name = "极地涡旋", rmaxFrac = 0.012, htopFrac = 0.15, hbaseM = 1000, aspectMin = 0.45,
			vmaxMs = 30, updraft = 0.25, developSec = 172800, matureSec = 432000, dissolveSec = 86400,
			rotate = 0.80, downburst = 0.05, line = 0.20, gustFront = 0.70,
			tornadoHost = false, defaultCat = 3, maxCat = 4, naturalUpTimeSec = 86400,
			precipKind = 1,
			desc = "极地涡旋：极地冷性大涡旋南下，大范围严寒"
		},   // 冷心旋转（rotate 0.80）；固态降水；三阶段 2天/5天/1天
		// 【16 超级风暴 Megastorm】假想（影视《后天》式）的全球尺度超强气旋：尺度行星级、
		// 眼内极寒"瞬冻"、生命超长。纯理论/科幻，仅手动召唤（cat6 上限）。
		new TypeSpec
		{
			name = "超级风暴", rmaxFrac = 0.020, htopFrac = 0.18, hbaseM = 500, aspectMin = 0.50,
			vmaxMs = 70, updraft = 0.60, developSec = 43200, matureSec = 432000, dissolveSec = 86400,
			rotate = 1.0, downburst = 0.40, line = 0.30, gustFront = 0.90,
			tornadoHost = false, defaultCat = 4, maxCat = 6, naturalUpTimeSec = 43200,
			precipKind = 0,
			desc = "超级风暴：假想全球尺度超强气旋（《后天》式）"
		}    // cat6 上限、Rmax 最大（行星级）；仅手动召唤
	};

	public static string TypeName(StormType t)
	{
		return Spec[(int)t].name;
	}

	// ===== 相态/类别判定辅助（渲染与逻辑共用，2026-09-12 扩展）=====
	// 原实现把"无降水/沙色/无闪电"散落成 `type == StormType.DustStorm` 硬编码；
	// 扩展类型后集中到此处，新增类型只改一处。
	public static bool IsPrecipFree(StormType t)    // 无降水（干燥/静稳系统：沙尘暴/浓雾/尘卷风）
	{
		return Spec[(int)t].precipKind == 2;
	}

	public static bool IsFrozenPrecip(StormType t)  // 固态降水（雪/冰粒：冬季风暴/冰暴）
	{
		return Spec[(int)t].precipKind == 1;
	}

	public static bool IsDusty(StormType t)         // 含尘沙色系统（沙尘暴/尘卷风，云色走沙黄）
	{
		return t == StormType.DustStorm || t == StormType.DustDevil;
	}

	public static bool IsFog(StormType t)           // 贴地低能见度雾（浓雾）
	{
		return t == StormType.DenseFog;
	}

	public static bool NoLightning(StormType t)     // 无云地闪（干燥/静稳系统）
	{
		return IsPrecipFree(t);
	}

	// 龙卷类型名（FxInst.variant 0-7，与 StormRenderer 的渲染分支一一对应）：
	// 供 HUD/雷达/指挥中心显示"这是什么龙卷"（预报文案用）。
	public static string TornadoVariantName(int v)
	{
		switch (v)
		{
		case 1: return "楔形";
		case 2: return "绳状";
		case 3: return "水龙卷";
		case 4: return "陆龙卷";
		case 5: return "多涡旋";
		case 6: return "卫星龙卷";
		case 7: return "阵风旋";
		default: return "标准";
		}
	}

	// 龙卷强度 → 增强藤田级（EF0-EF5，美国 NWS 风速阈值，m/s）：
	// EF0 29-38 / EF1 38-49 / EF2 49-60 / EF3 60-74 / EF4 74-89 / EF5 ≥89。
	// 风速口径取母体眼壁风（与渲染端的漏斗尺度同源），低于 EF0 阈值记 EF0。
	public static int EfFromWind(double windMs)
	{
		if (windMs >= 89.4) return 5;
		if (windMs >= 74.0) return 4;
		if (windMs >= 60.0) return 3;
		if (windMs >= 49.0) return 2;
		if (windMs >= 38.0) return 1;
		return 0;
	}

	// 等级名按类型：台风用萨菲尔辛普森（TD/TS/CAT-1~5），
	// 其余系统用通用强度档（解决"单体现 CAT-2"这类台风等级错配）。
	// 沙尘暴用气象标准分级（用户：沙尘暴也是有强度的）——
	// GB/T 20480-2017 沙尘暴等级（按水平能见度）：浮尘(<10km)/扬沙(<10km 尘粒)/
	// 沙尘暴(<1km)/强沙尘暴(<500m)/特强沙尘暴(<50m)。映射 category 0-6。
	public static string StrengthName(StormType t, int cat)
	{
		if (t == StormType.Typhoon)
		{
			return Category.Names[Category.Clamp(cat)];
		}
		if (t == StormType.DustStorm)
		{
			int c = Category.Clamp(cat);
			if (c <= 0)
			{
				return "浮尘";
			}
			if (c == 1)
			{
				return "扬沙";
			}
			if (c == 2)
			{
				return "沙尘暴";
			}
			if (c <= 4)
			{
				return "强沙尘暴";
			}
			return "特强沙尘暴";
		}
		// ===== 扩展类型分级（2026-09-12）：各按现实分级标准，不再一律套通用档 =====
		if (t == StormType.ExtratropicalCyclone)
		{
			return extrNames[Category.Clamp(cat)];
		}
		if (t == StormType.WinterStorm)
		{
			return winterNames[Category.Clamp(cat)];
		}
		if (t == StormType.IceStorm)
		{
			return iceNames[Category.Clamp(cat)];
		}
		if (t == StormType.DenseFog)
		{
			return fogNames[Category.Clamp(cat)];
		}
		if (t == StormType.AtmosphericRiver)
		{
			return arNames[Category.Clamp(cat)];
		}
		if (t == StormType.DustDevil)
		{
			return devilNames[Category.Clamp(cat)];
		}
		// 理论/极端风暴（2026-09-13）
		if (t == StormType.Firestorm)
		{
			return fireNames[Category.Clamp(cat)];
		}
		if (t == StormType.Derecho)
		{
			return derechoNames[Category.Clamp(cat)];
		}
		if (t == StormType.PolarVortex)
		{
			return polarNames[Category.Clamp(cat)];
		}
		if (t == StormType.Megastorm)
		{
			return megaNames[Category.Clamp(cat)];
		}
		return genericGradeNames[Category.Clamp(cat)];
	}

	// 通用强度档名（提到静态字段：原实现在 StrengthName 里写局部 `string[] g = {...}`，
	// 每次调用都新建一个 7 元素数组 —— HUD 每帧为每行系统 + 雷达 + 菜单各调一次，
	// 是纯热路径分配）。
	private static readonly string[] genericGradeNames = { "微弱", "弱", "中等", "较强", "强", "很强", "极端" };

	// 扩展类型的逐型分级名表（静态字段，避免热路径每次 new；沿用 genericGradeNames 的教训）。
	// 依据：温带气旋按强度描述（低压→风暴→爆发性气旋）、冬季风暴按降雪国标
	// （小/中/大/暴雪）、冰暴按积冰灾害、浓雾按 GB/T 雾分级、大气河按 AR Scale 1-5
	// （Ralph et al. 2019）、尘卷风按强度。
	private static readonly string[] extrNames = { "温带低压", "温带扰动", "温带风暴", "强温带气旋", "爆发性气旋", "超强温带气旋", "极端温带气旋" };
	private static readonly string[] winterNames = { "小雪", "中雪", "大雪", "暴雪", "大暴雪", "特大暴雪", "特大暴雪" };
	private static readonly string[] iceNames = { "冻雨", "冻雨", "冰暴", "强冰暴", "严重冰暴", "灾难性冰暴", "灾难性冰暴" };
	private static readonly string[] fogNames = { "轻雾", "雾", "大雾", "浓雾", "强浓雾", "特强浓雾", "特强浓雾" };
	private static readonly string[] arNames = { "弱大气河", "AR-1 弱", "AR-2 中等", "AR-3 强", "AR-4 极强", "AR-5 极端", "AR-5 极端" };
	private static readonly string[] devilNames = { "尘旋", "尘卷风", "强尘卷风", "强尘卷风", "强尘卷风", "强尘卷风", "强尘卷风" };
	// 理论/极端风暴分级名（2026-09-13）：火焰风暴按火强度、德雷科按直线风灾害、
	// 极地涡旋按严寒程度、超级风暴按假想强度。
	private static readonly string[] fireNames = { "小焰风暴", "焰风暴", "强焰风暴", "超级焰风暴", "超级焰风暴", "超级焰风暴", "超级焰风暴" };
	private static readonly string[] derechoNames = { "阵风线", "直线风", "德雷科", "强德雷科", "特强德雷科", "特强德雷科", "特强德雷科" };
	private static readonly string[] polarNames = { "弱极涡", "极地涡旋", "强极地涡旋", "严寒风暴", "超级严寒风暴", "超级严寒风暴", "超级严寒风暴" };
	private static readonly string[] megaNames = { "超级低压", "超级风暴", "强超级风暴", "超强超级风暴", "超级风暴·极", "超级风暴·极", "超级风暴·极" };

	// 台风眼壁半径（Rmax 比例，渲染+风场共用）：按强度分级贴近现实——
	// cat0/1(TD/TS) 无清晰眼（0）、cat2(STS) 朦胧眼 0.22、cat3(C1) 0.30、cat4(C2) 0.36、
	// cat5(C3) 0.38、cat6+(C4-C5) 0.40（超强台风眼大而清晰）。
	public static double TyphoonEyeR(int cat)
	{
		if (cat <= 1)
		{
			return 0.0;
		}
		if (cat == 2)
		{
			return 0.22;
		}
		if (cat == 3)
		{
			return 0.30;
		}
		if (cat == 4)
		{
			return 0.36;
		}
		if (cat == 5)
		{
			return 0.38;
		}
		return 0.40;
	}

	// 被云/雨包裹时的基准水平能见度（米），逐型对标现实（暴雨内 <1km）。
	// 现实参考：雷暴/超级单体暴雨 ~0.5-1km；飑线/台风雨带 ~1-2km；MCS 混合层状 ~1.5-3km；
	// 沙尘暴由 GB/T 20480 按强度另算（此处返回 -1 走 DustFactor）。等级越高雨越强、
	// 能见度越低（最高档 ×0.6）。
	public static double BaseVisM(StormType t, int cat)
	{
		double baseM;
		switch (t)
		{
			case StormType.Typhoon:    baseM = 1200.0; break;   // 雨带暴雨 ~1-2km（外缘略清）
			case StormType.Cell:       baseM = 800.0;  break;   // 单体雷暴暴雨 <1km
			case StormType.Multicell:  baseM = 900.0;  break;
			case StormType.Supercell:  baseM = 800.0;  break;   // 暴雨+冰雹 <1km
			case StormType.SquallLine: baseM = 1000.0; break;   // 飑线暴雨 ~1km
			case StormType.MCS:        baseM = 1500.0; break;   // 层状+对流混合 ~1.5-3km
			// 扩展类型能见度基准（2026-09-12）：各按自身现实观测区间
			case StormType.ExtratropicalCyclone: baseM = 1100.0; break;   // 锋面层状降水 ~1-2km
			case StormType.WinterStorm: baseM = 400.0;  break;   // 暴雪能见度 <400m（NWS 暴风雪判据）
			case StormType.IceStorm:    baseM = 600.0;  break;   // 冻雨低云 ~0.5-1km
			case StormType.DenseFog:    baseM = 120.0;  break;   // 浓雾 <200m（GB/T：强浓雾 <50m）
			case StormType.AtmosphericRiver: baseM = 1000.0; break;   // 暖区层状暴雨 ~1km
			case StormType.DustDevil:   baseM = 500.0;  break;   // 贴地尘柱局部 <1km
			// 理论/极端风暴（2026-09-13）
			case StormType.Firestorm:   baseM = 300.0;  break;   // 烟尘遮蔽 <300m
			case StormType.Derecho:     baseM = 700.0;  break;   // 直线风暴雨 ~0.7km
			case StormType.PolarVortex: baseM = 500.0;  break;   // 雪盲 ~0.5km
			case StormType.Megastorm:   baseM = 250.0;  break;   // 假想全球系统，能见度极差
			case StormType.DustStorm:  return -1.0;             // 走 DustFactor
			default:                   baseM = 1000.0; break;
		}
		return baseM * (1.0 - 0.4 * ((double)Category.Clamp(cat) / 6.0));
	}

	// ===== 实例 =====
	public StormType type = StormType.Cell;
	public Planet planet;
	public bool active;
	public int category = 3;
	public double centerAngle;
	public double Rmax;
	public double Router;
	public double Htop;
	public double Hbase;   // 云底高度（米 AGL，SFS = 现实×0.3），云体悬浮在空中
	public double Vmax;
	public double Wmax;
	public double drift = 9.0;        // 移动速度（m/s，沿经度方向）
	public double driftAngle;         // 移动方向（弧度，相对经度切线；缓慢随机旋转模拟路径）
	public double moveSpeed;          // 风暴实际移动速度（drift × 移速摆动因子，Advance 每帧更新；粒子跟随 CenterVelocity 用它，与风暴中心严格一致）
	// 行星大气分级（用户：金星/木星/土星/海王星特有风暴环境）：
	// atmoClass 0=地球类（大气 <70km，SFS Terra 基准）、1=厚大气金星类（70-120km）、
	// 2=巨行星类（>120km，木/土/海王星 mod）。按大气高度自动分级，任何行星 mod 生效，
	// 不硬编码行星名。分级系数见 Configure。
	public int atmoClass;
	public double rotateScale = 1.0;  // 巨行星反气旋强化（中气旋旋转 ×1.5）
	public double age;
	// 结构整理：lifetime/lifetimeSec 死字段已删（能量制后不参与任何判定）。
	// 注：其赋值 `spec.lifetimeSec * (0.8 + 0.4*(double)seed*0.5 + 0.4*0.5)` 还有量级 bug
	// （seed 到 100000 → 乘子上万倍），因字段从未被读取而无害；随字段一并移除。
	// developTime/dissolveTime 死字段已删（ 能量制后不参与判定，
	// 发展/消散时长由 developRate/dissolveRate 类型表驱动）
	public int stage;                 // 0 发展 1 成熟 2 消散
	public double intensity = 1.0;    // 强度乘子（随 stage 演化）
	// 消散衍生产物（等级上限与消散产物讨论）：台风残余低压——消散期逗点化
	// （commaK 0→1 残余化程度，渲染 num6 撕环+尾臂+螺旋减弱）、commaDir 逗点方向（种子
	// 确定）；泥雨 muddyFactor（0-1，消散沙尘暴附近有降雨系统时沙尘与雨混合，渲染雨色
	// 染黄）。均 gate atmoClass==0（巨行星大红斑是反气旋无残余低压、金星无降水）。
	public double commaDir = 1.0;
	public double commaK;
	public double muddyFactor;
	// 超级飓风（Hypercane，理论风暴，Emanuel 1988）：理论风速 ~800 km/h（≈222 m/s，约 2.85×CAT-5），
	// 需 SST ~48°C（火山/陨石撞击触发）。游戏可及阈值：海温 ≥30°C 且已达最高档（cat6）。
	// 峰值风速 ×2.85、HUD 显示"超级飓风 Hypercane"。
	public bool hypercane;
	private bool hyperManual;   // 指挥中心手动强制（否则仅 SST≥48°C + 满档自动触发）
	private double lastMuddyCheck = -999.0;   // 泥雨节流（age 基准）
	// EWRC 眼壁置换（演化讨论高价值 feature）：成熟强台风周期性"先降 20% 风速/
	// 能量、眼变糊眼径外扩 → 复强略超置换前"（真实台风眼壁置换周期 EWRC）。渲染端
	// 眼清晰度（eyeSharp 用 energy）自动跟随：能量降 → 眼糊、能量复 → 眼清。
	public double ewrcT = -1.0;       // 1 不激活；0→1 一个置换周期（3 分钟游戏，帧化 ≥30 帧现实）
	private double ewrcCooldown;      // 置换后冷却（防频繁）

	// 能量制生命周期（用户：不写死寿命，改能量制）：energy 0-100 取代固定
	// lifetime，stage 由能量驱动。发展 55→80 → 成熟 80 高位缓慢下降 → 能量 <30 进入
	// 消散 → 快速崩溃归零 → dissolving 60 帧渐隐 → active=false。地形输入因子天然
	// 实现真实气象（台风海上持久、登陆/沙漠水汽切断快速枯竭），HUD 显示能量百分比
	// 让生命周期永远可感知——任何时间倍率都能看到消散。
	public double energy;

	// 海温场（用户：冷水会冷死台风；先全面采样海陆分布，再按一套种子生成海温）：
	// 行星海温场 per-planet 一次性生成缓存（360° 每 1° 采样海陆 + 种子低频正弦平滑）。
	// 台风能量制海上按 SST 调制（暖水增强 / 冷水枯竭），HUD 显示海温。
	// 冷尾流（用户：台风挖冷水——强风埃克曼抽吸把深层冷水翻上来，暖水层）：
	// 海温场动态化——台风中心海域 SST 随停留时间下降（挖冷），受暖水层（混合层）深度
	// 限制（挖穿无冷水可挖），挖冷后能量输入降 → 台风自我削弱（负反馈）；海温缓慢
	// 恢复（sstOrig 为恢复目标）。
	public float sstDisplay = 27f;   // 台风中心最近海温（°C，HUD 显示）
	private double lastSstDig = -999.0;   // 挖冷节流（age 基准，1s 一次）
	private static readonly Dictionary<Planet, float[]> sstCache = new Dictionary<Planet, float[]>();
	private static readonly Dictionary<Planet, float[]> sstOrigCache = new Dictionary<Planet, float[]>();   // 原始场（恢复目标）
	private static readonly Dictionary<Planet, float[]> landCache = new Dictionary<Planet, float[]>();

	// 消散渐隐兜底（用户：时间加速 2500 万倍也看不到消散，等一两分钟风暴在面板
	// 上直接消失）：Manager 每帧推进 dt = 世界时间差（ 钳 30s/帧，覆盖 2000x 内
	// 同步）——2500 万倍下短寿命系统（单体 2100s）寿命被压成 70 帧 = 1.17s 现实，消散期
	// （dissolveTime 210s）只剩 7 帧 = 0.12s 现实渐隐，"啪"一下消失。修复：寿命到点后
	// 进入 60 帧（~1 秒现实）帧驱动渐隐倒计时，完成才 active=false——任何时间倍率下
	// 消散都可见；正常速度下 stage==2 的游戏时间渐隐（DissolveFade）与之双保险。
	public bool dissolving;
	public float dissolveCountdown;
	public int seed;
	// 当前 stage 内演化进度 0→1（HUD 进度条）：发展 f/0.25、成熟 (f-0.25)/0.5、
	// 消散 (f-0.75)/0.25；台风（lifetime=0）无寿命演化返回 0（持续型）。
	// 时间制：发展 developTime、成熟至 lifetime−dissolveTime、消散尾段。
	public double StageProgress()
	{
		// 能量制：发展 55→80 / 成熟 1.0 / 消散 30→0
		if (stage == 0)
		{
			return Clamp01((energy - 55.0) / 25.0);
		}
		if (stage == 1)
		{
			return 1.0;
		}
		return Clamp01((30.0 - energy) / 30.0);
	}

	// 生命周期百分比（0-1）。 能量制：= 能量百分比（生命周期剩余）。
	public double LifeFrac()
	{
		return Clamp01(energy / 100.0);
	}

	// 附属现象：龙卷（宿主成熟期产生，漏斗形渲染）与下击暴流（局部下沉+辐散）。
	// 附属现象多实例（用户：随机合适位置生成 + 数量限制解除）：龙卷/下暴从单标量
	// 改为 List<FxInst>，每个实例独立切向偏移（sOff，Rmax 单位，沿 s 轴）、强度、形成相位、
	// 消散标志、manual（手动常驻）。标量字段保留兼容（表示"至少有一个"）。
	public class FxInst
	{
		public double sOff;        // 切向偏移（Rmax，相对风暴中心；现象锚定不随风区偏移）
		public double strength;    // 强度（龙卷 = level/3，下暴 = 1.0）
		public double phase;       // 形成动画 0→1
		public bool dissolving;
		public bool manual;        // 手动添加常驻（不自然衰减）
		public double seed;        // 渲染/波动随机种子
		public double vOff;        // 相对母体的切向漂移速度（Rmax/游戏秒）：龙卷不再钉死位置
		public int variant;        // 龙卷类型（0=标准 1=楔形 2=绳状 3=水龙卷 4=陆龙卷，自动派生见 Advance）
		// 类型扩展：5=多涡旋（主漏斗内 2 子涡快速绕转）、6=卫星龙卷（外侧 1 小
		// 漏斗慢绕）、7=gustnado（阵风锋小尘旋，无冷凝漏斗）。子涡旋转相位（现实时间累计，
		// 渲染端每帧 += dt×转速，时间加速下子涡转速不失控）。
		public double subAngle;
	}
	public List<FxInst> tornadoes = new List<FxInst>();
	public List<FxInst> downbursts = new List<FxInst>();
	// 新增附属现象：阵风锋（下暴出流的地面前沿锋面，弧状云墙+强出流）与
	// 闪电风暴（中气旋内密集闪电，局部频率倍增）。沿用 FxInst 结构：sOff 锚定位置、
	// strength 跟母体、phase 形成动画。
	public List<FxInst> gustFronts = new List<FxInst>();
	public List<FxInst> lightningBursts = new List<FxInst>();
	public double tornadoStrength;    // 兼容：至少一个龙卷时的最大强度
	public double downburstStrength;  // 兼容：至少一个下暴时的最大强度
	public double gustFrontStrength;  // 至少一个阵风锋时的最大强度
	// 台风海陆检测（地理位置可行性落地）：SFS 是 2D 行星，无经纬度概念，
	// 但 GetTerrainHeightAtAngle 可判中心在海上还是陆地——台风登陆后水汽切断+地面摩擦
	// → 加速老化 + 强度衰减（真实气象：登陆后 12-24h 内消散）。
	public bool overLand;             // 台风中心判定在陆地上（滞回 20s 防海岸线抖动）
	private double landTimer;
	private double lastLandCheck;
	// 地形类型（沙漠/绿地/冰/岩/海）：随海陆检测一起节流采样。
	public TerrainKind terrainKind = TerrainKind.Ocean;
	// 形成动画相位 0→1（约 3 秒成型）：漏斗从云底缓缓垂到地面、粒子逐渐增强。
	// 结构整理：phaseTornado/phaseDownburst（只写不读的兼容镜像，渲染端统一读 fx.phase）
	// 与 tornadoLevel（被 0.5+category/6 取代）已删除。
	// 消散状态：ClearPhenomena 置 true 后强度以 ~3 秒速度衰减（消散动画），
	// 渲染端 grow = min(phase, strength) 让漏斗反向缩回云底。
	public bool dissolvingTornado;
	public bool dissolvingDownburst;
	// 附属现象现实配额（现实性审计）：本风暴一生可产生的现象个数；0 = 不产，
	// -1 = 无限（phenomenaRealism 关闭时退回旧连续生成）。逐次消耗，用尽即不再生成。
	public int tornadoQuota;
	public int downburstQuota;
	public int gustFrontQuota;
	// 登陆摩擦风速乘子（持久化 0.3-1.0）：台风登陆后风速按地形粗糙度持续衰减，
	// 回到海上缓慢恢复。原实现每帧只乘一次、立刻被档位公式覆盖 → 从未累积（潜伏 bug）。
	private double landWindK = 1.0;

	// ===== 风暴卷起的障碍物（树木 / 石头）：被风刮起 → 飞行翻滚 → 可撞击火箭 =====
	// 用户点出的机制：游戏自带"火箭被撞即毁"（DestructionReason.RocketCollision +
	// TerrainCollision），只要风暴能把地面物体刮起来，撞击后的解体和失败菜单全由游戏
	// 原生处理。游戏里没有原生树/石对象（只有职业树的 UI），所以由本 mod 造：
	// 自研质点模拟（重力 + 气动阻力 + 风暴风场）+ 复用渲染层画 quad，不碰 Unity 物理层
	// （SFS 地形碰撞不是标准 Collider2D，自己算落点更稳）。
	public class DebrisInst
	{
		public double s;        // 切向位置（相对风暴中心，米；与风场同一坐标）
		public double h;        // 高度 AGL（米）
		public double vs;       // 切向速度（m/s）
		public double vh;       // 垂直速度（m/s）
		public double size;     // 特征尺寸（米；石头半径 / 树高的一半）
		public double mass;     // 质量（kg）
		public double spin;     // 渲染自转角（弧度）
		public double spinV;    // 自转角速度
		public int kind;        // 0 = 石头，1 = 树
		public bool airborne;   // 已离地（离地后走飞行模拟，落地重新锚定）
		public double age;
	}
	public List<DebrisInst> debris = new List<DebrisInst>();
	private double lastDebrisSpawn = -999.0;
	// 本帧是否有 debris 处于"高速贴地/飞行"状态（HUD 危险提示用）。
	public bool debrisActive;

	// 环境阻力参数（现实值）：空气密度 ρ、阻力系数 Cd（石头钝体 1.2 / 树 1.5 带枝叶）。
	private const double DebrisRho = 1.225;
	private const double DebrisG = 9.81;
	// 起扬阈值：地面风 < 12 m/s（6 级）刮不动石块、树要 15 m/s。
	private const double DebrisLiftRock = 12.0;
	private const double DebrisLiftTree = 15.0;

	// 卷起/推进障碍物（每帧；spawn 节流）。dt = 游戏秒。
	private void AdvanceDebris(double dt)
	{
		int maxN = TyphoonConfig.I.stormDebris ? Math.Max(0, TyphoonConfig.I.debrisMaxPerStorm) : 0;
		debrisActive = false;
		// 生成：成熟期、陆地（树木只在绿地）、风够大、节流 2 游戏秒
		if (maxN > 0 && stage >= 1 && debris.Count < maxN && terrainKind != TerrainKind.Ocean
			&& vmaxDisplay > 22.0 && age - lastDebrisSpawn > 2.0)
		{
			lastDebrisSpawn = age;
			DebrisInst d = new DebrisInst();
			bool canTree = terrainKind == TerrainKind.Green;
			d.kind = (canTree && (seed + debris.Count * 7919) % 100 < 55) ? 1 : 0;
			if (d.kind == 1)
			{
				d.size = 4.0 + 4.0 * (double)((seed * 31 + debris.Count * 97) % 100) / 100.0;   // 树高 8-16 m
				d.mass = 150.0 + 450.0 * (double)((seed * 17 + debris.Count * 53) % 100) / 100.0;
			}
			else
			{
				d.size = 0.8 + 1.7 * (double)((seed * 13 + debris.Count * 29) % 100) / 100.0;
				d.mass = 200.0 + 1800.0 * (double)((seed * 7 + debris.Count * 41) % 100) / 100.0;
			}
			double r = (double)((seed * 101 + debris.Count * 211) % 1000) / 1000.0;
			double sign = ((seed + debris.Count) % 2 == 0) ? 1.0 : -1.0;
			d.s = sign * (0.15 + 0.75 * r) * Rmax;
			d.h = 0.0;
			d.age = 0.0;
			d.spin = 6.2831853 * (double)((seed * 3 + debris.Count * 11) % 100) / 100.0;
			debris.Add(d);
		}
		// 推进
		double R = (planet != null) ? planet.Radius : 1.0;
		for (int i = debris.Count - 1; i >= 0; i--)
		{
			DebrisInst d = debris[i];
			d.age += dt;
			// 风场：debris 位置 → 行星全局 → SampleWind → 投影回 (切向, 径向)
			double ang = centerAngle + d.s / R;
			double cos = Math.Cos(ang);
			double sin = Math.Sin(ang);
			Double2 radialDir = new Double2(cos, sin);
			Double2 tanDir = new Double2(0.0 - sin, cos);
			Double2 pos = radialDir * (R + d.h);
			Double2 wind = SampleWind(pos, false);   // 相对风（drift 平流由风暴中心速度承担）
			double wS = Double2.Dot(wind, tanDir);
			double wH = Double2.Dot(wind, radialDir);
			double lift = (d.kind == 1) ? DebrisLiftTree : DebrisLiftRock;
			double windAbs = Math.Sqrt(wS * wS + wH * wH);
			if (!d.airborne)
			{
				// 地面静止：风超过起扬阈值才被卷起（否则原地不动，避免"没风也在飘"）
				if (windAbs < lift)
				{
					continue;
				}
				d.airborne = true;
				d.vh = Math.Max(0.0, wH);
			}
			// 气动阻力（ρ Cd A |v_rel| v_rel / m）+ 重力；A ≈ 特征尺寸²
			double area = d.size * d.size;
			double cd = (d.kind == 1) ? 1.5 : 1.2;
			double k = 0.5 * DebrisRho * cd * area / Math.Max(d.mass, 1.0);
			double relS = wS - d.vs;
			double relH = wH - d.vh;
			double relAbs = Math.Sqrt(relS * relS + relH * relH);
			d.vs += k * relAbs * relS * dt;
			d.vh += (k * relAbs * relH - DebrisG) * dt;
			d.s += d.vs * dt;
			d.h += d.vh * dt;
			d.spinV = 0.5 * d.vs / Math.Max(d.size, 0.5);
			d.spin += d.spinV * dt;
			if (d.h <= 0.0)
			{
				// 落地：反弹 25% 并衰减（简单恢复系数），速度过低则重新锚定
				d.h = 0.0;
				d.vh = (d.vh < -1.0) ? (0.0 - d.vh * 0.25) : 0.0;
				d.vs *= 0.85;
				if (Math.Abs(d.vs) < 0.6 && d.vh <= 0.0)
				{
					d.airborne = false;
					d.vs = 0.0;
					d.vh = 0.0;
				}
			}
			// 出界/超龄回收
			if (Math.Abs(d.s) > Rmax * 6.0 || d.h > Htop * 2.0 || d.age > 600.0)
			{
				debris.RemoveAt(i);
				continue;
			}
			if (Math.Abs(d.vs) > 8.0 || d.h > 3.0)
			{
				debrisActive = true;
			}
		}
	}
	// 合并上限基准（防无限加强）。
	public double rmaxBase;
	public double vmaxBase;
	public int mergeCount;

	// 强度平滑过渡（用户：强度切换搞粒子过渡）：F8/自然升级设置 vmaxTarget，
	// 实际风场/渲染用 vmaxDisplay 每帧向 target 逼近（~3 秒）——切强度风场平滑爬升不跳变。
	public double vmaxDisplay;
	public double vmaxTarget;
	public double vmaxTargetBase;   // 当前 category 档位基准（SetCategory 更新，能量驱动风速用）
	public double wmaxDisplay;
	// 自然发展强度进度（用户：所有系统自然发展强度也需进度条、满了到下一等级）：
	// 成熟期自然累积，满 Spec.naturalUpTimeSec 后 category+1（钳 maxCat）并重算目标强度。
	// naturalUpTime 字段已删（升级节奏入 Spec：台风 12h/飑线 3.6h/沙尘暴 4.8h/级，
	// ×cL 大气分级——原恒 300s 与寿命现实化比例荒谬）。
	public double naturalProgress;      // 0~1 进度条
	// 系统类型转变（用户：系统可能向另一个系统转变，粒子过渡动画原基础上转变）：
	// transitionAnimT 0→1（3 秒），渲染端云 alpha 淡出淡入过渡；完成后 type=transitionTo。
	public StormType transitionTo;
	public double transitionAnimT = -1.0;
	// 中点已换型标记（淡出结束那一帧执行 type=transitionTo + 重配尺度 + 重建粒子）。
	private bool transitionSwitched;
	// 转变完成后通知渲染端重建粒子（按新类型分布）。
	public bool RebuildPuffsFlag;

	// 合并动画状态：被吞方/参与方启动 mergeAnimT(0→1，3 秒) 后渲染中心向
	// 目标/中点插值 + 渐隐，动画完成由 Manager 移除被吞方。
	// mergeMode: 0=一般吞噬(被吞方粒子靠近吞噬方) 1=台风吞噬(被吞方拆散飞向台风渐隐)
	// 2=双台风合并(双方粒子互相靠近中点，重组新台风，大台风保留吸收)
	public WeatherSystem mergeTarget;
	public double mergeAnimT = -1.0;
	public int mergeMode;
	public double mergeMidAng;

	// 消散渐隐因子（用户：粒子消失可能是消散机制问题）：原消散期（stage=2）粒子
	// alpha 完全不感知 stage/intensity——只有风场变弱（vmaxDisplay 降），寿命到头瞬间
	// active=false 整系统 Clear 销毁 = 无渐隐的"啪"消失。改为寿命尾段（dissolveTime 内）
	// 线性淡出 1→0，渲染端乘到粒子/雨/附属 alpha → 消散有完整渐隐动画。
	public float DissolveFade()
	{
		if (dissolving)
		{
			// 帧驱动兜底：能量枯竭后的 60 帧渐隐（时间加速下也可见，
			// 不再被 dt 压缩成零点几秒的"啪"消失）。
			return Mathf.Clamp01(dissolveCountdown / 300f);   // 演化：兜底 60→300 帧（5s 渐隐，别让消散最后 1s 啪消失）
		}
		if (stage != 2)
		{
			return 1f;
		}
		// 能量制：消散期能量 30→0 线性淡出（随崩溃渐隐，任何倍率可见）
		return (float)Math.Min(1.0, Math.Max(0.0, energy / 30.0));
	}

	// 合并动画渐隐因子：被吞方（mode 0/1）动画期间整体淡出（×0.9 斜率），
	// 双台风合并（mode 2）双方不淡出（只互相靠近重组）。
	public float MergeFade()
	{
		if (mergeAnimT >= 0.0 && mergeMode != 2)
		{
			return (float)(1.0 - Math.Min(1.0, mergeAnimT) * 0.9);
		}
		return 1f;
	}

	// 合并动画渲染中心：行星坐标（被吞方插值向目标/中点）。
	public Double2 MergedStormC()
	{
		Double2 c = StormCenterPos();
		if (mergeAnimT >= 0.0 && planet != null)
		{
			double t = Math.Min(1.0, mergeAnimT);
			if (mergeMode == 2)
			{
				Double2 midC = new Double2(Math.Cos(mergeMidAng) * planet.Radius, Math.Sin(mergeMidAng) * planet.Radius);
				// Fujiwhara 互旋（演化讨论：SFS 2D 单自由度完整互旋几何不可做，
				// 用切向摆动近似"互旋逼近"张力——两中心绕中点往返摆动，越接近摆幅越小，
				// 12s 动画内有 2-3 个来回）。只改渲染中心（MergedStormC），不动 centerAngle/
				// drift 字段——那会影响风场/粒子跟随，出"风暴呼吸"伪影（science-discuss 警告）。
				c = Double2.Lerp(c, midC, t);
				double rot = Math.Sin(mergeAnimT * 18.0) * (1.0 - t) * 0.6;   // ±0.6rad 起步，接近归零
				double ca = Math.Cos(rot);
				double sa = Math.Sin(rot);
				Double2 rel = c - midC;
				c = midC + new Double2(rel.x * ca - rel.y * sa, rel.x * sa + rel.y * ca);
			}
			else if (mergeTarget != null && mergeTarget.planet != null)
			{
				Double2 tc = new Double2(Math.Cos(mergeTarget.centerAngle) * planet.Radius, Math.Sin(mergeTarget.centerAngle) * planet.Radius);
				c = Double2.Lerp(c, tc, t);
			}
		}
		return c;
	}

	// 11 区索引（按 sR 分段，与 StormRenderer.windZoneNames 对应）。
	// 分段重排（用户确认 11 区是否覆盖整个台风——旧分段只到 ±2.2Rmax 而风场
	// 实际延伸到 ±9Rmax(Router)，且峰 |sR|=1.0 落在“较强”区不在“强·眼壁”区，调错区）：
	// 峰位归位到“强·眼壁”（0.7-1.2 含峰 1.0）、最外区 0/10 盖住 Router 尾巴（±3Rmax 外）。
	// 0:(≤−3.0) 1:(−3.0,−2.2] 2:(−2.2,−1.6] 3:(−1.6,−1.2] 4:(−1.2,−0.7](强眼壁·含峰)
	// 5:(−0.7,0.7] 风眼 6:(0.7,1.2](强眼壁·含峰) 7:(1.2,1.6] 8:(1.6,2.2] 9:(2.2,3.0]
	// 10:(>3.0)（最外区盖住 Router=9Rmax 尾巴，风场实际延伸到 ±9~13.5Rmax）
	public static int WindZoneIndex(double sR)
	{
		if (sR <= -3.0) return 0;
		if (sR <= -2.2) return 1;
		if (sR <= -1.6) return 2;
		if (sR <= -1.2) return 3;
		if (sR <= -0.7) return 4;
		if (sR <= 0.7) return 5;
		if (sR <= 1.2) return 6;
		if (sR <= 1.6) return 7;
		if (sR <= 2.2) return 8;
		if (sR <= 3.0) return 9;
		return 10;
	}

	// 蒲福风级（中国标准下限，m/s）：级0=静风 ... 级7=13.9(疾风) 级8=17.2 级9=20.8
	// 级10=24.5(狂风) 级11=28.5 级12=32.7(飓风) 级13=37 级14=41.5 级15=46.2 级16=51 级17=56.1。
	public static readonly double[] beaufortMin = { 0.0, 0.3, 1.6, 3.4, 5.5, 8.0, 10.8, 13.9, 17.2, 20.8, 24.5, 28.5, 32.7, 37.0, 41.5, 46.2, 51.0, 56.1, 61.2 };

	// 风速 → 蒲福风级（0-18）。
	public static int Beaufort(double ms)
	{
		int lv = 0;
		for (int i = 0; i < beaufortMin.Length; i++)
		{
			if (ms >= beaufortMin[i])
			{
				lv = i;
			}
		}
		return lv;
	}

	// 基础风力轮廓（无 11 区系数）：|sR|≤0.5 风眼 0.2→0.5、≤1.0 升到眼壁峰 1.0、
	// 之外衰减到 0.2（ 对称双峰）。静态，供 WindCircleRo/线性过渡复用。
	private static double BaseProfile(double aR)
	{
		if (aR <= 0.5)
		{
			return 0.2 + 0.3 * SmoothStep(aR, 0.0, 0.5);
		}
		if (aR <= 1.0)
		{
			return 0.5 + 0.5 * SmoothStep(aR, 0.5, 1.0);
		}
		return 1.0 - 0.8 * SmoothStep(aR - 1.0, 0.0, 1.5);
	}

	// 性能：WindCircleRo ×4 的结果只依赖系统状态（vmaxDisplay×intensity、Rmax、
	// Router、type、category、windZoneGain），与采样点位置 (s,h) 无关——却被放在每次风采样
	// （每粒子每 0.1s + ProbePlayer + Harmony 补丁）里重算 4×90 次迭代+Exp，头号 CPU 热点。
	// 缓存到本系统字段，参数指纹变化才重算（每系统一次 vs 每粒子每次）。
	private double cC7;
	private double cC7b;
	private double cC10;
	private double cC12;
	// 风圈脏标记：原每风采样重算指纹（~30 次整数运算 + 11 次数组读取 × 每帧
	// 数千次粒子采样 = 每帧几十万次冗余运算）。改参数写点（vmaxDisplay/intensity/
	// Rmax/Router/category/type 变化处）置 dirty，采样时仅 O(1) 检查 flag → 风圈每帧
	// 最多重算一次。行为等价（同帧参数不变则结果完全相同）。
	private bool cWindDirty = true;

	// 性能（同类缓存）：附属现象中心 = f(centerAngle, Rmax, sOff)。其中 Cos/Sin(centerAngle)
	// 与行星半径只随 centerAngle 变，却在每次风采样里对每个附属现象实例重算（Tornado/
	// DownburstHorizontal 每实例一次）——每帧数千次纯冗余的 Cos/Sin。按 centerAngle 值
	// 惰性缓存：Advance 每帧至多改一次 centerAngle → 每帧最多重算一次。
	private double cCenterAng = double.NaN;
	private Double2 cCenterPos;
	private Double2 cCenterTan;

	private void RefreshCenterCache()
	{
		if (cCenterAng == centerAngle)
		{
			return;
		}
		cCenterAng = centerAngle;
		double r = (planet != null) ? planet.Radius : 1.0;
		double cos = Math.Cos(centerAngle);
		double sin = Math.Sin(centerAngle);
		cCenterPos = new Double2(cos * r, sin * r);
		cCenterTan = new Double2(0.0 - sin, cos);
	}

	// 缓存刷新：参数写点标记后重算 4 个风圈（无参变化时零开销）。
	private void RefreshWindCircles()
	{
		if (!cWindDirty)
		{
			return;
		}
		cWindDirty = false;
		cC7 = WindCircleRo(13.9);
		cC7b = WindCircleRo(13.9, true);
		cC10 = WindCircleRo(24.5, true);
		cC12 = WindCircleRo(32.7, true);
	}

	// 风圈缓存失效标记（Rmax/Router/vmaxDisplay/intensity/category/type 写点调用）。
	public void MarkWindCirclesDirty()
	{
		cWindDirty = true;
	}

	// 缓存版风圈（HUD/黑框调用）：13.9/24.5/32.7 命中缓存，其他 levelMin 回落直接计算。
	public double WindCircleRoCached(double levelMin, bool baseOnly = false)
	{
		RefreshWindCircles();
		if (Math.Abs(levelMin - 13.9) < 0.01)
		{
			return baseOnly ? cC7b : cC7;
		}
		if (Math.Abs(levelMin - 24.5) < 0.01)
		{
			return baseOnly ? cC10 : WindCircleRo(24.5);   // 非 baseOnly 无缓存（仅 HUD/黑框偶用，现算）
		}
		if (Math.Abs(levelMin - 32.7) < 0.01)
		{
			return baseOnly ? cC12 : WindCircleRo(32.7);
		}
		return WindCircleRo(levelMin, baseOnly);
	}

	// 风圈半径（Rmax 单位）：风速 ≥ levelMin(m/s) 的最远半径。从外向内扫
	// BaseProfile×gain ≥ levelMin/Vmax 的第一个位置。返回 0 表示无该等级风圈（弱风暴够不到）。
	// baseOnly=true：忽略 11 区系数（纯 BaseProfile）——风圈保底用稳定骨架，
	// 系数被调 0 时风圈不塌（否则保底随系数消失，失去兜底意义）；HUD/黑框显示用默认（含系数）。
	// 扫描目标含 edge 衰减（×exp(−(a/2.5)²)，与 SampleComponents 的 edge 基准
	// 一致）——原纯 BaseProfile 在 aR>2.5 恒 0.2 假尾巴上算出虚大风圈（HUD 显示 7级圈 2.2R
	// 但实际乘 edge 后只有 ~8 m/s）；现在风圈半径 = 实际风速达标位置，HUD/黑框不再骗人。
	public double WindCircleRo(double levelMin, bool baseOnly = false)
	{
		double want = levelMin / Math.Max(vmaxDisplay * intensity, 1.0);
		double maxA = Router / Rmax;
		for (double a = maxA; a > 0.0; a -= 0.1)
		{
			double p = BaseProfile(a) * Math.Exp(0.0 - Pow2(a / 2.5));
			if (baseOnly ? (p >= want) : (p * StormRenderer.windZoneGain[WindZoneIndex(a)] >= want))
			{
				return a;
			}
		}
		return 0.0;
	}

	// 返回 bool：仅 tornadoHost 类型（超级单体/飑线/MCS）可产生龙卷。
	// 手动添加的附属现象常驻：manual=true 时不自然衰减（只响应显式解散）。
	// 多实例 + 随机位置：每次添加生成新实例，位置在风暴内随机（沿 s 轴）。
	public bool manualTornado;
	public bool manualDownburst;

	// 自然生成掷骰（确定性伪随机）：时间（age）混进哈希 → 每帧取值都变，概率 =
	// 每秒 rate/10000（∝ dt，时间加速下按游戏时间统计的期望次数不变，同存档可复现）。
	// 原实现是 `seed×常数 % 10000 < dt×rate`，而 seed 是建风暴时赋一次的常量 →
	// 条件恒不成立：自然龙卷/下暴/阵风锋/闪电与类型转变从未触发过。
	private bool Roll(double ratePerSec, double dt, uint salt)
	{
		uint h = (uint)seed * salt + (uint)(age * 100.0) * 2246822519u;
		h ^= h >> 15;
		h *= 2654435761u;
		h ^= h >> 13;
		return (double)(h % 10000u) < dt * ratePerSec;
	}

	// 确定性随机偏移（种子混合实例序号，同风暴同位置稳定）。
	// attempt 参数：防重叠重试时扰动种子（否则重试生成相同位置死循环）。
	private void RandomPos(FxInst fx, double sMax, int attempt = 0)
	{
		ulong n = (ulong)(tornadoes.Count + downbursts.Count + 1) + (ulong)attempt * 7919u;
		double h1 = (double)(((ulong)seed * 2654435761u + n * 97u) % 10000) / 10000.0;
		double h2 = (double)(((ulong)seed * 2246822519u + n * 131u) % 10000) / 10000.0;
		double mag = 0.2 + (sMax - 0.2) * h2;              // 偏移量 0.2~sMax（Rmax 单位）
		fx.sOff = ((h1 < 0.5) ? (0.0 - mag) : mag);        // 双侧随机，|sOff| ≥ 0.2Rmax（真正避开正中心）
		fx.vOff = (h2 - 0.5) * 0.02;                       // 相对母体切向漂移 ±0.01 Rmax/游戏秒（≈±4 m/s）
		fx.seed = (double)(((ulong)seed * 40503u + n * 911u) % 10000) / 100.0;
	}

	// 防重叠：与已有实例（同类 minGap、异类 crossGap，Rmax 单位）间距检查。
	private bool PosTooClose(double sOff, double minGap, double crossGap)
	{
		foreach (FxInst o in tornadoes)
		{
			if (Math.Abs(o.sOff - sOff) * Rmax < minGap * Rmax)
			{
				return true;
			}
		}
		foreach (FxInst o in downbursts)
		{
			if (Math.Abs(o.sOff - sOff) * Rmax < crossGap * Rmax)
			{
				return true;
			}
		}
		return false;
	}

	// 随机位置 + 防重叠（重试 12 次找不重叠落点；isTornado 决定同类间距）。
	private void RandomPosAvoid(FxInst fx, double sMax, bool isTornado)
	{
		double minGap = isTornado ? 0.6 : 0.5;   // 同类最小间距
		double crossGap = isTornado ? 0.4 : 0.35; // 异类（龙卷↔下暴）最小间距
		for (int k = 0; k < 12; k++)
		{
			RandomPos(fx, sMax, k);
			if (!PosTooClose(fx.sOff, minGap, crossGap))
			{
				return;
			}
		}
	}

	// 龙卷类型自动派生扩展（类型调研方案二期）：海洋→水龙卷（3）；超单强龙卷
	// (strength>0.75)→20% 多涡旋（5）/10% 卫星（6）/70% 楔形（1）；阵风锋宿主随机 →
	// gustnado（7，小尘旋）；沙尘暴→陆龙卷（4）；其余标准（0）。种子 fx.seed(0-100)
	// 确定性（同风暴同类型，渲染/波动稳定）。
	private int DeriveTornadoVariant(FxInst fx)
	{
		double r = (fx.seed % 100.0) / 100.0;
		// （终审🟢-7）— Ocean 判定移到超单强龙卷之后：原 Ocean 优先导致海上超单强
		// 龙卷恒为水龙卷（现实沿海超单可产普通强龙卷）；现在超单强→楔形/多涡/卫星优先，
		// 其余海洋→水龙卷。
		if (type == StormType.Supercell && fx.strength > 0.75)
		{
			if (r < 0.2)
			{
				return 5;
			}
			if (r < 0.3)
			{
				return 6;
			}
			return 1;
		}
		if (terrainKind == TerrainKind.Ocean)
		{
			return 3;
		}
		if (gustFronts.Count > 0 && r < 0.3)
		{
			return 7;
		}
		if (type == StormType.DustStorm)
		{
			return 4;
		}
		return 0;
	}

	public bool AddTornado()
	{
		if (Spec[(int)type].tornadoHost)
		{
			FxInst fx = new FxInst();
			// 强度跟母体（用户：龙卷/下击暴流根据母体强度确定强度）：
			// strength = 0.5 + category/6（cat3=1.0 基准），取代原 tornadoLevel/3.0。
			fx.strength = 0.5 + category / 6.0;
			fx.phase = 0.0;                     // 从零开始形成动画
			fx.dissolving = false;
			fx.manual = true;                   // 手动添加常驻
			// 龙卷类型自动派生（龙卷类型调研方案落地）； — 扩展多涡旋/
			// 卫星/gustnado/陆龙卷（DeriveTornadoVariant：海洋→水龙卷、超单强→楔形/
			// 多涡/卫星、阵风锋→gustnado、沙尘暴→陆龙卷）。
			fx.variant = DeriveTornadoVariant(fx);
			RandomPosAvoid(fx, 0.9, true);      // 随机合适位置 + 防重叠
			tornadoes.Add(fx);
			tornadoStrength = fx.strength;      // 兼容同步
			dissolvingTornado = false;
			return true;
		}
		return false;
	}

	// 下暴宿主白名单（用户：禁用台风/单体/多单体生成下击暴流——现实下暴主要
	// 与超级单体/飑线/弓形回波/MCS 相关）：仅 Supercell/SquallLine/MCS 可产下击暴流。
	public bool CanDownburst()
	{
		// 扩展（2026-09-12）：温带气旋的冷锋有强对流段、大气河的暖区抬升也能触发，
		// 均可产下击暴流；冬季风暴/冰暴/浓雾/尘卷风不产（静稳/浅薄/无冷池）。
		return type == StormType.Supercell || type == StormType.SquallLine || type == StormType.MCS
			|| type == StormType.ExtratropicalCyclone || type == StormType.AtmosphericRiver;
	}

	// 返回 bool（宿主不允许时拒绝并返回 false，F6 菜单可提示）。
	public bool AddDownburst()
	{
		if (!CanDownburst())
		{
			return false;
		}
		FxInst fx = new FxInst();
		// 强度跟母体：0.5 + category/6（cat3=1.0 基准）。
		fx.strength = 0.5 + category / 6.0;
		fx.phase = 0.0;
		fx.dissolving = false;
		fx.manual = true;
		RandomPosAvoid(fx, 0.6, false);         // 随机合适位置 + 防重叠
		downbursts.Add(fx);
		downburstStrength = fx.strength;        // 兼容同步
		dissolvingDownburst = false;
		return true;
	}

	// 阵风锋：下击暴流出流（冷池）在地面的推进前沿，气象上阵风锋出现在任何能
	// 产生下暴的系统（超级单体 RFD / 飑线弓形前沿 / MCS 出流边界）——白名单与下暴一致。
	public bool CanGustFront()
	{
		// 沙尘暴也允许挂阵风锋（Haboob 沙墙前沿 = 阵风锋的沙漠形态）；
		// 扩展（2026-09-12）：冬季风暴的暴风雪阵风锋、温带气旋的冷锋锋面同样挂阵风锋。
		return CanDownburst() || type == StormType.DustStorm || type == StormType.WinterStorm;
	}

	public bool AddGustFront()
	{
		if (!CanGustFront())
		{
			return false;
		}
		FxInst fx = new FxInst();
		fx.strength = 0.5 + category / 6.0;   // 强度跟母体
		fx.phase = 0.0;
		fx.dissolving = false;
		fx.manual = true;
		RandomPosAvoid(fx, 1.5, false);       // 阵风锋偏外圈（0-1.5Rmax，出流前沿）
		gustFronts.Add(fx);
		gustFrontStrength = fx.strength;
		return true;
	}

	// 闪电风暴：中气旋/眼壁内密集闪电。气象上所有深对流系统（含台风眼壁与
	// 螺旋雨带）都有闪电，白名单不限制（由 TyphoonConfig.lightning 整体开关控制）。
	public bool AddLightningBurst()
	{
		FxInst fx = new FxInst();
		fx.strength = 0.5 + category / 6.0;
		fx.phase = 0.0;
		fx.dissolving = false;
		fx.manual = true;
		RandomPosAvoid(fx, 1.2, false);
		lightningBursts.Add(fx);
		return true;
	}

	// 清除改为"渐消"：强度保留，由 Advance 以 ~3 秒速度衰减（消散动画）。
	// 多实例：全部实例置 dissolving。
	public void ClearPhenomena()
	{
		foreach (FxInst fx in tornadoes)
		{
			fx.dissolving = fx.strength > 0.001;
		}
		foreach (FxInst fx in downbursts)
		{
			fx.dissolving = fx.strength > 0.001;
		}
		foreach (FxInst fx in gustFronts)        //
		{
			fx.dissolving = fx.strength > 0.001;
		}
		foreach (FxInst fx in lightningBursts)   //
		{
			fx.dissolving = fx.strength > 0.001;
		}
		dissolvingTornado = tornadoes.Count > 0;
		dissolvingDownburst = downbursts.Count > 0;
	}

	// 附属现象合并（同类大吞小）：龙卷×龙卷 / 下暴×下暴 sOff 间距 < 0.4Rmax
	// → 保留强度大者，位置取强度加权中点（合并后中心微微偏向强的一方）；被吞者移除。
	// 每帧最多合一对（几帧内处理完所有相邻对）。
	private void MergeFx()
	{
		if (tornadoes.Count > 1)
		{
			for (int i = 0; i < tornadoes.Count; i++)
			{
				for (int j = i + 1; j < tornadoes.Count; j++)
				{
					FxInst a = tornadoes[i];
					FxInst b = tornadoes[j];
					if (Math.Abs(a.sOff - b.sOff) * Rmax < 0.4 * Rmax)
					{
						double wSum = a.strength + b.strength;
						double mid = (a.sOff * a.strength + b.sOff * b.strength) / wSum;
						if (a.strength >= b.strength)
						{
							a.sOff = mid;
							a.strength = Math.Min(1.4, a.strength + b.strength * 0.25);   // 吸收上限 2.0→1.4（终审🟡-5：原 90×2.0×1.82≈328 m/s 超 EF5）
							tornadoes.RemoveAt(j);
						}
						else
						{
							b.sOff = mid;
							b.strength = Math.Min(1.4, b.strength + a.strength * 0.25);   // 吸收上限 2.0→1.4（终审🟡-5）
							tornadoes.RemoveAt(i);
						}
						return;
					}
				}
			}
		}
		if (downbursts.Count > 1)
		{
			for (int i = 0; i < downbursts.Count; i++)
			{
				for (int j = i + 1; j < downbursts.Count; j++)
				{
					FxInst a = downbursts[i];
					FxInst b = downbursts[j];
					if (Math.Abs(a.sOff - b.sOff) * Rmax < 0.4 * Rmax)
					{
						double wSum = a.strength + b.strength;
						double mid = (a.sOff * a.strength + b.sOff * b.strength) / wSum;
						if (a.strength >= b.strength)
						{
							a.sOff = mid;
							a.strength = Math.Min(1.4, a.strength + b.strength * 0.25);   // 上限统一 1.4（原此处 2.0 → 55×2.0=110 m/s 出流远超观测极值 67 m/s）
							downbursts.RemoveAt(j);
						}
						else
						{
							b.sOff = mid;
							b.strength = Math.Min(1.4, b.strength + a.strength * 0.25);   // 吸收上限 2.0→1.4（终审🟡-5）
							downbursts.RemoveAt(i);
						}
						return;
					}
				}
			}
		}
		// 阵风锋同类合并（与龙卷/下暴同构）：间距 < 0.5Rmax 大吞小。
		if (gustFronts.Count > 1)
		{
			for (int i = 0; i < gustFronts.Count; i++)
			{
				for (int j = i + 1; j < gustFronts.Count; j++)
				{
					FxInst a = gustFronts[i];
					FxInst b = gustFronts[j];
					if (Math.Abs(a.sOff - b.sOff) * Rmax < 0.5 * Rmax)
					{
						if (a.strength >= b.strength)
						{
							a.sOff = (a.sOff * a.strength + b.sOff * b.strength) / (a.strength + b.strength);
							a.strength = Math.Min(1.4, a.strength + b.strength * 0.25);   // 上限统一 1.4（同下暴）
							gustFronts.RemoveAt(j);
						}
						else
						{
							b.sOff = (a.sOff * a.strength + b.sOff * b.strength) / (a.strength + b.strength);
							b.strength = Math.Min(1.4, b.strength + a.strength * 0.25);   // 吸收上限 2.0→1.4（终审🟡-5）
							gustFronts.RemoveAt(i);
						}
						return;
					}
				}
			}
		}
	}

	public void Configure(StormType t, Planet p, double angle, int catBoost)
	{
		TypeSpec spec = Spec[(int)t];
		type = t;
		planet = p;
		centerAngle = angle;
		// 初始档按类型 maxCat 钳（防 defaultCat+catBoost 超自然上限；F8 手动可
		// 越限——god mode 实验自由，HUD 标 ⚠超限）。limitMaxCategory 关 = chaos 全 6 级。
		int initMax = TyphoonConfig.I.limitMaxCategory ? Math.Max(1, Math.Min(6, spec.maxCat)) : 6;
		category = Math.Min(Category.Clamp(spec.defaultCat + catBoost), initMax);
		// 尺寸按现实 ×30%（用户：所有天气现象长宽度按现实 30%，忽略地表是现实 5%，
		// 压迫感十足）：原 Rmax=行星半径×rmaxFrac 在 5% 行星上把台风缩成 2.4km 没压迫感。
		// 改 Rmax = 6371km×rmaxFrac×0.3（现实核心半径×30%，无视行星大小）；钳 [100m, 0.3×行星半径]。
		// Rmax 赋值移至 atmoClass 分级系数（cR）声明之后（见下方）。
		// 高宽比下限：SFS 大气相对行星厚（30% vs 5%），纯比例会让小系统变成
		// 19:1 的细长柱（"立长方体"）。Rmax 至少 = Htop × aspectMin → 云团宽扁。
		double atmTop = 60000.0;
		if (p.HasAtmospherePhysics && p.AtmosphereHeightPhysics > 1000.0)
		{
			atmTop = p.AtmosphereHeightPhysics;
		}
		// 行星大气分级（SFS Terra 大气 ~60km 为基准，任何行星 mod 自动生效）：
		// 0 地球类（<70km）现状；1 厚大气金星类（70-120km）：硫酸云超自转 → 云顶/尺度×1.5、
		// 风速×1.3、移速×2、寿命×3；2 巨行星类（>120km）：大红斑/大白斑/大暗斑 →
		// 云顶×2.5、尺度×2、风速×1.6、移速×6（海王星太阳系最快纬向风）、寿命×30
		// （台风 3 天 → 90 天，超长但非永久——用户反对 0=持续）、反气旋 rotate×1.5。
		atmoClass = atmTop > 120000.0 ? 2 : ((atmTop > 70000.0) ? 1 : 0);
		double cH = (atmoClass == 2) ? 2.5 : ((atmoClass == 1) ? 1.5 : 1.0);
		double cR = (atmoClass == 2) ? 2.0 : ((atmoClass == 1) ? 1.5 : 1.0);
		double cV = (atmoClass == 2) ? 1.6 : ((atmoClass == 1) ? 1.3 : 1.0);
		double cD = (atmoClass == 2) ? 6.0 : ((atmoClass == 1) ? 2.0 : 1.0);
		double cL = (atmoClass == 2) ? 30.0 : ((atmoClass == 1) ? 3.0 : 1.0);
		rotateScale = (atmoClass == 2) ? 1.5 : 1.0;
		Rmax = 6371000.0 * spec.rmaxFrac * 0.3 * cR;   // × 大气分级（在 cR 声明后）
		// Htop 同改为现实×30%（100km 大气基准 × htopFrac × 0.3），钳制不超大气顶。
		Htop = 100000.0 * spec.htopFrac * 0.3 * cH;   // × 大气分级
		if (Htop < 500.0)
		{
			Htop = 500.0;
		}
		if (Htop > atmTop * 0.95)
		{
			Htop = atmTop * 0.95;
		}
		// 云底：现实 LCL 云底 × 0.3（SFS 大气 30%），钳 [30m, Htop×0.4]。
		// 云体悬浮在空中（云底离地），龙卷/下击暴流从云底垂下/冲出。
		Hbase = spec.hbaseM * 0.3;
		if (Hbase < 30.0)
		{
			Hbase = 30.0;
		}
		if (Hbase > Htop * 0.4)
		{
			Hbase = Htop * 0.4;
		}
		double minR = Htop * spec.aspectMin;
		if (Rmax < minR)
		{
			Rmax = minR;
		}
		if (Rmax < 100.0)
		{
			Rmax = 100.0;
		}
		double maxR = p.Radius * 0.3;
		if (Rmax > maxR)
		{
			Rmax = maxR;
		}
		Router = Rmax * 9.0;
		// 台风强度分级修正（用户：连热带低压都 11 级风）：原通用公式 0.5+cat/6
		// 在 cat=0(TD) 时给 62×0.5=31 m/s（11 级）离谱。台风用真实分级曲线。
		// （终审🟡-1）— 台风 catF 统一用 Category.PeakWind 萨菲尔表（与 SetCategory
		// 单一口径，"三套口径根治"只改了 SetCategory 漏了 Configure——原中国标准
		// 公式 0.2+cat/6×0.9 初始 cat4=49.6 首次切档跳 54 m/s +9%）。
		// 其他类型保持原曲线（单体 18×0.5+cat/6 合理）。
		double catF = (type == StormType.Typhoon) ? (Category.PeakWind[category] / 62.0) : (0.5 + category / 6.0);
		Vmax = spec.vmaxMs * catF * cV;   // × 大气分级（巨行星强对流高速）
		Wmax = Vmax * spec.updraft;
		rmaxBase = Rmax;
		vmaxBase = Vmax;
		mergeCount = 0;
		tornadoStrength = 0.0;
		downburstStrength = 0.0;
		// 附属现象现实配额（现实性审计）：现实只有约 1/3 的超级单体能产龙卷、一生 1-3 个；
		// 下击暴流/阵风锋同样 1-2 个。原实现是"每秒概率"连续生成（龙卷 0.6%/s → 8h 成熟期
		// 期望 ~170 次，恒满 4 个）——比现实高两个数量级。改为配额模型：种子决定这个风暴
		// 一生能产几个，逐个消耗；-1 = 不限额（配置 phenomenaRealism 关，退回旧连续生成）。
		if (TyphoonConfig.I.phenomenaRealism)
		{
			tornadoQuota = (spec.tornadoHost && seed % 100 < 30) ? (1 + seed % 3) : 0;
			downburstQuota = CanDownburst() ? (1 + (seed / 3) % 2) : 0;
			gustFrontQuota = CanGustFront() ? (1 + (seed / 7) % 2) : 0;
		}
		else
		{
			tornadoQuota = -1;
			downburstQuota = -1;
			gustFrontQuota = -1;
		}
		landWindK = 1.0;   // 登陆摩擦乘子归位（新风暴未受摩擦）
		age = 0.0;
		// 能量制：所有系统能量 55 起步（发展），stage 由 energy 驱动（无 lifetime
		// 特例——台风也走完整发展→成熟→消散，不再"持续成熟"）。
		energy = 55.0;
		stage = 0;
		intensity = 0.4;
		// 发展阶段 60-120s 随机（用户：发展太慢——原发展期 = 寿命 25%，单体
		// ~9 分钟太久）；消散期 = 寿命后 10%（最小 30s）。
		// developTime/dissolveTime 死字段赋值已删（ 消散窗口现实化是
		// lifetime 时间制时代的机制， 能量制已由 stage==2 帧化 + dissolveRate 接管；
		// 5 分钟窗口兜底随字段退役——用户：这些保留也可以去了）。
		// drift 按 SFS 行星尺度换算（×0.35）：原现实速度 9-20 m/s 在 5% 大小行星上
		// 绕行星快 20 倍，龙卷/风暴视觉移动过快。换算后超单 3.7-5.8 m/s、台风 5-7 m/s，
		// 接近真实龙卷平移（4.5-9 m/s）在 SFS 上的观感。
		drift = (5.0 + spec.vmaxMs * 0.15 + 6.0 * ((double)(seed % 97) / 97.0)) * 0.35 * cD * ((type == StormType.DustStorm) ? 3.0 : 1.0);   // × 大气分级； 沙尘暴随大风带移动快（×3）
		driftAngle = (double)(seed % 6283) / 1000.0;
		commaDir = ((seed % 2) == 0) ? 1.0 : -1.0;   // 残余低压逗点方向（种子确定，±1 随机）
		moveSpeed = drift;   // 实际移动速度初始 = drift（Advance 首帧起叠加移速摆动）
		active = true;
		// 强度平滑：显示值 = 目标值（初始无过渡）。
		vmaxDisplay = Vmax;
		vmaxTarget = Vmax;
		vmaxTargetBase = Vmax;   // 档位基准（能量驱动风速的乘数基准）
		wmaxDisplay = Wmax;
		naturalProgress = 0.0;
		cWindDirty = true;   // 初始参数已定，风圈缓存需按新参数重算
	}

	// 手动/自然设置强度档（F8 或自然升级共用）：只改目标值，实际风场由
	// Advance 平滑逼近（粒子过渡）；同时同步附属现象强度（龙卷/下暴跟母体）。
	public void SetCategory(int cat)
	{
		category = Category.Clamp(cat);
		// 待办5 🟡-8 三套口径根治：台风 catF 直接对齐 Category.PeakWind（萨菲尔表
		// 14/24/35/45/54/62/78）——原 0.2+cat/6×0.9（cat4→49.6 vs PeakWind[4]=54 错位约 1 级）。
		// 现在等级名（StrengthName 用 PeakWind）与峰值风（vmaxTargetBase）同源。
		// 非台风（无萨菲尔概念）保持 0.5+cat/6（相对系统基准）。
		double catF = (type == StormType.Typhoon) ? (Category.PeakWind[category] / 62.0) : (0.5 + category / 6.0);
		if (hypercane && type == StormType.Typhoon)
		{
			// 超级飓风：理论风速 ~800 km/h（≈222 m/s），约为 CAT-5（78 m/s）的 2.85 倍
			// （Emanuel 1988 假说；理论需 SST ~48°C，此处用游戏可及阈值触发）。
			catF *= 2.85;
		}
		// 审查🔴-2：补乘大气分级 cV（原漏乘 → 巨行星/金星系统升级后风速掉回地球基准）
		double cVnow = (atmoClass == 2) ? 1.6 : ((atmoClass == 1) ? 1.3 : 1.0);
		vmaxTargetBase = Spec[(int)type].vmaxMs * catF * cVnow;
		vmaxTarget = vmaxTargetBase;
		SyncFxStrength();
		cWindDirty = true;   // category 变化 → 风圈缓存失效
	}

	// 指挥中心手动触发超级飓风（Hypercane）：满档台风再强化一次即进入该状态
	// （理论需 SST≈48°C；这里走 hyperManual 绕过海温门槛，状态持续到消散）。
	public void MakeHypercane()
	{
		if (type != StormType.Typhoon)
		{
			return;
		}
		hyperManual = true;
		hypercane = true;
		SetCategory(6);
	}

	// 待办5 🟡-8：眼壁实际风速（风场 windZoneGain 眼壁区 1.2 增强的物理峰值），
	// HUD 显示用——等级名/峰值风/眼壁风三套口径统一为：基准 PeakWind → 显示 ×1.2 眼壁。
	// （终审🟡-14）— 眼壁风补 edge 衰减：原 vmaxDisplay×1.2（名义眼壁增益）但风场
	// 实际含 edge=exp(-(1/2.5)²)≈0.85 → 实测眼壁风 ≈ vmaxDisplay×1.02，HUD 高估 18%。
	// 显示含 edge 的实际值（= vmaxDisplay×1.2×0.85），与玩家测风一致。
	public double EyewallWind => vmaxDisplay * 1.2 * 0.85;

	// 附属现象强度跟母体：龙卷/下击暴流 strength = 0.5 + category/6（cat3=1.0）。
	private void SyncFxStrength()
	{
		double s = 0.5 + category / 6.0;
		foreach (FxInst fx in tornadoes)
		{
			// 只同步手动召唤（菜单常驻）的龙卷强度；自然生成的龙卷保持自身衰减，
			// 不被切档/自然升级"续命回满血"（原实现把所有现存龙卷覆写回 0.5+cat/6）。
			if (fx.manual)
			{
				fx.strength = s;
			}
		}
		foreach (FxInst fx in downbursts)
		{
			fx.strength = s;
		}
		foreach (FxInst fx in gustFronts)        // 阵风锋强度跟母体
		{
			fx.strength = s;
		}
		foreach (FxInst fx in lightningBursts)   // 闪电风暴强度跟母体
		{
			fx.strength = s;
		}
		tornadoStrength = (tornadoes.Count > 0) ? s : 0.0;
		downburstStrength = (downbursts.Count > 0) ? s : 0.0;
		gustFrontStrength = (gustFronts.Count > 0) ? s : 0.0;   //
	}

	// 类型转变（原基础上转变，粒子过渡动画）：3 秒淡出淡入，完成后按新类型重配尺度。
	public void TransitionTo(StormType newType)
	{
		if (newType == type || transitionAnimT >= 0.0 || newType == StormType.Typhoon)
		{
			return;
		}
		transitionTo = newType;
		transitionAnimT = 0.0;
	}

	// 渲染端过渡进度（0 = 无转变，0~1 = 过渡中）。
	public double TransitionBlend()
	{
		return (transitionAnimT >= 0.0) ? Math.Min(1.0, transitionAnimT) : 0.0;
	}

	// 类型转变 alpha 包络（1 → 0.05 → 1，3 秒）：渲染端折进 spawnAnimT（云/雨/附属/
	// 天空穹顶的 alpha 全链都乘它）→ 整团天气淡出、中点换型、再淡入。
	public float TransitionAlphaMul()
	{
		if (transitionAnimT < 0.0)
		{
			return 1f;
		}
		double tp = Math.Min(1.0, transitionAnimT);
		if (tp < 0.5)
		{
			return (float)(1.0 - 0.95 * (tp / 0.5));      // 淡出 1 → 0.05
		}
		return (float)(0.05 + 0.95 * ((tp - 0.5) / 0.5)); // 淡入 0.05 → 1
	}

	// 按当前 type 重配尺度（类型转变完成后调用，保留位置/年龄/种子）。
	private void ApplyTypeScale()
	{
		TypeSpec spec = Spec[(int)type];
		// 类型转变重配尺度同步大气分级（atmoClass 由 Configure 按行星大气高度设定）
		double cR2 = (atmoClass == 2) ? 2.0 : ((atmoClass == 1) ? 1.5 : 1.0);
		double cH2 = (atmoClass == 2) ? 2.5 : ((atmoClass == 1) ? 1.5 : 1.0);
		Rmax = 6371000.0 * spec.rmaxFrac * 0.3 * cR2;
		double atmTop = 60000.0;
		if (planet != null && planet.HasAtmospherePhysics && planet.AtmosphereHeightPhysics > 1000.0)
		{
			atmTop = planet.AtmosphereHeightPhysics;
		}
		Htop = 100000.0 * spec.htopFrac * 0.3 * cH2;
		if (Htop < 500.0)
		{
			Htop = 500.0;
		}
		if (Htop > atmTop * 0.95)
		{
			Htop = atmTop * 0.95;
		}
		Hbase = spec.hbaseM * 0.3;
		if (Hbase < 30.0)
		{
			Hbase = 30.0;
		}
		if (Hbase > Htop * 0.4)
		{
			Hbase = Htop * 0.4;
		}
		double minR = Htop * spec.aspectMin;
		if (Rmax < minR)
		{
			Rmax = minR;
		}
		if (Rmax < 100.0)
		{
			Rmax = 100.0;
		}
		double maxR = (planet != null) ? planet.Radius * 0.3 : 3.0e6;
		if (Rmax > maxR)
		{
			Rmax = maxR;
		}
		Router = Rmax * 9.0;
		rmaxBase = Rmax;
		// 修复：vmaxBase 原来取旧类型 Vmax（SetCategory 之前），合并上限基准错。
		// 顾问审查抓的潜伏 bug：类型转变后 SetCategory 只重算 vmaxTargetBase/
		// vmaxTarget（新类型基准），但 Vmax 字段仍是旧类型值（单体→MCS 风场按单体走）。
		// 补 Vmax = vmaxTargetBase（转变后即时用新类型峰值风）。
		SetCategory(category);
		Vmax = vmaxTargetBase;
		vmaxBase = vmaxTarget;
		cWindDirty = true;   // 类型转变重配尺度后风圈缓存失效
	}

	public void Advance(double dt)
	{
		if (!active || planet == null)
		{
			return;
		}
		if (dt < 0.0)
		{
			dt = 0.0;
		}
		// 钳制 5s→30s：原 5s 上限在时间加速（warp）下掉队——1000x 时每帧
		// worldTime 跳 ~16.7s 被砍到 5s，风暴演化/移动/自然升级严重慢于世界时间。
		// 30s 覆盖 2000x 内同步（防暂停恢复/卡顿导致的异常大跳）。
		if (dt > 30.0)
		{
			dt = 30.0;
		}
		// 附属现象合并：同类（龙卷×龙卷 / 下暴×下暴）sOff 间距 < 0.4Rmax → 大吞小
		// （保留强度大者，位置取强度加权中点；被吞者直接移除——与天气系统合并同风格）。
		MergeFx();
		// 消散窗口帧化（用户：任何时间倍率都看不到"消散"，等一两分钟风暴在面板
		// 上直接消失）：时间加速下 dt 钳 30s/帧（）会把消散压成零点几秒。
		// 能量制：stage==2（消散崩溃）时钳 0.5s/帧，能量归零过程可视化
		// （DissolveFade = energy/30 线性淡出，防大 dt 一次归零跳过渐隐）。
		// （终审🟡-7）— 消散帧化钳 0.5s→6s：原 0.5s/帧 = 恒 30 游戏秒/现实秒，台风 12h
		// 消散在 2500 万倍 warp 下仍需 24 分钟现实（"消散可见"退化为"消散漫长"）；6s/帧
		// = 360 游戏秒/现实秒 → 极端 warp 下 ~2 分钟现实走完，可见性与时长相平衡。配合
		// 移动改 effDt（专项 B），消散期移动/耗散同步钳制，不再绕地球。
		double effDt = dt;
		if (stage == 2)
		{
			effDt = Math.Min(dt, 6.0);
		}
		age += effDt;
		// 台风海陆检测（节流 8s，GetTerrainHeightAtAngle 有数组分配不能每帧调）+ 登陆衰减。
		// SFS 是 2D 行星无经纬度，但"海面/陆地"可精确判定：真实地形高度 >0.5m = 陆地，
		// 连续 20s 在陆上才判定登陆（滞回，防海岸线抖动误判）。
		// 气象真实：台风登陆后水汽供应切断 + 地面摩擦 → 强度衰减、生命周期缩短（现实 12-24h 消散）。
		// 实现：overLand 时 age 4 倍速老化（3 天 → ~18h 消散）+ vmaxTarget 每秒 0.5% 衰减
		// （下限 vmaxBase×0.35），vmaxDisplay 平滑逼近照常 → 风场/粒子平滑减弱。
		if (planet != null && age - lastLandCheck > 8.0)
		{
			lastLandCheck = age;
			// 地形类型统一判定（TerrainAt 内部：高度场分海陆 + 纹理色细分沙漠/绿地/冰/岩）
			// 地形检测对所有风暴生效（用户：地形对所有风暴生效）：terrainKind/overLand
			// 全系统更新（HUD 全风暴显示地形）；登陆加速老化仍为台风专属（雷暴无"登陆"概念）。
			terrainKind = TerrainAt(StormCenterPos());
			if (terrainKind != TerrainKind.Ocean)
			{
				landTimer += 8.0;
			}
			else
			{
				landTimer = Math.Max(0.0, landTimer - 8.0);
			}
			overLand = landTimer > 20.0;
		}
		if (overLand && type == StormType.Typhoon)
		{
			// 能量制替代 age×3 硬编码加速：地形输入因子（terrainE 陆地降）让
			// 登陆台风净耗散率升 → 能量快速枯竭 → 自然走消散流程（真实气象：登陆后
			// 水汽切断 + 地面摩擦，12-24h 内消散）。
			// 审查二轮：风速摩擦衰减改为**乘性**并移到能量公式之后（stage 段）
			// 执行——原减法在能量公式前被 vmaxTargetBase×能量因子覆盖成死代码。此处仅
			// 保留 overLand 状态（能量段用）。
		}
		// 强度平滑逼近（切强度/自然升级时风场 ~3 秒爬升，粒子过渡不跳变）。
		if (vmaxDisplay != vmaxTarget)
		{
			double k = Clamp01(dt / 3.0);
			vmaxDisplay += (vmaxTarget - vmaxDisplay) * k;
			if (Math.Abs(vmaxTarget - vmaxDisplay) < 0.5)
			{
				vmaxDisplay = vmaxTarget;
			}
			TypeSpec sp = Spec[(int)type];
			wmaxDisplay = vmaxDisplay * sp.updraft;
			cWindDirty = true;   // vmaxDisplay 变化 → 风圈缓存失效
		}
		// 类型转变推进：淡出(0→0.5) → 中点换型+重配尺度+重建粒子(0.5) → 淡入(0.5→1)。
		// 换型放在最暗那一帧，尺度突变与粒子重分布都被藏住。
		if (transitionAnimT >= 0.0)
		{
			transitionAnimT += dt / 3.0;
			if (!transitionSwitched && transitionAnimT >= 0.5)
			{
				transitionSwitched = true;
				type = transitionTo;
				ApplyTypeScale();
				RebuildPuffsFlag = true;   // 通知渲染端按新类型重建粒子分布
				cWindDirty = true;   // 类型变化 → 风圈缓存失效
			}
			if (transitionAnimT >= 1.0)
			{
				transitionAnimT = -1.0;
				transitionSwitched = false;
			}
		}
		// 合并动画推进（0→1，3 秒）；到 1 由 Manager 移除被吞方/重置状态。
		if (mergeAnimT >= 0.0)
		{
			// 演化：合并动画 3s→12s（Fujiwhara 互旋逼近的"互旋张力"需要更长
			// 的逼近过程，审查讨论：3s 秒合体太突兀，10-15s 有重组感）。
			// 合并动画帧化（用户：时间加速下是否可控）：原 dt/12 是游戏时间
			// 加速 100x 时 12s 合并被压成 ~7 帧（0.12s 现实）瞬间合体（Fujiwhara 摆动
			// 也看不到）。帧化：每帧最多推进 0.4 游戏秒 → 合并恒 ≥30 帧（~0.5s 现实），
			// 任何倍率能看到互旋逼近过程；正常 1x（dt≈0.016 < 0.4）不变，仍 12s 现实。
			mergeAnimT += Math.Min(dt, 0.4) / 12.0;
		}
		// 能量制生命周期（用户：不写死寿命，改能量制——任何倍率都要能看到消散）：
		// energy 0-100 取代固定 lifetime，stage 由能量驱动。发展 55→80（~2.5 分钟游戏）→
		// 成熟 80 高位缓慢下降 → 能量 <30 进入消散 → 快速崩溃归零 → dissolving 60 帧渐隐
		// → active=false。地形输入因子天然实现真实气象：台风海上持久（terrainE=1.0）、
		// 登陆/移入沙漠水汽切断（terrainE 降 → 净耗散升）→ 快速枯竭消散。时间加速下
		// energy 连续推进，消散期恒可见（HUD 能量百分比肉眼可见下降）。
		double terrainE = 1.0;
		if (terrainKind == TerrainKind.Green) terrainE = 0.55;
		else if (terrainKind == TerrainKind.Rock) terrainE = 0.45;
		// （终审🟢-4）— Ice 0.30→0.18、沙漠（非沙尘）0.22→0.28：冰原几乎无深对流
		// （无暖湿不稳定）能量输入应最低；沙漠夏季有干雷暴，输入高于冰原（原 Ice>Desert
		// 与物理直觉相反）。
		else if (terrainKind == TerrainKind.Ice) terrainE = 0.18;
		else if (terrainKind == TerrainKind.Desert) terrainE = (type == StormType.DustStorm || type == StormType.DustDevil) ? 0.9 : 0.28;   // 沙尘暴/尘卷风靠风驱动，沙漠里反而持久
		// 审查二轮：沙尘暴离开沙漠 → 0.35（无沙源衰竭，~42min 耗尽；不触发硬
		// 失败 → F6 海上召唤不消失，保留玩家自由）。顺带修掉 🟢-15"出海反而活更久"。
		else if (type == StormType.DustStorm || type == StormType.DustDevil) terrainE = 0.35;
		// 台风海上按海温调制（用户：冷水会冷死台风——气象铁律，SST 掉 1-2°C
		// 强度骤降）：海上 terrainE=1.0 基础上 × 海温因子 sstF——暖水（≥28°C）1.15 增强、
		// 临界 26.5°C ≈1.0、24°C 0.65、22°C 0.4、≤20°C 0.15（冷水：能量输入骤降 →
		// 净耗散暴增 → 快速枯竭消散）。海温场 per-planet 一次性采样生成，索引零开销。
		if (type == StormType.Typhoon && terrainKind == TerrainKind.Ocean)
		{
			float sst = SampleSst(planet, centerAngle, atmoClass);
			sstDisplay = sst;
			double sstF = 0.15 + Clamp01((sst - 20.0) / 8.0);
			// 🟡-4 数值层（审查：金星 33°C/巨行星 30°C 是地球值硬套——金星表面
			// 实际 460°C 是 CO2 温室、巨行星云顶 -100°C，均无\"海\"）：巨行星（atmoClass 2）
			// 无海洋，风暴是大气环流特征（大红斑），**不依赖海温**——sstF 恒 1.0（绕开
			// 海温场，能量全靠大气机制，配合 netPerSec÷cL 30 倍 → 持久大红斑）；金星
			// （atmoClass 1）无液态水/潜热（水汽极少），能量输入受限 ×0.55（干燥大气
			// 台风发育不良但存在——游戏性保留）。地球保持完整海温逻辑。
			if (atmoClass == 2)
			{
				sstF = 1.0;
			}
			else if (atmoClass == 1)
			{
				sstF = Math.Max(0.15, sstF * 0.55);
			}
			terrainE = Math.Max(0.15, terrainE * sstF);
			// 超级飓风（Hypercane，理论风暴）：极暖海温（≥30°C，理论需 48°C）且已达最高档 →
			// 进入该状态（峰值风速 ×2.85 ≈ 800 km/h、HUD 显示"超级飓风"）。翻转时重算峰值风。
			bool wasHyper = hypercane;
			hypercane = hyperManual || (sst >= 48f && category >= 6);   // 理论阈值 SST≈48°C；指挥中心可手动强制
			if (hypercane != wasHyper)
			{
				SetCategory(category);
			}
			DigColdWater();   // 冷尾流：台风挖冷水（本帧 sstF 用挖冷前值，下帧见挖后值，平滑）
		}
		// 类型成熟期净耗散（能量/游戏秒，海上参考；÷terrainE → 陆地更快枯竭）。
		// 按真实气象寿命（成熟期 80→30 掉 50 能量）：台风 5 天/单体 45min/多单体 8h/
		// 超单 8h/飑线 18h/MCS 18h/沙尘暴 24h。
		// （终审🔴-1）— 大气分级 cLnow 提前定义（三速率表共用）：developRate/dissolveRate
		// 与 netPerSec 同步 ÷cLnow——原只接 netPerSec 导致巨行星台风"发育 2.5 天→成熟 149 天
		// →消散 12h"三阶段尺度失衡（大红斑 90 天只兑现一半）。
		// 结构整理：三阶段速率改从 Spec 表读（原为三条 if-else 硬编码链，与 Spec 表重复）。
		// 口径：发展 25 能量、成熟 50 能量、消散 30 能量，除以表内现实时长（游戏秒）。
		// 现实依据：台风 2.5d/5d/12h（生命史 5-9 天）、单体 20min/45min/20min、
		// 多单体与超单 1.5h/8h/3h、2h、飑线与 MCS 3h/18h/6h、沙尘暴 4h/24h/8h。
		double cLnow = (atmoClass == 2) ? 30.0 : ((atmoClass == 1) ? 3.0 : 1.0);
		TypeSpec specNow = Spec[(int)type];
		double netPerSec = Math.Max(50.0 / Math.Max(specNow.matureSec, 1.0), 0.000002);
		// 发展期（55→80）与消散期（30→0）÷cLnow：大气分级同步（巨行星寿命 ×30，
		// 三阶段同比例，防"发育 2.5 天→成熟 149 天"的阶段尺度失衡）。
		double developRate = 25.0 / Math.Max(specNow.developSec, 1.0) / cLnow;
		double dissolveRate = 30.0 / Math.Max(specNow.dissolveSec, 1.0) / cLnow;
		// 下限 0.0005→0.00005：台风现实 5 天 = 0.000116/s，被旧下限钳成 28h
		// （"可等尺度"时代的钳值，现实化后失效）——下限只是防净耗散为 0 的保险，须低于
		// 最慢类型（台风海上 0.000116）。
		netPerSec = Math.Max(netPerSec / ((type == StormType.Typhoon && terrainKind == TerrainKind.Ocean) ? (terrainE * terrainE) : terrainE), 0.00005);   // 海上台风 ÷sstF²：24°C 骤降可见更快（冷水冷死台风）
		// 下限 0.00002→0.000002：巨行星台风 5 天×30 = 0.0000039/s，旧下限钳成 29 天
		netPerSec = Math.Max(netPerSec / cLnow, 0.000002);

		// 修复 stage 边界振荡（用户：79↔80% 反复发展、成熟横跳）：原 stage 每帧
		// 由 energy 瞬时值**无迟滞**推断（energy>=80→1、否则→0）——成熟期任何让能量跌破
		// 80 的事件（EWRC 置换能量降、冷水区 netPerSec 大、时间加速单帧大跳）都被踢回发展
		// 段，发展段再涨回 80 → 无限循环。改**单向推进**（气象正确：真实台风生命周期单向
		// 发展→成熟→消散，成熟期强度波动但阶段从不倒退）：成熟后只有能量 ≤30 才消散，
		// 能量波动保持成熟；消散后不回退。
		if (stage == 0 && energy >= 80.0) stage = 1;
		else if (stage == 1 && energy <= 30.0) stage = 2;
		// stage==1 保持 1（不回发展）；stage==2 保持 2（消散不回退）

		if (stage == 0)
		{
			// 发展期环境反馈：envF=terrainE（台风海上含 sstF）。硬失败仅台风（海上
			// <24°C 或陆地）与沙尘暴（无沙源）；对流系统慢发育不判死。HUD 三档自洽
			// （≥26.5 增强/≥24 临界/<24 冷水）。
			double envF = terrainE;
			bool hardFail = false;
			if (type == StormType.Typhoon)
			{
				hardFail = terrainKind != TerrainKind.Ocean || sstDisplay < 24.0;
			}
			else if (type == StormType.DustStorm || type == StormType.DustDevil)
			{
				// 沙尘暴/尘卷风都需要沙源（干旱区）：离开沙漠 → 发育失败
				// （F6 召唤为成熟态不触发；仅自然生成的发育期判死）
				hardFail = terrainKind != TerrainKind.Desert;
			}
			if (hardFail)
			{
				energy = Math.Max(0.0, energy - 0.05 * effDt);   // 发育失败：能量下跌 → 自然消散
			}
			else
			{
				// 发展速率现实化（用户：发展期交给时间加速）：原恒 0.167/s
				// （2.5 分钟成型—— 游戏化快进），改按类型现实发展时长（台风 2.5 天
				// 游戏才成熟，加速等）；×envF 环境因子保留（暖海快/冷水慢/硬失败判死）。
				energy = Math.Min(100.0, energy + developRate * envF * effDt);
			}
			intensity = 0.4 + 0.6 * Clamp01((energy - 55.0) / 25.0);
			cWindDirty = true;   // intensity 变化 → 风圈缓存失效
		}
		else if (stage == 1)
		{
			energy = Math.Max(30.0, energy - netPerSec * effDt);   // 成熟高位缓慢下降
			intensity = 1.0;
			cWindDirty = true;   // intensity 变化 → 风圈缓存失效
			// EWRC 眼壁置换（land decay 优先：登陆取消；海上成熟强台风触发）：
			// 每 10 分钟概率进入一个 3 分钟置换周期——先降 20% 风速/能量（眼糊），
			// 复强段略超置换前（×1.05 并回补能量）。渲染眼清晰度/眼径自动跟随。
			if (type == StormType.Typhoon && !overLand && energy > 70.0)
			{
				if (ewrcT < 0.0)
				{
					ewrcCooldown -= effDt;
					if (ewrcCooldown <= 0.0)
					{
						ewrcT = 0.0;
						// 现实性审计：真实 EWRC 周期 1-3 天，一次眼壁置换从开始到完成约 1 天；
						// 原冷却 1 天 → 5 天寿命内 ~5 次置换，偏多 → 改 2 天（1-2 次/生命史）。
						// 置换"过程"仍压缩成 180 游戏秒（帧化 ≥30 帧），否则玩家永远看不到。
						ewrcCooldown = 172800.0;
					}
				}
			}
			else if (overLand && type == StormType.Typhoon)
			{
				ewrcT = -1.0;   // 登陆取消置换（防"置换反弹"对抗登陆摩擦，整合复查确认）
			}
			// EWRC 置换过程帧化（用户：时间加速下是否可控）：原 effDt/180
			// 是游戏时间——加速 100x 时 3 分钟置换被压成 ~2 帧（0.03s 现实），"先弱
			// 后强、眼先糊后清"肉眼不可见（与 消散同样的问题）。帧化：每帧
			// 最多推进 6 游戏秒 → 置换恒 ≥30 帧（~0.5s 现实），任何倍率都能看到完整
			// 置换过程；正常 1x（effDt≈0.016 < 6）不变，仍 3 分钟现实。触发冷却保持
			// 游戏时间（600 游戏秒 = 10 分钟游戏触发频率，加速下事件密集但每轮过程
			// 完整可见——快进语义正确）。ewrcStep 提到两个 EWRC if 块前共用。
			double ewrcStep = Math.Min(effDt, 6.0) / 180.0;
			if (ewrcT >= 0.0)
			{
				ewrcT += ewrcStep;   // 3 分钟一个周期（帧化：≥30 帧现实）
				if (ewrcT >= 1.0)
				{
					ewrcT = -1.0;
				}
			}
			// 审查🔴-3：能量驱动风速渐进削弱（原挖冷/冷水/登陆只掉能量条不掉
			// 风速，台风满速撑到消散期才崩 = "延迟崩盘"）。成熟期 vmax 目标 = 档位基准 ×
			// 能量因子（energy 80 → ×1.0、30 → ×0.7），vmaxDisplay 平滑逼近 → 渐进减弱。
			double eFrac = Clamp01((energy - 30.0) / 50.0);
			vmaxTarget = Math.Max(vmaxBase * 0.35, vmaxTargetBase * (0.7 + 0.3 * eFrac));
			if (ewrcT >= 0.0)
			{
				// EWRC 风速相位：0-0.4 降 20% → 0.4-0.7 低位 → 0.7-1.0 复强 ×1.05
				double ewrcF;
				if (ewrcT < 0.4)
				{
					ewrcF = 1.0 - 0.2 * (ewrcT / 0.4);
				}
				else if (ewrcT < 0.7)
				{
					ewrcF = 0.8;
				}
				else
				{
					ewrcF = 0.8 + 0.25 * ((ewrcT - 0.7) / 0.3);
				}
				vmaxTarget *= ewrcF;
				// 能量联动（眼清晰度经 eyeSharp 自动跟随）：置换期能量降（眼糊）、复强回补
				// 用帧化 ewrcStep（与 ewrcT 同步，防能量降/回补与置换相位错位）
				// （终审🟡-2）— 升段 10→14：原降 12/升 10 净 -5.4/周期且是成熟期能量主耗散
				// （~9×netPerSec），"复强略超置换前"语义冲突——降段 0→0.7 掉 8.4、升段 0.7→1.0
				// 回 14×0.3=4.2，净 -4.2 但仍显著小于降幅；进一步把降段改 10（0→0.7 掉 7），
				// 净 ≈ -2.8/周期（小损耗，置换消耗能量的物理语义保留，不再主导成熟期衰减）。
				if (ewrcT < 0.7)
				{
					energy = Math.Max(30.0, energy - 10.0 * ewrcStep);
				}
				else
				{
					energy = Math.Min(100.0, energy + 14.0 * ewrcStep);
				}
			}
		}
		else
		{
			// 消散速率按类型现实时长（台风 12h/单体 20min/…），÷terrainE → 登陆摩擦加速
			// （台风登陆 12h → 岩石 5.4h/沙漠 2.7h）。残余低压（消散产物）：逗点化
			// （commaK 0→1，energy
			// 30→10 段增长，渲染 num6 撕环+尾臂+螺旋减弱）+ 消散延长——残余段 dissolveRate
			// ÷(1+commaK×0.8) 放慢 ~1.8 倍（12h→~18h，"残余低压持续降雨"语义）；gate
			// atmoClass==0（巨行星大红斑是反气旋、金星干燥，不触发残余化）。设置开关
			// residualLow 控制。
			// 台风：消散期残余低压逗点化（energy 30→10）。温带气旋：标志性逗点云盾——
			// 全程随强度存在（成熟期最强），不再只有台风才有逗点。
			if (type == StormType.ExtratropicalCyclone)
			{
				commaK = Clamp01(0.35 + 0.55 * (category / 4.0)) * Clamp01(energy / 30.0);
			}
			else
			{
				commaK = (type == StormType.Typhoon && atmoClass == 0) ? Clamp01((30.0 - energy) / 20.0) : 0.0;
			}
			double dr = dissolveRate / Math.Max(terrainE, 0.2);
			if (TyphoonConfig.I.residualLow)
			{
				dr /= (1.0 + commaK * 0.8);
			}
			energy = Math.Max(0.0, energy - dr * effDt);
			intensity = 1.0 - 0.8 * Clamp01((30.0 - energy) / 30.0);
			cWindDirty = true;   // intensity 变化 → 风圈缓存失效
			double eFrac2 = Clamp01((energy - 30.0) / 50.0);
			vmaxTarget = Math.Max(vmaxBase * 0.35, vmaxTargetBase * (0.7 + 0.3 * eFrac2));
		}
		// 泥雨 muddyFactor（消散产物讨论：沙尘暴消散沙尘沉降，与降雨系统相遇 →
		// 雨染黄"泥雨"）：8s 节流遍历其他系统，消散中沙尘暴（stage==2）距本系统 <Rmax×2.5
		// → muddyFactor=1（渲染 DrawRain 雨色插值）。gate atmoClass==0（金星无降水、巨行星
		// 无泥雨）。O(n) 8s 一次，渲染只读字段零耦合。设置开关 muddyRain 控制。
		muddyFactor = 0.0;
		if (atmoClass == 0 && age - lastMuddyCheck > 8.0)
		{
			lastMuddyCheck = age;
			for (int oi = 0; oi < TyphoonManager.systems.Count; oi++)
			{
				WeatherSystem o = TyphoonManager.systems[oi];
				if (o == null || o == this || !o.active || o.type != StormType.DustStorm || o.stage != 2)
				{
					continue;
				}
				double dAng = Math.Abs(WrapPi(o.centerAngle - centerAngle));
				if (dAng * planet.Radius < Rmax * 2.5)
				{
					muddyFactor = 1.0;
					break;
				}
			}
		}
		// 审查二轮：land decay 改乘性放能量公式后（原减法在能量公式之前执行，
		// 每帧被 vmaxTargetBase×能量因子覆盖成死代码）。登陆摩擦的风速衰减（快过程）与
		// 能量枯竭（慢过程）叠加：风先崩、能量后崩 = 物理正确（摩擦耗散快/热力耗散慢），
		// 🟡-7 从 bug 变 feature。系数永久不回弹（可接受简化）。
		// 登陆摩擦（持久化 landWindK）——现实性审计抓到的潜伏 bug：原实现每帧只对
		// vmaxTarget 乘一次 (1-0.005×friction×dt)，但下一帧 stage 分支会把 vmaxTarget
		// 按档位公式重新赋值 → 乘子从不累积、登陆风速实际不衰减（注释"系数永久不回弹"
		// 也不成立）。现在改为持久乘子，速率同时现实化：原 0.5%/s（e-folding 200s =
		// 3 分钟风毁）夸张两个数量级，现实登陆台风风衰减 e-folding 12-24h →
		// 用 64800 游戏秒（18h）÷ 地形粗糙度（岩石 1.6 / 绿地 1.0 / 沙漠 0.55）。
		// 回到海上缓慢恢复（现实：重回暖水可再增强，恢复尺度 ~1-2 天）。
		if (type == StormType.Typhoon && stage >= 1)
		{
			if (overLand)
			{
				double friction = (terrainKind == TerrainKind.Rock) ? 1.6 : ((terrainKind == TerrainKind.Green) ? 1.0 : 0.55);
				landWindK -= Math.Min(landWindK, dt / 64800.0 * friction);
			}
			else
			{
				landWindK += dt / 172800.0 * (1.0 - landWindK);
			}
			landWindK = Clamp(landWindK, 0.3, 1.0);
			vmaxTarget *= landWindK;
		}
		if (energy <= 0.0)
		{
			// 能量枯竭 → dissolving 60 帧渐隐兜底（渲染淡出 + HUD 显示消散，完成才移除）
				if (!dissolving)
				{
					dissolving = true;
					dissolveCountdown = 300f;   // 演化：60→300 帧（5 秒现实渐隐，任何倍率可见不"啪"消失）
				}
			else
			{
				dissolveCountdown -= 1f;
				if (dissolveCountdown <= 0f)
				{
					active = false;
					return;
				}
			}
			return;
		}
		// 移动：沿 driftAngle 方向（角速度 = drift/radius），driftAngle 缓慢随机漂移
		driftAngle += (0.05 + 0.2 * (double)(seed % 13) / 13.0) * dt * 0.02;
		// 审查二轮（🟡-11）：SFS 2D 行星表面是圆周、只有 1 个自由度，路径弯曲
		// 几何上不可实现。改 driftAngle 调制**移速**（走走停停的移动节奏波动）——局部
		// moveSpeed 只改 centerAngle，**不改 drift 字段**（drift 同时用于风场叠加，改了
		// 会出"风暴呼吸"伪影）。周期 0.05、振幅 0.25 永不为负。
		// 修复：moveSpeed 存字段并供 CenterVelocity() 使用——风暴实际移动速度
		// 是 moveSpeed（摆动）而非固定 drift，粒子跟随若用固定 drift 会与风暴中心产生
		// ±0.25×drift 相对速度差（science-discuss 警告的"呼吸伪影"正是这个：粒子相对
		// 风暴周期摆动，两侧交替逼近 4.6Rmax 重生线）。现在粒子跟随 = 实际移动速度。
		moveSpeed = drift * (1.0 + 0.25 * Math.Sin(driftAngle * 2.0 + age * 0.05));
		// （专项 B 绕圈真 bug）— 移动用 effDt 而非 dt：消散期（stage==2）能量衰减被
		// effDt=min(dt,0.5) 钳制（恒 30 游戏秒/现实秒），原移动用全量 dt（随 warp 放大）
		// → 消散期移动比耗散快 warp/30 倍，飑线/MCS 等对流系统 warp 高倍下绕地球数圈
		// （warp1000x 1.8 圈/5000x 3.1 圈）；"能量下降过慢没随加速反应"是同一 bug。
		// 修复：移动跟随能量时间基准（消散期同步钳制，1x 零变化，消散可见性保留）。
		centerAngle = WrapTwoPi(centerAngle + moveSpeed / planet.Radius * effDt);

		// 障碍物（树/石）卷起与飞行：用全量 dt（飞行是快过程，与粒子/雨同尺度节奏）
		AdvanceDebris(dt);

		// 附属现象自然演化（成熟期：超级单体/飑线/MCS 概率产龙卷；所有强系统概率下击暴流）
		// 消散动画：dissolving 时 ~3 秒衰减（ClearPhenomena 触发），否则自然缓慢衰减。
		TypeSpec spec = Spec[(int)type];
		if (stage == 1)
		{
			// 自然发展强度进度（用户：自然发展强度也需进度条、满了到下一等级）：
			// 成熟期自然累积 naturalUpTime（~5 分钟），满 → 下一等级（钳 6）→ 目标强度
			// 平滑过渡（粒子过渡动画）；手动 F8 切档时 naturalProgress 由 Manager 重置。
			// 满级钳制（用户：达到最高等级后进度条还在动，1000%都可以）：
			// category=6 时不再累积，进度条停满 100%（原条件 category<6 才重置，满级后
			// naturalProgress 继续涨 → HUD 显示 1000%+）。
			// 等级上限科学性（用户：所有天气系统都能到最高等级不科学）：判据
			// category<6 → category<maxCat（台风 6 萨菲尔全表/沙尘暴 4 国标/超单飑线 MCS 4/
			// 多单体 2/单体 1 永不升级）；升级节奏用 Spec.naturalUpTimeSec 现实值（升 1 档
			// = 成熟期寿命 10-20%，×cL 大气分级与寿命同比例——巨行星 12h→15 天）。F8 超限
			// （category>maxCat，god mode）不累积不降级，HUD 标 ⚠超限。
			int maxC = TyphoonConfig.I.limitMaxCategory ? Math.Max(1, Math.Min(6, spec.maxCat)) : 6;   // 关=chaos 全 6 级
			// （终审🟡-4）— naturalUpTimeSec=0 的类型（maxCat==defaultCat，本不需升级）
			// 跳过累积——原 dt/max(0,1)=dt 导致类型转变后（如单体→多单体 category 保持 1）
			// 1 游戏秒内瞬时跳级回 defaultCat。
			if (spec.naturalUpTimeSec > 0.0 && category < maxC)
			{
				// （fix-checker ⚠️-1）— 升级节奏 ÷cLnow（原 *cLnow 方向反：巨行星升级
				// 快 30 倍 = 24 分钟/级，应慢 30 倍 = 15 天/级，与寿命 ×30 同向——"巨行星
				// 12h→15 天"注释语义）。
				naturalProgress += dt / Math.Max(spec.naturalUpTimeSec, 1.0) / cLnow;
				if (naturalProgress >= 1.0)
				{
					naturalProgress = 0.0;
					SetCategory(category + 1);
					energy = Math.Min(100.0, energy + Math.Min(12.0, 450.0 * netPerSec));   // 审查二轮：升级回血 min(12, 450×netPerSec)——净回血不超过环境耗散（冷水 0.0185×450=8.3），不再掩盖冷水负反馈
				}
			}
			else
			{
				naturalProgress = Math.Min(naturalProgress, 1.0);
			}
			// 自然类型转变（用户：系统可能向另一个系统转变）：成熟期低概率向
			// 更强相关类型转变（单体→多单体→超级单体→MCS、飑线→MCS），台风不转；
			// 3 秒粒子过渡动画（渲染端云淡出淡入），完成后按新类型重配尺度。
			// 沙尘暴不参与类型转变（干燥系统，不会变成雷暴）
			// 扩展（2026-09-12）：只有对流族参与类型转变链（枚举值 ≤ MCS，且排除 Typhoon）；
			// 沙尘暴/尘卷风（干燥系统）与温带气旋/冬季风暴/冰暴/浓雾/大气河（非对流族）均不参与。
			if (transitionAnimT < 0.0 && type != StormType.Typhoon && type <= StormType.MCS && Roll(2.0, dt, 2246822519u))
			{
				// 默认 = 不变，并用 nt > type 硬保证"只能变强"。原实现默认值写成了
				// StormType.Cell，而 MCS 没有任何分支命中 → 中尺度会"退化"成单体
				// （成熟期 13 次判定，几乎必然发生；此前判定门恒为假所以没暴露）。
				StormType nt = type;
				if (type == StormType.Cell)
				{
					nt = StormType.Multicell;
				}
				else if (type == StormType.Multicell)
				{
					nt = (UnityEngine.Random.value < 0.5) ? StormType.Supercell : StormType.MCS;
				}
				else if (type == StormType.Supercell)
				{
					nt = StormType.MCS;
				}
				else if (type == StormType.SquallLine)
				{
					nt = StormType.MCS;
				}
				if (nt > type)
				{
					TransitionTo(nt);
				}
			}
			if (spec.tornadoHost)
			{
				// 多实例（数量限制解除，上限 4 防爆渲染）：遍历衰减（manual 常驻
				// 跳过）、自然触发随机位置添加； 自然寿命 150s、 解散 2.5s。
				for (int ti = tornadoes.Count - 1; ti >= 0; ti--)
				{
					FxInst fx = tornadoes[ti];
					// 独立平流：龙卷相对母体缓慢切向漂移（原实现 sOff 一次随机后永不改变、
					// 钉死在母体切向偏移上），限幅 ±1.1Rmax 内。
					fx.sOff = Math.Max(-1.1, Math.Min(1.1, fx.sOff + fx.vOff * effDt));
					if (fx.dissolving || !fx.manual)
					{
						fx.strength -= dt / (fx.dissolving ? 2.5 : 150.0);
					}
					if (fx.strength <= 0.0)
					{
						tornadoes.RemoveAt(ti);
					}
					else
					{
						fx.phase = Math.Min(1.0, fx.phase + dt / 3.0);
					}
				}
				// 现实配额制（phenomenaRealism，现实性审计）：一生只产 tornadoQuota 个
				// （现实仅约 1/3 超单产龙卷、一生 1-3 个），且同时最多 2 个存活（真实龙卷
				// 是一轮一轮生成而非同时挂 4 个）；quota<0 = 旧行为（不限额连续生成）。
				bool canSpawnTornado = (tornadoQuota < 0)
					? (tornadoes.Count < 4)
					: (tornadoQuota > 0 && tornadoes.Count < 2);
				// 刷新频率随母体强度：弱超单产龙卷更稀、强超单更密（现实 EF 强度与母体强度相关）。
				double tornadoRate = (tornadoQuota < 0) ? 60.0 : (3.0 + category);
				if (canSpawnTornado && Roll(tornadoRate, dt, 2654435761u))
				{
					if (tornadoQuota > 0)
					{
						tornadoQuota--;
					}
					FxInst fx = new FxInst();
					fx.strength = 0.5 + category / 6.0;   // 强度跟母体
					fx.phase = 0.0;
					fx.dissolving = false;
					fx.manual = false;                  // 自然触发按寿命衰减
					fx.variant = DeriveTornadoVariant(fx);   // 类型自动派生（同 AddTornado，含多涡/卫星/gustnado/陆龙卷）
					RandomPosAvoid(fx, 0.9, true);   // 随机 + 防重叠
					tornadoes.Add(fx);
				}
				tornadoStrength = 0.0;
				foreach (FxInst fx in tornadoes)
				{
					tornadoStrength = Math.Max(tornadoStrength, fx.strength);
				}
				dissolvingTornado = tornadoes.Count > 0;
			}
			// 下暴多实例同理（上限 4，自然寿命 240s）。
			for (int di = downbursts.Count - 1; di >= 0; di--)
			{
				FxInst fx = downbursts[di];
				if (fx.dissolving || !fx.manual)
				{
					fx.strength -= dt / (fx.dissolving ? 2.5 : 240.0);
				}
				if (fx.strength <= 0.0)
				{
					downbursts.RemoveAt(di);
				}
				else
				{
					fx.phase = Math.Min(1.0, fx.phase + dt / 3.0);
				}
			}
			// 下暴配额（现实：一次强雷暴过程 1-2 个微下击暴流，同时最多 2 个）。
			bool canSpawnDownburst = (downburstQuota < 0)
				? (downbursts.Count < 4)
				: (downburstQuota > 0 && downbursts.Count < 2);
			double downburstRate = (downburstQuota < 0) ? 30.0 : 4.0;
			if (canSpawnDownburst && CanDownburst() && Roll(downburstRate, dt, 1597334677u))
			{
				if (downburstQuota > 0)
				{
					downburstQuota--;
				}
				FxInst fx = new FxInst();
				fx.strength = 0.5 + category / 6.0;   // 强度跟母体
				fx.phase = 0.0;
				fx.dissolving = false;
				fx.manual = false;
				RandomPosAvoid(fx, 0.6, false);   // 随机 + 防重叠
				downbursts.Add(fx);
			}
			downburstStrength = 0.0;
			foreach (FxInst fx in downbursts)
			{
				downburstStrength = Math.Max(downburstStrength, fx.strength);
			}
			dissolvingDownburst = downbursts.Count > 0;
			// 阵风锋自然演化（成熟期，上限 3）：衰减/相位同下暴，寿命 300s。
			for (int gi = gustFronts.Count - 1; gi >= 0; gi--)
			{
				FxInst fx = gustFronts[gi];
				if (fx.dissolving || !fx.manual)
				{
					fx.strength -= dt / (fx.dissolving ? 2.5 : 300.0);
				}
				if (fx.strength <= 0.0)
				{
					gustFronts.RemoveAt(gi);
				}
				else
				{
					fx.phase = Math.Min(1.0, fx.phase + dt / 3.0);
				}
			}
			// 阵风锋配额（现实：每个成熟中尺度系统 1 条出流边界，同时最多 2 条）。
			bool canSpawnGust = (gustFrontQuota < 0)
				? (gustFronts.Count < 3)
				: (gustFrontQuota > 0 && gustFronts.Count < 2);
			double gustRate = (gustFrontQuota < 0) ? 12.0 : 3.0;
			if (canSpawnGust && CanGustFront() && Roll(gustRate, dt, 3266489909u))
			{
				if (gustFrontQuota > 0)
				{
					gustFrontQuota--;
				}
				FxInst fx = new FxInst();
				fx.strength = 0.5 + category / 6.0;
				fx.phase = 0.0;
				fx.dissolving = false;
				fx.manual = false;
				RandomPosAvoid(fx, 1.5, false);
				gustFronts.Add(fx);
			}
			gustFrontStrength = 0.0;
			foreach (FxInst fx in gustFronts)
			{
				gustFrontStrength = Math.Max(gustFrontStrength, fx.strength);
			}
			// 闪电风暴自然演化（成熟期，上限 2）：寿命 400s。闪电风暴不产生风，
			// 只由渲染端读取（UpdateLightning 增强：冷却缩短/闪点集中/亮度提升）。
			for (int li = lightningBursts.Count - 1; li >= 0; li--)
			{
				FxInst fx = lightningBursts[li];
				if (fx.dissolving || !fx.manual)
				{
					fx.strength -= dt / (fx.dissolving ? 2.5 : 400.0);
				}
				if (fx.strength <= 0.0)
				{
					lightningBursts.RemoveAt(li);
				}
				else
				{
					fx.phase = Math.Min(1.0, fx.phase + dt / 3.0);
				}
			}
			if (lightningBursts.Count < 2 && Roll(8.0, dt, 40503u))
			{
				FxInst fx = new FxInst();
				fx.strength = 0.5 + category / 6.0;
				fx.phase = 0.0;
				fx.dissolving = false;
				fx.manual = false;
				RandomPosAvoid(fx, 1.2, false);
				lightningBursts.Add(fx);
			}
		}
		else if (stage != 1)
		{
			// 手动添加的常驻：非成熟期也不衰减（仅显式解散时衰减）。多实例遍历。
			for (int ti = tornadoes.Count - 1; ti >= 0; ti--)
			{
				FxInst fx = tornadoes[ti];
				if (fx.dissolving || !fx.manual)
				{
					fx.strength = Math.Max(0.0, fx.strength - dt / 60.0);
				}
				if (fx.strength <= 0.0)
				{
					tornadoes.RemoveAt(ti);
				}
			}
			for (int di = downbursts.Count - 1; di >= 0; di--)
			{
				FxInst fx = downbursts[di];
				if (fx.dissolving || !fx.manual)
				{
					fx.strength = Math.Max(0.0, fx.strength - dt / 60.0);
				}
				if (fx.strength <= 0.0)
				{
					downbursts.RemoveAt(di);
				}
			}
			tornadoStrength = 0.0;
			foreach (FxInst fx in tornadoes)
			{
				tornadoStrength = Math.Max(tornadoStrength, fx.strength);
			}
			downburstStrength = 0.0;
			foreach (FxInst fx in downbursts)
			{
				downburstStrength = Math.Max(downburstStrength, fx.strength);
			}
		}
	}

	// ===== 风场采样（行星全局坐标） =====
	public void ToStormFrame(Double2 globalPos, out double s, out double h)
	{
		double radius = planet.Radius;
		Double2 val = globalPos;
		h = val.magnitude - radius;
		val = globalPos;
		s = WrapPi(val.AngleRadians - centerAngle) * radius;
	}

	// includeDrift：false 时返回"相对风暴的风"（不含 drift 平流分量）。
	// 粒子移动用相对风 + CenterVelocity()（跟随风暴整体移动）——修复粒子被不均匀
	// drift 分量（中心 0.675×drift < 风暴速度 1.0×drift）推挤到边缘 4.6Rmax 重生
	// 导致的云团"从左到右消"（一切系统都有 drift，一切系统都这样）。玩家测风/
	// Harmony 补丁用默认 true（绝对风，含平流）语义不变。
	public Double2 SampleWind(Double2 globalPos, bool includeDrift = true)
	{
		if (!active || planet == null)
		{
			return Double2.zero;
		}
		ToStormFrame(globalPos, out var s, out var h);
		return SampleWindLocal(s, h, globalPos, includeDrift);
	}

	// 风暴中心移动速度（drift 沿经度正方向切向，与 ToStormFrame 的 s 同向）：
	// 粒子绝对速度 = SampleWind(pos, false)（相对风）+ CenterVelocity()（跟随风暴整体
	// 移动）——粒子不再被不均匀 drift 分量推挤出风暴边缘，云团左右对称稳定。
	public Double2 CenterVelocity()
	{
		if (planet == null)
		{
			return Double2.zero;
		}
		return new Double2(0.0 - Math.Sin(centerAngle), Math.Cos(centerAngle)) * moveSpeed;   // 跟随实际移动速度（moveSpeed 摆动）而非固定 drift
	}

	public Double2 SampleWindLocal(double s, double h, Double2 globalPos, bool includeDrift = true)
	{
		SampleComponents(s, h, out var u, out var w, includeDrift);
		Double2 val = globalPos;
		Double2 normalized = val.normalized;
		Double2 wind = new Double2(0.0 - normalized.y, normalized.x) * u + normalized * w;
		// 附属现象水平矢量风（基于玩家相对风暴中心的水平位置）：
		// 龙卷=绕轴切向旋转（左右螺旋）、下击暴流=径向向外辐散（四周扩散）。
		// 标量 u 只承载通用环流；真螺旋/辐散必须用矢量，否则方向恒为行星切向。
		if (active && planet != null)
		{
			if (tornadoStrength > 0.05)
			{
				wind += TornadoHorizontal(s, h, globalPos);
			}
			if (downburstStrength > 0.05)
			{
				wind += DownburstHorizontal(s, h, globalPos);
			}
			if (gustFrontStrength > 0.05)   // 阵风锋：地面出流前沿强风
			{
				wind += GustFrontHorizontal(s, h, globalPos);
			}
		}
		return wind;
	}

	// 风暴中心的行星坐标（centerAngle 处、地面高度），供矢量风计算相对位置。
	private Double2 StormCenterPos()
	{
		RefreshCenterCache();
		return cCenterPos;
	}

	// 附属现象实例中心（风暴中心 + 切向偏移 sOff×Rmax，沿经度方向）。
	private Double2 FxCenterPos(FxInst fx)
	{
		RefreshCenterCache();
		return cCenterPos + cCenterTan * (fx.sOff * Rmax);
	}

	// 地形判定（正相 + 变相）：
	// 变相：高度场先分海陆（真实地形 >0.5m = 陆地；水下 = Ocean，比颜色判海更准）；
	// 正相：GetTerrainColor 采样行星地表纹理像素色 → 绿主导=Green、暖色主导=Desert、
	// 高亮低饱和=Ice、中性灰=Rock。注意 Ice 判定必须在 Green 前（白色 g 也高）。
	// 注意：GetTerrainHeightAtAngle/GetTerrainColor 都有数组/纹理采样开销，须节流调用
	// （台风海陆检测 8s 一次），不可每帧调。
	public TerrainKind TerrainAt(Double2 globalPos)
	{
		return TerrainAt(planet, globalPos);
	}

	// 行星海温场（用户：冷水会冷死台风；先全面采样海陆分布，再按一套种子生成海温）：
	// per-planet 一次性生成缓存。沿圆周每 1° 采样 GetTerrainHeightAtAngle（360 次，生成时
	// 卡顿可接受，此后零成本索引），陆地标记；海温 = 基础值（按 atmoClass）+ 种子低频正弦
	// 叠加（确定性：同一行星恒同场，平滑无锯齿）。SFS 2D 行星无纬度，海温场即角度函数。
	public static float[] GetSstField(Planet pl, int atmoClass)
	{
		if (pl == null)
		{
			return null;
		}
		if (sstCache.TryGetValue(pl, out var cached))
		{
			return cached;
		}
		float[] land = new float[360];
		for (int a = 0; a < 360; a++)
		{
			double h = 0.0;
			try
			{
				h = pl.GetTerrainHeightAtAngle(a * 0.017453292519943295, false);
			}
			catch
			{
			}
			land[a] = (h > 0.5) ? 1f : 0f;
		}
		landCache[pl] = land;
		float baseSst = (atmoClass == 2) ? 30f : ((atmoClass == 1) ? 33f : 27f);   // 地球 27 / 金星 33（温室）/ 巨行星 30（内部热源）
		int seed = (pl.name != null) ? (pl.name.GetHashCode() & 0x7fffffff) : 42;   // 行星名哈希 → 确定性
		System.Random rng = new System.Random(seed * 7919 + 13);
		float a1 = (float)(rng.NextDouble() * 4.0 - 2.0);   // ±2°C 大尺度
		float a2 = (float)(rng.NextDouble() * 2.0 - 1.0);   // ±1°C 小尺度
		float p1 = (float)(rng.NextDouble() * 6.2831853);
		float p2 = (float)(rng.NextDouble() * 6.2831853);
		float[] sst = new float[360];
		for (int a = 0; a < 360; a++)
		{
			double rad = a * 0.017453292519943295;
			float s = baseSst + a1 * (float)Math.Sin(rad * 2.0 + p1) + a2 * (float)Math.Sin(rad * 5.0 + p2);
			if (land[a] > 0.5f)
			{
				s = baseSst - 8f;   // 陆地标低值（台风登陆无海温 → 能量断供）
			}
			sst[a] = s;
		}
		sstCache[pl] = sst;
		sstOrigCache[pl] = (float[])sst.Clone();   // 原始场备份（冷尾流恢复目标）
		return sst;
	}

	// 角度（弧度）→ 海温°C（索引取模，零开销；场未生成时返回 27 默认）。
	public static float SampleSst(Planet pl, double angleRad, int atmoClass)
	{
		float[] f = GetSstField(pl, atmoClass);
		if (f == null)
		{
			return 27f;
		}
		int idx = (int)(angleRad * 57.29577951308232) % 360;
		if (idx < 0)
		{
			idx += 360;
		}
		return f[idx];
	}

	// 台风挖冷水（冷尾流）：强风埃克曼抽吸翻上深层冷水 → 中心 SST 降 → 能量制 sstF 降
	// → 自我削弱（负反馈闭环）。暖水层（混合层 3-6°C，观测上限 ~4.76°C）限可挖深度；
	// 恢复慢（现实 12-30 天）；快速台风冷尾迹偏路径右侧（drift 方向偏移 1°）。
	private void DigColdWater()
	{
		// （终审🟢-3）— gate atmoClass==0：巨行星无"海"、金星无液态水，挖冷是无用计算
		// 且永久污染海温缓存（sstF 已 gate 但缓存仍被改低）。
		if (atmoClass != 0 || planet == null || terrainKind != TerrainKind.Ocean || age - lastSstDig < 1.0)
		{
			return;
		}
		lastSstDig = age;
		float[] f = GetSstField(planet, atmoClass);
		float[] orig;
		float[] land;
		if (f == null || !sstOrigCache.TryGetValue(planet, out orig) || !landCache.TryGetValue(planet, out land))
		{
			return;
		}
		int seed = (planet.name != null) ? (planet.name.GetHashCode() & 0x7fffffff) : 42;
		double mixedLayer = 3.0 + (double)(seed % 4);   // 暖水层 3-6°C（观测降幅上限 ~4.76°C）
		int cIdx = (int)(centerAngle * 57.29577951308232) % 360;
		if (cIdx < 0)
		{
			cIdx += 360;
		}
		int bias = (drift >= 6.0) ? 1 : 0;   // 审查🟡-5：快速台风（drift≥6 m/s）冷尾迹偏路径右侧（近惯性混合），慢速挖中心（Ekman 泵主导）；SFS 2D 无科氏力，此为游戏化偏移
		cIdx = (cIdx + bias + 360) % 360;
		// 搅动强度与台风强度关系校准（用户：这块没搜——Nature 2025 原文：
		// 风应力对风速呈二次方依赖（quadratic dependence），垂直混合/埃克曼抽吸由
		// 风应力驱动 → 冷却速率按 vmax² 缩放而非线性）。
		// （终审🟡-3/13）— 冷尾流恢复与挖深校准（Dare & McBride 2011：冷尾流
		// e-folding 恢复 5-20 天、88% 30 天内恢复；强台风降幅 3-6°C）：①恢复系数
		// 0.001→0.00001（时间常数 1000s≈16.7min → 100000s≈1.16 天——现实 5-20 天的
		// 游戏化加速 5-20 倍，可观察持续）；②cool 系数 6e-7→1e-8（62 m/s 稳态挖深
		// 2.31°C→3.84°C，落进 3-6°C 暖水层；40 m/s 1.6°C——强台风才挖得穿）。
		double cool = vmaxDisplay * vmaxDisplay * 0.00000001;
		for (int d = -3; d <= 3; d++)
		{
			int idx = (cIdx + d + 360) % 360;
			if (land[idx] > 0.5f)
			{
				continue;
			}
			double edge = 1.0 - Math.Abs(d) * 0.25;   // 中心 1.0 → ±3° 边缘 0.25
			float floor = orig[idx] - (float)mixedLayer;   // 挖穿下限（暖水层底）
			f[idx] = Math.Max(floor, f[idx] - (float)(cool * edge));
			f[idx] += (orig[idx] - f[idx]) * 0.00001f;   // 恢复校准（时间常数 ~1.16 天，现实 5-20 天加速版）
		}
	}

	// 静态版（自然生成判断用，无需 WeatherSystem 实例）：行星 + 位置 → 地形类型。
	public static TerrainKind TerrainAt(Planet pl, Double2 globalPos)
	{
		if (pl == null)
		{
			return TerrainKind.Ocean;
		}
		double hLand;
		try
		{
			hLand = pl.GetTerrainHeightAtAngle(globalPos.AngleRadians, false);
		}
		catch
		{
			return TerrainKind.Rock;
		}
		if (hLand <= 0.5)
		{
			return TerrainKind.Ocean;
		}
		Color c;
		try
		{
			c = pl.GetTerrainColor(globalPos);
		}
		catch
		{
			return TerrainKind.Rock;
		}
		float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
		float mn = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
		float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
		float sat = (mx - mn) / Mathf.Max(mx, 0.01f);
		if (lum > 0.72f && sat < 0.25f)
		{
			return TerrainKind.Ice;
		}
		if (c.g >= c.r && c.g >= c.b && c.g > 0.28f)
		{
			return TerrainKind.Green;
		}
		if (c.r >= c.g && c.r > c.b)
		{
			return TerrainKind.Desert;
		}
		return TerrainKind.Rock;
	}

	// 地形中文名（HUD 显示）。
	public static string TerrainName(TerrainKind k)
	{
		switch (k)
		{
			case TerrainKind.Ocean:
				return "海上";
			case TerrainKind.Green:
				return "陆地·绿地";
			case TerrainKind.Desert:
				return "陆地·沙漠";
			case TerrainKind.Ice:
				return "陆地·冰原";
			default:
				return "陆地·山地";
		}
	}

	// 龙卷尺度基准（米）：漏斗宽度不能随母体 Rmax 无限放大 —— Rmax 按类型表给
	// （超单 3.6km / 飑线 5.9km / MCS 12.4km），Rmax×3.8% 在中尺度上就是 471m 核
	// （楔形 1037m）：1km 外还在核内满速，渲染端也画出 ~1km 宽的"漏斗"。以 4km 为
	// 基准钳制，风场与渲染端共用 → 看得见的漏斗 = 吸你的范围。
	public double TornadoRefR => Math.Min(Rmax, 4000.0);

	// EF5 风速阈值（m/s）：≥ 此值的龙卷进入"超巨型"档。
	public const double TornadoEf5Wind = 89.4;

	// 龙卷尺度倍率（实例）：类型倍率 × 超巨型倍率 —— 风场与渲染端共用同一个倍率。
	// 超巨型：峰值风速 ≥ EF5（89 m/s）的少数极端龙卷额外放大（现实最宽的 El Reno 2013
	// 达 4.2km，典型 EF5 楔形 1-2km）：倍率随风速 89→126 m/s 从 1 升到 3，于是极端
	// 中尺度楔形的核半径可到 ~1km（"核可以到 1000"）。只有约 15% 的强风暴龙卷能到 EF5，
	// 所以超巨型是稀有事件，不会遍地都是。
	public double TornadoSizeK(FxInst fx)
	{
		double k = 1.0;
		switch (fx.variant)
		{
			case 1: k = 2.2; break;    // 楔形：宽实墙
			case 2: k = 0.4; break;    // 绳状：细长
			case 3: k = 1.3; break;    // 水龙卷：水雾柱略宽
			case 4: k = 0.7; break;    // 陆龙卷：细尘柱
			case 5: k = 0.85; break;   // 多涡旋：主涡略细
			case 7: k = 0.7; break;    // gustnado：矮小尘旋
		}
		double pw = TornadoPeakWind(fx);
		if (pw > TornadoEf5Wind)
		{
			k *= 1.0 + 2.0 * Clamp01((pw - TornadoEf5Wind) / 37.0);
		}
		return k;
	}

	// 龙卷核半径（实例口径，米）= 尺度基准（钳 4km）× 0.038 × 尺度倍率，下限 30m。
	// 与渲染端地面漏斗半径同源（Rmax×0.024×1.6 ≈ ×0.038），风场 / EF / 卷入判定共用。
	public double TornadoCoreR(FxInst fx)
	{
		return Math.Max(TornadoRefR * 0.038 * TornadoSizeK(fx), 30.0);
	}

	// 尺度标签（HUD/雷达预报）：≥500m 超巨型、≥250m 大型。
	public static string TornadoSizeName(double coreM)
	{
		if (coreM >= 500.0)
		{
			return "超巨型";
		}
		if (coreM >= 250.0)
		{
			return "大型";
		}
		return "";
	}

	// 龙卷实例峰值风速（m/s）：90 m/s（EF5 基准）× 实例强度（0.5+cat/6）× 种子散布（0.5-1.0）。
	// 种子散布让"同一母体可产不同等级的龙卷"（现实：一个超单先产 EF1 再产 EF4）。
	// 【EF 核对】风场 / EF 显示 / 卷入判定 全部读这个值 —— 原来 EF 显示读的是**母体**
	// EyewallWind（20-40 m/s），显示的 EF1 和实际吸你 90-120 m/s 的风完全对不上。
	public double TornadoPeakWind(FxInst fx)
	{
		// 散布取平方（0.45-1.0，低端密高端疏）：现实 EF4+ 只占全部龙卷的 1-2%，
		// 线性散布会让"cat4 母体产的龙卷 68% 都是 EF5"，过于夸张。
		double p = (fx.seed % 100.0) / 100.0;
		double spread = 0.45 + 0.55 * p * p;
		return 90.0 * fx.strength * spread;
	}

	// 本系统当前最强龙卷的 EF 级（无龙卷返回 -1）：HUD 详情卡 / 雷达预报 / 指挥中心共用。
	public int StrongestTornadoEf()
	{
		int best = -1;
		for (int i = 0; i < tornadoes.Count; i++)
		{
			int ef = EfFromWind(TornadoPeakWind(tornadoes[i]));
			if (ef > best)
			{
				best = ef;
			}
		}
		return best;
	}

	// 龙卷水平旋转矢量：方向 = 相对龙卷中心的水平切向；大小 = 实例峰值风速 ×
	// Rankine 剖面（核内刚体 → 核外 1/r）× exp(-((r−rc)/6rc)²) 快速截断。
	// 尺度用核半径 rc（不是 Rmax×0.3）：核内满速、2rc 半速、1km 外基本无风，
	// 与判定/视觉对齐（原实现 5km 外还有 20 m/s）。多实例：逐龙卷独立中心与强度。
	private Double2 TornadoHorizontal(double s, double h, Double2 globalPos)
	{
		Double2 wind = Double2.zero;
		Double2 nrm = globalPos.normalized;              // 玩家位置的行星径向（竖直）
		for (int ti = 0; ti < tornadoes.Count; ti++)
		{
			FxInst fx = tornadoes[ti];
			if (fx.strength <= 0.05)
			{
				continue;
			}
			Double2 center = FxCenterPos(fx);
			Double2 r = globalPos - center;
			double rn = r.x * nrm.x + r.y * nrm.y;           // r 的径向分量（与锚点的高度差）
			Double2 rHoriz = r - nrm * rn;                   // 水平分量（垂直于径向）
			double rm = rHoriz.magnitude;
			if (rm < 0.5)
			{
				continue;
			}
			Double2 inward = new Double2(0.0 - rHoriz.x, 0.0 - rHoriz.y) / rm;   // 纯水平向心
			double rc = TornadoCoreR(fx);
			double rx = rm / rc;
			double prof = (rx <= 1.0) ? (0.6 + 0.4 * rx) : (1.0 / rx);            // Rankine 剖面
			double cut = Math.Exp(0.0 - Pow2((rm - rc) / (rc * 6.0)));            // 快速截断（非长尾）
			double hTopT = Math.Max(Hbase * 1.2, 300.0);                          // 垂直尺度 = 漏斗（云底）
			double funnel = Math.Pow(1.0 - Clamp01(Math.Max(0.0, h) / hTopT), 0.7);
			double grow = Math.Min(fx.phase, fx.strength);
			double speed = TornadoPeakWind(fx) * prof * cut * funnel * grow;
			wind += inward * speed;
		}
		return wind;
	}

	// 下击暴流水平辐散矢量：方向 = 玩家相对中心径向向外，大小 = 出流风速
	// （dro 0.4-1.6 起效、近地面最强）。 — 多实例遍历（独立中心/强度累加）。
	private Double2 DownburstHorizontal(double s, double h, Double2 globalPos)
	{
		Double2 wind = Double2.zero;
		Double2 nrm = globalPos.normalized;
		for (int di = 0; di < downbursts.Count; di++)
		{
			FxInst fx = downbursts[di];
			if (fx.strength <= 0.05)
			{
				continue;
			}
			Double2 center = FxCenterPos(fx);
			Double2 r = globalPos - center;
			double rn = r.x * nrm.x + r.y * nrm.y;
			Double2 rHoriz = r - nrm * rn;
			double rm = rHoriz.magnitude;
			if (rm < 0.5)
			{
				continue;
			}
			Double2 outDir = rHoriz / rm;   // 纯水平径向向外
			// 现实性审计：基准出流 60 m/s × strength（0.5+cat/6，合并上限 1.4）= 70-84 m/s，
			// 超过观测极值（微下击暴流地面出流峰值 67 m/s）→ 基准降到 55 m/s
			// （55×1.17=64 m/s 强微下击暴流，×1.4=77 m/s 仅在连续合并后短暂出现）。
			double sRel = s - fx.sOff * Rmax;
			double num = Math.Abs(sRel);
			double dro = num / Math.Max(Rmax * 0.9, 300.0);
			double droZ = dro / StormRenderer.downburstZoneHoriz;
			double spread = 0.5 + 0.5 * SmoothStep(droZ, 0.0, 0.85);
			if (droZ > 0.85)
			{
				spread *= 1.0 - SmoothStep((droZ - 0.85) / 0.75, 0.0, 1.0);
			}
			double ground = Clamp01(1.0 - Math.Max(0.0, h) / Htop / 0.5);
			double edge = Math.Exp(0.0 - Pow2(num / (Rmax * 2.5)));   // 收紧（原 Router）
			double grow = Math.Min(fx.phase, fx.strength);
			double speed = 55.0 * fx.strength * spread * (0.5 + 0.5 * ground) * edge * grow;
			wind += outDir * speed;
		}
		return wind;
	}

	// 阵风锋水平出流矢量：锋面（sOff 处）附近地面强出流，方向沿 +s（行星切向，
	// 风暴前进方向）。气象模型：下暴冷池出流推进前沿，近地面最强（h<1500m）、锋面中心
	// 峰值向两侧指数衰减；基准风速 28 m/s × 母体强度（阵风锋典型 20-40 m/s 强阵风）。
	private Double2 GustFrontHorizontal(double s, double h, Double2 globalPos)
	{
		Double2 wind = Double2.zero;
		Double2 nrm = globalPos.normalized;
		Double2 tan = new Double2(0.0 - nrm.y, nrm.x);   // +s 方向（s 增加 = 逆时针切向）
		for (int gi = 0; gi < gustFronts.Count; gi++)
		{
			FxInst fx = gustFronts[gi];
			if (fx.strength <= 0.05)
			{
				continue;
			}
			double sRel = s - fx.sOff * Rmax;            // 玩家相对锋面的切向距离
			double rho = sRel / Rmax;
			if (rho < -1.5 || rho > 2.0)
			{
				continue;                               // 超出锋面影响区
			}
			double profile = Math.Exp(0.0 - Pow2((rho - 0.15) / 0.75));   // 锋面中心约 0.15Rmax 前缘
			double ground = Clamp01(1.0 - Math.Max(0.0, h) / 1500.0);     // 地面出流，1.5km 以上消失
			double grow = Math.Min(fx.phase, fx.strength);
			double speed = 28.0 * fx.strength * profile * ground * grow;
			if (speed > 0.05)
			{
				wind += tan * speed;
			}
		}
		return wind;
	}

	// 公开诊断：玩家位置处下击暴流出流速度（m/s），用于 HUD 调试确认生效。
	public double DownburstSpeedAt(double s, double h, Double2 globalPos)
	{
		if (downburstStrength < 0.05)
		{
			return 0.0;
		}
		return DownburstHorizontal(s, h, globalPos).magnitude;
	}

	// ===== 参数化风场（按类型：切向 + 垂直 + 旋转 + 下沉/辐散 + 阵风锋） =====
	// includeDrift：false（粒子相对风）时跳过 drift 平流分量。Probe/玩家测风
	// 用默认 true（绝对风，语义不变）。粒子用它 + CenterVelocity() 跟随风暴整体移动。
	public void SampleComponents(double s, double h, out double u, out double w, bool includeDrift = true)
	{
		u = 0.0;
		w = 0.0;
		if (!active || planet == null)
		{
			return;
		}
		double num = Math.Abs(s);
		// 风暴范围外无风（用户：不在风暴范围仍有风影响，所有风暴都是）：
		// 原边界 Router×2.4（21.6Rmax）太远、edge 基准 Router(9Rmax) 使风暴外仍有
		// 明显风。收紧：采样边界 6Rmax、edge 基准 2.5Rmax（2.5Rmax 处 37%、5Rmax 处
		// ~1.8%）——视觉云团（Rmax×2-3）之外风迅速消失。
		if (num > Rmax * 6.0 || h > Htop * 1.4 || h < -600.0)
		{
			return;
		}
		// 风圈缓存刷新（与采样位置无关，指纹不变时零开销；原每采样重算 4×90 迭代）
		RefreshWindCircles();
		TypeSpec spec = Spec[(int)type];
		// 调试风区偏移（F2-F5）已移除，风场采样点不再平移（sWind = s）。
		double sWind = s;
		double numW = Math.Abs(sWind);
		double ro = numW / Rmax;
		double ht = Clamp01(Math.Max(0.0, h) / Htop);
		double num4 = (s >= 0.0) ? 1.0 : (-1.0);
		double edge = Math.Exp(0.0 - Pow2(numW / (Rmax * 2.5)));   // 收紧衰减
		double hDecay = Math.Exp((0.0 - ht) / 0.14);
		double gust = spec.gustFront;

		// 附属下击暴流：局部强下沉 + 低层辐散。
		// 水平辐散从标量 u 移出 → SampleWindLocal 的 DownburstHorizontal 矢量
		// （方向=玩家相对中心的径向向外，真正"四周扩散"）；这里只留垂直下沉 w。
		// w 也乘形成动画 grow（由弱到强，与视觉同步）。
		// **符号修复**：原为 `w +=`（上升），与注释"强下沉"矛盾——下击暴流
		// 应该是下沉气流（w 负），玩家在下击暴流中心被往下压。改 `w -=`（下沉）。
		// 下暴多实例：遍历下沉累加（每实例独立中心/强度/相位）。
		if (downbursts.Count > 0)
		{
			// 优化 — 循环不变式外提：torYield 只依赖采样点 num 与龙卷列表（与遍历到哪个
			// 下暴实例无关，且原代码对内层 torYield 是覆盖赋值而非累加），垂直衰减
			// Exp((0-ht)/0.35) 同理——原来两者都嵌在下暴循环内 = O(下暴数×龙卷数) 次
			// 重算同一份值。外提后 O(下暴数)+O(龙卷数)，数值结果完全等价。
			double torYield = 1.0;
			for (int ti = 0; ti < tornadoes.Count; ti++)
			{
				FxInst tfx = tornadoes[ti];
				if (tfx.strength <= 0.05)
				{
					continue;
				}
				// 以该龙卷自身中心为基准（原用 |s| = 距风暴中心 → 龙卷偏在一边时掩膜错位），
				// 尺度收到核半径的 3 倍内。
				double rcY = TornadoCoreR(tfx);
				double troY = Math.Abs(s - tfx.sOff * Rmax) / (rcY * 3.0);
				torYield = 1.0 - tfx.strength * Math.Exp(0.0 - Pow2(troY / 1.0));
			}
			double sinkVert = Math.Exp((0.0 - ht) / 0.35);
			for (int di = 0; di < downbursts.Count; di++)
			{
				FxInst fx = downbursts[di];
				if (fx.strength <= 0.05)
				{
					continue;
				}
				double sRelD = num - Math.Abs(fx.sOff * Rmax);   // 玩家相对该下暴的切向距离
				double dro = sRelD / Math.Max(Rmax * 0.9, 300.0);
				double droV = dro / StormRenderer.downburstZoneVert;
				double sink = Math.Exp(0.0 - Pow2(droV / 1.3));
				double growD = Math.Min(fx.phase, fx.strength);
				// 现实性审计：下沉核基准 55→40 m/s（观测微下击暴流下沉核 20-40 m/s，
				// 强出流 55 m/s 在地面出流层，不是下沉核本身）。
				w -= 40.0 * fx.strength * sink * sinkVert * growD * torYield;
			}
		}

		// / — 附属龙卷：极窄强旋转涡旋（叠加在宿主中心，漏斗状垂直结构）。
		// 多实例遍历：每龙卷独立中心/强度/相位累加上升吸力。
		for (int ti = 0; ti < tornadoes.Count; ti++)
		{
			FxInst fx = tornadoes[ti];
			if (fx.strength <= 0.05)
			{
				continue;
			}
			// 相对该龙卷的切向距离：原为 num−|sOff×Rmax|（num=|s| 是距风暴中心 ——
			// 龙卷在正侧时算错），且尺度用 Rmax×0.3 ≈ 1.1km → 500m 外仍有 ~35 m/s 上升
			// 气流（"隔得很远也被吸上天"）。现在与水平风同源：核半径 rc 的 1.8 倍内为主。
			double rcV = TornadoCoreR(fx);
			double rl = Math.Abs(s - fx.sOff * Rmax) / rcV;
			double upCore = Math.Exp(0.0 - Pow2(rl / 1.8));
			double funnel = Math.Pow(1.0 - Clamp01(ht / 0.9), 0.7);
			double growT = Math.Min(fx.phase, fx.strength);
			w += TornadoPeakWind(fx) * 0.6 * upCore * funnel * growT;
		}

		// 龙卷核心通用环流让位：通用旋转环流（行星切向）在中心两侧方向相反，
		// 叠加到龙卷向心风后 HUD 水平风左右不对称（用户实测左 40+ 右 10+）。龙卷核心内
		// 压制通用 u（rotCore 范围内 0 → 外圈恢复），让龙卷自身风场主导、左右对称。
		// 多实例：所有龙卷核心都让位（取最严）。
		double torMask = 1.0;
		for (int ti = 0; ti < tornadoes.Count; ti++)
		{
			FxInst fx = tornadoes[ti];
			if (fx.strength <= 0.05)
			{
				continue;
			}
			// 掩膜同样以龙卷自身中心为基准 —— 原来用 |s|（距风暴中心），而龙卷随机偏置在
			// ±0.9Rmax，掩膜常常落在没有龙卷的地方，这就是"HUD 左右风不对称"老问题
			// 其实一直没被真正压掉的原因。
			double rcM = TornadoCoreR(fx);
			double troM = Math.Abs(s - fx.sOff * Rmax) / (rcM * 3.0);
			torMask = Math.Min(torMask, 1.0 - fx.strength * Math.Exp(0.0 - Pow2(troM / 1.0)));
		}
		if (torMask < 0.1)
		{
			torMask = 0.1;
		}

		// 通用切向轮廓（台风/单体/多单体/超级单体/MCS）
		// 回退远古版风力轮廓（ 反编译确认）。
		// 补齐远古版缺失项（阵风/drift/outflow）。
		// 对称眼壁双峰（用户明确要的分布：弱-较弱-中-较强-强(眼壁)-弱(风眼)-强(眼壁)
		// 较强-中-较弱-弱 —— 真实台风结构！-73 的非对称单峰理解错了，改回对称）：
		// sR=0 风眼弱 0.2、|sR|=0.5 中 0.5、|sR|=1 双侧眼壁峰 1.0、|sR|>1 外围衰减到 0.2。
		double sR = sWind / Rmax;   // 偏移功能保留（默认 off=0 即对称双峰）
		double aR = Math.Abs(sR);
		// 风圈概念（用户：搞 7/10/12 级风圈，7 级往外线性过渡至边缘 + 突发性
		// 高一两级的大风）：num6 = BaseProfile × 11 区系数；若在 7 级风圈（13.9 m/s）之外，
		// 从 7 级值线性过渡到 Router 边缘归 0（替换原恒定 0.2 尾巴）。
		double num6 = BaseProfile(aR);
		num6 *= StormRenderer.windZoneGain[WindZoneIndex(sR)];
		// 台风眼壁峰对齐 TyphoonEyeR（用户：台风眼存在于每个等级 + 眼壁不猛）：
		// 视觉眼壁在 eyeR（0.22~0.40R），但 BaseProfile 风峰在 1.0R——风最猛处离视觉眼壁
		// 差 2-4 倍，玩家看到"眼壁不是最猛、外围才狂风"。重映射（仅台风，eyeR>0 即 cat≥2）：
		// 风眼(≤eyeR)压静 0.06 → 眼壁(eyeR) 峰 1.0 → 眼壁外过渡 0.5 → 外围衰减；
		// 11 区系数在重映射后仍相乘（风眼 gain[5]=1.0 保持平静）。TD/TS（eyeR=0）走原分布。
		double eyeRT = (type == StormType.Typhoon) ? TyphoonEyeR(category) : 0.0;
		if (eyeRT > 0.05)
		{
			double aRN = aR / eyeRT;
			if (aRN <= 1.0)
			{
				num6 = 0.06 + 0.94 * SmoothStep(aRN, 0.0, 1.0);   // 风眼平静 → 眼壁爬升
			}
			else if (aRN <= 1.6)
			{
				num6 = 1.0 - 0.5 * SmoothStep(aRN - 1.0, 0.0, 0.6);   // 眼壁峰 → 0.5
			}
			else
			{
				num6 = 0.5 * (1.0 - 0.8 * SmoothStep((aRN - 1.6) / 4.0, 0.0, 1.0));   // 外围衰减
			}
			num6 *= StormRenderer.windZoneGain[WindZoneIndex(sR)];
		}
		double circle7 = cC7;   // 缓存（RefreshWindCircles 已刷新）
		if (circle7 > 0.25 && aR > circle7)
		{
			double want7 = 13.9 / Math.Max(vmaxDisplay * intensity, 1.0);
			double tLin = Clamp01((aR - circle7) / Math.Max(Router / Rmax - circle7, 0.01));
			num6 = want7 * (1.0 - tLin);
		}
		// 风圈保底（用户确认：max 下限兜底 + 跳过风眼）：风眼外每个半径至少
		// 达到对应风圈等级——12级圈内≥32.7、10级圈内≥24.5、7级圈内≥13.9、7级圈外线性到 0；
		// 与 11 区分布取 max（系数调低时风圈兜住下限、调高则按系数走）。风圈半径全用 baseOnly
		// （纯基础分布稳定骨架）；保底钳制不超 Vmax×intensity×0.9。
		// ①跳过边界 0.7 → eyeRT（与渲染眼壁统一，TD/TS 无眼不跳过）；
		// ②保底 need 除以 edge——原保底只抬 num6，但 uBase=num6×edge 又乘了 edge，外圈
		// 保底被二次衰减吃掉（7级圈处宣称 13.9 实际只有 7.9 m/s）。现在 num6 抬到
		// floor/(Vmax×edge)，实际风速 = Vmax×num6×edge ≈ floor，保底名副其实。
		if (aR > Math.Max(eyeRT, 0.05))
		{
			double c7b = cC7b;   // 缓存（RefreshWindCircles 已刷新）
			double c10 = cC10;
			double c12 = cC12;
			if (c7b > 0.25)
			{
				double floor = 0.0;
				if (c12 > 0.25 && aR <= c12)
				{
					floor = 32.7;
				}
				else if (c10 > 0.25 && aR <= c10)
				{
					floor = 24.5;
				}
				else if (aR <= c7b)
				{
					floor = 13.9;
				}
				else
				{
					floor = 13.9 * (1.0 - Clamp01((aR - c7b) / Math.Max(Router / Rmax - c7b, 0.01)));
				}
				floor = Math.Min(floor, vmaxDisplay * intensity * 0.9);
				double need = floor / Math.Max(vmaxDisplay * intensity, 1.0) / Math.Max(edge, 0.03);
				num6 = Math.Max(num6, need);
			}
		}
		double outflow = Math.Exp(0.0 - Pow2((ht - 0.88) / 0.14));
		double uBase = num6 * edge * ((0.0 - num4) * hDecay + num4 * TyphoonConfig.I.outflowFraction * outflow);

		// 线状系统（飑线/MCS）：沿线的法向风 + 阵风锋（出流突增）
		if (spec.line > 0.05)
		{
			// 线状：用 |s| 沿线法向距离；风场以阵风锋为界（锋内大风、锋外环境）
			double gfr = 1.0 / Math.Max(spec.gustFront, 0.2);
			double front = Math.Exp(0.0 - Pow2((ro - gfr) / 0.35));
			uBase = num6 * edge * (0.4 + 0.9 * front) * (0.0 - num4);
		}

		// 旋转分量（超级单体中气旋）
		if (spec.rotate > 0.05)
		{
			// 旋转分量不再中心爆炸（用户：方形框任何边缘都没有最大风力，
			// 方形中心风力才最大）：原 rot=1/max(ro,0.3) 在 ro=0 时 3.3 倍，把总风峰
			// 拉回中心，与黑框（半长 Rmax = ro=1 风峰）不符。改 rot 随 num6 轮廓
			// （0.4 中心保底 + 0.6×num6，ro=1 峰 1.0）→ 总风峰回到框边（ro=1）。
			double rot = 0.4 + 0.6 * num6;
			u += vmaxDisplay * spec.rotate * rotateScale * 0.35 * rot * edge * hDecay * num4 * intensity * torMask;   // 巨行星反气旋 rotateScale
		}

		u += vmaxDisplay * uBase * intensity * torMask;

		// 远古版（）补回项：
		// ① 风暴移动叠加风：风暴漂移（drift）本身也是风，叠加到切向 u（低层最强，ro≈1 峰值）。
		// includeDrift=false（粒子相对风）时跳过该项：原 Gauss 调制（中心 0.675×drift、
		// 眼壁 1.0×drift）让粒子在中心比风暴移动慢 → 粒子相对风暴持续向边缘漂移 → 4.6Rmax
		// 重生消失 = 云团"从左到右消"。粒子改用相对风 + CenterVelocity() 整体跟随风暴。
		if (includeDrift)
		{
			u += drift * edge * (0.35 + 0.65 * Math.Exp(0.0 - Pow2((ro - 1.0) / 1.2))) * hDecay;
		}
		// 突发性大风（用户：7 级往外线性过渡 + 突发性高一两级的大风）：
		// 7 级风圈外的过渡区叠加随机短脉冲——约每 2.5-4 秒一次、持续 ~1.2 秒、瞬时
		// 风速 +8~12 m/s（高 1-2 级：7级→8-9级、10级→11-12级），位置相关相位保证
		// 不同地点不同时爆发（模拟真实台风外围的阵风锋/飑线突风）。edge 沿边缘衰减。
		// 严格限于风暴内部（用户：突发大风机制必须严格限风暴内部、判定线边缘
		// 向中心部近 100m）：原上限 Router(9Rmax) 太宽（风暴外也触发）→ 改为风暴判定边缘
		// Rmax×2.5（与 主风场 edge 基准一致）再向中心内缩 100m——突发风只在
		// [7级圈, 边缘−100m] 带内爆发，边缘外 100m 内与风暴外一律不触发。
		if (circle7 > 0.25 && aR > circle7 && aR * Rmax < Rmax * 2.5 - 100.0)
		{
			double ph = (age * 0.33 + num * 0.011 + (double)(seed % 7) * 0.13) % 1.0;
			double pulse = Math.Exp(0.0 - Pow2((ph - 0.12) / 0.09));
			double burst = 8.0 + 4.0 * ((double)((seed * 31 + (int)(num * 3.7)) % 100) / 100.0);
			u += burst * pulse * edge * num4 * (0.5 + 0.5 * hDecay);
		}
		// ② PerlinNoise 湍流阵风（ 原样，gustAmplitude=0.35）：风场不呆板、有阵风起伏。
		// 严格限风暴内部（用户：跟突发大风一样的判定）：边缘判定 Rmax×2.5 向中心
		// 内缩 100m——风暴边缘外 100m 与风暴外一律无湍流阵风（原只在采样边界 6Rmax 内）。
		double gustAmplitude = TyphoonConfig.I.gustAmplitude;
		if (gustAmplitude > 0.0001 && aR * Rmax < Rmax * 2.5 - 100.0)
		{
			float num11 = (float)(s / 3500.0 + age * 0.3);
			float num12 = (float)(h / 1200.0);
			double num13 = (double)Mathf.PerlinNoise(num11, num12) * 2.0 - 1.0;
			double num14 = (double)Mathf.PerlinNoise(num11 + 37.13f, num12 + 11.71f) * 2.0 - 1.0;
			double num15 = (double)Mathf.PerlinNoise(num11 * 3.7f + 5.5f, num12 * 3.1f) * 2.0 - 1.0;
			double num16 = edge * (0.35 + 0.65 * hDecay);
			// 波动下波动低（用户：参考值附近上下波动、下波动幅度低——持续风稳、
			// 阵风只在上方增强）：turb = gustAmplitude×(0.45+0.5×noise)，noise∈[−1.1,1.1]
			// → turb ∈ [−0.035, +0.35]（下波动仅 −3.5%、上方阵风 +35%，参考值为中心）。
			double turb = gustAmplitude * (0.45 + 0.5 * (num13 * 0.8 + num15 * 0.3));
			u += vmaxDisplay * turb * num6 * num16;
			w += wmaxDisplay * gustAmplitude * (num14 * 0.9 + num15 * 0.25) * (0.3 + 0.7 * Math.Pow(Math.Sin(Math.PI * ht), 0.7)) * edge;
		}

		// 垂直分量（上升为主；下暴系数为负）——通用 w 单独算：摩擦只削通用上升气流，
		// 附属 w（龙卷/下暴）保留累加不被覆盖（原赋值 bug 曾清零）；按类型分流——
		// 台风=眼壁模型（中心下沉+眼壁上升+外围下沉）；对流型=中心上升核心单调外衰减
		// +外围弱下沉出流（无"眼"，修复全系统共用台风眼壁的错位）。
		double vertShape;
		if (type == StormType.Typhoon)
		{
			vertShape = Math.Exp(0.0 - Pow2((ro - 1.0) / 0.42)) - 0.55 * Math.Exp(0.0 - Pow2(ro / 0.55)) - 0.22 * Math.Exp(0.0 - Pow2((ro - 2.8) / 1.2));
			// 垂直分量跟随水平非对称（用户：风的分布"强-弱-强"双峰不对——
			// 元凶是眼壁模型用对称 ro，−s 侧眼壁被垂直上升拉回强风，破坏"一边缘最大
			// 一边缘最小"）：×（0.35+0.65×num6）——+s 侧眼壁垂直强、−s 侧眼壁垂直弱，
			// 总风回归单峰非对称（穿台风：+s 眼壁峰 → 眼内弱 → −s 侧整体弱）。
			vertShape *= 0.35 + 0.65 * num6;
		}
		else
		{
			vertShape = Math.Exp(0.0 - Pow2(ro / 0.9)) - 0.2 * Math.Exp(0.0 - Pow2((ro - 2.2) / 1.0));
		}
		double vShape = Math.Pow(Math.Sin(Math.PI * ht), 0.7);
		double genW = wmaxDisplay * vertShape * edge * vShape * intensity;
		if (h < 40.0)
		{
			genW *= Clamp01(h / 40.0);   // 地面摩擦（仅通用上升；下击暴流下沉/龙卷吸力保留）
		}
		w += genW;

		// 阵风锋/出流（飑线/MCS/多单体：成熟期地面阵风）
		if (gust > 0.05 && stage >= 1)
		{
			double gf = Math.Exp(0.0 - Pow2((ro - 1.2) / 0.4)) * Clamp01(1.0 - ht / 0.4);
			u += vmaxDisplay * gust * 0.5 * gf * (0.0 - num4) * intensity;
		}

		// 地面摩擦已并入 genW（仅通用上升）；附属下沉/吸力不受摩擦。
		// 云顶之上衰减
		if (h > Htop)
		{
			double top = 1.0 - Clamp01((h - Htop) / (Htop * 0.4));
			u *= top;
			w *= top;
		}
	}

	private static double SmoothStep(double x, double a, double b)
	{
		double t = Clamp01((x - a) / (b - a));
		return t * t * (3.0 - 2.0 * t);
	}

	public void Probe(Double2 globalPos, out double s, out double h, out double u, out double w)
	{
		ToStormFrame(globalPos, out s, out h);
		SampleComponents(s, h, out u, out w);
	}

	// ===== 工具 =====
	public static double Pow2(double a)
	{
		return a * a;
	}

	public static double Clamp01(double a)
	{
		if (a < 0.0)
		{
			return 0.0;
		}
		if (a > 1.0)
		{
			return 1.0;
		}
		return a;
	}

	public static double Clamp(double a, double lo, double hi)
	{
		if (a < lo)
		{
			return lo;
		}
		if (a > hi)
		{
			return hi;
		}
		return a;
	}

	public static double WrapPi(double a)
	{
		a %= Math.PI * 2.0;
		if (a > Math.PI)
		{
			a -= Math.PI * 2.0;
		}
		if (a < -Math.PI)
		{
			a += Math.PI * 2.0;
		}
		return a;
	}

	public static double WrapTwoPi(double a)
	{
		a %= Math.PI * 2.0;
		if (a < 0.0)
		{
			a += Math.PI * 2.0;
		}
		return a;
	}
}
