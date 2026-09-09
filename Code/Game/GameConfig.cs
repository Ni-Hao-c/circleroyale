using System;

/// <summary>
/// 全局可调参数表（数值策划入口）。集中一处便于调整，后续可迁移到项目设置。
/// </summary>
public static class GameConfig
{
	// ---- 版本 ----

	/// <summary> 显示版本（HUD 右上角 + 日志；里程碑切换升次版本号，之后每次改代码第 4 段 +1，用于核对两端构建一致性） </summary>
	public const string Version = "v0.7.8.86-M5";

	/// <summary> 结算面板自动返回大厅的时长（秒）；空格可提前。多人返回 host 的房间（会话保留），单机回主菜单 </summary>
	public const float SettlementAutoReturnSeconds = 10f;

	// ---- 观战镜头 ----

	/// <summary> 观战镜头换目标阈值（菜单演示/死亡观战共用）：当前对象还活着时，
	/// 挑战者总质量须超过它 ×1.3 才切换——几个 bot 交替领先时镜头不再来回跳 </summary>
	public const float SpectateOvertakeRatio = 1.3f;

	/// <summary> 客户端加入倒计时（秒）：收到 host 开局指令后，房间页显示 3-2-1 再进入对局
	/// （给世界快照/食物/玩家列表留同步窗口，v0.6.4.0 用户定稿） </summary>
	public const float JoinCountdownSeconds = 3f;

	// ---- 地图 ----

	/// <summary> 场地半宽（M4 起 world 创建时可被 cr_arena_size 覆写，改成了静态属性；
	/// 客户端经 Replicated convar 自动同步，GridBackdrop 检测变化重建几何）。
	/// 用户定稿：3 倍场地（4096×4096 → 12288×12288，面积 9 倍） </summary>
	public static float ArenaHalfSize { get; set; } = DefaultArenaHalfSize;

	/// <summary> 默认场地半宽（convar 校验基准） </summary>
	public const float DefaultArenaHalfSize = 6144f;

	/// <summary> 背景网格间距 </summary>
	public const float GridStep = 128f;

	// ---- 玩家 ----

	/// <summary> 真人上限（与 sbproj.Metadata.MaxPlayers 保持一致） </summary>
	public const int MaxPlayers = 24;

	/// <summary> 初始质量（面积∝质量） </summary>
	public const float StartMass = 10f;

	/// <summary> 初始半径 </summary>
	public const float StartRadius = 32f;

	/// <summary> 初始质量下的移动速度 </summary>
	public const float BaseSpeed = 560f;

	/// <summary> 质量增大后的速度下限 </summary>
	public const float MinSpeed = 145f;

	// ---- 体积惯性（批18①：越大越慢 + 转向越笨拙，大球要提前预判走位）----

	/// <summary> 转向响应速度（速度趋近目标的时间常数）基准：起始质量时 8/s，很跟手 </summary>
	public const float BaseTurnSpeed = 8f;

	/// <summary> 转向响应随质量衰减的指数：6400 质量时约 2.2/s（明显笨重） </summary>
	public const float TurnCurve = 0.20f;

	/// <summary> 转向响应下限 </summary>
	public const float MinTurnSpeed = 2f;

	/// <summary> 速度曲线指数：v = BaseSpeed * (StartMass/mass)^SpeedCurve </summary>
	public const float SpeedCurve = 0.35f;

	/// <summary> 出生保护时长（秒） </summary>
	public const float SpawnProtectSeconds = 3f;

	/// <summary> 安全出生采样次数（在这么多随机点里选离威胁最远的，agar 式） </summary>
	public const int SpawnSafeSamples = 16;

	/// <summary> 出生保护闪烁频率（Hz，整球一闪一闪） </summary>
	public const float SpawnBlinkHz = 3f;

	// ---- 相机 ----

	/// <summary> 相机离地高度（正交投影不影响大小，仅影响裁剪面） </summary>
	public const float CameraHeight = 1000f;

	/// <summary> 初始正交视口高度（世界单位） </summary>
	public const float CameraBaseView = 1300f;

