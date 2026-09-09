using System;
using System.Collections.Generic;
using Sandbox.UI;

/// <summary>
/// 游戏 HUD（纯 C# Panel，编辑器热重载友好）：质量分数、排行榜、死亡重生、操作提示。
/// 数据源 CircleroyaleGame.Current；样式在同目录 GameHud.cs.scss（按"类名.cs.scss"自动加载）。
/// 注意：面板显隐用 Style.Display（Panel 无 Enabled 属性），根面板 pointer-events 保持 none
/// （重生走键盘 Space，不需要点击）。
/// </summary>
public sealed class GameHud : PanelComponent
{
	Label _mass;
	Label _hint;
	Label _buff;                            // 本地球道具 buff 状态（⚡/🧲/🛡 剩余秒，v0.7.3.0）
	Label _tips;                            // 玩法提示轮播条（左下角，v0.7.5.0 英文）
	SkillButton _btnQ;                      // 技能按钮 ×2（右下角，球球大作战手游风 v0.7.8.74）
	SkillButton _btnE;
	byte _prevPowerA = 255;                 // 本地球 Q 槽上一帧种类（入局/放技能沿 → 充能重开）
	byte _qDrawn = 255;                     // 上次绘制的槽内容（图标/透明度只在切换时写）
	byte _eDrawn = 255;
	bool _hadBall;                          // 上一帧本地球是否存在
	TimeSince _qCd = 999f;                  // Q 充能进度本地估算（host 只同步种类，进度自己计）
	float _qCdDur = 1f;
	float _qCdHeight = -1f;                 // 冷却罩上次写入的高度（防每帧写样式）
	const float SkillBtnSize = 76f;         // 与 scss 的 .skillq/.skille 尺寸保持一致
	Label _version;
	Label[] _rows;
	Label _selfRow;
	Image _crown;                           // 排行榜第一名皇冠（v0.7.8.38 像素风）
	Label _deathTitle;
	Label _deathSub;
	Panel _deathShade;                      // 死亡全屏黑透明遮罩（v0.7.8.77），复活即隐
	Label _deathBase;                       // 领土模式复活二选一（M7.2）：大本营/前线按钮
	Label _deathFront;
	Label _thint;                           // 领土情境引导（M7.3）：底部居中突出条，按情境触发带冷却
	string _thintText;
	TimeSince _sinceThint = 999f;           // 当前提示已显示时长
	float _thintDuration;                   // 当前提示停留时长
	int _lastOwnedForHint = -1;             // 本队占领数快照（捕获瞬间/开局引导检测；-1 = 未入局）
	static readonly Dictionary<string, TimeSince> HintCooldowns = new();   // 引导冷却（key → 上次显示）
	Panel _tmap;                            // 领土小地图（M7.3）：左中 292² 四格阵，色随占领/驻军
	Panel[] _tmapCells = new Panel[16];
	Panel[] _tmapBars = new Panel[16];
	Panel _tmapSelf;                        // 本机球点（白）
	Panel[] _tmapMates = new Panel[3];      // 队友点（队色）
	float _tmapCellPx = 71f;                // 单格像素宽（build 时算好，进度条宽用）
	TimeSince _sinceTmapTick = 1f;          // 小地图 10Hz 节流
	float _logicalWCache = 1920f;           // 根面板逻辑宽缓存（1s 刷新；每帧读 Box.Rect 强制布局）
	TimeSince _sinceLogicalW = 999f;
	readonly Color[] _tmapLastBg = new Color[16];   // 上一帧格色（同值守卫，防每帧重复写样式=闪烁）
	readonly float[] _tmapLastBar = new float[16];  // 上一帧进度条宽
	readonly Color[] _tmapLastMateBg = new Color[3];
	float _tmapSelfX = -1f, _tmapSelfY = -1f;       // 本机点上次写入位置
	readonly float[] _tmapMateX = { -1f, -1f, -1f };
	readonly float[] _tmapMateY = { -1f, -1f, -1f };
	static readonly Color ColorInvalid = new Color( 0f, 0f, 0f, -1f );   // 强制首帧写入的哨兵色
	Label _matchInfo;                       // 顶部中央：模式 + 倒计时（M5）
	Label[] _teamRows;                      // 队伍总分条（团队赛，M5）
	Label[] _mateRows;                      // 本队成员积分（排行榜下方，团队赛，v0.6.5.0）：0=队头 1..4=成员
	Dictionary<Guid, TagSlot> _tagMap = new();
	Dictionary<int, TagSlot> _pieceTagMap = new();   // 分身名牌：Key = 分身 Id（归属名字经主人球解析）
	List<TagSlot> _tagPool;
	readonly List<KillFeedEntry> _killFeed = new();
	readonly List<Banner> _banners = new();          // 个人高光横幅（中下艺术字，v0.7.6.0）

	// —— 排行榜/队友/队伍条每帧路径的复用缓冲（50Hz 下避免每帧堆分配）——
	readonly List<Ball> _rankList = new();
	readonly Dictionary<Ball, float> _rankMass = new();
	readonly List<Ball> _matesList = new();
	readonly Dictionary<Ball, float> _matesMass = new();
	readonly Dictionary<int, float> _teamTotals = new();
	readonly List<int> _teamOrder = new();
	readonly List<int> _teamShow = new();
	bool _built;

	/// <summary> 个人高光横幅条目（击杀/捡道具/放技能）：2.1s 生命周期，0.3s 弹入 + 1.2s 停留 + 0.6s 淡出 </summary>
	sealed class Banner
	{
		public Label Label;
		public TimeSince Since;
		public float ShownBottom = float.MinValue;
	}

	/// <summary> 右下角技能按钮（v0.7.8.74 球球大作战手游风）：方块底 + 大图标 + 冷却暗罩 + 倒数数字 + 键位角标 </summary>
	sealed class SkillButton
	{
		public Panel Root;
		public Image Icon;
		public Panel Cd;        // 冷却暗罩：贴底，高度由代码按充能进度驱动
		public Label Num;       // 充能倒数秒（居中大字，Q 专用）
	}

	/// <summary> 道具使用/激活的艺术字文案（按键释放与喂养触发共用） </summary>
	public static string ActivationText( byte kind )
	{
		switch ( (PowerUpManager.Kind)kind )
		{
			case PowerUpManager.Kind.Speed: return "SPEED BOOST!";
			case PowerUpManager.Kind.Magnet: return "MAGNET ON!";
			case PowerUpManager.Kind.Shield: return "SHIELD UP!";
			case PowerUpManager.Kind.Spike: return "SPIKE MINION DEPLOYED!";
			case PowerUpManager.Kind.Mass: return "+300 MASS!";
			default: return "";
		}
	}

	/// <summary> 道具种类色（横幅配色入口） </summary>
	public static Color KindColor( byte kind ) => PowerUpManager.ColorOf( kind );

	/// <summary> 击杀高光的金色（combo 也在这一族上放大） </summary>
	static readonly Color KillGold = new( 1f, 0.81f, 0.36f );

	/// <summary>
	/// 击杀横幅（v0.7.7.0 连播版）：第 1 杀常规行，10 秒窗口内连杀升级为连杀大字
	/// （DOUBLE KILL! → TRIPLE KILL! → RAMPAGE! → GODLIKE!，字号更大金色更亮）。
	/// </summary>
	public void AddKillBanner( int combo, string eatenName, float massGained )
	{
		string text;
		string classes;

		switch ( combo )
		{
			case 2: text = $"DOUBLE KILL!  +{massGained:0}"; break;
			case 3: text = $"TRIPLE KILL!  +{massGained:0}"; break;
			case 4: text = $"RAMPAGE!  +{massGained:0}"; break;
			case >= 5: text = $"GODLIKE!  +{massGained:0}"; break;
			default: text = $"YOU ATE {eatenName}  +{massGained:0}"; break;
		}

		classes = combo >= 2 ? "banner combo" : "banner";

		var l = new Label() { Classes = classes };
		l.Text = text;
		l.Style.FontColor = KillGold;
		Panel?.AddChild( l );
		_banners.Add( new Banner { Label = l, Since = 0 } );
	}

	// ---- 玩法提示轮播（v0.7.5.0 英文文案）：每 8s 换一条（含 0.5s 渐入），随主动使用/喂养机制更新 ----

