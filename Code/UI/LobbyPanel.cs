using System;
using System.Collections.Generic;
using Sandbox.UI;

/// <summary>
/// 游戏房间/大厅（M5 二级界面）：HOST / VS BOTS 点开后进入——host 可调比赛参数、看玩家列表、
/// 点 START 开局；客户端 JOIN 连上后进同一界面，参数与玩家列表由 host 经 LobbyState RPC 推送，
/// 显示"等待房主开始"，房主开局后经 MatchStarted RPC 自动进游戏。
/// 主菜单的设置区已移到这里（房间才是设置参数的地方）。
/// 纯 C# Panel + 同目录 LobbyPanel.cs.scss。
/// </summary>
public sealed class LobbyPanel : PanelComponent
{
	Label _status;
	Label[] _settings;       // 0=模式 1=时长 2=人数（host 可点击循环，client 只读展示）
	Label _settingHint;      // "CLICK A SETTING TO CYCLE IT"（仅 host，v0.7.8.0）
	Label[] _playerRows;     // 玩家列表：单列（滚动容器内，容量 = MaxPlayerRows）
	Panel _playersScroll;    // 玩家列表原生滚动容器（overflow-y + 滚轮）
	Label _botsNote;
	Label _start;
	Label _leave;
	Label _join;             // 客户端"JOIN GAME"按钮：对局进行中时显示，点击后 3-2-1 进入（v0.6.4.1）
	Label _countdown;        // 加入倒计时（客户端收到 host 开局指令后 3-2-1，v0.6.4.0）
	bool _matchRunning;      // 客户端视角：host 的对局进行中（JOIN GAME 按钮亮起）
	bool _modeGlowActive;    // 模式行呼吸色激活中（切回客户端身份时恢复 scss 基色一次）

	bool _built;
	bool _retryOpen;         // Panel 未就绪时的开门重试（EnsureWorld 会先于 OnStart 调用 ShowClient/ShowHost）
	bool _isHost;
	bool _shown;
	bool _connectionLost;    // 掉线重连中：状态行锁定提示（否则每帧 RefreshStatus 会覆盖掉）
	TimeSince _sinceListRefresh;

	// 客户端：房主推送的房间信息（LobbyState RPC）
	byte _clientMode;
	int _clientDuration;
	int _clientPlayers;
	List<string> _clientNames = new List<string>();

	const int MaxPlayerRows = 64;   // = MaxPlayers（sbproj），滚动列表装得下满房

	// 排版预览（2026-09-09 用户调样式用）：列表显示一批假玩家名。已调完关闭；下次预览再改 true
	// （static readonly 不用 const：const 的 true/false 都会把分支判成不可达代码，报 CS0162）
	static readonly bool ShowSamplePlayers = false;
	static readonly string[] SamplePlayers =
	{
		"FLYBIRD", "KITTEN_92", "小���鸟", "ProEater", "SNACKLORD", "汤圆", "Wobble", "XIAO_LONG",
		"Mochi", "大球王", "pixel_cat", "Doodle", "布丁", "GULP", "泥鳅", "Bubbles",
		"阿翔", "Tofu", "MUNCHER", "云朵",
	};

	protected override void OnStart()
	{
		base.OnStart();
		BuildUi();
		Hide();
	}