	/// <summary> 视口高度随质量缩放的范围 </summary>
	public const float CameraViewMin = 900f;
	public const float CameraViewMax = 3800f;

	// ---- 吃与成长 ----

	/// <summary> 吃球所需质量比：吃者 ≥ 被吃者 × EatRatio 才能吞并（1.10 = 用户定稿，接近大小也能吃） </summary>
	public const float EatRatio = 1.10f;

	/// <summary> 半径上限（3 倍场地下的定稿值，约场地半宽 6144 的 20%；1200 直径 2400，
	/// CameraViewMax=3800 视口仍装得下）：质量继续涨也不再变大，防止覆盖全场 </summary>
	public const float MaxRadius = 1200f;

	/// <summary> 质量上限（由 MaxRadius 反推：mass = StartMass × (MaxRadius/StartRadius)² ） </summary>
	public static readonly float MaxMass = StartMass * MathF.Pow( MaxRadius / StartRadius, 2f );

	// 吞噬距离 = 触碰即吃（用户定稿）：两心距 < 两球半径之和，边缘一接触就触发

	// ---- 食物 ----

	/// <summary> 食物数量**按场地面积自动缩放**（基准：半宽 2048 时 460 颗）。
	/// 3 倍场地若不缩放，密度只剩 1/9——实测视口内平均不足 10 颗，观感"没有食物" </summary>
	public static int FoodCount => (int) ( BaseFoodCount * ( ArenaHalfSize * ArenaHalfSize ) / ( 2048f * 2048f ) );

	/// <summary> 基准食物数量（半宽 2048 时的值） </summary>
	public const float BaseFoodCount = 460f;
	public const float FoodRadius = 8f;
	public const float FoodMass = 1f;

	/// <summary> 食物被吃后的重生延时（秒） </summary>
	public const float FoodRespawnSeconds = 2f;

	/// <summary> 食物圆盘渲染段数（数量大，取低模） </summary>
	public const int FoodSegments = 6;

	/// <summary> 食物与场边的最小间距 </summary>
	public const float FoodMargin = 64f;

	// ---- bot ----

	/// <summary> bot 决策间隔（秒） </summary>
	public const float BotDecisionInterval = 0.25f;

	/// <summary> 逃跑感知距离（叠加双方半径） </summary>
	public const float BotFleeRange = 700f;

	/// <summary> 追猎感知距离 </summary>
	public const float BotHuntRange = 900f;

	/// <summary> 觅食搜索距离（超过则游荡） </summary>
	public const float BotSeekFoodRange = 1600f;

	/// <summary> bot 阵亡后的重生延时（秒） </summary>
	public const float BotRespawnSeconds = 2.0f;

	/// <summary> bot 专用假 SteamId 基址（每 bot 递增一个，互相独立）：
	/// 分身的头像/名牌按 OwnerSteamId 归属解析，bot 共用 0 会分不清是谁的碎片 </summary>
	public const long BotSteamIdBase = 91000000000000000;

	/// <summary> 名牌投影池大小（球 + 分身共用）：满配 48 球 + 高峰期分身（上限 16×球）极端可到几百，
	/// 池耗尽时名牌静默缺失——取 320 兼顾覆盖与 Label 常驻开销 </summary>
	public const int NameTagPoolSize = 320;

	/// <summary> bot 名字池（顺序取用；64 个 ≥ 人数上限 48，配合"跳过占用"保证同局不撞名） </summary>
	public static readonly string[] BotNames =
	{
		"Nebula", "Vector", "Pulse", "Quasar", "Ion", "Cinder", "Flux", "Zephyr",
		"Onyx", "Volt", "Ember", "Cipher", "Nova", "Rogue", "Static", "Echo",
		"Havoc", "Lynx", "Vortex", "Drift", "Sable", "Rune", "Comet", "Shard",
		"Wisp", "Forge", "Blitz", "Tremor", "Umbra", "Zenith", "Kilo", "Mach",
		"Prism", "Halcyon", "Kraken", "Mantis", "Obelisk", "Pyre", "Quartz", "Raptor",
		"Scythe", "Talon", "Vega", "Wraith", "Xenon", "Yonder", "Zodiac", "Nimbus",
		"Oracle", "Phantom", "Gizmo", "Jinx", "Lumen", "Cobalt", "Dynamo", "Cyclone",
		"Meteor", "Pulsar", "Relay", "Sentry", "Tesla", "Fable", "Grim", "Hornet",
	};