	static readonly string[] Tips =
	{
		"SPACE / RMB: split ALL cells in half, toward your cursor",
		"R / LMB: eject mass — feed big teammates or spikes",
		"Pickups store into E — press E to use; Q recharges over time (small = faster)",
		"Spit mass AT a pickup to feed it — trigger at 5% of your weight",
		"Small balls feed cheap, big balls feed dear — steal half-fed pickups",
		"Spike minion: ram it into a bigger ball — burst = 3s invincible",
		"Spike minion reverts to a normal cell after 20s (mass refunded)",
		"Shield blocks being eaten, not spikes — don't hug spikes with it",
		"Magnet: 8s of double food pickup radius",
		"Big balls must drive OVER pickups to grab them — smalls grab easy",
		"Your split cells pick up powerups into the same Q / E bag",
		"Off-screen teammates show an edge arrow — follow it to regroup",
		"Main ball eaten? Biggest cell takes over — lose all cells and you're out",
		"P: bail out of the match back to the lobby",
		"Bigger = clumsier turning — plan your moves ahead",
		"Team mode: teammates can't eat each other — stick together",
		"Feed a purple spike with R mass — it grows, then bursts spikes",
		"Can't eat them? You're solid — body-block and squeeze",
		"Big balls slowly shrink — keep eating or fade away",
	};

	int _tipIndex = -1;           // 首次轮换落到 0
	TimeSince _sinceTip = 999f;   // 首帧立即渐入第一条

	/// <summary> 文本防抖（v0.7.1.2）：值没变就不赋——Label.Text 重设会触发布局重算，
	/// 每帧重设（哪怕同值）会让 HUD 闪；排行榜/计时器/名牌/队友区全是逐帧路径 </summary>
	static void SetText( Label l, string text )
	{
		if ( l is null || !l.IsValid() || l.Text == text ) return;
		l.Text = text;
	}

	/// <summary> 名牌槽：Label 按球身份（Component.Id）稳定绑定，防 bot 死亡/重生时名字换位跳闪 </summary>
	sealed class TagSlot
	{
		public Label Label;
		public bool Used;
		public bool Shown;
	}

	/// <summary> 击杀播报条目（M5"谁吃了谁"）：4.5s 后移除，3.5s 起淡出 </summary>
	sealed class KillFeedEntry
	{
		public Label Label;
		public TimeSince Since;
	}

	protected override void OnStart()
	{
		base.OnStart();
		BuildUi();
	}

	void BuildUi()
	{
		var root = Panel;
		if ( root is null ) return;

		// 已建好且引用有效 → 不重复建。热重载可能半残（引用失效），整体清空重建
		if ( _built && _mass.IsValid() && _hint.IsValid() && root.ChildrenCount > 0 ) return;

		root.DeleteChildren( true );

		root.Style.Position = PositionMode.Absolute;
		root.Style.Left = 0f;
		root.Style.Top = 0f;
		root.Style.Right = 0f;
		root.Style.Bottom = 0f;
		root.Style.PointerEvents = PointerEvents.None;

		// 死亡遮罩（v0.7.8.77）：第一个子节点 = 垫在世界之上、其余 HUD 文字之下，只压暗战场
		_deathShade = new Panel() { Classes = "death-shade" };
		_deathShade.Style.Display = DisplayMode.None;
		root.AddChild( _deathShade );

		_mass = new Label() { Classes = "mass" };
		root.AddChild( _mass );

		// 版本号（右上角、排行榜上方）：双开时肉眼核对两端构建一致性
		_version = new Label() { Classes = "version" };
		_version.Text = GameConfig.Version;
		root.AddChild( _version );

		_hint = new Label() { Classes = "hint" };
		_hint.Text = GameConfig.EnableSplit
			? "WASD MOVE — SPACE/RMB SPLIT — R/LMB FEED — Q/E USE ITEM"
			: "WASD / GAMEPAD — MOVE";
		root.AddChild( _hint );

		// 道具 buff 状态（v0.7.3.0）：操作提示上方，文字+倒计时，种类配色
		_buff = new Label() { Classes = "buff" };
		root.AddChild( _buff );

		// 玩法提示轮播（v0.7.4.2）：buff 下方、操作提示上方，每 8s 换一条
		_tips = new Label() { Classes = "tips" };
		root.AddChild( _tips );

		// 技能按钮（v0.7.8.74 球球大作战手游风）：右下角两枚方块钮（图标+冷却罩+键位角标）。
		// 冷却罩/倒数秒/角标挂 HUD 根面板（挂按钮里定位会乱，v0.7.8.76），Right 按按钮右距内联写
		_btnQ = BuildSkillButton( root, "skillq", "[Q]", 30 );
		_btnE = BuildSkillButton( root, "skille", "[E]", 118 );

		// 冷却罩/倒数秒是 Q 专属（E 道具无冷却）——不藏的话 E 上常驻一块暗斑
		_btnE.Cd.Style.Display = DisplayMode.None;
		_btnE.Num.Style.Display = DisplayMode.None;

		// 排行榜：右上角固定行数，坐标在代码里按行号排（避开 flex 陷阱）
		_rows = new Label[GameConfig.LeaderboardRows];
		for ( int i = 0; i < _rows.Length; i++ )
		{
			var row = new Label() { Classes = "row" };
			row.Style.Position = PositionMode.Absolute;
			row.Style.Right = 28f;
			row.Style.Top = 24f + i * 24f;
			root.AddChild( row );
			_rows[i] = row;
		}

		// 自身排名在榜外时显示在排行榜正下方
		_selfRow = new Label() { Classes = "row self" };
		_selfRow.Style.Position = PositionMode.Absolute;
		_selfRow.Style.Right = 28f;
		_selfRow.Style.Top = 24f + GameConfig.LeaderboardRows * 24f;
		root.AddChild( _selfRow );

		// 本队成员积分（团队赛，v0.6.5.0）：排行榜/自身行下方——1 行队头 + 4 行成员（4 人队封顶）
		_mateRows = new Label[5];
		for ( int i = 0; i < _mateRows.Length; i++ )
		{
			var m = new Label() { Classes = "row" };
			m.Style.Position = PositionMode.Absolute;
			m.Style.Right = 28f;
			m.Style.Top = 300f + i * 24f;
			m.Style.Display = DisplayMode.None;
			root.AddChild( m );
			_mateRows[i] = m;
		}

		// 死亡重生面板
		_deathTitle = new Label() { Classes = "death-title" };
		_deathTitle.Text = "YOU WERE EATEN";
		root.AddChild( _deathTitle );

		_deathSub = new Label() { Classes = "death-sub" };
		_deathSub.Text = "PRESS SPACE / CLICK TO RESPAWN";
		// lambda 捕获 this（实例方法）——无捕获的静态 lambda 热重载后无法重映射（实测点击抛 NoMatchLambda）
		_deathSub.AddEventListener( "onclick", () => OnDeathSubClicked() );
		root.AddChild( _deathSub );

		// 领土模式复活二选一（M7.2）：非领土模式隐藏
		_deathBase = new Label() { Classes = "death-btn death-btn-base" };
		_deathBase.Text = "RESPAWN — BASE";
		_deathBase.AddEventListener( "onclick", () => OnDeathRespawnClicked( false ) );
		root.AddChild( _deathBase );

		_deathFront = new Label() { Classes = "death-btn death-btn-front" };
		_deathFront.Text = "RESPAWN — FRONT";
		_deathFront.AddEventListener( "onclick", () => OnDeathRespawnClicked( true ) );
		root.AddChild( _deathFront );

		// 领土情境引导（M7.3）：底部居中白底小牌，情境触发 + 冷却，非领土模式不显示
		_thint = new Label() { Classes = "thint" };
		_thint.Style.Display = DisplayMode.None;
		root.AddChild( _thint );

		// 领土小地图（M7.3）：左中 292² 格阵。格位置按"屏幕系"摆放（相机 90° 滚转，
		// 地图上方 = 世界 -X、右方 = 世界 -Y，和玩家屏幕移动方向一致，导航不拧巴）
		_tmap = new Panel() { Classes = "tmap" };
		_tmap.Style.Display = DisplayMode.None;
		root.AddChild( _tmap );

		const float MapInner = 286f;   // 292 - 上下左右 3px 边框
		var scale = MapInner / ( GameConfig.ArenaHalfSize * 2f );
		var cellPx = TerritoryManager.CellSize * scale;
		_tmapCellPx = cellPx - 2f;
		for ( int cy = 0; cy < GameConfig.TerritoryGrid; cy++ )
		{
			for ( int cx = 0; cx < GameConfig.TerritoryGrid; cx++ )
			{
				var i = cy * GameConfig.TerritoryGrid + cx;
				var cell = new Panel() { Classes = "tmap-cell" };
				// 世界格 → 屏幕系地图：left = (half - y1)·scale，top = (half - x1)·scale
				var x0 = -GameConfig.ArenaHalfSize + cx * TerritoryManager.CellSize;
				var y0 = -GameConfig.ArenaHalfSize + cy * TerritoryManager.CellSize;
				cell.Style.Left = ( GameConfig.ArenaHalfSize - ( y0 + TerritoryManager.CellSize ) ) * scale + 1f;
				cell.Style.Top = ( GameConfig.ArenaHalfSize - ( x0 + TerritoryManager.CellSize ) ) * scale + 1f;
				cell.Style.Width = cellPx - 2f;
				cell.Style.Height = cellPx - 2f;
				if ( IsHqCell( cx, cy ) )
				{
					cell.Style.BorderWidth = 3f;   // 大本营格粗描边标记
					cell.Style.BorderColor = new Color( 0.29f, 0.23f, 0.39f, 0.9f );
				}
				_tmap.AddChild( cell );
				_tmapCells[i] = cell;

				var bar = new Panel() { Classes = "tmap-bar" };
				bar.Style.Width = 0f;
				cell.AddChild( bar );
				_tmapBars[i] = bar;
				_tmapLastBg[i] = ColorInvalid;
				_tmapLastBar[i] = -1f;
			}
		}
		_tmapSelf = new Panel() { Classes = "tmap-self" };
		_tmap.AddChild( _tmapSelf );
		for ( int i = 0; i < _tmapMates.Length; i++ )
		{
			var d = new Panel() { Classes = "tmap-mate" };
			d.Style.Display = DisplayMode.None;
			_tmap.AddChild( d );
			_tmapMates[i] = d;
		}

		// 比赛规则 UI（M5）：顶部中央模式+倒计时；团队赛在下面加队伍总分条
		_matchInfo = new Label() { Classes = "matchinfo" };
		root.AddChild( _matchInfo );

		_teamRows = new Label[6];
		for ( int i = 0; i < _teamRows.Length; i++ )
		{
			var t = new Label() { Classes = "teamscore" };
			t.Style.Position = PositionMode.Absolute;
			t.Style.Display = DisplayMode.None;
			root.AddChild( t );
			_teamRows[i] = t;
		}

		// 名牌投影层：Label 池 + 按球身份稳定绑定，坐标每帧由世界位置投影（见 UpdateNameTags）
		_tagPool = new List<TagSlot>();
		_tagMap = new Dictionary<Guid, TagSlot>();
		_pieceTagMap = new Dictionary<int, TagSlot>();
		for ( int i = 0; i < GameConfig.NameTagPoolSize; i++ )
		{
			var tag = new Label() { Classes = "nametag" };
			tag.Style.Position = PositionMode.Absolute;
			tag.Style.Width = 160f;
			tag.Style.Display = DisplayMode.None;
			root.AddChild( tag );
			_tagPool.Add( new TagSlot { Label = tag } );
		}

		// 皇冠（v0.7.8.38 像素风）：钉在排行榜第一名行左侧，位置每帧跟随行内容宽度
		_crown = new Image();
		_crown.Texture = Texture.Load( "ui/pixel/crown.png" );
		_crown.Style.Position = PositionMode.Absolute;
		_crown.Style.Width = 18f;
		_crown.Style.Height = 18f;
		_crown.Style.Display = DisplayMode.None;
		root.AddChild( _crown );

		_built = true;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		BuildUi();

		var game = CircleroyaleGame.Current;
		if ( game is null || !_built ) return;

		bool over = MatchState.MatchOver;

		UpdateMass( game, over );
		UpdateLeaderboard( game, over );
		UpdateMates( game, over );
		UpdateMatchInfo( game );
		UpdateKillFeed();
		UpdateDeathPanel( game, over );
		UpdateBuff( game );
		UpdateTips();
		UpdateTerritoryHint( game );   // 领土情境引导（M7.3）
		UpdateTerritoryMap( game );    // 领土小地图（M7.3，10Hz）
		UpdatePowerSlots( game );
		UpdateBanners();
		GameAchievements.MatchTick( game );   // 成就每帧体检：质量里程碑/8身体/存活5分钟/背包双满
		GameXp.MatchTick( game );             // 等级：本机等级写进自己的球（owner [Sync]，全场名牌可见）
		// 名牌投影改由 NeonCamera 在写完相机姿态的同一时刻调用（UpdateTagPositions），
		// 保证投影用的相机位置/变焦与渲染帧完全一致——引擎查询有帧间差，快速滑屏时名牌会飞出屏（实测）
	}

