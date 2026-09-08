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
	Label _legendHead;       // 道具图例标题（v0.7.8.2）
	Label[] _legendNames;    // 图例行：色点+道具名（种类色）
	Label[] _legendDescs;    //   一句话说明（灰白）
	Label _legendNote;       // 获取方式提示
	Label[] _playerRows;     // 玩家列表：2 列 × 12 行
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

	const int RowsPerCol = 12;

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
		root.Style.BackgroundColor = new Color( 0.008f, 0.012f, 0.05f, 0.55f );   // 半透明：网格/背景对战透出来（用户反馈"黑幕"）
		root.Style.PointerEvents = PointerEvents.All;

		var title = new Label() { Classes = "lp-title" };
		title.Text = "GAME LOBBY";
		root.AddChild( title );

		_status = new Label() { Classes = "lp-status" };
		root.AddChild( _status );

		var settingsHead = new Label() { Classes = "lp-head" };
		settingsHead.Text = "MATCH SETTINGS";
		root.AddChild( settingsHead );

		_settings = new Label[3];
		string[] ids = { "set-mode", "set-time", "set-players" };
		for ( int i = 0; i < _settings.Length; i++ )
		{
			var l = new Label() { Classes = $"lp-setting {ids[i]}" };
			int index = i;
			l.AddEventListener( "onclick", () => CycleSetting( index ) );
			root.AddChild( l );
			_settings[i] = l;
		}

		// 设置可点提示（v0.7.8.0）：仅 host 可见——配合模式行呼吸金光，让玩家知道能改模式
		_settingHint = new Label() { Classes = "lp-setting-hint" };
		_settingHint.Text = "CLICK A SETTING TO CYCLE IT";
		root.AddChild( _settingHint );

		// 道具图例（v0.7.8.2）：玩家列表左侧空列，色点+名称+一句话说明——开局前学习道具
		_legendHead = new Label() { Classes = "lp-legend-head" };
		_legendHead.Text = "POWERUPS";
		root.AddChild( _legendHead );

		byte[] legendKinds = { (byte)PowerUpManager.Kind.Speed, (byte)PowerUpManager.Kind.Magnet,
			(byte)PowerUpManager.Kind.Shield, (byte)PowerUpManager.Kind.Spike, (byte)PowerUpManager.Kind.Mass };
		string[] legendDescs =
		{
			"8s move boost",
			"8s double food range",
			"5s can't be eaten (not spikes)",
			"burst bigger balls = 3s invincible",
			"instant +300 mass",
		};

		_legendNames = new Label[legendKinds.Length];
		_legendDescs = new Label[legendKinds.Length];
		for ( int i = 0; i < legendKinds.Length; i++ )
		{
			var kind = legendKinds[i];
			var rowTop = 690f + i * 34f;   // 左下角图例（v0.7.8.4 用户定稿：红箭头指向的位置）

			var name = new Label() { Classes = "lp-legend-name" };
			name.Text = $"■ {PowerUpManager.NameOf( kind )}";
			name.Style.FontColor = PowerUpManager.ColorOf( kind );
			name.Style.Top = rowTop;
			root.AddChild( name );
			_legendNames[i] = name;

			var desc = new Label() { Classes = "lp-legend-desc" };
			desc.Text = legendDescs[i];
			desc.Style.Top = rowTop + 2f;
			root.AddChild( desc );
			_legendDescs[i] = desc;
		}

		_legendNote = new Label() { Classes = "lp-legend-note" };
		_legendNote.Text = "PICK UP OR SPIT AT IT — USE WITH Q / E";
		root.AddChild( _legendNote );

		var playersHead = new Label() { Classes = "lp-head players" };
		playersHead.Text = "PLAYERS";
		root.AddChild( playersHead );

		// 玩家列表 2 列 × 12 行（左列 0..11，右列 12..23）
		_playerRows = new Label[RowsPerCol * 2];
		for ( int i = 0; i < _playerRows.Length; i++ )
		{
			var row = new Label() { Classes = "lp-player" };
			row.Style.Position = PositionMode.Absolute;
			row.Style.Left = i < RowsPerCol ? 560f : 1010f;
			row.Style.Width = 440f;
			row.Style.Top = 384f + ( i % RowsPerCol ) * 30f;
			root.AddChild( row );
			_playerRows[i] = row;
		}

		_botsNote = new Label() { Classes = "lp-note" };
		_botsNote.Text = "BOTS FILL REMAINING SLOTS AT START";
		root.AddChild( _botsNote );

		// 加入倒计时（客户端）：收到 host 开局指令后显示 3-2-1
		_countdown = new Label() { Classes = "lp-countdown" };
		_countdown.Style.Display = DisplayMode.None;
		root.AddChild( _countdown );

		// JOIN GAME（客户端）：对局进行中时点此进入（3-2-1 倒计时）
		_join = new Label() { Classes = "lp-join" };
		_join.Text = "JOIN GAME";
		_join.AddEventListener( "onclick", () => OnJoinClicked() );
		root.AddChild( _join );

		_start = new Label() { Classes = "lp-start" };
		_start.Text = "START GAME";
		_start.AddEventListener( "onclick", () => OnStartClicked() );
		root.AddChild( _start );

		_leave = new Label() { Classes = "lp-leave" };
		_leave.Text = "LEAVE";
		_leave.AddEventListener( "onclick", () => OnLeaveClicked() );
		root.AddChild( _leave );

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
				_settings[0].Style.FontColor = new Color( 0.604f, 0.847f, 1f );   // scss .lp-setting 基色
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
			_ => "MODE — TEAM (4 / TEAM)",
		} );

		SetText( _settings[1], duration < 60f
			? $"TIME — {duration:0} SEC"
			: $"TIME — {duration / 60f:0} MIN" );

		SetText( _settings[2], $"PLAYERS — {players}" );
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

		if ( names.Count == 0 ) names = new List<string> { "..." };

		for ( int i = 0; i < _playerRows.Length; i++ )
		{
			var row = _playerRows[i];
			if ( !row.IsValid() ) continue;

			if ( i >= names.Count )
			{
				SetText( row, "" );
				continue;
			}

			var tag = i == 0 ? "  (HOST)" : "";
			SetText( row, $"{i + 1}.  {names[i]}{tag}" );
			row.Style.FontColor = i == 0 ? new Color( 0.21f, 0.94f, 1f ) : new Color( 1f, 1f, 1f, 0.75f );
		}
	}

	void RefreshButtons()
	{
		// host 有 START；客户端有 JOIN GAME（对局进行中才亮）。LEAVE 两边都有
		if ( _start.IsValid() )
			_start.Style.Display = _isHost ? DisplayMode.Flex : DisplayMode.None;

		if ( _join.IsValid() )
			_join.Style.Display = !_isHost && _matchRunning ? DisplayMode.Flex : DisplayMode.None;

		// 设置可点提示（v0.7.8.0）：只有 host 能改设置，提示也只给 host
		if ( _settingHint.IsValid() )
			_settingHint.Style.Display = _isHost ? DisplayMode.Flex : DisplayMode.None;
	}

	/// <summary> JOIN GAME 点击（实例方法：热重载可重映射） </summary>
	void OnJoinClicked() => CircleroyaleGame.Current?.ClientJoinMatch();

	/// <summary> START 点击 </summary>
	void OnStartClicked() => CircleroyaleGame.Current?.StartMatchFromLobby();

	/// <summary> LEAVE 点击 </summary>
	void OnLeaveClicked() => CircleroyaleGame.Current?.ResetToMenu();

	// ---- host 调参（与主菜单时代的循环逻辑一致）----

	void CycleSetting( int index )
	{
		if ( !_isHost ) return;

		switch ( index )
		{
			case 0:
				// 模式 4 档循环（v0.6.5.0）：普通赛 → 2 人队 → 3 人队 → 4 人队
				MatchState.PendingMode = (MatchState.Mode) ( ( (int)MatchState.PendingMode + 1 ) % 4 );
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