	// ---- 比赛规则（M5：host 在主菜单设置，开局经 MatchState 广播下发）----

	/// <summary> 默认一局时长（秒，12 分钟） </summary>
	public const float DefaultMatchSeconds = 720f;

	/// <summary> 菜单可选的一局时长（秒） </summary>
	public static readonly float[] MatchDurationChoices = { 300f, 480f, 720f, 900f, 1200f };

	/// <summary> 菜单可选的场上目标人数（真人 + bot） </summary>
	public static readonly int[] PlayerTargetChoices = { 12, 20, 32, 48 };

	/// <summary> 默认场上目标人数 </summary>
	public const int DefaultPlayerTarget = 32;

	/// <summary> 房间蛰伏球总数（= 人数选项最大值）：开房时一次性生成 Alive=false 的球
	/// （引擎"进行中 NetworkSpawn 不复制到已连接客户端"，球必须在各端快照前存在），
	/// 开局按 PLAYERS 激活，多余的保持蛰伏不可见 </summary>
	public const int LobbyReserveBalls = 48;

	// ---- 房间设置 ConVar（M4，控制台设置；除特别说明外重进 Play 生效）----

	/// <summary> 场地半宽，host 设置经 Replicated 自动下发客户端 </summary>
	[ConVar( "cr_arena_size", ConVarFlags.Replicated, Help = "Arena half size (default 6144). Re-enter Play to apply." )]
	public static float ConvarArenaSize { get; set; } = DefaultArenaHalfSize;

	// ---- 撞墙脉冲（v0.7.8.78）----

	/// <summary> 触发撞墙脉冲的最低撞击速度（低于它算贴墙滑行，不爆圈） </summary>
	public const float WallHitFxMinSpeed = 60f;

	/// <summary> 同一球两次撞墙脉冲的最小间隔（秒，沿墙推挤时不连爆） </summary>
	public const float WallHitFxCooldown = 0.3f;

	/// <summary> 泛光开关：霓虹风时代为开；v0.7.8.36 像素风改默认关（加色泛光会糊掉糖果色与像素颗粒），
	/// cr_bloom 1 可重开，本机即时生效 </summary>
	[ConVar( "cr_bloom", Help = "Bloom glow 1=on 0=off (default, pixel art). Applies instantly." )]
	public static bool ConvarBloom { get; set; } = false;

	/// <summary> 主菜单"加入"的默认地址（local = 本机房主；要连远程改这里） </summary>
	public const string DefaultJoinAddress = "local";

	// ---- bot 难度分档（M4）：按比例随机分配，只影响行为不联网 ----

	/// <summary> 王牌占比（其余按老手/新兵分） </summary>
	public const float BotAceFraction = 0.15f;

	/// <summary> 老手占比（新兵 = 1 - 王牌 - 老手） </summary>
	public const float BotVeteranFraction = 0.35f;

	/// <summary> 决策间隔（秒）：新兵 / 王牌（老手用 BotDecisionInterval） </summary>
	public const float BotRookieDecisionInterval = 0.40f;
	public const float BotAceDecisionInterval = 0.16f;

	/// <summary> 感知范围缩放（逃跑/追猎/觅食距离）：新兵 0.6 / 王牌 1.35 </summary>
	public const float BotRookieRangeScale = 0.6f;
	public const float BotAceRangeScale = 1.35f;

	/// <summary> 瞄准噪声（方向随机偏转 ±弧度）：新兵打不直，王牌几乎指哪打哪 </summary>
	public const float BotRookieAimError = 0.50f;
	public const float BotVeteranAimError = 0.18f;
	public const float BotAceAimError = 0.05f;

	// ---- bot 进阶能力（v0.6.6.0：分裂捕猎 / 吐孢子喂队友 / 团队集结）----