	/// <summary>
	/// 名牌：把每颗球的世界位置投影到屏幕坐标摆 Label。
	/// 不手推相机轴向——用 CameraComponent.ScreenToWorld 现场标定"世界↔屏幕像素"的
	/// 线性映射（投影中心/右100px/下100px 三个点解基向量），对任何相机朝向与缩放都成立。
	/// Label 按球 Id 稳定绑定；边缘带滞回（出屏 100px 才隐藏），防球在边界来回时闪烁。
	/// </summary>
	/// <summary>
	/// 名牌摆放（由 NeonCamera 在写完相机姿态后调用——投影与渲染用同一份相机数据，零帧间差）。
	/// 解析投影：相机为固定正交俯视（滚动 90°：屏幕右=世界-Y、屏幕下=世界-X），
	/// 只依赖相机位置与 OrthoHeight 两个渲染真源。
	/// </summary>
	public void UpdateTagPositions( CircleroyaleGame game, Vector3 camPos, float orthoHeight )
	{
		if ( orthoHeight <= 0.1f ) return;
		if ( _tagPool is null || _tagPool[0].Label is null || !_tagPool[0].Label.IsValid() ) return;

		var panel = Panel;
		if ( !panel.IsValid() ) return;

		var size = panel.Box.Rect.Size;
		if ( size.x < 1f || size.y < 1f ) return;

		// 每帧先清占用标记，球死亡后其槽位才能被新球复用（漏掉这步池子会逐渐耗尽）
		foreach ( var ts in _tagPool )
		{
			ts.Used = false;
		}

		// ScreenToWorld 是物理像素；Style.Left/Top 是逻辑单位（RootPanel 按 1080p 基准缩放）
		var rootPanel = panel.FindRootPanel();
		var uiScale = rootPanel is not null && rootPanel.Scale > 0.001f ? rootPanel.Scale : 1f;

		// 解析投影：s = 每像素世界单位；屏幕右=-Y、屏幕下=-X
		var s = orthoHeight / size.y;

		// 队友出屏指示（v0.7.8.12）：团队赛 + 本机球有效时启用
		var local = game.LocalBall;
		bool allyMode = MatchState.IsTeam && local.IsValid();
		var marginW = 56f * s;                      // 贴边内缩（世界单位）
		var halfX = size.y * 0.5f * s;              // 屏幕高度方向 ↔ 世界 X
		var halfY = size.x * 0.5f * s;              // 屏幕宽度方向 ↔ 世界 Y

		// 投影 + 摆字（球与分身共用一份逻辑）
		void Place( TagSlot slot, Vector3 worldPos, string text )
		{
			var px = size.x * 0.5f - ( worldPos.y - camPos.y ) / s;
			var py = size.y * 0.5f - ( worldPos.x - camPos.x ) / s;
			bool inView = px > -20f && px < size.x + 20f && py > -20f && py < size.y + 20f;

			if ( !inView )
			{
				if ( slot.Shown )
				{
					slot.Label.Style.Display = DisplayMode.None;
					slot.Shown = false;
				}
				return;
			}

			slot.Label.Style.Display = DisplayMode.Flex;
			slot.Label.Style.Left = px / uiScale - 80f;
			slot.Label.Style.Top = py / uiScale + 8f;
			slot.Label.Style.FontColor = new Color( 0.29f, 0.23f, 0.39f, 0.95f );   // 复位梅紫（队友出屏指示曾设为队伍色）
			SetText( slot.Label, text );
			slot.Shown = true;
		}

		TagSlot TakeSlot() => _tagPool.FirstOrDefault( s2 => !s2.Used );

		foreach ( var b in game.Balls )
		{
			if ( !b.IsValid() || !b.Alive ) continue;   // 死亡/蛰伏球：头像都隐藏，不挂名牌（浮空尸名）

			if ( !_tagMap.TryGetValue( b.Id, out var slot ) )
			{
				slot = TakeSlot();
				if ( slot is null ) continue;   // 池耗尽
				_tagMap[b.Id] = slot;
			}
			slot.Used = true;

			// 队友出屏指示（v0.7.8.12）：主球钳到视口贴边——世界空间箭头指向真实方位，
			// 名字贴在箭头内侧（队友色）。视野内走下方普通名牌路径
			if ( allyMode && b != local && b.TeamIndex >= 0 && b.TeamIndex == local.TeamIndex )
			{
				var cx = Math.Clamp( b.WorldPosition.x, camPos.x - halfX + marginW, camPos.x + halfX - marginW );
				var cy = Math.Clamp( b.WorldPosition.y, camPos.y - halfY + marginW, camPos.y + halfY - marginW );
				var dx = b.WorldPosition.x - cx;
				var dy = b.WorldPosition.y - cy;
				var dl = MathF.Sqrt( dx * dx + dy * dy );

				if ( dl > 0.001f )   // ≈0 = 在视口内（钳制没生效），落普通名牌
				{
					dx /= dl;
					dy /= dl;

					NeonRenderer.EdgeArrow( new Vector3( cx, cy, 0f ), new Vector2( dx, dy ), b.NeonColor, 30f * s );

					var lx = cx - dx * 46f * s;   // 名字朝屏内偏 ~46px
					var ly = cy - dy * 46f * s;
					var px = size.x * 0.5f - ( ly - camPos.y ) / s;
					var py = size.y * 0.5f - ( lx - camPos.x ) / s;

					slot.Label.Style.Display = DisplayMode.Flex;
					slot.Label.Style.Left = px / uiScale - 80f;
					slot.Label.Style.Top = py / uiScale + 8f;
					SetText( slot.Label, TagText( b.PlayerName, b.TeamIndex ) );
					slot.Label.Style.FontColor = b.NeonColor;
					slot.Shown = true;
					continue;
				}
			}

			Place( slot, b.WorldPosition, TagText( b.PlayerName, b.TeamIndex, b.AccountLevel ) );
		}

		// 分身名牌（M4）：名字经主人球解析（bot 有独立假 SteamId，能精确到哪个 bot）
		foreach ( var c in game.Cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;

			var owner = game.FindBallBySteamId( c.OwnerSteamId );
			if ( !owner.IsValid() || !owner.Alive ) continue;

			if ( !_pieceTagMap.TryGetValue( c.Id, out var slot ) )
			{
				slot = TakeSlot();
				if ( slot is null ) continue;
				_pieceTagMap[c.Id] = slot;
			}
			slot.Used = true;

			Place( slot, new Vector3( c.DrawPos.x, c.DrawPos.y, 0f ), TagText( owner.PlayerName, owner.TeamIndex, owner.AccountLevel ) );
		}

		// 本帧未用到的槽 = 实体已死亡/分身已合并：隐藏并解除绑定
		for ( int i = _tagMap.Count - 1; i >= 0; i-- )
		{
			var kv = _tagMap.ElementAt( i );
			if ( kv.Value.Used ) continue;

			kv.Value.Label.Style.Display = DisplayMode.None;
			kv.Value.Shown = false;
			_tagMap.Remove( kv.Key );
		}
		for ( int i = _pieceTagMap.Count - 1; i >= 0; i-- )
		{
			var kv = _pieceTagMap.ElementAt( i );
			if ( kv.Value.Used ) continue;

			kv.Value.Label.Style.Display = DisplayMode.None;
			kv.Value.Shown = false;
			_pieceTagMap.Remove( kv.Key );
		}
	}