	void BuildUi()
	{
		var root = Panel;
		if ( root is null ) return;
		if ( _built && _status.IsValid() && root.ChildrenCount > 0 ) return;

		root.DeleteChildren( true );

		root.Style.Position = PositionMode.Absolute;
		root.Style.Left = 0f;
		root.Style.Top = 0f;
		root.Style.Right = 0f;
		root.Style.Bottom = 0f;
		root.Style.BackgroundColor = new Color( 0.918f, 0.957f, 0.988f, 0.82f );   // 浅蓝磨砂（像素风定稿；旧深灰遮罩在浅色世界上一坨脏灰，用户嫌丑）
		root.Style.PointerEvents = PointerEvents.All;

		UiKit.AttachStyles( root );   // 共享部件样式（.cr-pill 药丸按钮）

		var title = new Label() { Classes = "lp-title" };
		title.Text = "GAME LOBBY";
		root.AddChild( title );

		_status = new Label() { Classes = "lp-status" };
		root.AddChild( _status );

		var settingsHead = new Label() { Classes = "lp-head settings" };
		settingsHead.Text = "MATCH SETTINGS";
		root.AddChild( settingsHead );

		// 设置牌纵排（参考图：右栏三张横牌从上到下），host 可点击循环，client 只读展示
		_settings = new Label[3];
		for ( int i = 0; i < _settings.Length; i++ )
		{
			var l = new Label() { Classes = "lp-setting" };
			l.Style.Top = 330f + i * 88f;   // ★ 设置牌起点 + 行距（精修改这里）
			int index = i;
			l.AddEventListener( "onclick", () => CycleSetting( index ) );
			root.AddChild( l );
			_settings[i] = l;
		}

		// 设置可点提示（v0.7.8.0）：仅 host 可见——配合模式行呼吸金光，让玩家知道能改模式
		_settingHint = new Label() { Classes = "lp-setting-hint" };
		_settingHint.Text = "CLICK A SETTING TO CYCLE IT";
		root.AddChild( _settingHint );

		// 道具图例已按用户要求整个移除（2026-09-09）——Q/E 用法开局后 HUD 里还有提示

		var playersHead = new Label() { Classes = "lp-head players" };
		playersHead.Text = "PLAYERS";
		root.AddChild( playersHead );

		// 玩家列表容器底（dobou InventoryHotbar 九宫格，用户指定 2026-09-09）：
		// 先于行牌创建压在底层；空列表时也是一个有框的整洁面板，不再是裸留白
		var playersBg = new Panel() { Classes = "lp-players-bg" };
		root.AddChild( playersBg );

		// 玩家列表：单列流内排列装进原生滚动容器（overflow-y + 滚轮，网页式滚动条），
		// 容量提到 64（= MaxPlayers）；hotbar 底框只是装饰，滚动发生在它上面的透明容器里
		_playersScroll = new ScrollSoundPanel() { Classes = "lp-players-scroll" };
		root.AddChild( _playersScroll );

		_playerRows = new Label[MaxPlayerRows];
		for ( int i = 0; i < _playerRows.Length; i++ )
		{
			var row = new Label() { Classes = "lp-player" };
			_playersScroll.AddChild( row );
			_playerRows[i] = row;
		}

		_botsNote = new Label() { Classes = "lp-note" };
		_botsNote.Text = "BOTS FILL REMAINING SLOTS AT START";
		root.AddChild( _botsNote );

		// 加入倒计时（客户端）：收到 host 开局指令后显示 3-2-1
		_countdown = new Label() { Classes = "lp-countdown" };
		_countdown.Style.Display = DisplayMode.None;
		root.AddChild( _countdown );

		// 三个按钮全走 UiKit 药丸（外观在 Assets/ui/cruikit.scss 的 .cr-pill），这里只管摆放类
		_join = UiKit.PillButton( root, "JOIN GAME", "lp-join", () => OnJoinClicked() );

		_start = UiKit.PillButton( root, "START GAME", "lp-start", () => OnStartClicked() );

		_leave = UiKit.PillButton( root, "LEAVE", "lp-leave", () => OnLeaveClicked() );

		_built = true;
	}

	/// <summary> host 进入房间（HOST / VS BOTS 开房后调用） </summary>
	public void ShowHost()
	{
		_isHost = true;
		Open();
	}

	/// <summary> 客户端连入后进入房间（等待房主开局；列表等 LobbyState 推送） </summary>
	public void ShowClient()
	{
		_isHost = false;
		Open();
	}