	/// <summary> bot 分裂捕猎的最小自身质量（太小分裂=白送质量） </summary>
	public const float BotSplitMinMass = 80f;

	/// <summary> bot 分裂捕猎的打击距离（超出就不值得炸）：自身半径 + 此值×难度系数 </summary>
	public const float BotSplitRange = 620f;

	/// <summary> bot 分裂捕猎的冷却（秒，防止把自己拆成碎片送人） </summary>
	public const float BotSplitCooldown = 5f;

	/// <summary> 分裂风险评估圈（v0.7.8.32）：减半后这个距离内仍有够吃"减半我"的敌人（球或分身）就放弃分裂 </summary>
	public const float BotSplitRiskRange = 420f;

	/// <summary> 分裂捕猎猎物价值门槛（v0.7.8.32）：猎物低于自身质量×此比例就不浪费分裂冷却（追着吃就够） </summary>
	public const float BotSplitPreyFraction = 0.18f;

	/// <summary> bot 喂队友的最小自身质量（先自保再喂大哥） </summary>
	public const float BotFeedMinMass = 36f;

	/// <summary> bot 喂队友的感知距离（×难度系数）：目标要比我大 1.25 倍才值得喂 </summary>
	public const float BotFeedRange = 1500f;

	/// <summary> bot 喂孢子的决策冷却（秒；再叠加决策间隔与难度概率节流） </summary>
	public const float BotFeedCooldown = 0.6f;

	// ---- bot 领土目标层（M7.5）----

	/// <summary> bot 朝旗吐球的最小质量（低于就放下目标先去觅食，别饿瘦白给） </summary>
	public const float BotFeedFlagMinMass = 20f;

	/// <summary> bot 喂旗节流（秒）——套在 DoEject 内置 0.15s 之上，一只 bot 约 3.3 分/秒（按 1g=2 分） </summary>
	public const float BotFeedFlagCooldown = 0.6f;

	/// <summary> bot 重估领土目标的间隔（秒）——选旗自带随机抖动分散扎堆 </summary>
	public const float BotFlagPickInterval = 2f;

	// ---- 网络 ----

	/// <summary> 球预制体路径（用户可在编辑器创建；缺失时代码构建，不阻塞开发） </summary>
	public const string BallPrefabPath = "entities/ball/ball.prefab";

	/// <summary> 头像占位缩略图（bot 与头像加载失败时使用） </summary>
	public const string AvatarPlaceholderPath = "ui/avatar_placeholder.png";

	/// <summary> 合法瞬移后的位移校验宽限期（秒）：远程球重生换位/分身晋升期间，
	/// owner 的位置更新是干级别跳变，host 反瞬移橡皮筋在窗口内放行并持续锚定新位置。
	/// 需覆盖 [Sync] Alive 下发 → owner 换位 → 位置更新回传的往返（含插值延迟） </summary>
	public const float TeleportGraceSeconds = 2f;

	// ---- 功能开关（预留接口，后续里程碑开启） ----

	/// <summary> 分裂（M3 已开启：空格 = Jump；分身走纯数据实体 + 静态 RPC 同步） </summary>
	public const bool EnableSplit = true;

	/// <summary> 吐孢子（M3 已开启：R = Reload；W 是移动键不能占用，PLAN 原定 W 已改） </summary>
	public const bool EnableEject = true;

	// ---- 分裂 / 吐孢子 / 合并（M3 数值）----

	/// <summary> 分裂质量门槛（用户定稿：**最低 1**，开局就能玩）。
	/// 取 2 而不是 1：分裂是对半分，门槛 2 保证分完两半各 ≥1（质量下限 1 不击穿） </summary>
	public const float SplitMinMass = 2f;

	/// <summary> 每个玩家的分身上限（不含主球，用户定稿 16） </summary>
	public const int MaxSplitPieces = 16;

	/// <summary> 合并冷却基准（秒，StartMass 小分身时的值）——v0.7.8.27 用户定稿改**随质量缩放曲线**：
	/// 分身越大合并越久（球球大作战式，分裂从零风险变赌博）。主动分裂/尖刺分身转正走曲线，
	/// 撞刺碎片仍用 SpikePieceMergeCooldown 短冷却（主人快速吃回的保护设计） </summary>
	public const float MergeCooldownBase = 1f;