	void UpdateMass( CircleroyaleGame game, bool over )
	{
		if ( !_mass.IsValid() ) return;

		// 比赛结束：数值类 HUD 全部让位给结算面板
		_mass.Style.Display = over ? DisplayMode.None : DisplayMode.Flex;
		if ( over ) return;

		var ball = game.LocalBall;
		SetText( _mass, ball.IsValid() ? $"{game.TotalMassFor( ball ):0}" : "—" );
	}

	void UpdateLeaderboard( CircleroyaleGame game, bool over )
	{
		if ( _rows is null || _rows[0] is null || !_rows[0].IsValid() ) return;

		// 领土模式（M7.3 用户定稿）：右侧战绩榜整体隐藏——小地图下方团队分数替代
		if ( MatchState.IsTerritory )
		{
			for ( int i = 0; i < _rows.Length; i++ )
				SetDisplay( _rows[i], DisplayMode.None );
			SetDisplay( _selfRow, DisplayMode.None );
			SetDisplay( _crown, DisplayMode.None );
			return;
		}

		if ( over )
		{
			for ( int i = 0; i < _rows.Length; i++ )
			{
				if ( _rows[i].IsValid() ) _rows[i].Text = "";
			}
			if ( _selfRow.IsValid() ) _selfRow.Style.Display = DisplayMode.None;
			if ( _crown.IsValid() ) _crown.Style.Display = DisplayMode.None;
			return;
		}

		// 按总质量（主球+分身）降序；球数 ≤ 48（真人+bot 上限），缓冲复用零分配
		_rankList.Clear();
		_rankMass.Clear();
		foreach ( var b in game.Balls )
		{
			if ( b.IsValid() && b.Alive ) _rankList.Add( b );
		}
		foreach ( var b in _rankList )
			_rankMass[b] = game.TotalMassFor( b );
		_rankList.Sort( ( x, y ) => _rankMass[y].CompareTo( _rankMass[x] ) );
		var list = _rankList;
		var massOf = _rankMass;

		var local = game.LocalBall;
		var selfRank = -1;
		for ( int i = 0; i < list.Count; i++ )
		{
			if ( local.IsValid() && list[i] == local )
			{
				selfRank = i;
				break;
			}
		}

		for ( int i = 0; i < _rows.Length; i++ )
		{
			var row = _rows[i];
			if ( !row.IsValid() ) continue;

			if ( i >= list.Count )
			{
				row.Text = "";
				continue;
			}

			var ball = list[i];
			var isSelf = local.IsValid() && ball == local;
			SetText( row, TeamModeRow( $"{i + 1}.", ball.PlayerName, $"{massOf[ball]:0}", ball.TeamIndex ) );
			row.Style.FontColor = isSelf ? new Color( 0.898f, 0.282f, 0.553f ) : new Color( 0.29f, 0.23f, 0.39f, 0.78f );
		}

		// 榜外自身行
		var showSelf = selfRank >= _rows.Length;
		if ( _selfRow.IsValid() )
		{
			_selfRow.Style.Display = showSelf ? DisplayMode.Flex : DisplayMode.None;
			if ( showSelf )
				SetText( _selfRow, TeamModeRow( $"#{selfRank + 1}.", local.PlayerName, $"{massOf[local]:0}", local.TeamIndex ) );
		}

		// 皇冠钉在第一名行左侧（跟随行框左缘，长名不会顶到图标；Box 是物理像素，样式是逻辑单位要除 Scale）
		if ( _crown.IsValid() )
		{
			var show = list.Count > 0;
			_crown.Style.Display = show ? DisplayMode.Flex : DisplayMode.None;
			if ( show )
			{
				var rootPanel = _rows[0].FindRootPanel();
				var sc = rootPanel is not null && rootPanel.Scale > 0.001f ? rootPanel.Scale : 1f;
				_crown.Style.Left = _rows[0].Box.Rect.Left / sc - 24f;
				_crown.Style.Top = _rows[0].Box.Rect.Top / sc - 3f;
			}
		}
	}

	/// <summary> 死亡面板点击重生（实例方法：热重载可重映射） </summary>
	void OnDeathSubClicked() => CircleroyaleGame.Current?.TryRespawnLocal();

	/// <summary> 领土模式死亡面板二选一（M7.2）：true=前线（系统挑离阵亡点最近的己方占领格） </summary>
	void OnDeathRespawnClicked( bool frontLine ) => CircleroyaleGame.Current?.TryRespawnLocal( frontLine );