	void Open()
	{
		BuildUi();
		if ( !_built )
		{
			_retryOpen = true;   // Panel 未就绪（EnsureWorld 立即调 ShowClient/ShowHost 时），OnUpdate 重试
			return;
		}

		_retryOpen = false;
		Panel.Style.Display = DisplayMode.Flex;
		if ( _shown )
		{
			// 已显示：不重置状态（ReconcileUi 每帧调，JOIN 按钮/倒计时不能被清掉）。
			// 但状态行要恢复——掉线重连成功回到房间时，"CONNECTION LOST" 不能一直挂着
			if ( !_isHost ) RefreshStatus();
			return;
		}
		_shown = true;
		_matchRunning = false;   // 每次进房间重置（MatchStarted 到达后再亮 JOIN 按钮）
		HideJoinCountdown();
		_sinceListRefresh = 1f;   // 首帧立即刷
		RefreshAll();
	}

	/// <summary> 收起（开局 / 回菜单） </summary>
	public void Hide()
	{
		_shown = false;
		_retryOpen = false;
		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.None;
	}

	/// <summary> 客户端应用房主推送的房间信息（CircleroyaleGame.OnLobbyStateRemote 转发） </summary>
	public void UpdateFromHost( byte mode, int duration, int players, string[] names )
	{
		_connectionLost = false;   // host 有消息 = 连接活着，解除掉线提示
		_clientMode = mode;
		_clientDuration = duration;
		_clientPlayers = players;
		_clientNames = names is not null ? new List<string>( names ) : new List<string>();

		if ( _shown && !_isHost )
		{
			RefreshSettings();
			RefreshPlayers();
			RefreshButtons();   // 掉线恢复后把常驻按钮摆回来（WAITING/JOIN 文案也靠它刷）
		}
	}

	/// <summary> 显示加入倒计时（CircleroyaleGame 每帧调用） </summary>
	public void ShowJoinCountdown( float secondsLeft )
	{
		if ( _countdown is null || !_countdown.IsValid() ) return;

		_countdown.Style.Display = DisplayMode.Flex;
		_countdown.Text = $"JOINING MATCH IN {MathF.Ceiling( MathF.Max( 0f, secondsLeft ) ):0}";

		// 倒计时期间收起 JOIN 按钮
		if ( _join.IsValid() ) _join.Style.Display = DisplayMode.None;
	}

	/// <summary> 收起加入倒计时 </summary>
	public void HideJoinCountdown()
	{
		if ( _countdown.IsValid() ) _countdown.Style.Display = DisplayMode.None;
	}

	/// <summary> 客户端视角：host 的对局进行中（房间页亮起 JOIN GAME 按钮） </summary>
	public void SetMatchRunning()
	{
		_matchRunning = true;
		RefreshButtons();

		SetText( _status, "MATCH IN PROGRESS — JOIN NOW" );
	}

	/// <summary> 文本防抖（v0.7.1.2）：值没变就不赋——Label.Text 每帧重设（哪怕同值）会触发布局
	/// 重算；ReconcileUi 每帧调 ShowClient→RefreshStatus 把状态行反复重刷 = UI 一闪一闪（实测） </summary>
	static void SetText( Label l, string text )
	{
		if ( !l.IsValid() || l.Text == text ) return;
		l.Text = text;
	}