	/// <summary> 合并冷却曲线指数：cd = Base × (mass/StartMass)^0.5（100 质量≈3.2s、1000≈10s） </summary>
	public const float MergeCooldownCurve = 0.5f;

	/// <summary> 合并冷却封顶（秒，大球分身） </summary>
	public const float MergeCooldownMax = 14f;

	/// <summary> 分身合并冷却按其质量取值 </summary>
	public static float MergeCooldownFor( float mass ) =>
		MathF.Min( MergeCooldownMax, MergeCooldownBase * MathF.Pow( MathF.Max( 1f, mass ) / StartMass, MergeCooldownCurve ) );

	/// <summary> 分裂冲量初速（世界单位/秒），随时间指数衰减 </summary>
	public const float SplitImpulseSpeed = 1000f;

	/// <summary> 分裂冲量衰减速率（每秒指数底），衰减完剩纯跟随移动 </summary>
	public const float SplitImpulseDecay = 3.0f;

	/// <summary> 吐孢子质量门槛（用户定稿：**最低 1**）——实际可用质量 = 门槛 + 消耗，吐完落在下限 1 </summary>
	public const float EjectMinMass = 1f;

	/// <summary> 每颗孢子减重的质量百分比（批18②：约 1%，随体量缩放——喂球有层次） </summary>
	public const float EjectMassPercent = 0.01f;

	/// <summary> 孢子返还比例（吐 100 收 80，输送有损耗，防无限乒乓） </summary>
	public const float EjectBlobEfficiency = 0.8f;

	/// <summary> 单颗孢子最小消耗（极小球也至少吐 1 质量） </summary>
	public const float EjectMinCost = 1f;

	/// <summary> 孢子冲量初速与衰减（吐出去滑行一段后停下） </summary>
	public const float EjectImpulseSpeed = 780f;
	public const float EjectImpulseDecay = 2.2f;

	/// <summary> 吐孢子节流（按住 R 连吐的间隔，秒；host 侧同样校验） </summary>
	public const float EjectCooldownSeconds = 0.15f;

	/// <summary> 孢子吐出后经过该时长才可被敌人吃（自己+队友走 0.2s 快档；秒） </summary>
	public const float EjectedEdibleDelay = 0.5f;

	/// <summary> 自己+队友吃孢子的延迟（秒，v0.7.8.22/23 用户定稿"马上能吃"：球球大作战式
	/// 短时互喂——自食回收+喂队友大哥都走这一档，敌人另走 EjectedEdibleDelay）。
	/// 不是 0：大球吐的孢子半径可超过 16px 出生偏移，零延迟会被吐出者出生同帧吸回 </summary>
	public const float EjectedAllyEdibleDelay = 0.2f;

	/// <summary> 场上孢子总量上限（超出挤掉最老的） </summary>
	public const int MaxEjectedBlobs = 96;

	/// <summary> 分身/孢子状态快照广播间隔（秒，≈15Hz；客户端做插值平滑） </summary>
	public const float CellSyncInterval = 0.066f;

	// ---- Q 技能（v0.7.8.30 用户定稿：Q 随时间自动充能，E 仍为拾取入包）----

	/// <summary> Q 技能最短充能时间（秒，起步质量） </summary>
	public const float QSkillCooldownMin = 15f;

	/// <summary> Q 技能最长充能时间（秒，质量到 QSkillMassRef 后封顶） </summary>
	public const float QSkillCooldownMax = 60f;

	/// <summary> Q 技能充能达到最长值的质量（线性内插） </summary>
	public const float QSkillMassRef = 1000f;

	/// <summary> Q 技能充能时长按当前质量取值（越轻越快；10→15s、500→≈38s、1000+→60s） </summary>
	public static float QSkillCooldownFor( float mass )
	{
		var t = Math.Clamp( ( mass - StartMass ) / ( QSkillMassRef - StartMass ), 0f, 1f );
		return MathX.Lerp( QSkillCooldownMin, QSkillCooldownMax, t );
	}