	/// <summary>
	/// 本队成员积分（团队赛，排行榜下方，v0.6.5.0）：队头 "MY TEAM [T#]" + 成员按质量降序，
	/// 自己那行金色高亮。普通赛 / 没有本机球 / 结算期整块隐藏。
	/// </summary>
	void UpdateMates( CircleroyaleGame game, bool over )
	{
		if ( _mateRows is null || _mateRows[0] is null || !_mateRows[0].IsValid() ) return;

		var local = game.LocalBall;
		bool show = !over && MatchState.IsTeam && !MatchState.IsTerritory && local.IsValid();   // 领土隐藏 MY TEAM（M7.3：地图下团队分数替代）

		if ( !show )
		{
			foreach ( var r in _mateRows )
				SetDisplay( r, DisplayMode.None );
			return;
		}

		int myTeam = Math.Max( 0, local.TeamIndex );

		// 本队存活成员按总质量降序（按块分队后每队 ≤ TeamSize ≤ 4 人，缓冲复用直接排序）
		_matesList.Clear();
		_matesMass.Clear();
		foreach ( var b in game.Balls )
		{
			if ( b.IsValid() && b.Alive && b.TeamIndex == myTeam ) _matesList.Add( b );
		}
		foreach ( var m in _matesList )
			_matesMass[m] = game.TotalMassFor( m );
		_matesList.Sort( ( x, y ) => _matesMass[y].CompareTo( _matesMass[x] ) );
		var mates = _matesList;
		var massOf = _matesMass;

		for ( int i = 0; i < _mateRows.Length; i++ )
		{
			var row = _mateRows[i];
			if ( !row.IsValid() ) continue;

			if ( i == 0 )
			{
				row.Style.Display = DisplayMode.Flex;
				SetText( row, $"MY TEAM [T{myTeam + 1}]  {mates.Sum( m => massOf[m] ):0}" );
				row.Style.FontColor = MatchState.TeamColor( myTeam );
				continue;
			}

			int k = i - 1;
			if ( k >= mates.Count )
			{
				row.Style.Display = DisplayMode.None;
				continue;
			}

			var mate = mates[k];
			bool self = mate == local;
			row.Style.Display = DisplayMode.Flex;
			SetText( row, $"{( self ? ">" : " ")} {mate.PlayerName}  {massOf[mate]:0}" );
			row.Style.FontColor = self ? new Color( 0.851f, 0.541f, 0f ) : new Color( 0.29f, 0.23f, 0.39f, 0.85f );
		}
	}

	/// <summary> 名牌文本（团队赛带 [T#] 队伍前缀，v0.6.5.0 用户需求） </summary>
	string TagText( string name, int team, int level = 0 ) =>
		( MatchState.IsTeam && team >= 0 ? $"[T{team + 1}] " : "" )
		+ ( level > 0 ? $"LV.{level} " : "" )
		+ name;

	/// <summary> 排行榜行文本（团队赛带 T# 前缀；普通赛只有名次/名字/分数） </summary>
	string TeamModeRow( string rank, string name, string mass, int team )
	{
		// team<0（蛰伏/无队球）不显示队伍前缀——Math.Max(0,-1) 曾让无队 bot 全挂 [T1]（v0.7.8.25）
		return ( MatchState.IsTeam && team >= 0 ) ? $"{rank} [T{team + 1}] {name}  {mass}" : $"{rank}  {name}  {mass}";
	}

	// ---- 比赛规则 UI（M5）----

	void UpdateMatchInfo( CircleroyaleGame game )
	{
		if ( !_matchInfo.IsValid() ) return;

		if ( MatchState.MatchOver )
		{
			_matchInfo.Style.Display = DisplayMode.None;
			HideTeamRows();
			return;
		}

		_matchInfo.Style.Display = DisplayMode.Flex;

		// 倒计时格式 m:ss——分钟必须截断取整（"0" 格式会四舍五入，44 秒显示成 "1:44" 的教训）
		var total = (int)MathF.Ceiling( MathF.Max( 0f, MatchState.TimeLeft ) );
		var mode = MatchState.IsTerritory ? "TERRITORY WAR" : MatchState.IsTeam ? "TEAM BATTLE" : "FREE FOR ALL";
		SetText( _matchInfo, $"{mode}   {total / 60}:{total % 60:00}" );

		// 平时深梅紫（浅色世界里白字看不清，v0.7.8.77 用户定稿）；最后 30 秒变红提示
		var mic = total < 30 ? new Color( 1f, 0.35f, 0.45f ) : new Color( 0.29f, 0.23f, 0.39f );
		if ( !_matchInfo.Style.FontColor.HasValue || !SameColor( _matchInfo.Style.FontColor.Value, mic ) )
			_matchInfo.Style.FontColor = mic;

		if ( MatchState.IsTeam ) UpdateTeamScores( game );
		else HideTeamRows();
	}

	/// <summary> 团队赛队伍总分条：前 5 名队伍 + 本队（不在前 5 时补一条），按总分降序。
	/// 领土战争（M7）走 UpdateTerritoryScores——条上显示占领格数而非总质量 </summary>
	void UpdateTeamScores( CircleroyaleGame game )
	{
		if ( MatchState.IsTerritory )
		{
			UpdateTerritoryScores( game );
			return;
		}

		_teamTotals.Clear();
		foreach ( var b in game.Balls )
		{
			if ( !b.IsValid() || !b.Alive ) continue;
			if ( b.TeamIndex < 0 ) continue;   // 无队球不进队伍聚合——Math.Max(0,-1) 曾把蛰伏误复活的 16 颗无队 bot 全灌进 T1（v0.7.8.25）
			var team = b.TeamIndex;
			_teamTotals.TryGetValue( team, out var m );
			_teamTotals[team] = m + game.TotalMassFor( b );
		}

		_teamOrder.Clear();
		_teamOrder.AddRange( _teamTotals.Keys );
		_teamOrder.Sort( ( x, y ) => _teamTotals[y].CompareTo( _teamTotals[x] ) );

		var local = game.LocalBall;
		var myTeam = local.IsValid() ? Math.Max( 0, local.TeamIndex ) : -1;

		_teamShow.Clear();
		foreach ( var t in _teamOrder )
		{
			if ( _teamShow.Count >= 5 ) break;
			_teamShow.Add( t );
		}
		if ( myTeam >= 0 && _teamTotals.ContainsKey( myTeam ) && !_teamShow.Contains( myTeam ) )
			_teamShow.Add( myTeam );
		_teamShow.Sort( ( x, y ) => _teamTotals[y].CompareTo( _teamTotals[x] ) );
		var show = _teamShow;

		// 横排居中（逻辑宽 1920），每格 170
		float width = 170f;
		float startX = 960f - show.Count * width / 2f;

		for ( int i = 0; i < _teamRows.Length; i++ )
		{
			var row = _teamRows[i];
			if ( !row.IsValid() ) continue;

			if ( i >= show.Count )
			{
				row.Style.Display = DisplayMode.None;
				continue;
			}

			var team = show[i];
			row.Style.Display = DisplayMode.Flex;
			row.Style.Left = startX + i * width;
			SetText( row, $"T{team + 1}  {_teamTotals[team]:0}" );

			bool mine = team == myTeam;
			row.Style.FontColor = new Color( 0.29f, 0.23f, 0.39f );   // 统一深梅紫（v0.7.8.77 用户定稿），本队靠不透明度区分
			row.Style.Opacity = mine ? 1f : 0.75f;
		}
	}

	/// <summary> 领土战争（M7）队伍条：四队占领格数（格数相同也全显，0 格队伍也要看见自己），按格数降序 </summary>
	void UpdateTerritoryScores( CircleroyaleGame game )
	{
		_teamTotals.Clear();
		for ( var t = 0; t < 4; t++ )
		{
			var pts = TerritoryManager.Points( t );
			if ( pts > 0f ) _teamTotals[t] = pts;   // 占旗积分（M7.4：每旗每 3s +1，点数定胜负）
		}

		var local = game.LocalBall;
		var myTeam = local.IsValid() ? Math.Max( 0, local.TeamIndex ) : -1;

		_teamShow.Clear();
		foreach ( var kv in _teamTotals ) _teamShow.Add( kv.Key );
		if ( myTeam >= 0 && !_teamShow.Contains( myTeam ) ) _teamShow.Add( myTeam );
		_teamShow.Sort( ( x, y ) =>
		{
			_teamTotals.TryGetValue( x, out var vx );
			_teamTotals.TryGetValue( y, out var vy );
			return vy.CompareTo( vx );
		} );

		// 领土（M7.3 用户定稿）：竖排在小地图正下方，右缘与地图对齐（小地图右上角）
		// 逻辑宽 1s 缓存——每帧 FindRootPanel+读 Box.Rect 会强制布局（参与闪烁，v0.7.8.86 修）
		if ( _sinceLogicalW > 1f )
		{
			_sinceLogicalW = 0;
			var rootPanel = Panel.FindRootPanel();
			var uiScale = rootPanel is not null && rootPanel.Scale > 0.001f ? rootPanel.Scale : 1f;
			_logicalWCache = Panel.Box.Rect.Width / uiScale;
		}
		float width = 170f;
		var left = _logicalWCache - 28f - width;   // 28 = 小地图 right 边距

		for ( int i = 0; i < _teamRows.Length; i++ )
		{
			var row = _teamRows[i];
			if ( !row.IsValid() ) continue;

			if ( i >= _teamShow.Count )
			{
				SetDisplay( row, DisplayMode.None );
				continue;
			}

			var team = _teamShow[i];
			// 位置/字色只在"隐藏→显示"沿写一次（Resize 后由下沿刷新）；同值不写防闪烁
			if ( row.Style.Display != DisplayMode.Flex )
			{
				row.Style.Display = DisplayMode.Flex;
				row.Style.Left = left;
				row.Style.Top = 326f + i * 40f;   // 地图 top24 + 高292 + 间隙
				row.Style.FontColor = new Color( 0.29f, 0.23f, 0.39f );   // 统一深梅紫（同团队赛，v0.7.8.77）
			}
			_teamTotals.TryGetValue( team, out var pts );
			SetText( row, $"T{team + 1}  {pts:0} PTS" );

			bool mine = team == myTeam;
			var op = mine ? 1f : 0.75f;
			if ( row.Style.Opacity != op ) row.Style.Opacity = op;
		}
	}

