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
	Label _slotQ;                           // 背包槽显示（右下角，v0.7.5.0 主动使用）：[Q] 道具名
	Label _slotE;                           //   [E] 道具名
	byte _slotQShown = 255;                 // 上次渲染的槽内容（颜色只在种类切换时赋值）
	byte _slotEShown = 255;
	Label _version;
	Label[] _rows;
	Label _selfRow;
	Label _deathTitle;
	Label _deathSub;
	Label _matchInfo;                       // 顶部中央：模式 + 倒计时（M5）
	Label[] _teamRows;                      // 队伍总分条（团队赛，M5）
	Label[] _mateRows;                      // 本队成员积分（排行榜下方，团队赛，v0.6.5.0）：0=队头 1..4=成员
	Dictionary<Guid, TagSlot> _tagMap = new();
	Dictionary<int, TagSlot> _pieceTagMap = new();   // 分身名牌：Key = 分身 Id（归属名字经主人球解析）
	List<TagSlot> _tagPool;
	readonly List<KillFeedEntry> _killFeed = new();
	readonly List<Banner> _banners = new();          // 个人高光横幅（中下艺术字，v0.7.6.0）
	bool _built;

	/// <summary> 个人高光横幅条目（击杀/捡道具/放技能）：2.1s 生命周期，0.3s 弹入 + 1.2s 停留 + 0.6s 淡出 </summary>
	sealed class Banner
	{
		public Label Label;
		public TimeSince Since;
		public float ShownBottom = float.MinValue;
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
		"Pickups store into your Q / E slots — press the key to use",
		"Spit mass AT a pickup to feed it — trigger at 5% of your weight",
		"Small balls feed cheap, big balls feed dear — steal half-fed pickups",
		"Spike minion: ram it into a bigger ball — burst = 3s invincible",
		"Spike minion reverts to a normal cell after 20s (mass refunded)",
		"Shield blocks being eaten, not spikes — don't hug spikes with it",
		"Magnet: 8s of double food pickup radius",
		"Big balls must drive OVER pickups to grab them — smalls grab easy",
		"Main ball eaten? Biggest cell takes over — lose all cells and you're out",
		"P: bail out of the match back to the lobby",
		"Bigger = clumsier turning — plan your moves ahead",
		"Team mode: teammates can't eat each other — stick together",
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

		// 背包槽显示（v0.7.5.0 主动使用）：右下角两格，[Q]/[E] + 道具名（种类配色）
		_slotQ = new Label() { Classes = "slotq" };
		_slotE = new Label() { Classes = "slote" };
		root.AddChild( _slotQ );
		root.AddChild( _slotE );

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
		UpdatePowerSlots( game );
		UpdateBanners();
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

			Place( slot, b.WorldPosition, TagText( b.PlayerName, b.TeamIndex ) );
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

			Place( slot, new Vector3( c.DrawPos.x, c.DrawPos.y, 0f ), TagText( owner.PlayerName, owner.TeamIndex ) );
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

		if ( over )
		{
			for ( int i = 0; i < _rows.Length; i++ )
			{
				if ( _rows[i].IsValid() ) _rows[i].Text = "";
			}
			if ( _selfRow.IsValid() ) _selfRow.Style.Display = DisplayMode.None;
			return;
		}

		// 按总质量（主球+分身）降序；球数 ≤ BotFillTarget，拷贝排序的开销可忽略
		var list = new List<Ball>();
		foreach ( var b in game.Balls )
		{
			if ( b.IsValid() && b.Alive ) list.Add( b );
		}
		var massOf = new Dictionary<Ball, float>();
		foreach ( var b in list )
			massOf[b] = game.TotalMassFor( b );
		list.Sort( ( x, y ) => massOf[y].CompareTo( massOf[x] ) );

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
			SetText( row, TeamModeRow( $"{i + 1}.", ball.PlayerName, $"{massOf[ball]:0}", Math.Max( 0, ball.TeamIndex ) ) );
			row.Style.FontColor = isSelf ? new Color( 0.21f, 0.94f, 1f ) : new Color( 1f, 1f, 1f, 0.55f );
		}

		// 榜外自身行
		var showSelf = selfRank >= _rows.Length;
		if ( _selfRow.IsValid() )
		{
			_selfRow.Style.Display = showSelf ? DisplayMode.Flex : DisplayMode.None;
			if ( showSelf )
				SetText( _selfRow, TeamModeRow( $"#{selfRank + 1}.", local.PlayerName, $"{massOf[local]:0}", Math.Max( 0, local.TeamIndex ) ) );
		}
	}

	/// <summary> 死亡面板点击重生（实例方法：热重载可重映射） </summary>
	void OnDeathSubClicked() => CircleroyaleGame.Current?.TryRespawnLocal();

	/// <summary>
	/// 本队成员积分（团队赛，排行榜下方，v0.6.5.0）：队头 "MY TEAM [T#]" + 成员按质量降序，
	/// 自己那行金色高亮。普通赛 / 没有本机球 / 结算期整块隐藏。
	/// </summary>
	void UpdateMates( CircleroyaleGame game, bool over )
	{
		if ( _mateRows is null || _mateRows[0] is null || !_mateRows[0].IsValid() ) return;

		var local = game.LocalBall;
		bool show = !over && MatchState.IsTeam && local.IsValid();

		if ( !show )
		{
			foreach ( var r in _mateRows )
			{
				if ( r.IsValid() ) r.Style.Display = DisplayMode.None;
			}
			return;
		}

		int myTeam = Math.Max( 0, local.TeamIndex );

		// 本队存活成员按总质量降序（按块分队后每队 ≤ TeamSize ≤ 4 人，直接收集排序）
		var mates = new List<Ball>();
		foreach ( var b in game.Balls )
		{
			if ( b.IsValid() && b.Alive && b.TeamIndex == myTeam ) mates.Add( b );
		}
		var massOf = new Dictionary<Ball, float>();
		foreach ( var m in mates )
			massOf[m] = game.TotalMassFor( m );
		mates.Sort( ( x, y ) => massOf[y].CompareTo( massOf[x] ) );

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
			row.Style.FontColor = self ? new Color( 1f, 0.85f, 0.3f ) : new Color( 1f, 1f, 1f, 0.75f );
		}
	}

	/// <summary> 名牌文本（团队赛带 [T#] 队伍前缀，v0.6.5.0 用户需求） </summary>
	string TagText( string name, int team ) =>
		MatchState.IsTeam && team >= 0 ? $"[T{team + 1}] {name}" : name;

	/// <summary> 排行榜行文本（团队赛带 T# 前缀；普通赛只有名次/名字/分数） </summary>
	string TeamModeRow( string rank, string name, string mass, int team )
	{
		return MatchState.IsTeam ? $"{rank} [T{team + 1}] {name}  {mass}" : $"{rank}  {name}  {mass}";
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
		var mode = MatchState.IsTeam ? "TEAM BATTLE" : "FREE FOR ALL";
		SetText( _matchInfo, $"{mode}   {total / 60}:{total % 60:00}" );

		// 最后 30 秒变红提示
		_matchInfo.Style.FontColor = total < 30 ? new Color( 1f, 0.35f, 0.45f ) : new Color( 1f, 1f, 1f, 0.85f );

		if ( MatchState.IsTeam ) UpdateTeamScores( game );
		else HideTeamRows();
	}

	/// <summary> 团队赛队伍总分条：前 5 名队伍 + 本队（不在前 5 时补一条），按总分降序 </summary>
	void UpdateTeamScores( CircleroyaleGame game )
	{
		var totals = new Dictionary<int, float>();
		foreach ( var b in game.Balls )
		{
			if ( !b.IsValid() || !b.Alive ) continue;
			var team = Math.Max( 0, b.TeamIndex );
			totals.TryGetValue( team, out var m );
			totals[team] = m + game.TotalMassFor( b );
		}

		var order = new List<int>( totals.Keys );
		order.Sort( ( x, y ) => totals[y].CompareTo( totals[x] ) );

		var local = game.LocalBall;
		var myTeam = local.IsValid() ? Math.Max( 0, local.TeamIndex ) : -1;

		var show = new List<int>();
		foreach ( var t in order )
		{
			if ( show.Count >= 5 ) break;
			show.Add( t );
		}
		if ( myTeam >= 0 && totals.ContainsKey( myTeam ) && !show.Contains( myTeam ) )
			show.Add( myTeam );
		show.Sort( ( x, y ) => totals[y].CompareTo( totals[x] ) );

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
			SetText( row, $"T{team + 1}  {totals[team]:0}" );

			bool mine = team == myTeam;
			row.Style.FontColor = mine ? new Color( 1f, 1f, 1f ) : MatchState.TeamColor( team );
			row.Style.Opacity = mine ? 1f : 0.75f;
		}
	}

	void HideTeamRows()
	{
		if ( _teamRows is null ) return;
		foreach ( var r in _teamRows )
		{
			if ( r.IsValid() ) r.Style.Display = DisplayMode.None;
		}
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

		// 自上而下重排（老条目删除后不串位）+ 尾段淡出
		for ( int i = 0; i < _killFeed.Count; i++ )
		{
			var e = _killFeed[i];
			e.Label.Style.Top = 68f + i * 26f;
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
	/// 背包槽显示（v0.7.5.0 主动使用）：右下角 [Q]/[E] 两格——持有时显示道具名（种类色），
	/// 空槽显示键位暗淡。文本防抖；颜色只在种类切换时赋值。
	/// </summary>
	void UpdatePowerSlots( CircleroyaleGame game )
	{
		if ( _slotQ is null || !_slotQ.IsValid() || _slotE is null || !_slotE.IsValid() ) return;

		var ball = game.LocalBall;
		if ( !ball.IsValid() )
		{
			if ( _slotQShown != 255 || _slotEShown != 255 )
			{
				SetText( _slotQ, "" );
				SetText( _slotE, "" );
				_slotQShown = 255;
				_slotEShown = 255;
			}
			return;
		}

		DrawSlot( _slotQ, ball.PowerA, "[Q] ", ref _slotQShown );
		DrawSlot( _slotE, ball.PowerB, "[E] ", ref _slotEShown );
	}

	void DrawSlot( Label label, byte kind, string keyPrefix, ref byte shownKind )
	{
		if ( shownKind == kind ) return;
		shownKind = kind;

		if ( kind == 255 )
		{
			SetText( label, $"{keyPrefix}— " );
			label.Style.FontColor = new Color( 1f, 1f, 1f, 0.22f );
			return;
		}

		SetText( label, $"{keyPrefix}{PowerUpManager.NameOf( kind )} " );
		label.Style.FontColor = PowerUpManager.ColorOf( kind );
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
		if ( _deathTitle.IsValid() )
			_deathTitle.Style.Display = dead ? DisplayMode.Flex : DisplayMode.None;
		if ( _deathSub.IsValid() )
			_deathSub.Style.Display = dead ? DisplayMode.Flex : DisplayMode.None;
	}
}