	// ---- 尖刺（M3，M4 定稿紫色渲染）----

	/// <summary> 场上尖刺数量（静态障碍，host 生成后全量同步） </summary>
	public const int SpikeCount = 12;

	/// <summary> 绿刺参照质量（决定半径与"多大才算撞得炸"的标尺） </summary>
	public const float SpikeMass = 100f;

	/// <summary> 绿刺半径（由质量定律反推，≈101） </summary>
	public static readonly float SpikeRadius = StartRadius * MathF.Sqrt( SpikeMass / StartMass );

	/// <summary> 刺的碰撞距离 = 细胞半径 + SpikeRadius × 此系数（刺芯判定，不要求完全压上） </summary>
	public const float SpikeTouchFactor = 0.35f;

	/// <summary> 撞刺炸裂碎片的合并冷却（秒）——与主动分裂一致（用户定稿 1 秒） </summary>
	public const float SpikePieceMergeCooldown = 1f;

	// ---- 喂刺/刺爆（v0.7.8.24 球球大作战式：吐丝喂刺长大，喂满爆开射刺）----

	/// <summary> 喂满一朵刺所需的孢子质量总和（喂满即爆，喂食量归零重新攒） </summary>
	public const float SpikeGrowTarget = 150f;

	/// <summary> 刺的视觉长大倍率上限（喂满前从 1.0 涨到 1+该值；仅显示，判定半径不变） </summary>
	public const float SpikeGrowVisual = 0.6f;

	/// <summary> 刺爆一次射出的尖刺数量（均匀圆周，确定性角度） </summary>
	public const int SpikeBurstCount = 10;

	/// <summary> 刺爆尖刺初速（世界单位/秒） </summary>
	public const float SpineSpeed = 850f;

	/// <summary> 刺爆尖刺存活时长（秒，到期消散） </summary>
	public const float SpineLife = 1.2f;

	/// <summary> 刺爆尖刺的命中半径 </summary>
	public const float SpineRadius = 12f;

	/// <summary> 被尖刺命中损失的质量比例（中立伤害，谁都扣；下限 StartMass） </summary>
	public const float SpineHitMassFraction = 0.15f;

	// ---- 球体推挤 / 质量衰减（v0.7.8.24 球球大作战手感）----

	/// <summary> 每个固定步内推挤修正的位移上限（世界单位）——防瞬移/复活重叠时一帧弹飞 </summary>
	public const float PushMaxStep = 64f;

	/// <summary> 质量超过该值才开始衰减（小球免衰减） </summary>
	public const float MassDecayMinMass = 300f;

	/// <summary> 每秒质量衰减比例（0.004 = 0.4%/秒；球与分身同率，压制滚雪球） </summary>
	public const float MassDecayPerSecond = 0.004f;

	/// <summary> 分身"自动滚回主球"的引力生效延时（秒）——冷却 1s 内可手动贴上合并，
	/// 超过该时长（或分身比主球重）自动进入倒流引力，对应设计稿"约15秒自动合球"的加速版 </summary>
	public const float PieceHomeAfterSeconds = 8f;

	/// <summary> 倒流引力的权重（相对摇杆输入方向；1 = 引力与输入等权，倒流明显） </summary>
	public const float PieceAttractWeight = 1f;

	// ---- 领土战争模式（M7，用户定稿 V2.1：驻军模型/无自动衰减/无补给线/大本营免大体型衰减）----

	/// <summary> 领土网格边长（格数）——4×4 共 16 格，格子边长 = 场地边长 / 4（默认 6144 半宽 → 3072 一格） </summary>
	public const int TerritoryGrid = 4;

	/// <summary> 占领/驻军满分：中立格喂满变色占领（驻军=100）；敌方吐球先啃驻军，啃光回中立 </summary>
	public const float TerritoryCaptureScore = 100f;

	/// <summary> 每克孢子计入的旗帜分（1g = 2 分） </summary>
	public const float TerritoryScorePerMass = 2f;

	/// <summary> 单颗孢子计入上限（分）——压制巨球一轮吐满旗 </summary>
	public const float TerritoryScoreCapPerBlob = 12f;