	/// <summary> 掉线自动重连中（CircleroyaleGame.OnDisconnectRecoveryStarted）：状态行提示 + 收掉 JOIN 按钮 </summary>
	public void SetConnectionLost()
	{
		_connectionLost = true;
		SetText( _status, "CONNECTION LOST — RECONNECTING ..." );
		if ( _join.IsValid() ) _join.Style.Display = DisplayMode.None;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		BuildUi();

		// EnsureWorld 先于 OnStart 调过 ShowClient/ShowHost 且 Panel 未就绪 → 此刻补开
		if ( _retryOpen && !_shown )
		{
			Open();
			return;
		}

		if ( !_shown ) return;

		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.Flex;
		Mouse.Visibility = MouseVisibility.Visible;   // 按钮/设置项要能点

		// host：玩家进出、设置变化本地就能看到，0.5s 轮刷；client 列表靠 LobbyState 推送
		if ( _isHost && _sinceListRefresh > 0.5f )
		{
			_sinceListRefresh = 0;
			RefreshPlayers();
			RefreshStatus();
		}

		// 模式行呼吸金光（v0.7.8.0，仅 host）：设置行是可点的，用光效引导玩家去改模式。
		// 颜色动画每帧在变（Sin 连续），不碰 Text 不触发布局重算；客户端恢复静态基色
		if ( _settings is not null && _settings[0].IsValid() )
		{
			if ( _isHost )
			{
				var glow = 0.72f + 0.28f * MathF.Sin( Time.Now * 3.2f );
				_settings[0].Style.FontColor = new Color( 1f * glow, 0.76f * glow, 0.30f * glow );
				_modeGlowActive = true;
			}
			else if ( _modeGlowActive )
			{
				_settings[0].Style.FontColor = new Color( 0.29f, 0.23f, 0.39f );   // scss .lp-setting 基色（梅紫 #4a3b63）
				_modeGlowActive = false;
			}
		}
	}

	void RefreshAll()
	{
		RefreshSettings();
		RefreshPlayers();
		RefreshStatus();
		RefreshButtons();
	}

	void RefreshStatus()
	{
		if ( _isHost )
		{
			var names = CircleroyaleGame.Current?.LobbyPlayerNames();
			int n = names?.Count ?? 1;
			SetText( _status, $"ROOM OPEN — {n} PLAYER{( n > 1 ? "S" : "" )} IN LOBBY" );
		}
		else if ( _connectionLost )
		{
			SetText( _status, "CONNECTION LOST — RECONNECTING ..." );
		}
		else
		{
			SetText( _status, _matchRunning ? "MATCH IN PROGRESS — JOIN NOW" : "WAITING FOR HOST TO START ..." );
		}
	}

	void RefreshSettings()
	{
		if ( _settings is null || _settings[0] is null || !_settings[0].IsValid() ) return;

		byte mode;
		float duration;
		int players;

		if ( _isHost )
		{
			mode = (byte)MatchState.PendingMode;
			duration = MatchState.PendingDuration;
			players = MatchState.PendingPlayerTarget;
		}
		else
		{
			mode = _clientMode;
			duration = _clientDuration;
			players = _clientPlayers;
		}

		SetText( _settings[0], (MatchState.Mode)mode switch
		{
			MatchState.Mode.Ffa => "MODE — FREE FOR ALL",
			MatchState.Mode.Team2 => "MODE — TEAM (2 / TEAM)",
			MatchState.Mode.Team3 => "MODE — TEAM (3 / TEAM)",
			MatchState.Mode.Territory => "MODE — TERRITORY WAR",
			_ => "MODE — TEAM (4 / TEAM)",
		} );

		SetText( _settings[1], duration < 60f
			? $"TIME — {duration:0} SEC"
			: $"TIME — {duration / 60f:0} MIN" );

		// 领土模式人数固定 4 队 × 4 人（host Apply 强制 16），菜单选的 PLAYERS 无效——明示避免困惑
		SetText( _settings[2], (MatchState.Mode)mode == MatchState.Mode.Territory ? "PLAYERS — 16 (4 TEAMS)" : $"PLAYERS — {players}" );
	}