	void HideTeamRows()
	{
		if ( _teamRows is null ) return;
		foreach ( var r in _teamRows )
			SetDisplay( r, DisplayMode.None );
	}

	// ---- 击杀播报（M5"谁吃了谁"）----

	/// <summary> 加一条播报（CircleroyaleGame.OnBallEaten 双端调用）：自己参与的高亮（金=吃 红=被吃） </summary>
	public void AddKillFeed( string eaterName, string eatenName, float massGained, long eaterSteamId, long eatenSteamId )
	{
		var root = Panel;
		if ( root is null || !root.IsValid() ) return;

		// 上限 6 条，挤掉最老
		while ( _killFeed.Count >= 6 )
		{
			if ( _killFeed[0].Label.IsValid() ) _killFeed[0].Label.Delete();
			_killFeed.RemoveAt( 0 );
		}

		bool iAte = GameSfx.IsMine( eaterSteamId );
		bool iDied = GameSfx.IsMine( eatenSteamId );

		var l = new Label() { Classes = iAte ? "killfeed good" : iDied ? "killfeed bad" : "killfeed" };
		var text = $"{eaterName}  ⟫  {eatenName}";
		if ( iAte && massGained >= 1f ) text += $"   +{massGained:0}";
		l.Text = text;
		l.Style.Position = PositionMode.Absolute;
		l.Style.Left = 30f;
		root.AddChild( l );

		_killFeed.Add( new KillFeedEntry { Label = l, Since = 0 } );
	}

	void UpdateKillFeed()
	{
		for ( int i = _killFeed.Count - 1; i >= 0; i-- )
		{
			var e = _killFeed[i];
			if ( !e.Label.IsValid() || e.Since > 4.5f )
			{
				if ( e.Label.IsValid() ) e.Label.Delete();
				_killFeed.RemoveAt( i );
			}
		}

		// 自上而下重排（老条目删除后不串位）+ 尾段淡出；起始 78 = 分数牌下方留足空隙（v0.7.8.77 用户定稿）
		for ( int i = 0; i < _killFeed.Count; i++ )
		{
			var e = _killFeed[i];
			e.Label.Style.Top = 108f + i * 26f;
			e.Label.Style.Opacity = e.Since < 3.5f ? 1f : Math.Clamp( 1f - ( e.Since - 3.5f ) / 1f, 0f, 1f );
		}
	}

	/// <summary>
	/// 本地球道具 buff 状态（v0.7.3.0）：左下角操作提示上方，种类名 + 剩余秒 + 种类色。
	/// 剩余秒每帧在变（文本防抖挡不住），颜色只在种类切换时赋值。
	/// </summary>
	void UpdateBuff( CircleroyaleGame game )
	{
		if ( _buff is null || !_buff.IsValid() ) return;

		var ball = game.LocalBall;
		if ( !ball.IsValid() || !ball.Alive || ball.BuffKind == 255 || ball.BuffLeft <= 0f )
		{
			SetText( _buff, "" );
			return;
		}

		SetText( _buff, $"{PowerUpManager.NameOf( ball.BuffKind )}  {ball.BuffLeft:0}s" );
		var c = PowerUpManager.ColorOf( ball.BuffKind );
		if ( _buff.Style.FontColor != c ) _buff.Style.FontColor = c;
	}

	/// <summary>
	/// 玩法提示轮播（v0.7.4.2）：每 8s 换一条，换条瞬间 0.5s 透明度渐入（Opacity 不触发
	/// 布局重算，渐入结束值恒 1 不再写样式）；文本走 SetText 防抖。
	/// </summary>
	void UpdateTips()
	{
		if ( _tips is null || !_tips.IsValid() ) return;

		if ( _sinceTip > 8f )
		{
			_tipIndex = ( _tipIndex + 1 ) % Tips.Length;
			_sinceTip = 0;
			SetText( _tips, Tips[_tipIndex] );
		}

		var op = Math.Clamp( _sinceTip / 0.5f, 0f, 1f );
		if ( _tips.Style.Opacity != op )
			_tips.Style.Opacity = op;   // 渐入 0.5s 内逐帧变化；结束后同值不再写（防无谓样式刷新）
	}

	/// <summary>
	/// 右下角技能按钮（v0.7.8.74 球球大作战手游风：大图标 + 冷却暗罩 + 倒数秒 + 键位角标）。
	/// Q 充能进度是**本地估算**：host 只把槽种类（PowerA 字节）同步过来，客户端在
	/// "入局/复活拿到球"与"放技能（种类→空）"两个沿上按当前质量对应的充能时长起表；
	/// 估算跑完也封顶 97% 不提前亮——host 真发种类、按钮点亮那刻才是权威时刻。
	/// </summary>
	void UpdatePowerSlots( CircleroyaleGame game )
	{
		if ( _btnQ is null || _btnQ.Root is null || !_btnQ.Root.IsValid() ) return;

		var ball = game.LocalBall;
		if ( !ball.IsValid() )
		{
			_hadBall = false;
			DrawQ( 255, 0f );
			DrawE( 255 );
			return;
		}

		// 领土模式（M7.4）：Q = 职业技能（系统分配、冷却制）；E 道具照旧
		if ( MatchState.IsTerritory )
		{
			DrawClassQ( ball );
			DrawE( ball.PowerB );
			return;
		}

		if ( !_hadBall && ball.PowerA == 255 )
		{
			_qCd = 0;
			_qCdDur = GameConfig.QSkillCooldownFor( ball.Mass );
		}
		_hadBall = true;

		if ( ball.PowerA != _prevPowerA )
		{
			if ( ball.PowerA == 255 )   // 刚放掉/重生清空：冷却重新起算
			{
				_qCd = 0;
				_qCdDur = GameConfig.QSkillCooldownFor( ball.Mass );
			}
			_prevPowerA = ball.PowerA;
		}

		var p = _qCdDur > 0f ? Math.Clamp( _qCd / _qCdDur, 0f, 1f ) : 1f;
		DrawQ( ball.PowerA, MathF.Min( p, 0.97f ) );
		DrawE( ball.PowerB );
	}

	/// <summary>
	/// 技能按钮骨架：九宫格底 + 大图标留在按钮面板里（这两层实测定位正常）；
	/// 冷却罩/倒数秒/键位角标挂 HUD 根面板、内联 Right 定位（rightPx = 按钮右距）。
	/// 冷却罩贴底、高度代码驱动：底边固定上缘下沉，像手游那样从上往下"排空"亮出图标
	/// </summary>
	SkillButton BuildSkillButton( Panel root, string cls, string keyHint, int rightPx )
	{
		var button = new Panel() { Classes = cls };
		var b = new SkillButton { Root = button };

		b.Icon = new Image() { Classes = "skill-icon" };
		button.AddChild( b.Icon );

		root.AddChild( button );

		b.Cd = new Panel() { Classes = "skill-cd" };
		b.Cd.Style.Right = rightPx + 5;
		root.AddChild( b.Cd );

		b.Num = new Label() { Classes = "skill-num" };
		b.Num.Style.Right = rightPx + 5;
		root.AddChild( b.Num );

		var key = new Label() { Classes = "skill-key" };
		key.Text = keyHint;
		key.Style.Right = rightPx + 8;
		root.AddChild( key );

		return b;
	}

	static readonly string[] PowerSlugs = { "speed", "magnet", "shield", "spike", "mass" };

	Texture IconTexture( byte kind ) =>
		Texture.Load( $"ui/pixel/pw_{PowerSlugs[Math.Clamp( (int)kind, 0, PowerSlugs.Length - 1 )]}.png" );