	/// <summary> 旗帜判定半径（吐球落进旗圈才计分；约 1/6 格宽） </summary>
	public const float TerritoryFlagRadius = 480f;

	/// <summary> 征服胜利：占领全部可占领格（12 = 16 - 4 个不可沦陷大本营）立即获胜 </summary>
	public const int TerritoryWinCells = TerritoryGrid * TerritoryGrid - 4;

	/// <summary> 占旗积分（用户定稿）：每占领一旗每 N 秒 +1 分；没人占满全部格时，计时结束点数最多者胜 </summary>
	public const float TerritoryPointSeconds = 3f;

	/// <summary> 己方球在己方大本营内免大体型衰减（回家保养的拉扯点，用户定稿 ②） </summary>
	public const bool TerritoryHqStopsDecay = true;

	/// <summary> 敌方球在敌营大本营内每秒损失的质量比例（5%/秒；下限出生质量不致死，驱赶而不处刑） </summary>
	public const float TerritoryHqHurtPerSecond = 0.05f;

	/// <summary> 敌方球在敌营大本营内的移速乘数（0.6 = 减速 40%） </summary>
	public const float TerritoryHqSlowFactor = 0.6f;

	// ---- 职业技能（M7.4，领土模式 Q 键；职业由系统随机分配，每次复活重掷）----

	/// <summary> 指挥官·战术转移冷却（秒）：传送到最大队友身边 </summary>
	public const float ClassCdCommander = 45f;

	/// <summary> 工兵·疾行冷却（秒）：6 秒大加速（复用 Speed buff） </summary>
	public const float ClassCdEngineer = 25f;

	/// <summary> 护卫·固守尖刺冷却（秒）：原地锚定尖刺分身 8 秒 </summary>
	public const float ClassCdGuard = 30f;

	/// <summary> 坦克·质量爆发冷却（秒）：+40% 当前质量 10 秒后回收，期间禁分裂 </summary>
	public const float ClassCdTank = 50f;

	/// <summary> 工兵疾行持续（秒） </summary>
	public const float ClassEngineerSeconds = 6f;

	/// <summary> 坦克爆发持续（秒），到期回收临时质量 </summary>
	public const float ClassTankSeconds = 10f;

	/// <summary> 坦克临时质量 = 当前质量 × 此比例（下限 ClassTankMassMin） </summary>
	public const float ClassTankMassFraction = 0.4f;

	/// <summary> 坦克临时质量下限 </summary>
	public const float ClassTankMassMin = 15f;

	/// <summary> 护卫固守尖刺存在时长（秒），到期变回普通分身跟随主人 </summary>
	public const float ClassGuardLife = 8f;

	/// <summary> bot 避刺感知距离 = 自身半径 + SpikeRadius × 此系数 </summary>
	public const float BotSpikeAwareness = 2.2f;

	// ---- 表现力（M3）----

	/// <summary> CPU 粒子上限（NeonRenderer 内置粒子系统，超出丢弃最老） </summary>
	public const int FxMaxParticles = 600;

	// ---- 客户端自动重连（M3，兜引擎握手偶发非主线程断言）----

	/// <summary> 连入后经过该时长仍没有自己的球 → 判定卡死，自动重连（秒） </summary>
	public const float ReconnectAfterSeconds = 12f;

	/// <summary> 掉线自动重连的重试间隔（秒，v0.6.4.6）：断联后按此节奏重试，耗尽次数回主菜单 </summary>
	public const float ReconnectRetrySeconds = 2f;

	/// <summary> 掉线自动重连的总时长上限（秒，v0.6.8.0）：单次 Connect 可能长时间挂在
	/// "正在握手"上，超上限一律放弃重连回主菜单（防个别尝试卡死导致永远不返回） </summary>
	public const float ReconnectGiveUpSeconds = 20f;

	/// <summary> host 心跳超时（秒，v0.6.8.0）：对局中 host 1Hz 广播校时、房间中 3s 广播名单，
	/// 静默超过此时长 = host 大概率已死，客户端强制拆掉僵尸会话走重连
	/// （host 进程被杀时引擎会话可能不自动失效——IsActive 一直 true，恢复循环永远不触发） </summary>
	public const float HostLivenessTimeout = 6f;