	void RefreshPlayers()
	{
		if ( _playerRows is null || _playerRows[0] is null || !_playerRows[0].IsValid() ) return;

		List<string> names;
		if ( _isHost )
		{
			names = CircleroyaleGame.Current?.LobbyPlayerNames() ?? new List<string>();
		}
		else
		{
			names = _clientNames;
		}

		if ( ShowSamplePlayers )
			names = new List<string>( SamplePlayers );   // 排版预览：假名替代真实列表（看完关掉）

		if ( names.Count == 0 ) names = new List<string> { "..." };

		for ( int i = 0; i < _playerRows.Length; i++ )
		{
			var row = _playerRows[i];
			if ( !row.IsValid() ) continue;

			if ( i >= names.Count )
			{
				SetText( row, "" );
				row.Style.Display = DisplayMode.None;   // 空位不画药片条（满屏空条很难看，实测截图）
				continue;
			}

			row.Style.Display = DisplayMode.Flex;
			var tag = i == 0 ? "  (HOST)" : "";
			SetText( row, $"{i + 1}.  {names[i]}{tag}" );
			row.Style.FontColor = i == 0 ? new Color( 0.898f, 0.282f, 0.553f ) : new Color( 0.29f, 0.23f, 0.39f, 0.85f );
		}
	}

	void RefreshButtons()
	{
		// host 有 START；客户端第二颗按钮常驻（用户需求 2026-09-09）：未开局显示 WAITING FOR HOST
		// （点了不响应），开局亮成 JOIN GAME。LEAVE 两边都有
		if ( _start.IsValid() )
			_start.Style.Display = _isHost ? DisplayMode.Flex : DisplayMode.None;

		if ( _join.IsValid() )
		{
			_join.Style.Display = !_isHost ? DisplayMode.Flex : DisplayMode.None;
			SetText( _join, _matchRunning ? "JOIN GAME" : "WAITING FOR HOST" );
		}

		// 设置可点提示（v0.7.8.0）：只有 host 能改设置，提示也只给 host
		if ( _settingHint.IsValid() )
			_settingHint.Style.Display = _isHost ? DisplayMode.Flex : DisplayMode.None;
	}

	/// <summary> JOIN GAME 点击（实例方法：热重载可重映射）。WAITING FOR HOST 状态点了不响应 </summary>
	void OnJoinClicked()
	{
		if ( !_matchRunning ) return;
		CircleroyaleGame.Current?.ClientJoinMatch();
	}

	/// <summary> START 点击 </summary>
	void OnStartClicked() => CircleroyaleGame.Current?.StartMatchFromLobby();

	/// <summary> LEAVE 点击 </summary>
	void OnLeaveClicked() => CircleroyaleGame.Current?.ResetToMenu();

	// ---- host 调参（与主菜单时代的循环逻辑一致）----

	void CycleSetting( int index )
	{
		if ( !_isHost ) return;

		GameSfx.UiClick();   // 点了真会改参数才响（client 只读，点了不响）

		switch ( index )
		{
			case 0:
				// 模式 5 档循环（v0.7.8.34）：普通赛 → 2 人队 → 3 人队 → 4 人队 → 领土战争
				MatchState.PendingMode = (MatchState.Mode) ( ( (int)MatchState.PendingMode + 1 ) % 5 );
				break;

			case 1:
				MatchState.PendingDuration = NextChoice( GameConfig.MatchDurationChoices, MatchState.PendingDuration );
				break;

			case 2:
				MatchState.PendingPlayerTarget = NextIntChoice( GameConfig.PlayerTargetChoices, MatchState.PendingPlayerTarget );
				break;
		}

		RefreshSettings();
		CircleroyaleGame.Current?.MarkLobbyDirty();   // 让等待中的客户端看到新参数
	}

	static float NextChoice( float[] choices, float current )
	{
		for ( int i = 0; i < choices.Length; i++ )
		{
			if ( MathF.Abs( choices[i] - current ) < 0.5f )
				return choices[( i + 1 ) % choices.Length];
		}
		return choices[0];
	}

	static int NextIntChoice( int[] choices, int current )
	{
		for ( int i = 0; i < choices.Length; i++ )
		{
			if ( choices[i] == current )
				return choices[( i + 1 ) % choices.Length];
		}
		return choices[0];
	}
}