	/// <summary> Q 钮：充能中 = 闪电暗图标 + 底部暗罩随进度下排 + 倒数秒；就绪 = 技能大图标点亮 </summary>
	void DrawQ( byte kind, float progress )
	{
		var b = _btnQ;
		bool charging = kind == 255;

		if ( kind != _qDrawn )   // 状态切换才动图标/透明度/显隐
		{
			_qDrawn = kind;
			b.Icon.Texture = charging ? Texture.Load( "ui/skin/ic_bolt.png" ) : IconTexture( kind );
			b.Icon.Style.Opacity = charging ? 0.4f : 1f;
			b.Num.Style.Display = charging ? DisplayMode.Flex : DisplayMode.None;
			b.Cd.Style.Display = charging ? DisplayMode.Flex : DisplayMode.None;
			if ( charging ) _qCdHeight = -1f;   // 重新进充能：强制下帧把罩高写回满格
		}

		if ( !charging ) return;

		var remain = MathF.Max( 1f, MathF.Ceiling( (1f - progress) * _qCdDur ) );
		SetText( b.Num, ((int)remain).ToString() );

		var h = MathF.Round( (1f - progress) * SkillBtnSize );
		if ( MathF.Abs( h - _qCdHeight ) > 0.5f )
		{
			_qCdHeight = h;
			b.Cd.Style.Height = h;
		}
	}

	/// <summary> 领土 Q 钮（M7.4）：职业技能——就绪显示职业短标，冷却显示倒数秒+排空罩 </summary>
	void DrawClassQ( Ball ball )
	{
		var b = _btnQ;
		var cls = ball.TerritoryClass;

		if ( cls > 3 )
		{
			DrawQ( 255, 0f );   // 无职业（理论不出现）：按空槽处理
			return;
		}

		if ( _qDrawn != (byte)( cls + 100 ) )   // +100 避开道具 kind 段
		{
			_qDrawn = (byte)( cls + 100 );
			b.Icon.Texture = Texture.Load( "ui/skin/ic_bolt.png" );
			b.Icon.Style.Opacity = 1f;
			b.Num.Style.Display = DisplayMode.Flex;
			_qCdHeight = -1f;
		}

		var cd = ClassSkillManager.CooldownOf( cls );
		var remain = ball.ClassCooldownRemaining;
		var cooling = remain > 0.1f;

		SetText( b.Num, cooling ? MathF.Ceiling( remain ).ToString() : ClassSkillManager.TagOf( cls ) );

		var h = cooling ? MathF.Round( remain / cd * SkillBtnSize ) : 0f;
		if ( MathF.Abs( h - _qCdHeight ) > 0.5f )
		{
			_qCdHeight = h;
			b.Cd.Style.Height = h;
		}
		SetDisplay( b.Cd, cooling ? DisplayMode.Flex : DisplayMode.None );
	}

	/// <summary> E 钮：持有道具 = 大图标点亮；空槽 = 整钮压暗（键位角标仍可见） </summary>
	void DrawE( byte kind )
	{
		if ( kind == _eDrawn ) return;
		_eDrawn = kind;

		var b = _btnE;
		b.Icon.Texture = kind == 255 ? null : IconTexture( kind );
		b.Root.Style.Opacity = kind == 255 ? 0.55f : 1f;
	}

	/// <summary>
	/// 个人高光横幅（v0.7.6.0）：屏幕中下堆叠最多 3 条，大号发光字——
	/// 击杀（金）/ 捡到道具（种类色）/ 使用与喂养触发（种类色）。
	/// 弹入（透明度+下落 20px 回位）→ 停留 → 淡出；最老先删，位置逐帧重排（变了才写样式）。
	/// </summary>
	public void AddBanner( string text, Color color )
	{
		if ( _banners.Count >= 3 )
		{
			var oldest = _banners[0];
			if ( oldest.Label.IsValid() ) oldest.Label.Delete();
			_banners.RemoveAt( 0 );
		}

		var l = new Label() { Classes = "banner" };
		l.Text = text;
		l.Style.FontColor = color;
		Panel?.AddChild( l );
		_banners.Add( new Banner { Label = l, Since = 0 } );
	}

	void UpdateBanners()
	{
		for ( int i = _banners.Count - 1; i >= 0; i-- )
		{
			var b = _banners[i];
			if ( b.Label is null || !b.Label.IsValid() )
			{
				_banners.RemoveAt( i );
				continue;
			}

			var age = b.Since;
			if ( age > 2.1f )
			{
				b.Label.Delete();
				_banners.RemoveAt( i );
				continue;
			}

			// 堆叠位置：最新的最靠下（贴近屏幕中下），被挤掉后其余逐帧回位
			float bottom = 170f + i * 46f;
			if ( b.ShownBottom != bottom )
			{
				b.ShownBottom = bottom;
				b.Label.Style.Bottom = bottom;
			}

			float opacity;
			if ( age < 0.3f )
			{
				opacity = age / 0.3f;
				b.Label.Style.PaddingTop = ( 1f - opacity ) * 20f;   // 弹入：从下方落回原位
			}
			else if ( age > 1.5f )
			{
				opacity = 1f - ( age - 1.5f ) / 0.6f;
			}
			else
			{
				opacity = 1f;
			}

			if ( b.Label.Style.Opacity != opacity )
				b.Label.Style.Opacity = opacity;
		}
	}

	void UpdateDeathPanel( CircleroyaleGame game, bool over )
	{
		var dead = game.IsLocalDead && !over;   // 结算期死亡面板让位
		if ( _deathShade.IsValid() )
			_deathShade.Style.Display = dead ? DisplayMode.Flex : DisplayMode.None;
		if ( _deathTitle.IsValid() )
			_deathTitle.Style.Display = dead ? DisplayMode.Flex : DisplayMode.None;
		if ( _deathSub.IsValid() )
		{
			_deathSub.Style.Display = dead ? DisplayMode.Flex : DisplayMode.None;

			// 死亡观战提示（v0.7.8.28）：跟谁看战局写在副标题里
			var spec = game.Camera?.SpectateName;
			var text = string.IsNullOrEmpty( spec )
				? "PRESS SPACE / CLICK TO RESPAWN"
				: $"SPECTATING {spec} — PRESS SPACE TO RESPAWN";
			if ( _deathSub.Text != text ) _deathSub.Text = text;
		}

		// 领土模式二选一按钮（M7.2）：非领土隐藏；前线没占领格时置灰禁用（点了也回落大本营）
		var territory = dead && MatchState.IsTerritory;
		SetDisplay( _deathBase, territory ? DisplayMode.Flex : DisplayMode.None );
		SetDisplay( _deathFront, territory ? DisplayMode.Flex : DisplayMode.None );
		if ( territory && _deathFront.IsValid() )
		{
			var myTeam = game.LocalBall.IsValid() ? game.LocalBall.TeamIndex : -1;
			var hasFront = TerritoryManager.HasFrontCell( myTeam );
			var ft = hasFront ? "RESPAWN — FRONT" : "FRONT — NO CELLS";
			if ( _deathFront.Text != ft ) _deathFront.Text = ft;
			var fop = hasFront ? 1f : 0.35f;
			if ( _deathFront.Style.Opacity != fop ) _deathFront.Style.Opacity = fop;
		}
	}

	// ---- 领土玩法引导 + 小地图（M7.3）----

	/// <summary> 同值守卫式显隐（M6.3 教训：每帧重复写样式=布局重算=客户端 UI 闪烁回归） </summary>
	static void SetDisplay( Panel p, DisplayMode mode )
	{
		if ( p.IsValid() && p.Style.Display != mode ) p.Style.Display = mode;
	}

	static bool SameColor( Color a, Color b ) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

	static bool IsHqCell( int cx, int cy )
	{
		for ( var t = 0; t < 4; t++ )
		{
			var hq = TerritoryManager.HqCell( t );
			if ( hq.Cx == cx && hq.Cy == cy ) return true;
		}
		return false;
	}

	/// <summary> 情境引导：冷却 key 在冷却期内则跳过；否则显示 duration 秒并记冷却 </summary>
	void ShowTerritoryHint( string text, string key, float cooldown, float duration )
	{
		if ( HintCooldowns.TryGetValue( key, out var since ) && since < cooldown ) return;
		HintCooldowns[key] = 0;
		if ( _thintText != text ) SetText( _thint, text );
		_thintText = text;
		_sinceThint = 0;
		_thintDuration = duration;
	}