	/// <summary> 自动重连最大尝试次数（防止对已关闭的 host 无限重试） </summary>
	public const int ReconnectMaxAttempts = 5;

	/// <summary> 道具系统（v0.7.5.0 起：拾取存入背包 **主动使用**——真人按 Q/E 释放，bot 拾取即用） </summary>
	public const bool EnablePowerUps = true;

	// ---- 道具（v0.7.3.0 数值，v0.7.5.0 改主动使用）----

	/// <summary> 场上道具槽数（固定槽位：被捡后同槽延时重刷，全量同步便宜） </summary>
	public const int PowerUpOnField = 9;

	/// <summary> 道具视觉/拾取判定半径 </summary>
	public const float PowerUpRadius = 26f;

	/// <summary> 道具被捡后的重刷延时（秒） </summary>
	public const float PowerUpRefillSeconds = 6f;

	/// <summary> 道具与场边的最小间距 </summary>
	public const float PowerUpMargin = 200f;

	/// <summary> 加速：时长（秒）与移速倍率 </summary>
	public const float PowerUpSpeedSeconds = 8f;
	public const float PowerUpSpeedBoost = 1.4f;

	/// <summary> 磁铁：时长（秒）；生效期主球吸食半径翻倍（只影响食物，不影响球/分身） </summary>
	public const float PowerUpMagnetSeconds = 8f;

	/// <summary> 护盾：时长（秒）；生效期主球不会被更大的球/分身吃掉（不防尖刺） </summary>
	public const float PowerUpShieldSeconds = 5f;

	/// <summary> 质量罐：拾取瞬间加的质量（一次性，无时长） </summary>
	public const float PowerUpMassBonus = 300f;

	// ---- 道具平衡（v0.7.4.0，用户定稿）----

	/// <summary> 拾取判定用的球半径封顶：大球不再"路过顺走"道具，必须刻意碾上去才吃得到 </summary>
	public const float PowerUpPickRadiusCap = 200f;

	/// <summary> 喂食触发门槛（占喂食者当前体重的比例）：道具共享积分池 ≥ 本次喂食者体重×此值 → 触发给喂食者。
	/// 吐一颗 = 体重 1%×0.8 返还 = 0.8% 积分，恒定约 6.25 次吐丝达标——小球门槛低可虎口夺食，大球买得贵 </summary>
	public const float PowerUpFeedRatio = 0.05f;

	/// <summary> 尖刺分身道具：跟随主人的时长（秒），到期变回普通分身（质量合体回收） </summary>
	public const float SpikeMinionLife = 20f;

	/// <summary> 尖刺分身的质量占比（生成时从主人现扣；到期变回普通分身合体回收） </summary>
	public const float SpikeMinionMassPercent = 0.1f;

	/// <summary> 尖刺分身撞破"比自己（主人）大的球"后，主人的无敌时长（秒，复用护盾 buff） </summary>
	public const float SpikeMinionRewardSeconds = 3f;

	// ---- 渲染 ----

	/// <summary> 球外环线段数 </summary>
	public const int RingSegments = 40;

	/// <summary> 实心圆细分 </summary>
	public const int DiscSegments = 20;

	/// <summary> 排行榜显示行数 </summary>
	public const int LeaderboardRows = 10;

	/// <summary> 霓虹配色（球/食物/道具公用，按索引取用） </summary>
	public static readonly Color[] Palette =
	{
		new Color( 0.357f, 0.784f, 0.961f ),   // 天蓝 #5BC8F5
		new Color( 0.949f, 0.447f, 0.655f ),   // 糖果粉 #F272A7
		new Color( 1.000f, 0.788f, 0.302f ),   // 奶油黄 #FFC94D
		new Color( 0.494f, 0.851f, 0.341f ),   // 草绿 #7ED957
		new Color( 0.710f, 0.541f, 0.969f ),   // 丁香紫 #B58AF7
		new Color( 1.000f, 0.957f, 0.878f ),   // 米白 #FFF4E0
	};
}