	/// <summary> 领土情境引导（玩家靠近旗提示吐球等）：优先级从高到低逐条尝试，当帧无人触发则维持/淡出上一条 </summary>
	void UpdateTerritoryHint( CircleroyaleGame game )
	{
		var active = MatchState.IsTerritory && game.IsMatchStarted && !MatchState.MatchOver;
		if ( !active || _thint is null || !_thint.IsValid() )
		{
			if ( _thint is not null && _thint.IsValid() && _thint.Style.Display != DisplayMode.None )
				_thint.Style.Display = DisplayMode.None;
			_lastOwnedForHint = -1;
			return;
		}

		var ball = game.LocalBall;
		if ( ball.IsValid() && ball.Alive )
		{
			var team = ball.TeamIndex;
			var pos = ball.WorldPosition;

			// 捕获/开局检测（占领数上升沿；入局首帧=开局引导）
			var owned = TerritoryManager.OwnedCount( team );
			if ( _lastOwnedForHint < 0 )
			{
				_lastOwnedForHint = owned;
				ShowTerritoryHint( "HELD FLAGS EARN POINTS EVERY 3S — HOLD ALL 12 FOR INSTANT WIN", "win", 9999f, 5.5f );
			}
			else if ( owned > _lastOwnedForHint )
			{
				_lastOwnedForHint = owned;
				ShowTerritoryHint( "CELL CAPTURED! ENEMY SPIT WEAKENS IT — REFEED TO REINFORCE", "captured", 30f, 3.5f );
			}
			else
			{
				_lastOwnedForHint = owned;
			}

			// 情境提示（优先级序）：敌营 > 非邻接旗 > 旗点吐球 > 回家回血
			if ( TerritoryManager.InHostileHq( team, pos ) )
			{
				ShowTerritoryHint( "ENEMY CASTLE — YOU ARE SLOW AND LOSING MASS, GET OUT!", "hostilehq", 20f, 3.5f );
			}
			else
			{
				// 最近旗点（16 格循环，量可忽略）
				var nd = float.MaxValue;
				int ncx = 0, ncy = 0;
				for ( int cy = 0; cy < GameConfig.TerritoryGrid; cy++ )
				{
					for ( int cx = 0; cx < GameConfig.TerritoryGrid; cx++ )
					{
						var d = TerritoryManager.CellCenter( cx, cy ).Distance( pos );
						if ( d < nd )
						{
							nd = d;
							ncx = cx;
							ncy = cy;
						}
					}
				}

				if ( nd < GameConfig.TerritoryFlagRadius * 1.8f )
				{
					TerritoryManager.CellState( ncx, ncy, out var owner, out _, out _ );
					if ( owner != team )
					{
						if ( !TerritoryManager.CanActOnCell( ncx, ncy, team ) )
							ShowTerritoryHint( "TOO FAR — ONLY CELLS NEXT TO YOUR TERRITORY CAN BE TAKEN", "adjacent", 45f, 3.5f );
						else
							ShowTerritoryHint( "HOLD LMB / R — SPIT INTO THE FLAG TO CAPTURE IT", "nearflag", 45f, 4f );
					}
				}
				else if ( TerritoryManager.IsOwnHq( team, pos ) && ball.Mass > GameConfig.MassDecayMinMass )
				{
					ShowTerritoryHint( "HOME CASTLE STOPS MASS DECAY — FALL BACK TO RECOVER", "homehq", 90f, 3.5f );
				}
			}
		}

		// 显示与淡出（渐入 0.3s / 渐出 0.5s；结束后隐藏）
		var showIt = _sinceThint < _thintDuration;
		if ( _thint.Style.Display != ( showIt ? DisplayMode.Flex : DisplayMode.None ) )
			_thint.Style.Display = showIt ? DisplayMode.Flex : DisplayMode.None;
		if ( showIt )
		{
			var op = Math.Clamp( MathF.Min( _sinceThint / 0.3f, ( _thintDuration - _sinceThint ) / 0.5f ), 0f, 1f );
			if ( _thint.Style.Opacity != op ) _thint.Style.Opacity = op;
		}
	}

	/// <summary> 领土小地图（M7.3）：10Hz 刷新格色（随占领/驻军变实）+ 进度条 + 本机/队友点。
	/// 敌方球不上图（避免全图透视；队友/自己才显示） </summary>
	void UpdateTerritoryMap( CircleroyaleGame game )
	{
		var show = MatchState.IsTerritory && game.IsMatchStarted && !MatchState.MatchOver;
		if ( _tmap.IsValid() && _tmap.Style.Display != ( show ? DisplayMode.Flex : DisplayMode.None ) )
			_tmap.Style.Display = show ? DisplayMode.Flex : DisplayMode.None;
		if ( !show || _sinceTmapTick < 0.1f ) return;
		_sinceTmapTick = 0;

		var half = GameConfig.ArenaHalfSize;
		var scale = 286f / ( half * 2f );

		for ( int cy = 0; cy < GameConfig.TerritoryGrid; cy++ )
		{
			for ( int cx = 0; cx < GameConfig.TerritoryGrid; cx++ )
			{
				var i = cy * GameConfig.TerritoryGrid + cx;
				TerritoryManager.CellState( cx, cy, out var owner, out var scoreTeam, out var score );
				var frac = Math.Clamp( score / GameConfig.TerritoryCaptureScore, 0f, 1f );

				Color bg;
				if ( owner >= 0 )
				{
					var c = MatchState.TeamColor( owner );
					bg = new Color( c.r, c.g, c.b, 0.3f + 0.55f * frac );
				}
				else if ( scoreTeam >= 0 && score > 0f )
				{
					var c = MatchState.TeamColor( scoreTeam );
					bg = new Color( c.r, c.g, c.b, 0.10f + 0.30f * frac );
				}
				else
				{
					bg = new Color( 0.85f, 0.90f, 0.95f, 0.55f );
				}
				if ( !SameColor( _tmapLastBg[i], bg ) )
				{
					_tmapLastBg[i] = bg;
					_tmapCells[i].Style.BackgroundColor = bg;
				}
				var barW = _tmapCellPx * frac;
				if ( MathF.Abs( _tmapLastBar[i] - barW ) > 0.5f )
				{
					_tmapLastBar[i] = barW;
					_tmapBars[i].Style.Width = barW;
				}
			}
		}

		var local = game.LocalBall;
		var myTeam = local.IsValid() ? local.TeamIndex : -1;

		// 本机点（死了就不画，避免钉在阵亡处误导）
		if ( _tmapSelf.IsValid() )
		{
			var showSelf = local.IsValid() && local.Alive;
			SetDisplay( _tmapSelf, showSelf ? DisplayMode.Flex : DisplayMode.None );
			if ( showSelf )
			{
				var p = local.WorldPosition;
				var px = ( half - p.y ) * scale - 8f;
				var py = ( half - p.x ) * scale - 8f;
				if ( MathF.Abs( px - _tmapSelfX ) > 0.5f || MathF.Abs( py - _tmapSelfY ) > 0.5f )
				{
					_tmapSelfX = px;
					_tmapSelfY = py;
					_tmapSelf.Style.Left = px;
					_tmapSelf.Style.Top = py;
				}
			}
		}

		// 队友点（活着的同队球，最多 3 个）
		var mi = 0;
		if ( myTeam >= 0 )
		{
			foreach ( var b in game.Balls )
			{
				if ( mi >= _tmapMates.Length ) break;
				if ( !b.IsValid() || !b.Alive || b == local ) continue;
				if ( b.TeamIndex != myTeam ) continue;

				var dot = _tmapMates[mi++];
				SetDisplay( dot, DisplayMode.Flex );
				var mc = MatchState.TeamColor( myTeam );
				if ( !SameColor( _tmapLastMateBg[mi - 1], mc ) )
				{
					_tmapLastMateBg[mi - 1] = mc;
					dot.Style.BackgroundColor = mc;
				}
				var p = b.WorldPosition;
				var px = ( half - p.y ) * scale - 6f;
				var py = ( half - p.x ) * scale - 6f;
				if ( MathF.Abs( px - _tmapMateX[mi - 1] ) > 0.5f || MathF.Abs( py - _tmapMateY[mi - 1] ) > 0.5f )
				{
					_tmapMateX[mi - 1] = px;
					_tmapMateY[mi - 1] = py;
					dot.Style.Left = px;
					dot.Style.Top = py;
				}
			}
		}
		for ( ; mi < _tmapMates.Length; mi++ )
		{
			SetDisplay( _tmapMates[mi], DisplayMode.None );
			_tmapMateX[mi] = -1f;
			_tmapMateY[mi] = -1f;
		}
	}
}