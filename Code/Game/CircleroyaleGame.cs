using System;
using System.Collections.Generic;

/// <summary>
/// CircleRoyale 主系统：运行时生成整个世界（相机/HUD/背景/球），承载 host 权威逻辑。
/// GameObjectSystem 会在场景加载时自动实例化（无需挂载到场景），但编辑器的编辑模式下
/// 也会 tick，所以所有游戏逻辑都用 Game.IsPlaying 拦截。模板场景对象会被整体屏蔽。
/// </summary>
public sealed class CircleroyaleGame : GameObjectSystem<CircleroyaleGame>
{
	GameObject _root;
	NeonCamera _camera;
	GameHud _hud;
	EndBoard _endBoard;
	LobbyPanel _lobby;
	RoomBrowser _browser;
	SettingsPanel _settings;
	RankingsPanel _rankings;
	FoodManager _food;
	PowerUpManager _power;
	NetworkManager _net;
	MainMenu _menu;

	readonly List<Ball> _balls = new();
	readonly Dictionary<Guid, Vector3> _lastPos = new();   // host 位移校验用
	readonly Dictionary<Guid, TimeSince> _teleportGrace = new();   // host：合法瞬移（重生换位/晋升）后的校验宽限期
	TimeSince _sinceScan = 1f;         // 球注册扫描
	TimeSince _sinceFoodRequest = 1f;  // 客户端请求食物全量的节流（首帧即请求）
	TimeSince _sinceMatchTick = 1f;    // 倒计时 1Hz 校时广播
	TimeSince _sinceTerritoryWire = 1f;   // 领土状态变化广播节流（5Hz 上限 + 2s 保活）
	bool _pendingFrontRespawn;            // 本机下次复活的前线偏好（领土 M7.2，owner 换位时消费）
	int _botNameIndex;
	int _teamSeq;                      // 团队赛轮转分队计数（开局重置）

	// ---- 分身/孢子（M3，纯数据实体见 CellPiece）----
	readonly List<CellPiece> _cells = new();                    // host 权威模拟；客户端镜像（CellsState RPC）
	readonly Dictionary<Guid, Vector2> _steerDir = new();       // host：每球平滑转向（分身跟随用）
	readonly Dictionary<long, int> _teamScratch = new();        // 团队赛阵营映射复用缓冲（每帧 3 处先后构建，省堆分配）
	readonly Dictionary<int, CellWire> _wireById = new();       // CellsState 应用缓冲（按 Id O(1) 查找）
	readonly HashSet<int> _cellIdScratch = new();               // CellsState 新增判定缓冲
	readonly Dictionary<Guid, TimeSince> _sinceEject = new();   // host：吐孢子节流
	TimeSince _sinceCellSync = 1f;
	TimeSince _sinceCellHeal;        // 分身主人失联自愈的 ScanBalls 节流（独立计时，别挪用 _sinceScan 打乱球扫描节奏）
	bool _cellsDirty;                                           // 增删标记（列表清空后还要补发一次空快照）
	int _nextCellId = 1;
	Vector2[] _spikes;                                          // 绿刺位置（host 生成；客户端经 SpikesFull 同步）
	float[] _spikeFed;                                          // 每朵刺累计喂入质量（host 权威；客户端 SpikeFed RPC 镜像，v0.7.8.24）
	readonly List<SpikeSpine> _spines = new();                  // 刺爆弹幕：host=权威模拟（含命中），客户端=纯视觉弹道（v0.7.8.24）

	/// <summary> 全部球（真人 + bot） </summary>
	public IReadOnlyList<Ball> Balls => _balls;

	/// <summary> 本地球（M2 起：真人=自己那颗，bot 无） </summary>
	public Ball LocalBall { get; private set; }

	/// <summary> 本地玩家是否处于死亡待重生状态（HUD 显示死亡面板） </summary>
	public bool IsLocalDead => _worldReady && LocalBall.IsValid() && !LocalBall.Alive;

	/// <summary> 食物管理（渲染与 AI 读） </summary>
	public FoodManager Food => _food;

	/// <summary> 道具管理（渲染读；host 权威，客户端为事件镜像） </summary>
	public PowerUpManager PowerUps => _power;

	/// <summary> 全部分身/孢子（渲染读；host 权威，客户端为 15Hz 快照镜像） </summary>
	public IReadOnlyList<CellPiece> Cells => _cells;

	/// <summary> 绿刺位置（静态障碍；host 生成，客户端经 SpikesFull 全量同步） </summary>
	public Vector2[] Spikes => _spikes;

	/// <summary> 刺的累计喂食量（0~SpikeGrowTarget，渲染长大用；host 权威，客户端经 SpikeFed RPC 镜像） </summary>
	public float SpikeFedAt( int index ) =>
		_spikeFed is not null && (uint)index < (uint)_spikeFed.Length ? _spikeFed[index] : 0f;

	/// <summary> 刺爆弹幕（渲染读；host 权威模拟含命中，客户端为纯视觉弹道） </summary>
	public IReadOnlyList<SpikeSpine> Spines => _spines;

	/// <summary> HUD（提供屏幕光标桥接等） </summary>
	public GameHud Hud => _hud;

	/// <summary> 跟随相机（屏幕震动触发入口） </summary>
	public NeonCamera Camera => _camera;

	/// <summary> 房间列表页（主菜单 [2] 打开，点行加入） </summary>
	public RoomBrowser Browser => _browser;

	/// <summary> host：房间开着（等 START）；此阶段玩家可调参/进出，比赛尚未生成 </summary>
	public bool IsLobbyOpen => _lobbyOpen;

	/// <summary> 比赛是否已开始（客户端区分"进房间等"还是"直接进游戏"） </summary>
	public bool IsMatchStarted => _gameStarted;

	/// <summary> 客户端视角：正在会话里（房间等待/对局/加入倒计时中）——掉线自动重连判定用 </summary>
	public bool IsClientInSession => _lobbyWaiting || _gameStarted || _matchRunning || _joinCountdown >= 0f;

	/// <summary> 客户端是否停在等待页（P 退局/掉线恢复中）——本机球停止操控（球留在原地，防隔屏盲开） </summary>
	public bool IsClientWaiting => _lobbyWaiting;

	/// <summary>
	/// 客户端进入房间等待态（所有入口统一走这里，v0.7.1.1）：
	/// 置标志 + **重置/解除房间心跳**——进入等待的时刻 host 不一定在房间阶段（可能对局进行中，
	/// 没有名单广播），旧计时器/武装状态会立刻误判超时，必须等 host 的下一条 LobbyState 重新武装。
	/// </summary>
	void EnterClientWaiting()
	{
		if ( _lobbyWaiting ) return;   // 已在等待态：别重置计时（否则心跳永远判不超时）

		_lobbyWaiting = true;
		_lobbyHeartbeatArmed = false;
		_sinceLobbyHeartbeat = 0;
	}

	static GameObject _sharedRoot;   // 热重载/重建时复用同一个世界根，避免堆叠多套场景

	bool _worldReady;
	bool _networkReady;
	bool _gameStarted;              // 比赛进行中（模拟门控）；房间/菜单阶段 false
	bool _menuDemo;                 // 主菜单背景演示赛：authority 离线跑一场纯 bot 对战（v0.6.3.0）
	bool _lobbyOpen;                // host：房间开着（还没点 START）
	bool _lobbyWaiting;             // client：已连入，在房间里等房主开局
	bool _joinRequested;            // 客户端点了"加入"：等会话以客户端身份激活（连接前几帧 IsAuthority 不可信）
	TimeSince _sinceJoinRequest;
	TimeSince _sinceMatchOver;      // 结算显示计时（10 秒后自动回大厅/主菜单）
	bool _lobbyListDirty;           // 房间玩家名单变了（主线程下一拍广播 LobbyState）
	byte _lobbyMode;                // client：房主推送的房间参数（LobbyState RPC）
	int _lobbyDuration;
	int _lobbyPlayers;
	string[] _lobbyNames = System.Array.Empty<string>();
	float _joinCountdown = -1f;     // 客户端加入倒计时（≥0 时房间页显示 3-2-1，归零进入对局）
	bool _matchRunning;             // 客户端视角：host 的对局进行中（房间页亮 JOIN GAME 按钮）

	// host 心跳（v0.6.8.0）：host 房间阶段 3s 广播一次名单当心跳；客户端收到归零，
	// 静默超时 = host 死了（进程被杀时引擎会话可能不自动失效），强制拆线走重连
	TimeSince _sinceLobbyPing = 999f;   // host：房间心跳节拍
	bool _lobbyHeartbeatArmed;          // client：收到过第一次名单后才武装（防刚进房就误判）
	TimeSince _sinceLobbyHeartbeat;

	public CircleroyaleGame( Scene scene ) : base( scene )
	{
		Listen( Stage.UpdateBones, 0, Tick, "CircleroyaleGame.Tick" );
	}

	void Tick()
	{
		if ( !Game.IsPlaying ) return;

		EnsureWorld();

		// 客户端：会话激活但世界还在菜单演示态（断线重连时 EnsureWorld 在断线期把分支跑成了
		// 演示赛，且重连不会置 _joinRequested）→ 收演示赛，回到会话加入流
		if ( !NetworkManager.IsAuthority && Networking.IsActive && _menuDemo )
			StopMenuDemo();

		// 客户端：在会话里但哪个界面都不在（重连/热重载后）→ 回房间等待并拉当前状态
		if ( !NetworkManager.IsAuthority && Networking.IsActive && !_gameStarted
			&& !_lobbyWaiting && _joinCountdown < 0f )
		{
			EnterClientWaiting();
			_menu?.Hide();
			_browser?.Hide();
			_lobby?.ShowClient();
			NetworkManager.RequestMatchState();
		}

		// 客户端：Replicated 的 cr_arena_size 由引擎落到 ConvarArenaSize，这里同步到 ArenaHalfSize。
		// host 在 OnNetworkReady 做同款钳制；此前客户端从不赋值——网格/边界/钳制一直用静态默认值，
		// 场地改 3 倍后与 host 实测脱节（host 2048 / 客户端 6144，客户端网格大了 3 倍）
		if ( !NetworkManager.IsAuthority )
			GameConfig.ArenaHalfSize = Math.Clamp( GameConfig.ConvarArenaSize, 1024f, 12288f );

		// 客户端加入流：点了"加入"后等会话以客户端身份激活。
		// ⚠️ 不能点完就信 IsAuthority——连接建立前的几帧 IsActive=false，IsAuthority 恒为 true
		if ( _joinRequested && !_gameStarted )
		{
			if ( Networking.IsActive && !Networking.IsHost )
			{
				GameLog.Info( "[menu] join: connected as client, entering room" );
				_joinRequested = false;
				EnterClientWaiting();
				_menu?.Hide();
				_browser?.Hide();   // 房间列表页收起（从列表点进来的正常收尾）
				_lobby?.ShowClient();
				NetworkManager.RequestMatchState();
			}
			else if ( _sinceJoinRequest > 12f )
			{
				_joinRequested = false;
				_menu?.SetStatus( "JOIN FAILED — IS THE HOST UP? PRESS [2] TO RETRY" );
				_browser?.SetStatus( "JOIN TIMED OUT — PICK ANOTHER ROOM OR REFRESH" );
			}
		}

		// 客户端在会话界面里连接断开：**不立刻回菜单**——界面留在原地由 NetworkManager
		// 自动重连（TickDisconnectRecovery），重试耗尽回调 OnReconnectFailed 才回主菜单（v0.6.4.6）

		// 房间名单/参数有变（网络线程只置脏标，主线程统一广播 LobbyState）
		if ( _lobbyOpen && _lobbyListDirty )
		{
			_lobbyListDirty = false;
			BroadcastLobbyState();
		}

		// host 房间心跳（v0.6.8.0）：名单没变也定时广播，客户端靠它判断"host 还活着"
		if ( _lobbyOpen && Networking.IsActive && _sinceLobbyPing > 3f )
		{
			_sinceLobbyPing = 0;
			BroadcastLobbyState();
		}

		// host 心跳超时（客户端，v0.6.8.0）：host 进程被杀时引擎会话可能不自动失效
		// （僵尸态，IsActive 恒 true，掉线恢复循环不触发）——对局/房间各自有心跳，
		// 静默超时主动拆线，交给 TickDisconnectRecovery（重连耗尽回主菜单）
		// v0.7.1.1 修：两条心跳**按 host 实际阶段取一**——host 在对局阶段不发名单广播，
		// 等在房间页的客户端不能拿"名单静默"误判（此前中途加入 6s 即被踢回主菜单）；
		// 对局心跳（MatchTick 1Hz）反过来也覆盖"客户端在房间页等、host 对局还在跑"的情况
		if ( !NetworkManager.IsAuthority && Networking.IsActive && GameConfig.HostLivenessTimeout > 0f )
		{
			bool matchSilent = ( _gameStarted || _matchRunning ) && !MatchState.MatchOver
				&& MatchState.SinceLastTick > GameConfig.HostLivenessTimeout;
			bool lobbySilent = _lobbyWaiting && _lobbyHeartbeatArmed && !_matchRunning
				&& _sinceLobbyHeartbeat > GameConfig.HostLivenessTimeout;

			if ( matchSilent || lobbySilent )
			{
				Log.Warning( "[net] host silent beyond liveness timeout — forcing disconnect" );
				try { Networking.Disconnect(); } catch { }

				// 对局已死：立刻退出对局态，恢复期停在房间页（CONNECTION LOST），
				// 不再对着冻结画面干等（_gameStarted 挂着会让 HUD/冻结对局一直占屏）
				_gameStarted = false;
				_matchRunning = false;
				_joinCountdown = -1f;
				_lobbyHeartbeatArmed = false;
				_lobbyWaiting = true;   // 保住 IsClientInSession：掉线恢复循环要靠它启动
				LocalBall = null;
			}
		}

		// 音乐循环（菜单/战斗都要）：音量跟随 convar + 曲终自动重播
		GameMusic.Tick();

		ReconcileUi();

		// P 键快速退局（v0.6.9.0 用户需求）：对局中直接返回大厅——host 广播取消让全员一起回房，
		// 客户端自己回等待页；单机（无会话）回主菜单。结算期不受理（EndBoard 自动返回在管）
		if ( _gameStarted && !MatchState.MatchOver && Input.Keyboard.Pressed( "P" ) )
		{
			GameLog.Info( "[match] quit to lobby via P" );
			if ( Networking.IsActive && NetworkManager.IsAuthority )
				NetworkManager.MatchCancelled();
			AfterSettlement();
			return;
		}

		// 客户端加入倒计时（v0.6.4.0 用户定稿）：MatchStarted 后房间页 3-2-1，归零进入对局
		if ( _joinCountdown >= 0f )
		{
			_joinCountdown -= Time.Delta;
			_lobby?.ShowJoinCountdown( MathF.Max( 0f, _joinCountdown ) );
			if ( _joinCountdown < 0f )
			{
				_lobby?.HideJoinCountdown();
				OnMatchStartedLocal();
			}
		}

		// 菜单阶段：零模拟（食物/bot/吃判定/同步全都不跑）——除非背景演示赛在跑（v0.6.3.0）
		if ( !_gameStarted && !_menuDemo ) return;

		// 比赛进程（M5）：host 权威倒计时，归零广播结算；结束后全场冻结由结算面板接管
		TickMatch();
		if ( MatchState.MatchOver )
		{
			// 结算显示 10 秒后自动回大厅（多人）/主菜单（单机）；EndBoard 空格可提前
			if ( _sinceMatchOver > GameConfig.SettlementAutoReturnSeconds )
				AfterSettlement();
			return;
		}

		// 权威端：食物重生 / bot 原地复活 / 位移校验（顺便产出转向方向）/ 分身模拟 / 吃判定。
		// 分身模拟放在位移校验之后——转向方向取自球的本帧位移；吃判定最后跑（吃到的是新位置）
		if ( NetworkManager.IsAuthority )
		{
			_food.Tick();
			_power?.Tick( _balls, _cells );
			TickBots();
			TickMovementValidation();
			TickCells();
			TickEating();
			TickSpikes();

			// 分身/孢子状态 15Hz 快照广播（列表清空后还要补发一次空快照让客户端也清空）
			if ( _sinceCellSync > GameConfig.CellSyncInterval )
			{
				_sinceCellSync = 0;
				if ( _cells.Count > 0 || _cellsDirty ) BroadcastCells();
			}
		}

		// 刺爆弹幕（客户端）：纯视觉弹道推进（host 在 TickSpikes 权威模拟+命中）
		if ( !NetworkManager.IsAuthority ) TickSpinesVisual();

		// 细胞渲染位置平滑：host 逐帧权威位置直接贴齐；客户端把 15Hz 快照插顺
		if ( _cells.Count > 0 )
		{
			var st = 1f - MathF.Exp( -12f * Time.Delta );
			foreach ( var c in _cells )
				c.DrawPos = NetworkManager.IsAuthority ? c.Pos : Vector3.Lerp( c.DrawPos, c.Pos, st );
		}

		// 球注册：扫描场景收编（网络对象的 OnStart 不触发，实测）+ 清理失效球
		if ( _sinceScan > 1f )
		{
			_sinceScan = 0;
			ScanBalls();
		}

		// 客户端：本地食物列表未就绪（热重载/新 FoodManager）→ 节流请求全量。
		// 等球扫描到（=场景快照已收）再请求；刚连入时 host 的 OnActive 会主动发，无需抢跑
		if ( !NetworkManager.IsAuthority && _food.Foods is null && _balls.Count > 0 && _sinceFoodRequest > 3f )
		{
			_sinceFoodRequest = 0;
			try
			{
				NetworkManager.RequestFoodFull();
			}
			catch ( Exception e )
			{
				GameLog.Info( $"[net] RequestFoodFull failed (session not ready): {e.Message}" );
			}
		}

		TickRespawn();
		TickTerritory();
	}

	/// <summary> 领土模式每帧（M7）：host 检查征服胜利 + 变化/保活广播 5Hz 快照；客户端只等 ApplyRemote。
	/// 终局快照先行——客户端结算榜按领土胜负显示，慢一拍就会按旧格数判错冠军 </summary>
	void TickTerritory()
	{
		if ( !MatchState.IsTerritory || MatchState.MatchOver ) return;

		if ( !NetworkManager.IsAuthority )
		{
			_sinceTerritoryWire = 1f;
			return;
		}

		TerritoryManager.TickPoints( Time.Delta );   // 占旗积分（每旗每 3s +1，M7.4 用户定稿）
		ClassSkillManager.Tick();                    // 坦克临时质量到期回收

		if ( TerritoryManager.ConquerWinner >= 0 )
		{
			NetworkManager.TerritoryState( TerritoryManager.Snapshot(), TerritoryManager.PointsWire() );
			EndMatch();
			return;
		}

		if ( TerritoryManager.NeedsSync || _sinceTerritoryWire > 2f )
		{
			_sinceTerritoryWire = 0;
			NetworkManager.TerritoryState( TerritoryManager.Snapshot(), TerritoryManager.PointsWire() );
		}
	}

	// ---- 吃与成长 ----

	void TickEating()
	{
		// 球 × 食物：食物中心进入球体即被吃（空间哈希粗筛 + 精确复核，4140 颗时省 ~20 万次/帧）
		// 磁铁 buff（v0.7.3.0）：吸食半径翻倍——隔着一个身位也能把食物吸进嘴里（只影响食物）
		var foods = _food.Foods;
		foreach ( var ball in _balls )
		{
			if ( !ball.IsValid() || !ball.Alive ) continue;

			var bp = ball.WorldPosition;
			var r = ball.HasBuff( PowerUpManager.Kind.Magnet ) ? ball.Radius * 2f : ball.Radius;
			var near = _food.Nearby( bp, r );

			for ( int k = 0; k < near.Count; k++ )
			{
				int i = near[k];
				if ( !foods[i].Alive ) continue;

				var dx = foods[i].Pos.x - bp.x;
				var dy = foods[i].Pos.y - bp.y;
				if ( dx * dx + dy * dy > r * r ) continue;

				if ( _food.TryEat( i, ball.OwnerSteamId ) )
				{
					ball.Mass = MathF.Min( GameConfig.MaxMass, ball.Mass + GameConfig.FoodMass );
					NeonRenderer.Spark( new Vector3( foods[i].Pos.x, foods[i].Pos.y, 0f ), foods[i].ColorIndex );
					if ( GameSfx.IsMine( ball.OwnerSteamId ) )
						GameSfx.EatFood( new Vector3( foods[i].Pos.x, foods[i].Pos.y, 0f ) );
				}
			}
		}

		// 质量衰减（v0.7.8.24 球球大作战手感）：大球持续掉重，压制滚雪球、逼大哥不停行动。
		// 小球免衰减；分身同率（TickCells）。Mass 是 [Sync(FromHost)]，衰减值自动下发。
		// 领土模式（M7 用户定稿②）：己方球在己方大本营内免衰减（回家保养的拉扯点）
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() || !b.Alive ) continue;
			if ( b.Mass > GameConfig.MassDecayMinMass
				&& !( GameConfig.TerritoryHqStopsDecay && TerritoryManager.IsOwnHq( b.TeamIndex, b.WorldPosition ) ) )
				b.Mass = MathF.Max( GameConfig.MassDecayMinMass, b.Mass - b.Mass * GameConfig.MassDecayPerSecond * Time.Delta );
		}

		// 敌营安全屋（M7.2）：敌方球闯大本营持续掉重——驱赶而不处刑（下限出生质量）
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() || !b.Alive ) continue;
			if ( TerritoryManager.InHostileHq( b.TeamIndex, b.WorldPosition ) )
				b.Mass = MathF.Max( GameConfig.StartMass, b.Mass - b.Mass * GameConfig.TerritoryHqHurtPerSecond * Time.Delta );
		}

		// 球 × 球：触碰即吃（质量比门槛；护盾期/死亡免战）。
		// 死亡不销毁对象——MarkDead 后原地复用（进行中 NetworkSpawn 不复制到已连接客户端，实测）
		for ( int i = 0; i < _balls.Count; i++ )
		{
			var a = _balls[i];
			if ( !a.IsValid() || !a.Alive ) continue;

			for ( int j = i + 1; j < _balls.Count; j++ )
			{
				var b = _balls[j];
				if ( !b.IsValid() || !b.Alive ) continue;

				var bigger = a.Mass >= b.Mass ? a : b;
				var smaller = bigger == a ? b : a;
				if ( SameTeam( a, b ) ) continue;   // 队友免伤（团队赛，v0.6.5.0）
				if ( bigger.Mass < smaller.Mass * GameConfig.EatRatio ) continue;
				if ( smaller.IsProtected || bigger.IsProtected ) continue;   // 护盾期双向免战
				if ( smaller.HasBuff( PowerUpManager.Kind.Shield ) ) continue;   // 道具护盾：不可被吃（不防尖刺）

				var d = bigger.WorldPosition.Distance( smaller.WorldPosition );
				if ( d >= bigger.Radius + smaller.Radius ) continue;   // 触碰即吃（用户定稿）

				// 吞并封顶：不设上限的话连锁吞噬会让半径超过场地一半，
				// 边界钳制 min>max 抛异常（每帧刷屏）且重生者秒被吃（死亡循环）——实测
				float gained = smaller.Mass;
				bigger.Mass = MathF.Min( GameConfig.MaxMass, bigger.Mass + gained );
				SpawnPopFx( smaller );
				// 吞球音只对双方播（吃者 + 被吃者）+ 全场"谁吃了谁"播报（双端统一入口）
				NotifyBallEaten( bigger.OwnerSteamId, smaller.OwnerSteamId, gained );

				// agar 多身体规则（v0.6.2.0 用户定稿）：单个身体死亡不导致玩家死亡——
				// 主球被吃时最大的分身升级成新主球，全部身体死光才算死
				if ( TryPromoteFromPieces( smaller ) )
				{
					GameLog.Info( $"[game] body lost: {smaller.PlayerName} <- {bigger.PlayerName} (promoted)" );
					continue;
				}

				smaller.MarkDead();
				GameLog.Info( "[game] eaten: " + smaller.PlayerName + " <- " + bigger.PlayerName );
			}
		}

		// ---- 分身/孢子参与吃（M3）----

		// 分身 × 食物（同款空间哈希粗筛）
		foreach ( var c in _cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;

			var cp = c.Pos;
			var cr = c.Radius;
			var near = _food.Nearby( cp, cr );

			for ( int k = 0; k < near.Count; k++ )
			{
				int i = near[k];
				if ( !foods[i].Alive ) continue;

				var dx = foods[i].Pos.x - cp.x;
				var dy = foods[i].Pos.y - cp.y;
				if ( dx * dx + dy * dy > cr * cr ) continue;

				if ( _food.TryEat( i, c.OwnerSteamId ) )
				{
					c.Mass = MathF.Min( GameConfig.MaxMass, c.Mass + GameConfig.FoodMass );
					NeonRenderer.Spark( new Vector3( foods[i].Pos.x, foods[i].Pos.y, 0f ), foods[i].ColorIndex );
					if ( GameSfx.IsMine( c.OwnerSteamId ) )
						GameSfx.EatFood( new Vector3( foods[i].Pos.x, foods[i].Pos.y, 0f ) );
				}
			}
		}

		// 队伍映射（团队赛）：孢子可食延迟分档 + 下方球×分身免伤共用
		var teamOf = BuildTeamMap();

		// 孢子被吞：自己+队友 0.2s 档（v0.7.8.22/23 用户定稿，球球大作战式短时互喂——
		// 自己马上回收、喂队友大哥跟自食一样顺）；敌人 0.5s 档。0.2s 只是出生净空：
		// 大球吐的孢子半径可超过 16px 出生偏移，零延迟会被吐出者出生同帧吸回，吐丝直接失效
		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var blob = _cells[i];
			if ( blob.PieceKind != CellPiece.Kind.EjectedMass ) continue;

			bool eaten = false;

			// 孢子喂道具（"血量池"，v0.7.4.0 用户定稿）：孢子碰道具被吸收充入共享积分池，
			// 积分 ≥ 喂食者当前体重 5% 即触发效果（立即生效，不入背包）——虎口夺食。
			// ⚠️ 判定必须在 EjectedEdibleDelay **之前**（v0.7.5.0 修"喂不中"bug）：
			// 孢子吐出滑行 ~355 距离，0.5s 可食期到时已飞过 236——经过道具的那一帧几乎
			// 总在延迟期内，等延迟过了孢子早飞远了。喂食无质量回流，没有"秒吃自己"漏洞。
			if ( _power?.Ready ?? false )
			{
				var slots = _power.Slots;
				for ( int s = 0; s < slots.Count; s++ )
				{
					var slot = slots[s];
					if ( !slot.Alive ) continue;

					var pp = new Vector3( slot.Pos.x, slot.Pos.y, 0f );
					if ( blob.Pos.Distance( pp ) >= GameConfig.PowerUpRadius + 8f ) continue;

					eaten = true;
					_power.Feed( FindBallBySteamId( blob.OwnerSteamId ), s, blob.Mass );
					break;
				}

				if ( eaten )
				{
					_cells.RemoveAt( i );
					_cellsDirty = true;
					continue;
				}
			}

			foreach ( var b in _balls )
			{
				if ( !b.IsValid() || !b.Alive ) continue;
				var ally = SameSide( blob.OwnerSteamId, b.OwnerSteamId, teamOf );
				if ( blob.SinceSpawn < ( ally ? GameConfig.EjectedAllyEdibleDelay : GameConfig.EjectedEdibleDelay ) ) continue;
				if ( b.WorldPosition.Distance( blob.Pos ) < b.Radius + blob.Radius )
				{
					b.Mass = MathF.Min( GameConfig.MaxMass, b.Mass + blob.Mass );
					eaten = true;
					break;
				}
			}
			if ( !eaten )
			{
				foreach ( var c in _cells )
				{
					if ( c.PieceKind != CellPiece.Kind.SplitPiece || c == blob ) continue;
					var ally = SameSide( blob.OwnerSteamId, c.OwnerSteamId, teamOf );
					if ( blob.SinceSpawn < ( ally ? GameConfig.EjectedAllyEdibleDelay : GameConfig.EjectedEdibleDelay ) ) continue;
					if ( c.Pos.Distance( blob.Pos ) < c.Radius + blob.Radius )
					{
						c.Mass = MathF.Min( GameConfig.MaxMass, c.Mass + blob.Mass );
						eaten = true;
						break;
					}
				}
			}

			if ( eaten )
			{
				_cells.RemoveAt( i );
				_cellsDirty = true;
			}
		}

		// 跨主人：球 × 分身（同主人碰撞走 TickCells 的合并，不在这里）
		// 队友免伤（v0.6.5.0）：团队赛下分身不与队友的主球互吃——teamOf 已在上方建好

		for ( int i = 0; i < _balls.Count; i++ )
		{
			var ball = _balls[i];
			if ( !ball.IsValid() || !ball.Alive ) continue;
			if ( ball.IsProtected ) continue;   // 护盾期双向免战（分身没有护盾，只会被吃）

			for ( int j = _cells.Count - 1; j >= 0; j-- )
			{
				var c = _cells[j];
				if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				if ( c.OwnerSteamId == ball.OwnerSteamId ) continue;
				if ( MatchState.IsTeam
					&& teamOf.TryGetValue( c.OwnerSteamId, out var pt )
					&& pt == ball.TeamIndex ) continue;   // 队友的分身/主球互不吃

				// 球吞分身
				if ( ball.Mass >= c.Mass * GameConfig.EatRatio
					&& ball.WorldPosition.Distance( c.Pos ) < ball.Radius + c.Radius )
				{
					ball.Mass = MathF.Min( GameConfig.MaxMass, ball.Mass + c.Mass );
					_cells.RemoveAt( j );
					_cellsDirty = true;
					continue;
				}

				// 分身反吞主球（v0.6.2.0 agar 规则）：只损失主球这个身体——最大分身升级成新主球
				// 带盾主球免疫（v0.7.3.0）：护盾连"被分身吞"也挡住
				if ( !ball.HasBuff( PowerUpManager.Kind.Shield )
					&& c.Mass >= ball.Mass * GameConfig.EatRatio
					&& c.Pos.Distance( ball.WorldPosition ) < c.Radius + ball.Radius )
				{
					float gained = ball.Mass;
					c.Mass = MathF.Min( GameConfig.MaxMass, c.Mass + gained );
					SpawnPopFx( ball );
					NotifyBallEaten( c.OwnerSteamId, ball.OwnerSteamId, gained );

					if ( TryPromoteFromPieces( ball ) )
					{
						GameLog.Info( $"[game] body lost: {ball.PlayerName} <- cell#{c.Id} (promoted)" );
					}
					else
					{
						ball.MarkDead();
						if ( !ball.IsBot ) PopPiecesOf( ball.OwnerSteamId );
						GameLog.Info( "[game] eaten: " + ball.PlayerName + " <- cell#" + c.Id );
					}
					break;
				}
			}
		}

		// 跨主人：分身 × 分身（i 升序遍历；被吞的自己要退索引）
		for ( int i = 0; i < _cells.Count; i++ )
		{
			var a = _cells[i];
			if ( a.PieceKind != CellPiece.Kind.SplitPiece ) continue;

			for ( int j = _cells.Count - 1; j > i; j-- )
			{
				var b = _cells[j];
				if ( b.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				if ( b.OwnerSteamId == a.OwnerSteamId ) continue;
				if ( MatchState.IsTeam
					&& teamOf.TryGetValue( a.OwnerSteamId, out var ta )
					&& teamOf.TryGetValue( b.OwnerSteamId, out var tb )
					&& ta == tb ) continue;   // 队友分身互不吃（v0.6.5.0）

				var big = a.Mass >= b.Mass ? a : b;
				var small = big == a ? b : a;
				if ( big.Mass < small.Mass * GameConfig.EatRatio ) continue;
				if ( big.Pos.Distance( small.Pos ) >= big.Radius + small.Radius ) continue;

				big.Mass = MathF.Min( GameConfig.MaxMass, big.Mass + small.Mass );
				_cellsDirty = true;
				if ( small == a )
				{
					_cells.RemoveAt( i );
					i--;   // 下一个元素移位补位，外层 i++ 后正好指向它
					break;
				}
				_cells.RemoveAt( j );
			}
		}
	}

	/// <summary> bot 死亡后原地复活（不再销毁重建——进行中 NetworkSpawn 不复制到已连接客户端，实测）
	/// ⚠️ 蛰伏球（Dormant）不是死亡——复活它=凭空多出无队 FFA bot（v0.7.8.25 修） </summary>
	void TickBots()
	{
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() || !b.IsBot || b.Alive ) continue;
			if ( b.Dormant ) continue;   // 蛰伏备用球：等开局激活，绝不自动复活
			if ( b.SinceDeath < GameConfig.BotRespawnSeconds ) continue;

			// 领土模式 bot 自动上前线（M7.2）：没前线格时 TryFrontSpawn 内部回落大本营
			RespawnBall( b, frontLine: MatchState.IsTerritory );
		}
	}

	void TickRespawn()
	{
		if ( !LocalBall.IsValid() ) return;
		if ( LocalBall.Alive ) return;
		if ( !Input.Pressed( "Jump" ) ) return;

		TryRespawnLocal();
	}

	/// <summary> 本机重生请求（空格 / 死亡面板点击共用）：权威端直接原地复活；
	/// 客户端发静态 RPC（host 端 Rpc.Caller 找回连接，Alive 同步下发）。
	/// 领土模式 frontLine=true 复活到最近己方占领格（M7.2 二选一，默认大本营）。
	/// 菜单/房间阶段不受理（蛰伏球是死的，但比赛没开始；空格在结算面板另有用途） </summary>
	public void TryRespawnLocal( bool frontLine = false )
	{
		if ( !_gameStarted || MatchState.MatchOver ) return;
		if ( !LocalBall.IsValid() || LocalBall.Alive ) return;

		GameLog.Info( "[game] local respawn requested" );
		if ( NetworkManager.IsAuthority )
		{
			RespawnBall( LocalBall, frontLine: frontLine );
		}
		else
		{
			// 远程球的位置由 owner 自己算（Alive 翻转后本地换位）——前线偏好随点击存本机，host 传参无意义
			_pendingFrontRespawn = frontLine;
			NetworkManager.RequestRespawn( frontLine );
		}
	}

	/// <summary> 取走本机下次复活的"前线"偏好（owner 换位时用；每次重生请求都会重写，消费即清） </summary>
	public bool ConsumeFrontRespawn()
	{
		var v = _pendingFrontRespawn;
		_pendingFrontRespawn = false;
		return v;
	}

	/// <summary> 原地复活一颗球：重置数值 + Alive=true（同步下发；对象永不销毁）。
	/// teamIndex ≥ 0 时重写队伍号（开局激活时的团队分配；普通赛 -1 不动）。
	/// 领土模式 frontLine=true 复活到最近己方占领格（M7.2 二选一），否则大本营。
	/// cue=false 用于开局批量激活（提示音只用于比赛中的复活，v0.7.8.11） </summary>
	void RespawnBall( Ball ball, int teamIndex = -1, bool cue = true, bool frontLine = false )
	{
		ball.Mass = GameConfig.StartMass;
		// 团队赛兜底（v0.7.8.25）：无队球（蛰伏误复活/历史异常态）复活时补编队，绝不带 T=-1 上场
		if ( teamIndex < 0 && MatchState.IsTeam && ball.TeamIndex < 0 )
			teamIndex = NextTeamIndex();
		ball.Init( ball.IsBot, teamIndex );   // 重置保护期/速度/颜色（同步下发）
		ball.Dormant = false;
		ball.Alive = true;

		// 领土职业（M7.4）：系统随机分配，每次复活重掷——"由系统决定"且无跨局残留态
		if ( MatchState.IsTerritory )
			ball.TerritoryClass = (byte)Game.Random.Int( 0, 3 );

		// host 自己的球/bot：直接换位；真人客户端的球：该客户端在 Alive→true 时自行随机换位（owner 模拟）
		if ( ball.IsBot || ball.OwnerSteamId == Game.SteamId.Value )
		{
			// nearPos 取死亡位置（此刻还没被覆盖）——前线复活挑最近的占领格（M7.2）
			ball.WorldPosition = RandomSpawnPos( ball.TeamIndex, frontLine, ball.WorldPosition );
		}
		else
		{
			// 远程球：host 不代换位，owner 收到 Alive 后自行瞬移——开位移校验宽限期，
			// 否则 owner 的干级别位置更新会被 TickMovementValidation 永久拉回旧处（v0.7.8.33 修）
			_teleportGrace[ball.Id] = 0;
		}

		GameLog.Info( $"[net] respawned '{ball.PlayerName}'" );

		if ( cue ) CueRespawn( ball.OwnerSteamId, ball.TeamIndex );
	}

	/// <summary> 本机是否该听到这声重生：**仅队友**——自己那条由 Ball owner 模拟分支播，这里跳过防叠音 </summary>
	bool ShouldHearRespawn( long steamId, int teamIndex )
	{
		if ( GameSfx.IsMine( steamId ) ) return false;
		if ( !MatchState.IsTeam || teamIndex < 0 ) return false;

		var local = LocalBall;
		return local.IsValid() && local.TeamIndex >= 0 && local.TeamIndex == teamIndex;
	}

	/// <summary> 重生提示音（v0.7.8.11）：bot/敌方重生是噪音，只让"自己+队友"听见——
	/// host 本地判定 + RespawnCue 广播，各端同一条"队友才响"规则 </summary>
	void CueRespawn( long steamId, int teamIndex )
	{
		if ( ShouldHearRespawn( steamId, teamIndex ) )
			GameSfx.Respawn( Vector3.Zero );
		if ( Networking.IsActive ) NetworkManager.RespawnCue( steamId, teamIndex );
	}

	/// <summary> 客户端落地（RespawnCue 广播） </summary>
	public void OnRespawnCueRemote( long steamId, int teamIndex )
	{
		if ( NetworkManager.IsAuthority ) return;   // host 已在 CueRespawn 播过（挡广播回声）
		if ( ShouldHearRespawn( steamId, teamIndex ) )
			GameSfx.Respawn( Vector3.Zero );
	}

	// ---- 比赛进程（M5）：host 权威倒计时，归零广播结算榜 ----

	void TickMatch()
	{
		if ( MatchState.MatchOver ) return;

		// 双端本地递减：host 是权威，客户端 1Hz 校时后自己续走（观感平滑）
		MatchState.TimeLeft = MathF.Max( 0f, MatchState.TimeLeft - Time.Delta );

		if ( !NetworkManager.IsAuthority ) return;

		if ( _sinceMatchTick > 1f )
		{
			_sinceMatchTick = 0;
			NetworkManager.MatchTick( MatchState.TimeLeft );
		}

		if ( MatchState.TimeLeft <= 0f ) EndMatch();
	}

	/// <summary> 比赛结束（host）：按总质量（主球+分身）排结算榜，广播 + 本地落地。
	/// 菜单背景演示赛时间到就就地重开，不走结算。 </summary>
	void EndMatch()
	{
		if ( MatchState.MatchOver ) return;

		if ( _menuDemo )
		{
			MatchState.ActivateForMatch();   // 演示赛循环重开（无连接，RPC 空发无害）
			GameLog.Info( "[menu] demo match restarted" );
			return;
		}

		var rows = new List<ScoreWire>();
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() ) continue;
			rows.Add( new ScoreWire
			{
				SteamId = b.OwnerSteamId,
				Name = b.PlayerName,
				Mass = TotalMassFor( b ),
				Team = (byte)Math.Max( 0, b.TeamIndex ),
				Bot = b.IsBot,
			} );
		}
		var arr = rows.ToArray();
		Array.Sort( arr, ( x, y ) => y.Mass.CompareTo( x.Mass ) );

		// 领土模式（M7.4 积分制）：名次按占旗积分（征服=占满全部可占领格），积分同分比格数再比质量；
		// 终局快照先行——客户端结算按领土胜负显示，慢一拍就会按旧格数判错冠军
		if ( MatchState.IsTerritory )
		{
			NetworkManager.TerritoryState( TerritoryManager.Snapshot(), TerritoryManager.PointsWire() );
			Array.Sort( arr, ( x, y ) =>
			{
				var byPts = TerritoryManager.Points( y.Team ).CompareTo( TerritoryManager.Points( x.Team ) );
				if ( byPts != 0 ) return byPts;
				var byCells = TerritoryManager.OwnedCount( y.Team ).CompareTo( TerritoryManager.OwnedCount( x.Team ) );
				return byCells != 0 ? byCells : y.Mass.CompareTo( x.Mass );
			} );
		}

		NetworkManager.MatchOver( arr );   // 客户端展示用
		OnMatchOverRemote( arr );          // host 立即落地（广播回声被 MatchOver 守卫挡掉）

		GameLog.Info( $"[match] over — {arr.Length} players, winner: {( arr.Length > 0 ? arr[0].Name : "?" )}" );
	}

	/// <summary> 比赛结束落地（全端）：冻结 + 结算面板；本机成绩上云、本地榜落盘（各端各自做）。
	/// 10 秒后自动回大厅（多人）/主菜单（单机），见 AfterSettlement。 </summary>
	public void OnMatchOverRemote( ScoreWire[] standings )
	{
		if ( MatchState.MatchOver ) return;   // host 广播回声防重入
		MatchState.MatchOver = true;
		_sinceMatchOver = 0;

		var local = LocalBall;
		_endBoard?.Show( standings,
			local.IsValid() ? local.OwnerSteamId : 0,
			local.IsValid() ? local.PlayerName : "" );
		LocalBoard.Record( standings );
		ScoreUploader.SubmitOwn( standings, this );
		bool won = GameAchievements.Settlement( standings, local.IsValid() ? local.OwnerSteamId : 0 );   // 成就：夺冠/登顶
		GameXp.Settlement( standings, local.IsValid() ? local.OwnerSteamId : 0, won );                    // 经验结算上报（v0.7.8.19）
		GameLog.Info( $"[match] settlement shown ({standings?.Length ?? 0} rows)" );
	}

	/// <summary> 吞球通知（host）：广播客户端 + 本机播报/音效 </summary>
	void NotifyBallEaten( long eaterSteamId, long eatenSteamId, float massGained )
	{
		if ( Networking.IsActive ) NetworkManager.BallEaten( eaterSteamId, eatenSteamId, massGained );   // 无会话（菜单演示赛）不广播
		OnBallEaten( eaterSteamId, eatenSteamId, massGained );
	}

	/// <summary> 吞球事件（双端统一入口）：全场"谁吃了谁"播报；音效只对参与的双方；
	/// 击杀者=本机时弹个人高光横幅（v0.7.6.0，金色艺术字） </summary>
	public void OnBallEaten( long eaterSteamId, long eatenSteamId, float massGained )
	{
		var eater = FindBallBySteamId( eaterSteamId );
		var eaten = FindBallBySteamId( eatenSteamId );

		// 吞球滑入（v0.7.8.28 juice）：双端统一入口——被吃者缩团滑进吃者嘴里。
		// 分身反吞主球时 eater 是其主球（取不到具体分身），滑向其主队身体，观感为"被收编"
		if ( eater.IsValid() && eaten.IsValid() )
			NeonRenderer.Swallow( eaten.WorldPosition, eaten.ColorIndex, eater.WorldPosition, eaten.Radius );

		if ( GameSfx.IsMine( eaterSteamId ) || GameSfx.IsMine( eatenSteamId ) )
			GameSfx.EatBall( eaten.IsValid() ? eaten.WorldPosition : Vector3.Zero );

		if ( GameSfx.IsMine( eaterSteamId ) )
		{
			// 击杀高光三连（v0.7.7.0）：连击横幅（文案/字号随档位）+ 升调音 + 屏幕震动
			int combo = GameSfx.RegisterKill();
			_hud?.AddKillBanner( combo, eaten?.PlayerName ?? "???", massGained );
			GameSfx.ComboSound( combo );
			_camera?.Shake( MathF.Min( 8f + combo * 1.5f, 14f ) );

			GameAchievements.LocalKill( combo, massGained, eatenSteamId );   // 成就：击杀族（v0.7.8.14）
		}
		else if ( GameSfx.IsMine( eatenSteamId ) )
		{
			GameAchievements.LocalDeath( eaterSteamId );   // 成就：首次被吃 + 记仇名单
		}

		_hud?.AddKillFeed( eater?.PlayerName ?? "???",
			eaten?.PlayerName ?? "???",
			massGained, eaterSteamId, eatenSteamId );
	}

	/// <summary> host 位移校验：远端模拟的球单帧位移超理论上限 3 倍即拉回（反瞬移橡皮筋）。
	/// 合法瞬移（重生换位/分身晋升）有 TeleportGraceSeconds 宽限——窗口内放行 owner 的
	/// 干级别位置更新并持续锚定 _lastPos，窗口一过恢复常规校验（v0.7.8.33 修"晋升/重生后
	/// host 端球位被钉死在旧处"：此前 _lastPos 从不随瞬移重置，owner 的每次更新都被拉回） </summary>
	void TickMovementValidation()
	{
		var mySteamId = Game.SteamId.Value;
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() ) continue;

			var key = b.Id;
			var last = _lastPos.TryGetValue( key, out var lp ) ? lp : b.WorldPosition;

			bool grace = _teleportGrace.TryGetValue( key, out var g );
			if ( grace && g > GameConfig.TeleportGraceSeconds )
			{
				_teleportGrace.Remove( key );
				grace = false;
			}

			if ( !grace && !b.IsBot && b.OwnerSteamId != mySteamId )
			{
				var maxMove = b.Speed * Time.Delta * 3f + 16f;
				if ( b.WorldPosition.Distance( last ) > maxMove )
					b.WorldPosition = last;
			}

			_lastPos[key] = b.WorldPosition;

			// 转向方向（分身跟随用）：取本帧位移方向平滑；静止时保持原方向。
			// 宽限期内位移是瞬移跳变，不代表真实航向，跳过
			if ( grace ) continue;

			var delta = b.WorldPosition - last;
			if ( delta.Length > 0.1f )
			{
				var n = new Vector3( delta.x, delta.y, 0f ).Normal;
				var prev = _steerDir.TryGetValue( key, out var sd ) ? sd : new Vector2( n.x, n.y );
				_steerDir[key] = new Vector2( prev.x + ( n.x - prev.x ) * 0.4f, prev.y + ( n.y - prev.y ) * 0.4f );
			}
		}
	}

	int CountPlayers( bool isBot )
	{
		int n = 0;
		foreach ( var b in _balls )
		{
			if ( b.IsValid() && b.IsBot == isBot ) n++;
		}
		return n;
	}

	/// <summary>
	/// 扫描场景里的 Ball 并收编进列表（网络对象的 OnStart 不可靠，实测不触发）。
	/// 本机球判定用所有权字段：非代理 + 非bot + OwnerSteamId 是自己的 SteamId。
	/// </summary>
	void ScanBalls()
	{
		// 客户端：快照世界根自愈启用（首次 Tick 可能早于快照应用完成，EnsureWorld 扑空时兜住）
		if ( !NetworkManager.IsAuthority )
		{
			AdoptSnapshotWorld();
		}

		foreach ( var b in Scene.GetAllComponents<Ball>() )
		{
			if ( !b.IsValid() ) continue;

			// 存量 bot 自愈：v0.5.1.3 之前生成的 bot OwnerSteamId=0（分身头像/名牌无法归属）——
			// host 补发唯一假 id（[Sync] 自动下发客户端），热重载/老会话里的 bot 也被覆盖
			if ( NetworkManager.IsAuthority && b.IsBot && b.OwnerSteamId < GameConfig.BotSteamIdBase )
			{
				b.OwnerSteamId = NextBotSteamId();
				GameLog.Info( $"[game] legacy bot '{b.PlayerName}' assigned steam id {b.OwnerSteamId}" );
			}

			if ( !_balls.Contains( b ) )
			{
				_balls.Add( b );
			}

			// 本机球 = 自己拥有的那颗（IsProxy=false 即所有权，天然可靠）。
			// 不要用 OwnerSteamId 匹配：同机双开时连接是合成 ID，与 Game.SteamId 对不上（实测踩坑）。
			// ⚠️ 每次扫描都要断言（不能只在新收编时）：客户端的 Ball.OnStart 跑在快照期，
			// 早于本端 EnsureWorld 建相机，首次 SetLocalBall 时 _camera 为 null 被跳过；
			// SetTarget 内部有同球守卫，重复调用零开销。
			if ( !b.IsProxy && !b.IsBot )
			{
				SetLocalBall( b );
			}
		}

		for ( int i = _balls.Count - 1; i >= 0; i-- )
		{
			if ( !_balls[i].IsValid() ) _balls.RemoveAt( i );
		}
	}

	/// <summary> 球注册（Ball.OnStart 在所有机器上调用；host 生成与客户端复制统一入口） </summary>
	public void RegisterBall( Ball ball )
	{
		if ( !_balls.Contains( ball ) ) _balls.Add( ball );
	}

	/// <summary> 标记本机拥有的球，并让跟随相机锁定它。
	/// host 上为远程连接生成的球（IsRemoteOwned）绝不充当本机球——同账号双开时
	/// 它们与 host 球同名同 SteamId，唯一可靠的区别就是生成意图（实测踩坑）。 </summary>
	public void SetLocalBall( Ball ball )
	{
		if ( ball is null || ball.IsRemoteOwned ) return;

		LocalBall = ball;
		_camera?.SetTarget( ball );
	}

	/// <summary> 场地内随机出生点：**agar 式安全出生**——采样多点，选离"能吃掉出生质量"的
	/// 最近威胁最远的点。随机裸生会被路过的中 bot 秒吃（实测：加入 1.6s 被吃） </summary>
	/// <summary> 场地内出生点：走游戏系统的安全采样。领土模式（M7）给队号时在大本营格内采样（出生即回营）。
	/// teamIndex 小于 0 或非领土模式 = 全场随机（既有行为） </summary>
	/// <summary> 场地内出生点：走游戏系统的安全采样。领土模式（M7）给队号时在大本营格内采样；
	/// frontLine=true（M7.2 复活二选一）改最近己方占领格（没有前线格回落大本营）。
	/// teamIndex 小于 0 或非领土模式 = 全场随机（既有行为） </summary>
	public Vector3 RandomSpawnPos( int teamIndex = -1, bool frontLine = false, Vector3 nearPos = default )
	{
		var half = GameConfig.ArenaHalfSize - 128f;

		float minX = -half, maxX = half, minY = -half, maxY = half;
		if ( MatchState.IsTerritory && teamIndex >= 0 )
		{
			if ( frontLine && TerritoryManager.TryFrontSpawn( teamIndex, nearPos, out var front ) )
				return front;
			TerritoryManager.HqBounds( teamIndex, out minX, out maxX, out minY, out maxY );
		}

		Vector3 best = new Vector3( Game.Random.Float( minX, maxX ), Game.Random.Float( minY, maxY ), 0f );
		var bestScore = float.MinValue;

		for ( int i = 0; i < GameConfig.SpawnSafeSamples; i++ )
		{
			var p = new Vector3( Game.Random.Float( minX, maxX ), Game.Random.Float( minY, maxY ), 0f );
			var score = float.MaxValue;

			foreach ( var b in _balls )
			{
				if ( !b.IsValid() || !b.Alive ) continue;
				if ( b.Mass < GameConfig.StartMass * GameConfig.EatRatio ) continue;   // 吃不动出生球的忽略

				var d = p.Distance( b.WorldPosition ) - b.Radius;
				if ( d < score ) score = d;
			}

			if ( score > bestScore )
			{
				bestScore = score;
				best = p;
			}
		}
		return best;
	}

	static string LocalPlayerName()
	{
		try { return Connection.Local?.DisplayName ?? "You"; }
		catch { return "You"; }
	}

	// ---- 分身/孢子：host 权威模拟（M3）----

	/// <summary>
	/// 分身/孢子每帧模拟：冲量衰减 → 跟随主人转向 → 场地钳制 → 合并检查 → 清理。
	/// 孢子无转向（滑行到停），分身按主人球的平滑转向方向跟随移动。
	/// </summary>
	void TickCells()
	{
		if ( _cells.Count == 0 ) return;

		var dt = Time.Delta;

		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var c = _cells[i];

			// 主人球引用失效（热重载换程序集对象 / 主人真没了）→ 先按 SteamId 重挂自愈
			// （v0.7.8.29：热重载会把组件换成新程序集对象，旧 OwnerBall 引用全失效，
			// 旧逻辑直接消散 = 每次热重载分身凭空蒸发）；重挂不上才消散（孢子中性存活，不清理）
			if ( c.PieceKind != CellPiece.Kind.EjectedMass
				&& ( !c.OwnerBall.IsValid() || !c.OwnerBall.Alive ) )
			{
				var re = FindBallBySteamId( c.OwnerSteamId );
				if ( ( !re.IsValid() || !re.Alive ) && _sinceCellHeal > 0.5f )
				{
					_sinceCellHeal = 0;
					ScanBalls();   // 热重载窗口：主人球其实在场景里（新程序集对象），立刻收编再试
					re = FindBallBySteamId( c.OwnerSteamId );
				}
				if ( re.IsValid() && re.Alive )
				{
					c.OwnerBall = re;
				}
				else
				{
					_cells.RemoveAt( i );
					_cellsDirty = true;
					continue;
				}
			}

			// 尖刺分身到期（v0.7.4.0 用户定稿 B）：变回普通分身——质量保留，
			// 按质量取合并冷却（v0.7.8.27 曲线）后按普通分身规则合体回收；此后不再是武器。
			// MaxLife 覆盖（M7.4 护卫固守尖刺 8s）：到期同时解除锚定
			if ( c.PieceKind == CellPiece.Kind.SpikeMinion
				&& c.SinceSpawn > ( c.MaxLife > 0f ? c.MaxLife : GameConfig.SpikeMinionLife ) )
			{
				c.PieceKind = CellPiece.Kind.SplitPiece;
				c.Anchored = false;
				c.SinceSpawn = 0;
				c.MergeCooldown = GameConfig.MergeCooldownFor( c.Mass );
				NeonRenderer.Puff( c.Pos, c.ColorIndex );
				_cellsDirty = true;
				continue;
			}

			// 质量衰减（v0.7.8.24 球球大作战手感）：分身与球同率持续掉重（孢子不衰减）
			if ( c.PieceKind != CellPiece.Kind.EjectedMass && c.Mass > GameConfig.MassDecayMinMass )
			{
				c.Mass = MathF.Max( GameConfig.MassDecayMinMass, c.Mass - c.Mass * GameConfig.MassDecayPerSecond * dt );
			}

			c.Impulse *= MathF.Exp( -( c.PieceKind == CellPiece.Kind.EjectedMass
				? GameConfig.EjectImpulseDecay : GameConfig.SplitImpulseDecay ) * dt );

			if ( c.PieceKind != CellPiece.Kind.EjectedMass )
			{
				var dir = SteerDirOf( c.OwnerBall );
				var target = new Vector3( dir.x, dir.y, 0f ) * c.Speed;

				// 倒流引力（批18④，"秒合"的物理基础）：分身比主球重（吐球减重可制造体重差）
				// 或出生超过 PieceHomeAfterSeconds → 自动转向主球滚回，与摇杆输入按权重混合。
				// 尖刺分身排除——20 秒武器期绝不倒流合体（用户定稿 A/B）
				var owner = c.OwnerBall;
				if ( owner.IsValid() && owner.Alive && c.PieceKind == CellPiece.Kind.SplitPiece && c.IsMergeReady
					&& ( c.Mass > owner.Mass || c.SinceSpawn > GameConfig.PieceHomeAfterSeconds ) )
				{
					var to = owner.WorldPosition - c.Pos;
					if ( to.Length > 1f )
					{
						to = to.Normal;
						var blended = new Vector3(
							to.x * GameConfig.PieceAttractWeight + dir.x,
							to.y * GameConfig.PieceAttractWeight + dir.y, 0f );
						if ( blended.Length > 0.001f )
							target = blended.Normal * c.Speed;
					}
				}

				// 分身转向响应同样随质量衰减（与主球同曲线，大分身一样笨拙）
				var turn = MathF.Max( GameConfig.MinTurnSpeed,
					GameConfig.BaseTurnSpeed * MathF.Pow( GameConfig.StartMass / c.Mass, GameConfig.TurnCurve ) );
				var t = 1f - MathF.Exp( -turn * dt );
				c.SteerVel = Vector3.Lerp( c.SteerVel, target, t );
			}

			// 护卫固守尖刺（M7.4）：锚定分身原地不动（不跟随主人、无转向）
			if ( c.Anchored ) c.SteerVel = Vector3.Zero;

			var pos = c.Pos + ( c.Impulse + c.SteerVel ) * dt;
			var half = MathF.Max( 0f, GameConfig.ArenaHalfSize - c.Radius );
			pos.x = Math.Clamp( pos.x, -half, half );
			pos.y = Math.Clamp( pos.y, -half, half );
			pos.z = 0f;
			c.Pos = pos;

			// 领土喂旗（M7）：孢子落进旗圈即计分并消耗（驻军模型，host 权威）。
			// 没进旗圈或规则不允许（非邻接/大本营）时 Feed 返回 false，孢子继续当普通孢子飞，质量不白费
			if ( c.PieceKind == CellPiece.Kind.EjectedMass && MatchState.IsTerritory )
			{
				if ( TerritoryManager.Feed( c.TeamIndex, c.Pos, c.Mass ) )
				{
					_cells.RemoveAt( i );
					_cellsDirty = true;
					continue;
				}
			}
		}

		// 同主人合并（冷却按质量曲线 v0.7.8.27：小分身 1s、大分身最长 14s）：**碰到即合并**（圆缘相触，
		// 不再要求嵌进圆心——用户实测反馈：靠在一起不动不算，观感就是"不能合并"）。优先并回主球，其次大分身吞小分身。
		// ⚠️ 内层 j 只向上扫（j > i）：j 掉到 i 以下再 RemoveAt(i) 会索引越界
		//（下方元素被删后 i 已漂移，实测 ArgumentOutOfRangeException 刷屏）
		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var a = _cells[i];
			if ( a.PieceKind != CellPiece.Kind.SplitPiece || !a.IsMergeReady ) continue;

			var owner = a.OwnerBall;
			if ( owner.IsValid() && owner.Alive && owner.WorldPosition.Distance( a.Pos ) < owner.Radius + a.Radius )
			{
				owner.Mass = MathF.Min( GameConfig.MaxMass, owner.Mass + a.Mass );
				_cells.RemoveAt( i );
				_cellsDirty = true;
				continue;
			}

			for ( int j = i - 1; j >= 0; j-- )
			{
				var b = _cells[j];
				if ( b.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				if ( b.OwnerSteamId != a.OwnerSteamId || !b.IsMergeReady ) continue;

				var big = a.Mass >= b.Mass ? a : b;
				var small = big == a ? b : a;
				if ( big.Pos.Distance( small.Pos ) >= big.Radius + small.Radius ) continue;

				big.Mass = MathF.Min( GameConfig.MaxMass, big.Mass + small.Mass );
				_cells.RemoveAt( j );   // 只删 j < i 的，a 的索引 i 恒定安全
				_cellsDirty = true;
				break;   // 每帧每分身并一个，下一帧继续
			}
		}

		// 敌方分身相互推挤（v0.7.8.24 球球大作战手感）：吃不动=实心，按质量权重互相让位。
		// 同主人走上方合并；团队赛队友可重叠；尖刺分身是武器不参与（碰到走武器判定）
		var pushTeamOf = BuildTeamMap();
		for ( int i = 0; i < _cells.Count; i++ )
		{
			var a = _cells[i];
			if ( a.PieceKind != CellPiece.Kind.SplitPiece ) continue;
			for ( int j = i + 1; j < _cells.Count; j++ )
			{
				var b = _cells[j];
				if ( b.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				if ( a.OwnerSteamId == b.OwnerSteamId ) continue;
				if ( MatchState.IsTeam
					&& pushTeamOf.TryGetValue( a.OwnerSteamId, out var at )
					&& pushTeamOf.TryGetValue( b.OwnerSteamId, out var bt )
					&& at == bt ) continue;
				// 能吃就吃（TickEating 处理），不推——否则推挤会把可吞的对手挡开，大分身永远吃不掉小分身
				var hi = MathF.Max( a.Mass, b.Mass );
				var lo = MathF.Min( a.Mass, b.Mass );
				if ( hi >= lo * GameConfig.EatRatio ) continue;

				var dx = b.Pos.x - a.Pos.x;
				var dy = b.Pos.y - a.Pos.y;
				var rr = a.Radius + b.Radius;
				var d2 = dx * dx + dy * dy;
				if ( d2 >= rr * rr ) continue;
				var d = MathF.Sqrt( MathF.Max( d2, 0.0001f ) );
				var push = MathF.Min( rr - d, GameConfig.PushMaxStep );
				var nx = d > 0.001f ? dx / d : 1f;
				var ny = d > 0.001f ? dy / d : 0f;
				var wa = b.Mass / ( a.Mass + b.Mass );   // 越重让得越少
				a.Pos = new Vector3( a.Pos.x - nx * push * wa, a.Pos.y - ny * push * wa, 0f );
				b.Pos = new Vector3( b.Pos.x + nx * push * ( 1f - wa ), b.Pos.y + ny * push * ( 1f - wa ), 0f );
				_cellsDirty = true;
			}
		}
	}

	/// <summary>
	/// 执行分裂（host 权威；owner 本地直调 / 客户端经 RequestSplit RPC）。
	/// **agar 标准全员分裂（v0.7.1.0 用户需求）**：空格 = 主球 + 每个够重的分身**同时对半分裂**，
	/// 新身体**各自朝光标弹出**（v0.7.8.7 手感：aimPoint 为光标世界坐标，主球与每个分身
	/// 独立算指向，agar 标准扇形铺开）——连按可快速铺开到分身上限（1→2→4→8→16 只要 4 按）。
	/// 每颗身体独立过质量门槛（对半后不击穿质量下限 1），槽满即停。
	/// </summary>
	public void DoSplit( Ball ball, Vector2 aimPoint )
	{
		if ( !GameConfig.EnableSplit || !NetworkManager.IsAuthority ) return;
		if ( ball is null || !ball.IsValid() || !ball.Alive ) return;
		if ( ball.Mass < GameConfig.SplitMinMass ) return;
		if ( ClassSkillManager.IsTankSurging( ball ) ) return;   // 坦克爆发期禁分裂（临时质量分裂=套现，M7.4）

		var mainDir = FallbackDir( ball, DirTo( ball.WorldPosition, aimPoint ) );

		// ⚠️ 先捕获旧分身数量再生成主球新分身：SpawnPiece 是尾部追加，若循环从
		// _cells.Count-1 起步会第一轮就遍历到刚弹出的新分身把它再劈一次——
		// 一次空格出 3 颗（本体+新分身+新分身的孪生），实测 v0.7.8.26 修
		int oldCount = _cells.Count;

		// 主球对半
		if ( CountPiecesOf( ball.OwnerSteamId ) < GameConfig.MaxSplitPieces )
		{
			var pieceMass = ball.Mass * 0.5f;
			ball.Mass -= pieceMass;
			SpawnPiece( ball, mainDir, pieceMass );
			NeonRenderer.Puff( ball.WorldPosition, ball.ColorIndex );
		}

		// 每个够重的**旧**分身也对半（**各自朝光标弹出**；倒序只遍历到 oldCount，
		// 本轮新生成的主球分身/孪生一律不参与再分裂）。
		// 新分身同样贴着母分身**外缘**生成（v0.7.2.0），否则 1s 冷却一到就被吸回去
		for ( int i = oldCount - 1; i >= 0; i-- )
		{
			var c = _cells[i];
			if ( c.PieceKind != CellPiece.Kind.SplitPiece || c.OwnerSteamId != ball.OwnerSteamId ) continue;
			if ( c.Mass < GameConfig.SplitMinMass * 2f ) continue;
			if ( CountPiecesOf( ball.OwnerSteamId ) >= GameConfig.MaxSplitPieces ) break;

			var half = c.Mass * 0.5f;
			c.Mass -= half;
			var twinRadius = GameConfig.StartRadius * MathF.Sqrt( half / GameConfig.StartMass );
			var dir = DirOr( c.Pos, aimPoint, mainDir );

			var twin = new CellPiece
			{
				Id = _nextCellId++,
				PieceKind = CellPiece.Kind.SplitPiece,
				OwnerSteamId = c.OwnerSteamId,
				OwnerBall = c.OwnerBall,
				ColorIndex = c.ColorIndex,
				Mass = half,
				Pos = c.Pos + new Vector3( dir.x, dir.y, 0f ) * ( c.Radius + twinRadius ),
				DrawPos = c.Pos,
				Impulse = new Vector3( dir.x, dir.y, 0f ) * GameConfig.SplitImpulseSpeed,
				SinceSpawn = 0,
				MergeCooldown = GameConfig.MergeCooldownFor( half ),   // v0.7.8.27 曲线
			};
			ClampToArena( twin );
			_cells.Add( twin );
			_cellsDirty = true;
		}

		GameLog.Info( $"[game] split-all '{ball.PlayerName}'" );
	}

	/// <summary> 生成一个分裂分身（调用方已扣主球质量）。
	/// **贴着主球外缘生成（v0.7.2.0）**：原 0.4×半径在球体内部，大球分身飞不出合并吸附范围，
	/// 1s 冷却一到就被吸回主球——玩家实际永远只有一颗身体，主球一死无身可晋升直接真死 </summary>
	CellPiece SpawnPiece( Ball ball, Vector2 dir, float mass )
	{
		var pieceRadius = GameConfig.StartRadius * MathF.Sqrt( mass / GameConfig.StartMass );
		var c = new CellPiece
		{
			Id = _nextCellId++,
			PieceKind = CellPiece.Kind.SplitPiece,
			OwnerSteamId = ball.OwnerSteamId,
			OwnerBall = ball,
			ColorIndex = ball.ColorIndex,
			Mass = mass,
			Pos = ball.WorldPosition + new Vector3( dir.x, dir.y, 0f ) * ( ball.Radius + pieceRadius ),
			DrawPos = ball.WorldPosition,
			Impulse = new Vector3( dir.x, dir.y, 0f ) * GameConfig.SplitImpulseSpeed,
			SinceSpawn = 0,
			MergeCooldown = GameConfig.MergeCooldownFor( mass ),   // v0.7.8.27 曲线：分身越大合回越久
		};
		ClampToArena( c );
		_cells.Add( c );
		_cellsDirty = true;
		return c;
	}

	/// <summary>
	/// 撞刺炸裂：连环强制对半分裂，直到分身槽满或质量掉到分裂门槛以下。
	/// 返回是否真炸了东西——大球停在刺上会每帧触发，靠它挡住特效/日志刷屏。
	/// </summary>
	bool DoBurst( Ball ball )
	{
		var slots = GameConfig.MaxSplitPieces - CountPiecesOf( ball.OwnerSteamId );
		if ( slots <= 0 || ball.Mass < GameConfig.SplitMinMass * 2f ) return false;

		int n = 0;
		while ( n < slots && ball.Mass >= GameConfig.SplitMinMass * 2f )
		{
			var pieceMass = ball.Mass * 0.5f;
			ball.Mass -= pieceMass;
			var p = SpawnPiece( ball, RandomDir(), pieceMass );
			p.MergeCooldown = GameConfig.SpikePieceMergeCooldown;   // 撞刺碎片：短冷却，主人可以很快吃回来
			n++;
		}

		SpawnPopFx( ball );
		GameLog.Info( $"[game] burst on spike: '{ball.PlayerName}' into {n} pieces" );
		return true;
	}

	/// <summary> 大分身撞刺：对半裂成两个（各自重置合并冷却）；槽满则被刺吸收 </summary>
	void BurstPiece( CellPiece c )
	{
		var slots = GameConfig.MaxSplitPieces - CountPiecesOf( c.OwnerSteamId );
		if ( slots <= 0 )
		{
			_cells.Remove( c );
			_cellsDirty = true;
			return;
		}

		var dir = RandomDir();
		var half = c.Mass * 0.5f;

		c.Mass = half;
		c.SinceSpawn = 0;
		c.MergeCooldown = GameConfig.SpikePieceMergeCooldown;
		c.Impulse = new Vector3( dir.x, dir.y, 0f ) * GameConfig.SplitImpulseSpeed;

		var d2 = -dir;
		var other = new CellPiece
		{
			Id = _nextCellId++,
			PieceKind = CellPiece.Kind.SplitPiece,
			OwnerSteamId = c.OwnerSteamId,
			OwnerBall = c.OwnerBall,
			ColorIndex = c.ColorIndex,
			Mass = half,
			Pos = c.Pos + new Vector3( d2.x, d2.y, 0f ) * ( c.Radius * 0.5f ),
			DrawPos = c.Pos,
			Impulse = new Vector3( d2.x, d2.y, 0f ) * GameConfig.SplitImpulseSpeed,
			SinceSpawn = 0,
			MergeCooldown = GameConfig.SpikePieceMergeCooldown,
		};
		ClampToArena( other );
		_cells.Add( other );
		_cellsDirty = true;

		NeonRenderer.Pop( c.Pos, c.ColorIndex, c.Radius * 2f );
		if ( GameSfx.IsMine( c.OwnerSteamId ) )
			GameSfx.Pop( c.Pos );
		NetworkManager.PoppedEffect( c.Pos, (byte)c.ColorIndex, c.Radius * 2f, c.OwnerSteamId );
	}

	/// <summary>
	/// 尖刺判定（host，v0.5.1.4 用户定稿 agar 病毒规则）：
	/// - 细胞**比刺小**：碰到即被尖刺吃掉（玩家死亡 / 分身消失）；
	/// - 细胞**比刺大**：把尖刺吃掉（+SpikeMass）并强制触发分裂（DoBurst 连环炸），尖刺在别处重生（场上恒 12 颗）；
	/// - 孢子碰刺芯被吸收（喂刺）。出生保护期内免疫尖刺。
	/// </summary>
	void TickSpikes()
	{
		var spikes = _spikes;
		if ( spikes is null ) return;

		var touch = GameConfig.SpikeRadius * GameConfig.SpikeTouchFactor;

		// 球 × 刺
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() || !b.Alive || b.IsProtected ) continue;   // 护盾期免疫尖刺

			var bp = b.WorldPosition;
			for ( int i = 0; i < spikes.Length; i++ )
			{
				var dx = spikes[i].x - bp.x;
				var dy = spikes[i].y - bp.y;
				var rr = b.Radius + touch;
				if ( dx * dx + dy * dy > rr * rr ) continue;

				if ( b.Radius > GameConfig.SpikeRadius )
				{
					// 吃刺：+100 质量 + 强制分裂，刺重生到新位置
					b.Mass = MathF.Min( GameConfig.MaxMass, b.Mass + GameConfig.SpikeMass );
					DoBurst( b );
					RespawnSpike( i );
				}
				else
				{
					// 被刺吃（v0.6.2.0 agar 规则）：只损失这个身体——最大分身升级成新主球
					SpawnPopFx( b );

					if ( TryPromoteFromPieces( b ) )
					{
						GameLog.Info( $"[game] body lost to spike: {b.PlayerName} (promoted)" );
					}
					else
					{
						b.MarkDead();
						if ( !b.IsBot ) PopPiecesOf( b.OwnerSteamId );
						GameLog.Info( "[game] eaten by spike: " + b.PlayerName );
					}
				}
				break;
			}
		}

		// 分身 × 刺（同规则；倒序遍历——被吃要移除）
		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var c = _cells[i];
			if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;

			for ( int sIdx = 0; sIdx < spikes.Length; sIdx++ )
			{
				var dx = spikes[sIdx].x - c.Pos.x;
				var dy = spikes[sIdx].y - c.Pos.y;
				var rr = c.Radius + touch;
				if ( dx * dx + dy * dy > rr * rr ) continue;

				if ( c.Radius > GameConfig.SpikeRadius )
				{
					c.Mass = MathF.Min( GameConfig.MaxMass, c.Mass + GameConfig.SpikeMass );
					BurstPiece( c );   // 强制对半裂（碎片短冷却，主人能快速吃回）
					RespawnSpike( sIdx );
				}
				else
				{
					NeonRenderer.Puff( c.Pos, c.ColorIndex );
					_cells.RemoveAt( i );
					_cellsDirty = true;
				}
				break;
			}
		}

		// 孢子喂刺（v0.7.8.24 球球大作战式）：吸收充能 → 喂满爆开射刺。刺不换位不重生，
		// 喂食量归零重新攒——"在敌人身边养刺"的战术核心
		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var c = _cells[i];
			if ( c.PieceKind != CellPiece.Kind.EjectedMass ) continue;

			for ( int sIdx = 0; sIdx < spikes.Length; sIdx++ )
			{
				var dx = spikes[sIdx].x - c.Pos.x;
				var dy = spikes[sIdx].y - c.Pos.y;
				if ( dx * dx + dy * dy > touch * touch ) continue;

				if ( _spikeFed is not null && (uint)sIdx < (uint)_spikeFed.Length )
				{
					_spikeFed[sIdx] += c.Mass;
					NetworkManager.SpikeFed( sIdx, _spikeFed[sIdx] );   // 客户端镜像（刺长大显示用）
					if ( _spikeFed[sIdx] >= GameConfig.SpikeGrowTarget )
						BurstSpike( sIdx );
				}

				_cells.RemoveAt( i );
				_cellsDirty = true;
				break;
			}
		}

		TickSpines();
		TickSpikeMinions( spikes );
	}

	/// <summary> 刺爆（host 权威，v0.7.8.24）：喂食量清零 + 均匀角度射出弹幕（命中在 TickSpines）。
	/// 角度公式 SpineDir 与客户端 OnSpikeBurstRemote 共用（确定性，视觉弹道=权威弹道） </summary>
	void BurstSpike( int index )
	{
		if ( _spikes is null || _spikeFed is null || (uint)index >= (uint)_spikes.Length ) return;
		_spikeFed[index] = 0f;

		var pos = _spikes[index];
		var n = GameConfig.SpikeBurstCount;
		for ( int k = 0; k < n; k++ )
		{
			_spines.Add( new SpikeSpine
			{
				Pos = new Vector2( pos.x, pos.y ),
				Vel = SpineDir( k, n, index ) * GameConfig.SpineSpeed,
				SinceSpawn = 0,   // TimeSince 默认值≠0（记录时刻 0 → 读出巨大值立即过期），必须显式归零
			} );
		}
		NetworkManager.SpikeBurst( index );   // 全端爆效；客户端在同款角度生成视觉弹幕
	}

	/// <summary> 弹幕方向（确定性公式，host 权威与客户端视觉共用）：均匀圆周 + 按刺序号错开 </summary>
	static Vector2 SpineDir( int k, int n, int index )
	{
		var ang = k / (float)n * MathF.PI * 2f + index * 0.7f;
		return new Vector2( MathF.Cos( ang ), MathF.Sin( ang ) );
	}

	/// <summary> 刺爆弹幕推进（host 权威）：命中球/分身扣质量（护盾不防尖刺族；出生保护免疫），
	/// 到期消散。中立伤害——命中谁都扣（包括喂刺者自己），弹幕本体消失 </summary>
	void TickSpines()
	{
		var dt = Time.Delta;
		for ( int i = _spines.Count - 1; i >= 0; i-- )
		{
			var s = _spines[i];
			if ( s.SinceSpawn > GameConfig.SpineLife ) { _spines.RemoveAt( i ); continue; }
			s.Pos += s.Vel * dt;

			bool consumed = false;

			foreach ( var b in _balls )
			{
				if ( !b.IsValid() || !b.Alive || b.IsProtected ) continue;
				var dx = b.WorldPosition.x - s.Pos.x;
				var dy = b.WorldPosition.y - s.Pos.y;
				var rr = b.Radius + GameConfig.SpineRadius;
				if ( dx * dx + dy * dy >= rr * rr ) continue;

				b.Mass = MathF.Max( GameConfig.StartMass, b.Mass * ( 1f - GameConfig.SpineHitMassFraction ) );
				NetworkManager.SpineHit( s.Pos.x, s.Pos.y, (byte)Math.Clamp( b.ColorIndex, 0, 255 ) );
				consumed = true;
				break;
			}
			if ( consumed ) { _spines.RemoveAt( i ); continue; }

			for ( int j = _cells.Count - 1; j >= 0; j-- )
			{
				var c = _cells[j];
				if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				var dx = c.Pos.x - s.Pos.x;
				var dy = c.Pos.y - s.Pos.y;
				var rr = c.Radius + GameConfig.SpineRadius;
				if ( dx * dx + dy * dy >= rr * rr ) continue;

				c.Mass = MathF.Max( GameConfig.StartMass, c.Mass * ( 1f - GameConfig.SpineHitMassFraction ) );
				_cellsDirty = true;
				NetworkManager.SpineHit( s.Pos.x, s.Pos.y, (byte)Math.Clamp( c.ColorIndex, 0, 255 ) );
				_spines.RemoveAt( i );
				consumed = true;
				break;
			}
			if ( consumed ) continue;
		}
	}

	/// <summary> 刺爆弹幕（客户端镜像）：纯视觉弹道推进 + 到期消散，不判命中（扣质量由 host 权威下发） </summary>
	void TickSpinesVisual()
	{
		var dt = Time.Delta;
		for ( int i = _spines.Count - 1; i >= 0; i-- )
		{
			var s = _spines[i];
			if ( s.SinceSpawn > GameConfig.SpineLife ) { _spines.RemoveAt( i ); continue; }
			s.Pos += s.Vel * dt;
		}
	}

	/// <summary>
	/// 尖刺分身判定（v0.7.4.0）：敌方玩家/分身碰到按野刺规则——比刺大被炸（连环对半+刺被吃），
	/// 比刺小被刺吃（死/晋升，刺不消耗）。**同主人与团队赛队友免疫**（武器不伤自己人）。
	/// 炸掉"比自己（主人）大的球"→ 主人奖 3 秒无敌（用户定稿 C：武器专给小球反大球，
	/// 大球用它炸小球零奖励）。判定芯与野刺同款（SpikeRadius × SpikeTouchFactor）。
	/// </summary>
	void TickSpikeMinions( Vector2[] spikes )
	{
		var touch = GameConfig.SpikeRadius * GameConfig.SpikeTouchFactor;
		var teamOf = BuildTeamMap();

		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var minion = _cells[i];
			if ( minion.PieceKind != CellPiece.Kind.SpikeMinion ) continue;

			var owner = minion.OwnerBall;
			bool ownerAlive = owner.IsValid() && owner.Alive;

			bool consumed = false;

			// 敌方球 × 尖刺分身
			foreach ( var b in _balls )
			{
				if ( !b.IsValid() || !b.Alive ) continue;
				if ( b == owner ) continue;   // 主人免疫
				if ( MatchState.IsTeam && ownerAlive && SameTeam( b, owner ) ) continue;

				var d = b.WorldPosition.Distance( minion.Pos );
				if ( d >= b.Radius + touch ) continue;

				if ( b.Radius > GameConfig.SpikeRadius )
				{
					// 大球被炸：吃掉尖刺分身的质量 + 连环对半炸裂；
					// 炸掉"比主人大的球" → 主人 3s 无敌（复用护盾 buff）
					float gained = minion.Mass;
					b.Mass = MathF.Min( GameConfig.MaxMass, b.Mass + gained );
					DoBurst( b );
					NotifyBallEaten( minion.OwnerSteamId, b.OwnerSteamId, gained );

					if ( ownerAlive && b.Mass > owner.Mass )
					{
						owner.ApplyBuff( (byte)PowerUpManager.Kind.Shield, GameConfig.SpikeMinionRewardSeconds );
						GameAchievements.SpikeMinionBlast( minion.OwnerSteamId );   // 成就：以小博大（v0.7.8.13）
						GameLog.Info( $"[game] spike minion burst '{b.PlayerName}' -> '{owner.PlayerName}' invincible {GameConfig.SpikeMinionRewardSeconds}s" );
					}

					NeonRenderer.Pop( minion.Pos, minion.ColorIndex, minion.Radius * 3f );
					if ( Networking.IsActive ) NetworkManager.PoppedEffect( minion.Pos, (byte)minion.ColorIndex, minion.Radius * 3f, minion.OwnerSteamId );
					consumed = true;
				}
				else
				{
					// 比刺小：被刺吃（刺保留，与野刺一致）
					SpawnPopFx( b );
					if ( !TryPromoteFromPieces( b ) )
					{
						b.MarkDead();
						if ( !b.IsBot ) PopPiecesOf( b.OwnerSteamId );
					}
					GameLog.Info( $"[game] eaten by spike minion: {b.PlayerName}" );
				}
				break;
			}

			// 敌方分身 × 尖刺分身（大分身吃刺+炸裂，小分身被吃）
			if ( !consumed )
			{
				for ( int j = _cells.Count - 1; j >= 0; j-- )
				{
					var c = _cells[j];
					if ( c.PieceKind != CellPiece.Kind.SplitPiece || c == minion ) continue;
					if ( c.OwnerSteamId == minion.OwnerSteamId ) continue;   // 主人免疫
					if ( MatchState.IsTeam
						&& teamOf.TryGetValue( c.OwnerSteamId, out var pt )
						&& ownerAlive && owner.TeamIndex >= 0 && pt == owner.TeamIndex ) continue;

					if ( c.Pos.Distance( minion.Pos ) >= c.Radius + touch ) continue;

					if ( c.Radius > GameConfig.SpikeRadius )
					{
						c.Mass = MathF.Min( GameConfig.MaxMass, c.Mass + minion.Mass );
						BurstPiece( c );
						NeonRenderer.Pop( minion.Pos, minion.ColorIndex, minion.Radius * 3f );
						consumed = true;
					}
					else
					{
						NeonRenderer.Puff( c.Pos, c.ColorIndex );
						_cells.RemoveAt( j );
						_cellsDirty = true;
						if ( j < i ) i--;   // 已移除的分身在当前遍历位之前，补索引
					}
					break;
				}
			}

			if ( consumed )
			{
				_cells.RemoveAt( i );
				_cellsDirty = true;
			}
		}
	}

	/// <summary> 刺被吃掉后在别处重生（避开中心出生区），全量广播（12 个位置小包，幂等） </summary>
	void RespawnSpike( int index )
	{
		_spikes[index] = RandomSpikePos();
		if ( Networking.IsActive ) NetworkManager.SpikesFull( _spikes );   // 无会话不广播
	}

	Vector2 RandomSpikePos()
	{
		var half = GameConfig.ArenaHalfSize - GameConfig.FoodMargin - GameConfig.SpikeRadius;
		Vector2 p;
		do
		{
			p = new Vector2( Game.Random.Float( -half, half ), Game.Random.Float( -half, half ) );
		}
		while ( p.Length < 512f );   // 中心区留空（出生/重生不至于脸上有刺）
		return p;
	}

	/// <summary> host 生成尖刺（避开中心出生区）并广播；新连接走 OnActive 定向补发 </summary>
	void GenerateSpikes()
	{
		_spikes = new Vector2[GameConfig.SpikeCount];
		_spikeFed = new float[GameConfig.SpikeCount];
		_spines.Clear();
		for ( int i = 0; i < _spikes.Length; i++ )
		{
			_spikes[i] = RandomSpikePos();
		}
		if ( Networking.IsActive ) NetworkManager.SpikesFull( _spikes );   // 无会话（菜单演示赛）不广播
		GameLog.Info( $"[game] spikes generated: {_spikes.Length}" );
	}

	/// <summary>
	/// 执行吐孢子（host 权威，**agar 标准多身体喂球**）：对玩家的每一个身体——主球和所有
	/// 够质量的分身——各吐一颗中性孢子（v0.7.8.7 手感：aimPoint 为光标世界坐标，
	/// 每颗身体**独立朝光标**弹出，孢子扇形散开）。按住连吐。
	/// </summary>
	public void DoEject( Ball ball, Vector2 aimPoint )
	{
		if ( !GameConfig.EnableEject || !NetworkManager.IsAuthority ) return;
		if ( ball is null || !ball.IsValid() || !ball.Alive ) return;
		if ( _sinceEject.TryGetValue( ball.Id, out var t ) && t < GameConfig.EjectCooldownSeconds ) return;
		_sinceEject[ball.Id] = 0;

		var mainDir = FallbackDir( ball, DirTo( ball.WorldPosition, aimPoint ) );

		EjectFrom( ball, mainDir );

		// 两阶段（先收集后生成）：SpawnBlob 超孢子上限会 _cells.Remove 挤掉最老一颗，
		// 边遍历 _cells 边生成 = "Collection was modified"（实测炸 bot 喂大哥路径，v0.7.8.8 修）
		List<CellPiece> ejectors = null;

		foreach ( var c in _cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece || c.OwnerSteamId != ball.OwnerSteamId ) continue;
			if ( EjectCost( c.Mass ) <= 0f ) continue;

			( ejectors ??= new List<CellPiece>() ).Add( c );
		}

		if ( ejectors is null ) return;

		foreach ( var c in ejectors )
		{
			var cost = EjectCost( c.Mass );   // 两阶段间质量不会被改，重算即得
			if ( cost <= 0f ) continue;

			c.Mass -= cost;
			SpawnBlob( c.Pos, DirOr( c.Pos, aimPoint, mainDir ), c.Radius, c.OwnerSteamId, ball.TeamIndex, c.ColorIndex, cost * GameConfig.EjectBlobEfficiency );
		}
	}

	/// <summary> 身体 → 光标的单位指向；光标正贴该身体时返回 fallback（主球方向） </summary>
	static Vector2 DirOr( Vector3 from, Vector2 aimPoint, Vector2 fallback )
	{
		var d = DirTo( from, aimPoint );
		return d.Length > 0.001f ? d : fallback;
	}

	/// <summary> 身体 → 瞄准点的单位指向；正贴时返回 Zero（调用方走 FallbackDir 兜底） </summary>
	static Vector2 DirTo( Vector3 from, Vector2 aimPoint )
	{
		var dx = aimPoint.x - from.x;
		var dy = aimPoint.y - from.y;
		var len = MathF.Sqrt( dx * dx + dy * dy );
		return len > 0.001f ? new Vector2( dx / len, dy / len ) : Vector2.Zero;
	}

	/// <summary> 该质量吐一颗的成本（约 1% 随体量缩放，下限 1）；返回 0 = 会击穿质量下限 1，不允许 </summary>
	static float EjectCost( float mass )
	{
		var cost = MathF.Max( GameConfig.EjectMinCost, mass * GameConfig.EjectMassPercent );
		return mass - cost >= GameConfig.EjectMinMass ? cost : 0f;
	}

	/// <summary> 主球吐出一颗孢子 </summary>
	void EjectFrom( Ball ball, Vector2 dir )
	{
		var cost = EjectCost( ball.Mass );
		if ( cost <= 0f ) return;

		ball.Mass -= cost;
		SpawnBlob( ball.WorldPosition, dir, ball.Radius, ball.OwnerSteamId, ball.TeamIndex, ball.ColorIndex, cost * GameConfig.EjectBlobEfficiency );
	}

	/// <summary> 生成一颗中性孢子（质量=消耗×返还率）并做总量上限收敛（挤掉最老）。
	/// team = 吐球者队伍（领土喂旗计分按队伍；bot 不按 OwnerSteamId 查——bot 的 SteamId 全是 0 会串队） </summary>
	void SpawnBlob( Vector3 pos, Vector2 dir, float radius, long owner, int team, int colorIndex, float mass )
	{
		var c = new CellPiece
		{
			Id = _nextCellId++,
			PieceKind = CellPiece.Kind.EjectedMass,
			OwnerSteamId = owner,
			TeamIndex = team,
			ColorIndex = colorIndex,
			Mass = mass,
			Pos = pos + new Vector3( dir.x, dir.y, 0f ) * ( radius + 16f ),
			DrawPos = pos,
			Impulse = new Vector3( dir.x, dir.y, 0f ) * GameConfig.EjectImpulseSpeed,
			SinceSpawn = 0,
		};
		ClampToArena( c );
		_cells.Add( c );
		_cellsDirty = true;
		NeonRenderer.Puff( c.Pos, colorIndex );

		// 孢子总量上限：挤掉最老的一颗（TimeSince 越大越老）
		int blobs = 0;
		CellPiece oldest = null;
		foreach ( var x in _cells )
		{
			if ( x.PieceKind != CellPiece.Kind.EjectedMass ) continue;
			blobs++;
			if ( oldest is null || x.SinceSpawn > oldest.SinceSpawn ) oldest = x;
		}
		if ( blobs > GameConfig.MaxEjectedBlobs && oldest is not null )
		{
			_cells.Remove( oldest );
		}
	}

	/// <summary> host → 全体：分身/孢子状态快照（全量替换式，量小频率低，不做增量） </summary>
	void BroadcastCells()
	{
		_cellsDirty = false;

		var wires = new CellWire[_cells.Count];
		for ( int i = 0; i < _cells.Count; i++ )
		{
			var c = _cells[i];
			wires[i] = new CellWire
			{
				Id = c.Id,
				Kind = (byte)c.PieceKind,
				ColorIndex = (byte)c.ColorIndex,
				Owner = c.OwnerSteamId,
				X = c.Pos.x,
				Y = c.Pos.y,
				Mass = c.Mass,
			};
		}
		NetworkManager.CellsState( wires );
	}

	/// <summary> 客户端应用快照：按 Id 就地更新（保留 DrawPos 平滑），新增补入，消失移除。
	/// 经缓冲字典 O(n+m)（原双重全扫 O(n×m)，满配 800+ 分身/孢子时是 15Hz 热路径） </summary>
	public void OnCellsStateRemote( CellWire[] wires )
	{
		if ( NetworkManager.IsAuthority ) return;   // host 回声挡掉

		_wireById.Clear();
		if ( wires is not null )
		{
			foreach ( var w in wires )
				_wireById[w.Id] = w;
		}

		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var c = _cells[i];
			if ( !_wireById.TryGetValue( c.Id, out var w ) )
			{
				_cells.RemoveAt( i );
				continue;
			}

			c.Pos = new Vector3( w.X, w.Y, 0f );
			c.Mass = w.Mass;
			c.ColorIndex = w.ColorIndex;
			c.OwnerSteamId = w.Owner;
			c.PieceKind = (CellPiece.Kind)w.Kind;
		}

		_cellIdScratch.Clear();
		foreach ( var c in _cells )
			_cellIdScratch.Add( c.Id );

		foreach ( var kv in _wireById )
		{
			if ( _cellIdScratch.Contains( kv.Key ) ) continue;

			var w = kv.Value;
			var pos = new Vector3( w.X, w.Y, 0f );
			_cells.Add( new CellPiece
			{
				Id = w.Id,
				PieceKind = (CellPiece.Kind)w.Kind,
				ColorIndex = w.ColorIndex,
				OwnerSteamId = w.Owner,
				Mass = w.Mass,
				Pos = pos,
				DrawPos = pos,
			} );
			NeonRenderer.Puff( pos, w.ColorIndex );   // 客户端补上生成喷发（host 已在本地放过）
		}
	}

	/// <summary> 死亡爆裂特效：host 本地生成 + 广播（客户端经 PoppedEffect RPC 生成同款）。
	/// 爆裂音只播"自己死亡/爆裂"的（视觉特效仍全场播，声音收窄到本人） </summary>
	void SpawnPopFx( Ball ball )
	{
		var pos = ball.WorldPosition;
		NeonRenderer.Pop( pos, ball.ColorIndex, ball.Radius );
		if ( GameSfx.IsMine( ball.OwnerSteamId ) )
			GameSfx.Pop( pos );
		if ( Networking.IsActive ) NetworkManager.PoppedEffect( pos, (byte)ball.ColorIndex, ball.Radius, ball.OwnerSteamId );   // 无会话不广播
	}

	/// <summary> 清掉某主人的全部分身（主球死亡/断线时调用；孢子保留） </summary>
	public void PopPiecesOf( long ownerSteamId )
	{
		for ( int i = _cells.Count - 1; i >= 0; i-- )
		{
			var c = _cells[i];
			if ( c.PieceKind != CellPiece.Kind.SplitPiece || c.OwnerSteamId != ownerSteamId ) continue;
			_cells.RemoveAt( i );
			_cellsDirty = true;
		}
	}

	/// <summary>
	/// 尖刺分身（v0.7.4.0 道具效果）：从主人现扣 SpikeMinionMassPercent 的质量生成一颗跟随分身，
	/// 20 秒内是"刺"——敌方玩家碰到按尖刺规则处理（大球被炸/小球死），到期变回普通分身合体回收质量。
	/// 生成即带主人转向跟随（TickCells 的 SpikeMinion 分支）。
	/// </summary>
	/// <summary> 护卫固守尖刺（M7.4 职业）：原地生成锚定尖刺分身（8 秒，不扣主人质量），
	/// 敌方触碰走尖刺分身既有判定；到期变回普通分身开始跟随主人 </summary>
	public void SpawnGuardSpike( Ball ball )
	{
		if ( ball is null || !ball.IsValid() || !ball.Alive ) return;

		var c = new CellPiece
		{
			Id = _nextCellId++,
			PieceKind = CellPiece.Kind.SpikeMinion,
			OwnerSteamId = ball.OwnerSteamId,
			OwnerBall = ball,
			TeamIndex = ball.TeamIndex,
			ColorIndex = ball.ColorIndex,
			Mass = MathF.Max( 10f, ball.Mass * 0.15f ),
			Pos = ball.WorldPosition,
			DrawPos = ball.WorldPosition,
			SinceSpawn = 0,
			Anchored = true,
			MaxLife = GameConfig.ClassGuardLife,
		};
		_cells.Add( c );
		_cellsDirty = true;
		NeonRenderer.Puff( c.Pos, c.ColorIndex );
	}

	public void SpawnSpikeMinion( Ball ball )
	{
		if ( ball is null || !ball.IsValid() || !ball.Alive ) return;

		var mass = MathF.Max( 10f, ball.Mass * GameConfig.SpikeMinionMassPercent );
		mass = MathF.Min( mass, ball.Mass - 1f );
		if ( mass < 10f ) return;   // 质量太低（极小球）扣不出 10，放弃（道具白拿——可接受的极端边界）

		ball.Mass -= mass;

		var dir = RandomDir();
		var c = new CellPiece
		{
			Id = _nextCellId++,
			PieceKind = CellPiece.Kind.SpikeMinion,
			OwnerSteamId = ball.OwnerSteamId,
			OwnerBall = ball,
			ColorIndex = ball.ColorIndex,
			Mass = mass,
			Pos = ball.WorldPosition + new Vector3( dir.x, dir.y, 0f ) * ( ball.Radius + 40f ),
			DrawPos = ball.WorldPosition,
			SinceSpawn = 0,
		};
		ClampToArena( c );
		_cells.Add( c );
		_cellsDirty = true;

		NeonRenderer.Puff( c.Pos, ball.ColorIndex );
		GameLog.Info( $"[game] spike minion spawned for '{ball.PlayerName}' (mass {mass:0})" );
	}

	/// <summary>
	/// agar 多身体规则（v0.6.2.0 用户定稿）：主球被吃/撞刺时，把最大的分身"升级"成新主球
	/// ——球对象原地续命（质量/位置换成该分身的），玩家继续玩；全部身体死光才算真死。
	/// 主球与分身在同一 tick 内完成交接，Alive 从不翻转，客户端不会看到死亡/重置。
	/// 返回是否成功（没有分身 = 真死亡，走原 MarkDead 流程）。
	/// </summary>
	bool TryPromoteFromPieces( Ball ball )
	{
		if ( ball.IsBot ) return false;   // bot 不分裂，无分身可晋升

		CellPiece best = null;
		foreach ( var c in _cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece || c.OwnerSteamId != ball.OwnerSteamId ) continue;
			if ( best is null || c.Mass > best.Mass ) best = c;
		}
		if ( best is null ) return false;

		float mass = best.Mass;
		Vector3 pos = best.Pos;
		_cells.Remove( best );
		_cellsDirty = true;

		ball.Mass = mass;
		ball.Alive = true;
		ball.WorldPosition = pos;   // host 自己的球：owner 模拟从这里继续
		_lastPos[ball.Id] = pos;   // 位移校验锚点同步瞬移，否则下一帧被当瞬移作弊拉回（v0.7.8.33 修）

		// 远程玩家的球归其主人模拟，位置要通知主人瞬移过去（否则下一帧被主人的模拟写回旧位置）
		if ( ball.IsRemoteOwned || ball.OwnerSteamId != Game.SteamId.Value )
		{
			foreach ( var conn in Connection.All )
			{
				if ( conn is null || !conn.IsActive ) continue;
				if ( conn.SteamId.Value != ball.OwnerSteamId ) continue;

				using ( Rpc.FilterInclude( conn ) )
				{
					NetworkManager.MainPromoted( pos.x, pos.y );
				}
				break;
			}
		}

		GameLog.Info( $"[game] promoted piece -> main: {ball.PlayerName} mass={mass:0}" );
		return true;
	}

	/// <summary>
	/// 视角焦点（v0.6.2.0 用户定稿"视角跟随最大的分身"）：主球与自己所有分身中
	/// 质量最大的那个身体的位置/质量。
	/// </summary>
	public Vector3 FocusPositionOf( Ball ball, out float biggestMass )
	{
		biggestMass = ball.IsValid() ? ball.Mass : 0f;
		var pos = ball.IsValid() ? ball.WorldPosition : Vector3.Zero;
		if ( !ball.IsValid() ) return pos;

		foreach ( var c in _cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece || c.OwnerSteamId != ball.OwnerSteamId ) continue;
			if ( c.Mass > biggestMass )
			{
				biggestMass = c.Mass;
				pos = c.Pos;
			}
		}
		return pos;
	}

	int CountPiecesOf( long ownerSteamId )
	{
		int n = 0;
		foreach ( var c in _cells )
		{
			if ( c.PieceKind == CellPiece.Kind.SplitPiece && c.OwnerSteamId == ownerSteamId ) n++;
		}
		return n;
	}

	/// <summary> 该玩家是否还有分身护体（主球被吃保护 + 护盾虚环渲染用） </summary>
	public bool HasPieces( long ownerSteamId ) => CountPiecesOf( ownerSteamId ) > 0;

	/// <summary> 玩家总质量 = 主球 + 全部分身（排行榜/HUD 用；分身的质量也是真实战力） </summary>
	public float TotalMassFor( Ball ball )
	{
		if ( ball is null || !ball.IsValid() ) return 0f;

		float m = ball.Mass;
		var sid = ball.OwnerSteamId;
		foreach ( var c in _cells )
		{
			if ( c.PieceKind == CellPiece.Kind.SplitPiece && c.OwnerSteamId == sid ) m += c.Mass;
		}
		return m;
	}

	Vector2 SteerDirOf( Ball ball )
	{
		if ( !ball.IsValid() ) return Vector2.Zero;
		return _steerDir.TryGetValue( ball.Id, out var d ) ? d : Vector2.Zero;
	}

	/// <summary> 弹射方向：优先用输入方向，没给（静止开火）就沿用球的移动方向，再退随机 </summary>
	Vector2 FallbackDir( Ball ball, Vector2 dir )
	{
		if ( dir.Length > 0.001f ) return dir.Normal;
		var d = SteerDirOf( ball );
		if ( d.Length > 0.001f ) return d.Normal;

		return RandomDir();
	}

	/// <summary> 均匀随机单位方向 </summary>
	Vector2 RandomDir()
	{
		var a = Game.Random.Float( 0f, MathF.PI * 2f );
		return new Vector2( MathF.Cos( a ), MathF.Sin( a ) );
	}

	void ClampToArena( CellPiece c )
	{
		var half = MathF.Max( 0f, GameConfig.ArenaHalfSize - c.Radius );
		c.Pos.x = Math.Clamp( c.Pos.x, -half, half );
		c.Pos.y = Math.Clamp( c.Pos.y, -half, half );
		c.Pos.z = 0f;
	}

	// ---- 世界搭建 ----

	/// <summary>
	/// UI 对账（v0.6.4.5）：由状态标志**推导**各面板应有的可见性，每帧强制执行。
	/// 修"客户端加入后 UI 全消失 / 房间和主菜单叠在一起"——此前 Show/Hide 全靠各路径自觉调用，
	/// 任何一条路径漏调（中途加入、热重载重建面板、残留倒计时卡住恢复分支）就永远停在坏界面。
	/// </summary>
	void ReconcileUi()
	{
		bool inSession = Networking.IsActive;

		// 残留加入倒计时会让客户端恢复分支（_joinCountdown<0 才走）永久失效——对局已不在就清掉
		if ( _joinCountdown >= 0f && ( !_matchRunning || _gameStarted || !inSession ) )
			_joinCountdown = -1f;

		if ( _gameStarted )
		{
			_menu?.Hide();
			_lobby?.Hide();
			_browser?.Hide();
			if ( !MatchState.MatchOver )
			{
				// 对局中 HUD 必须在：没建过就建（热重载重建世界后引用失效 → 重建自愈）
				if ( _hud.IsValid() )
				{
					if ( !_hud.GameObject.Enabled ) _hud.GameObject.Enabled = true;
				}
				else
				{
					CreateHud();
				}
			}
			return;
		}

		if ( _lobbyOpen )
		{
			_menu?.Hide();
			_lobby?.ShowHost();   // 幂等：已显示不重置状态
			return;
		}

		if ( inSession && !NetworkManager.IsAuthority )
		{
			// 客户端在会话里但没进对局：必须停在房间页（等待 / JOIN GAME 按钮 / 3-2-1）
			_menu?.Hide();
			_browser?.Hide();
			if ( !_lobbyWaiting )
			{
				EnterClientWaiting();
				NetworkManager.RequestMatchState();
			}
			_lobby?.ShowClient();
			return;
		}

		if ( !inSession && NetworkManager.IsReconnecting )
		{
			// 掉线自动重连中：房间页留在原地显示 "CONNECTION LOST — RECONNECTING"，
			// 重试耗尽 OnReconnectFailed 才回主菜单（v0.6.4.6）
			_lobby?.ShowClient();
			return;
		}

		_lobby?.Hide();

		// 终态保证（v0.6.8.2）：无会话且不在任何流程 → **主菜单必须可见**。
		// 之前只 Hide 房间不亮菜单——任何路径漏掉 Show（热重载重建、异常中断）就是全空屏
		if ( _browser.IsValid() && _browser.IsOpen ) return;   // 房间列表页开着：主菜单让位
		if ( _settings.IsValid() && _settings.IsOpen ) return;   // 设置页开着：主菜单让位（v0.7.8.16）
		if ( _rankings.IsValid() && _rankings.IsOpen ) return;   // 排行页开着：主菜单让位（v0.7.8.19）
		if ( _menu.IsValid() && !_menu.GameObject.Enabled ) _menu.Show();
	}

	void EnsureWorld()
	{
		if ( _worldReady ) return;
		_worldReady = true;

		// 复用旧的世界根（热重载时避免堆叠多套世界），并清掉其动态子物体。
		// 注意：Play 停止后 static 引用可能指向已卸载的旧场景对象（IsValid 仍为真），
		// 必须校验场景归属，否则向旧场景根挂子物体直接断言失败
		if ( _sharedRoot.IsValid() && _sharedRoot.Scene == Scene )
		{
			_root = _sharedRoot;
			foreach ( var child in _root.Children.ToArray() )
			{
				if ( child.IsValid() ) child.Destroy();
			}
			_balls.Clear();
			_lastPos.Clear();
			_teleportGrace.Clear();
			_cells.Clear();
			_steerDir.Clear();
			_sinceEject.Clear();
			_nextCellId = 1;
			LocalBall = null;
		}
		else
		{
			_sharedRoot = null;
			// 命名避开 "CircleroyaleGame"：客户端上快照世界里 host 的世界根也叫这个名
			_root = new GameObject( true, "CircleroyaleGameLocal" );
			_sharedRoot = _root;
		}

		if ( NetworkManager.IsAuthority )
		{
			// 屏蔽场景里模板留下的所有对象（示例相机、方块等），只跑我们生成的世界。
			// Scene 本身就是根 GameObject，顶层对象都在它的 Children 里
			foreach ( var go in Scene.Children )
			{
				if ( go.IsValid() && go != _root ) go.Enabled = false;
			}

			CreateBackdrop();
			CreateNeon();
		}
		else
		{
			// 客户端：**直接启用快照世界根**（host 的世界，球都在里面），只剥掉它自带的
			// 相机/HUD（用本机的，避免双相机双 HUD）。⚠️ 不能走"把球 reparent 到本机根"
			// 的路线——引擎不允许客户端改 host 拥有对象的层级，Parent= 静默失效，球永远
			// Active=false：头像不渲染、OnUpdate 不跑（死亡头像隐藏失效→"尸体圈"）
			// ——2026-09-05 探针实测（actFalse=32，自愈扫描每秒跑也无济于事）。
			AdoptSnapshotWorld();
		}

		CreateCamera();
		CreateMenu();
		CreateLobbyPanel();
		CreateRoomBrowser();
		CreateSettingsPanel();
		CreateRankingsPanel();

		// 食物管理与联机管理：**每台机器一份本地实例，自身不联网**。
		// 食物状态经静态 RPC 同步（FoodFull 定向全量 / FoodEaten / FoodRespawned 广播增量）；
		// 此前挂在网络对象（GameNet）上的 NetList 无法复制到客户端，食物/重生全废——实测。
		// M4 主菜单：壳到此为止——建大厅还是连人由菜单点击决定（旧流程开机自动建大厅，
		// 双开第二实例必须赶在 CreateLobby 完成前连上才有"客户端"身份，天然竞态）。
		_food = new FoodManager();
		_power = new PowerUpManager();
		_net = _root.AddComponent<NetworkManager>();
		GameMusic.PlayMenu();   // 菜单/大厅阶段播 journey（2026-09-09 用户定稿）

		// 主菜单背景演示赛（v0.6.3.0）：离线 authority 跑一场纯 bot 对战当背景
		if ( NetworkManager.IsAuthority && !Networking.IsActive )
		{
			StartMenuDemo();
		}
		else if ( !NetworkManager.IsAuthority && Networking.IsActive )
		{
			// 客户端在会话里（重连/热重载/自动重连后重建世界）：不亮主菜单——
			// 直接回房间等待或对局（向 host 要当前状态，见 RequestMatchState）
			_menu?.Hide();
			EnterClientWaiting();
			_lobby?.ShowClient();
			NetworkManager.RequestMatchState();
		}
	}

	/// <summary> 主菜单动作（0=建服 1=快速加入 2=人机对战 3=退出）。
	/// HOST / VS BOTS 都先进**房间**（M5 二级界面）：调参、看玩家、点 START 才开局 </summary>
	public void MenuAction( int index )
	{
		if ( _gameStarted || _lobbyOpen ) return;

		switch ( index )
		{
			case 0:   // HOST：本机当房主（建公开大厅，别人可加入）→ 进房间
				StopMenuDemo();
				OpenHostLobby();
				GameLog.Info( "[menu] host game (room)" );
				_net.RunHostFlow();
				break;

			case 1:   // JOIN：房间列表页（v0.6.7.0）——列出 Steam 公开大厅点选加入；JOIN LOCAL 是同机调试兜底
				StopMenuDemo();
				_menu?.Hide();
				_browser?.Show();
				break;

			case 2:   // VS BOTS：人机对战（不建大厅，离线房间，一样调参后 START）
				StopMenuDemo();
				OpenHostLobby();
				GameLog.Info( "[menu] vs bots (offline room)" );
				_net.RunHostFlow( createLobby: false );
				break;

			case 3:   // QUIT：编辑器里退不出应用，只提示
				if ( Game.IsEditor )
					_menu?.SetStatus( "QUIT IS STANDALONE-ONLY (THIS IS THE EDITOR)" );
				else
					Game.Close();
				break;

			case 4:   // SETTINGS：设置页（泛光开关/音乐音量；BACK 回菜单）
				_menu?.Hide();
				_settings?.Show();
				break;

			case 5:   // RANKINGS：全球 XP 排行页（v0.7.8.19；BACK 回菜单）
				_menu?.Hide();
				_rankings?.Show();
				break;
		}
	}

	/// <summary>
	/// 主菜单背景演示赛（v0.6.3.0 用户需求）：authority 离线跑一场纯 bot 对战当菜单背景，
	/// 镜头观战最大 bot；规则随菜单设置，时间到就地重开。进房间/加入时 StopMenuDemo 收场。
	/// </summary>
	void StartMenuDemo()
	{
		if ( _menuDemo ) return;
		_menuDemo = true;

		GameConfig.ArenaHalfSize = Math.Clamp( GameConfig.ConvarArenaSize, 1024f, 12288f );
		MatchState.ActivateForMatch();   // 按当前菜单设置跑（无连接，RPC 空发无害）
		_food.Init();
		GenerateSpikes();
		_power.Init();

		var target = Math.Clamp( MatchState.PlayerTarget, 2, 64 );
		for ( int i = 0; i < target; i++ )
		{
			SpawnBall( RandomSpawnPos(), isBot: true, owner: null );
		}
		GameLog.Info( $"[menu] demo bots started ({target})" );
	}

	/// <summary> 收演示赛：清 bot/分身，重置初始化标记（真实开局的 OnLobbyOpened 会重新投食） </summary>
	void StopMenuDemo()
	{
		if ( !_menuDemo ) return;
		_menuDemo = false;

		foreach ( var b in _balls.ToArray() )
		{
			if ( b.IsValid() ) b.GameObject.Destroy();
		}
		_balls.Clear();
		_cells.Clear();
		_lastPos.Clear();
		_teleportGrace.Clear();
		_steerDir.Clear();
		_sinceEject.Clear();
		_power?.Reset();
		_networkReady = false;
		GameLog.Info( "[menu] demo stopped" );
	}

	/// <summary> host 开房：藏菜单、亮房间界面（蛰伏球由 OnLobbyOpened 在会话就绪后生成） </summary>
	void OpenHostLobby()
	{
		_lobbyOpen = true;

		// 开新房默认领土战争（v0.7.8.85 用户定稿）：显式归位而不是只靠静态初始化器——
		// 热重载保留旧静态值，开房时重设才能自愈；同一房间连续多局
		// （P 退局/结算回房）不走这里，玩家改过的选择保留
		MatchState.PendingMode = MatchState.Mode.Territory;

		_menu?.Hide();
		_lobby?.ShowHost();
	}

	/// <summary> 菜单状态行（NetworkManager 的异步流程也用它报进度） </summary>
	public void MenuStatus( string text ) => _menu?.SetStatus( text );

	/// <summary> 开局公共尾巴：藏菜单、上 HUD（回菜单后再开局复用已有 HUD，不重复建）、切战斗曲 </summary>
	void StartGame()
	{
		_joinRequested = false;
		_menu?.Hide();

		if ( _hud.IsValid() )
		{
			_hud.GameObject.Enabled = true;
			if ( _endBoard.IsValid() ) _endBoard.Hide();
		}
		else
		{
			CreateHud();
		}

		GameMusic.PlayBattle();
	}

	/// <summary>
	/// 客户端：启用快照世界根（幂等；EnsureWorld 调一次 + ScanBalls 每秒自愈——
	/// 首次 Tick 可能早于快照应用完成，扑空后靠自愈兜住）。
	/// 含球对象的顶层 GO 视为快照世界：启用之，并剥掉其相机/HUD 子物体；其余顶层对象
	/// （模板残留）照旧禁用。
	/// </summary>
	void AdoptSnapshotWorld()
	{
		foreach ( var go in Scene.Children.ToArray() )
		{
			if ( !go.IsValid() || go == _root ) continue;

			var hasBalls = false;
			foreach ( var b in go.GetComponentsInChildren<Ball>( true ) )
			{
				hasBalls = true;
				break;
			}

			if ( !hasBalls )
			{
				go.Enabled = false;
				continue;
			}

			if ( !go.Enabled )
			{
				go.Enabled = true;
				GameLog.Info( "[game] snapshot world root enabled" );
			}

			foreach ( var child in go.Children.ToArray() )
			{
				if ( !child.IsValid() ) continue;
				// 剥掉 host 快照里自带的相机与全部 UI 面板（会随快照复制过来，客户端留着就是
				// 双份界面/host 视角的页面）——只用本机自己建的那套。
				// ⚠️ 新增 UI 面板必须同步加进这个名单（RoomBrowser/Settings/Rankings 曾漏，v0.7.8.33 补）
				if ( child.Name == "Camera" || child.Name == "Hud" || child.Name == "MainMenu"
					|| child.Name == "LobbyPanel" || child.Name == "EndBoard"
					|| child.Name == "RoomBrowser" || child.Name == "SettingsPanel" || child.Name == "RankingsPanel" )
				{
					var name = child.Name;
					child.Destroy();
					GameLog.Info( $"[game] stripped snapshot '{name}'" );
				}
			}
		}
	}

	/// <summary>
	/// 房间就绪（host 建完大厅 / 离线房间时调用，M5）：生成**蛰伏球**——host 球 + 满编预备 bot，
	/// Alive=false 不可见不模拟。引擎约束："进行中 NetworkSpawn 不复制到已连接客户端"（实测），
	/// 所有球必须在各端初始快照前就存在，开局（StartMatchFromLobby）只激活、不再新建。
	/// 食物/尖刺是纯 RPC 数据（不经快照），开局时才生成。客户端不调用。
	/// </summary>
	public void OnLobbyOpened()
	{
		if ( _networkReady ) return;
		_networkReady = true;

		GameLog.Info( $"[game] CircleRoyale {GameConfig.Version} lobby opened auth={NetworkManager.IsAuthority}" );
		if ( !NetworkManager.IsAuthority ) return;

		// 房间设置：convar 覆写默认值（arena 经 Replicated 同步给客户端）
		GameConfig.ArenaHalfSize = Math.Clamp( GameConfig.ConvarArenaSize, 1024f, 12288f );

		SpawnBall( Vector3.Zero, isBot: false, owner: null, dormant: true );
		for ( int i = 1; i < GameConfig.LobbyReserveBalls; i++ )
		{
			SpawnBall( RandomSpawnPos(), isBot: true, owner: null, dormant: true );
		}
	}

	/// <summary>
	/// 房间内 START（host）：规则生效、投食/尖刺、激活蛰伏球（远程真人/host/bot 补齐到目标数）、
	/// 给等在房间里的客户端定向补发食物尖刺、广播开局。队伍分配也在这里做（此刻模式才确定）。
	/// </summary>
	public void StartMatchFromLobby()
	{
		if ( !_lobbyOpen || _gameStarted ) return;
		_lobbyOpen = false;
		_gameStarted = true;

		MatchState.ActivateForMatch();
		_teamSeq = 0;

		_food.Init();
		GenerateSpikes();
		_power.Init();

		// 每局球组复位（球对象永不销毁——v0.6.4.2 连续多局）：真人球（远程+host）全部复位，
		// bot 按剩余名额激活、多余的蛰伏；上一局的质量/生死态一并清零
		int remotes = 0;
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() ) continue;
			if ( !b.IsRemoteOwned && b.IsBot ) continue;   // bot 走下面的名额补齐

			RespawnBall( b, NextTeamIndex(), cue: false );
			if ( b.IsRemoteOwned ) remotes++;
		}

		int botsWanted = Math.Clamp( MatchState.PlayerTarget - 1 - remotes, 0, GameConfig.LobbyReserveBalls - 1 );
		int bots = 0;
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() || !b.IsBot ) continue;

			if ( bots < botsWanted )
			{
				RespawnBall( b, NextTeamIndex(), cue: false );
				bots++;
			}
			else
			{
				b.Alive = false;   // 多余 bot 蛰伏（不可见不模拟）
				b.Dormant = true;
			}
		}

		// 等在房间里的客户端没收到过食物/尖刺（OnActive 时还没生成）：逐一定向补发
		var foods = _food.Foods;
		var spikes = _spikes;
		var powerWires = _power.ToWireForSync();
		foreach ( var conn in Connection.All )
		{
			if ( conn is null || !conn.IsActive ) continue;
			if ( Connection.Local is not null && conn.Id == Connection.Local.Id ) continue;

			using ( Rpc.FilterInclude( conn ) )
			{
				if ( foods is not null ) NetworkManager.FoodFull( foods );
				if ( spikes is not null ) NetworkManager.SpikesFull( spikes );
				if ( powerWires is not null ) NetworkManager.PowerUpFull( powerWires );
			}
		}

		if ( Networking.IsActive ) NetworkManager.MatchStarted();   // 无会话不广播
		OnMatchStartedLocal();

		GameLog.Info( $"[match] started — mode={MatchState.CurrentMode} target={MatchState.PlayerTarget} remotes={remotes} bots={bots}" );
	}

	/// <summary> 团队赛按块分队（v0.6.5.0 修 bug：原 %TeamSize 轮转=只有 TeamSize 个队、每队十几人；
	/// 用户语义是"每队 N 人"→ 48 人 3 人队应分出 16 队）。普通赛恒 -1 </summary>
	int NextTeamIndex()
	{
		if ( !MatchState.IsTeam ) return -1;
		var team = _teamSeq++ / MatchState.TeamSize;
		// 领土固定 4 队（v0.7.8.35）：真人超编（大于 16）时挤进既有队，绝不开第 5 队——大本营只有四角
		return MatchState.IsTerritory ? Math.Min( team, 3 ) : team;
	}

	/// <summary> 两颗球是否同队（TeamIndex 小于 0 = 普通赛/未分队，不算同队） </summary>
	static bool SameTeam( Ball a, Ball b ) =>
		a.TeamIndex >= 0 && a.TeamIndex == b.TeamIndex;

	/// <summary> 推挤用：两个 SteamId 是否同阵营（同一 SteamId=自己的身体；团队赛按球上的 TeamIndex 查）。
	/// 同阵营不推挤（BoB 惯例：自己和队友可重叠） </summary>
	public bool SameTeamSteam( long a, long b )
	{
		if ( a == b ) return true;
		if ( !MatchState.IsTeam ) return false;
		var ta = TeamIndexOfSteam( a );
		return ta >= 0 && ta == TeamIndexOfSteam( b );
	}

	/// <summary> 团队赛阵营映射（SteamId → 队伍号）：返回**复用缓冲**——每帧 TickEating/TickCells/
	/// TickSpikeMinions 三处先后构建使用，调用方当帧用完即弃，不得跨方法/跨帧持有；
	/// 普通赛返回空映射 </summary>
	Dictionary<long, int> BuildTeamMap()
	{
		_teamScratch.Clear();
		if ( !MatchState.IsTeam ) return _teamScratch;

		foreach ( var b in _balls )
		{
			if ( b.IsValid() && b.TeamIndex >= 0 ) _teamScratch[b.OwnerSteamId] = b.TeamIndex;
		}
		return _teamScratch;
	}

	int TeamIndexOfSteam( long sid )
	{
		foreach ( var b in _balls )
		{
			if ( b.IsValid() && b.OwnerSteamId == sid && b.TeamIndex >= 0 ) return b.TeamIndex;
		}
		return -1;
	}

	/// <summary> eater 与孢子主人是否同阵营（v0.7.8.23）：同一 SteamId（自己），
	/// 或团队赛下查 teamOf 同队。同阵营走 0.2s 快档，敌人 0.5s。
	/// 注：bot 的 OwnerSteamId 全是 0——bot 之间的 blob 恒判"自己"快档（既有 SteamId 0 设计的既有模糊，无害） </summary>
	static bool SameSide( long blobOwner, long eater, Dictionary<long, int> teamOf )
	{
		if ( eater == blobOwner ) return true;
		return MatchState.IsTeam
			&& teamOf.TryGetValue( eater, out var et )
			&& teamOf.TryGetValue( blobOwner, out var bt )
			&& et == bt;
	}

	/// <summary> 开局落地（本机）：出房间、上 HUD、切战斗曲（host 直调；客户端经 RPC）。
	/// 客户端必须在这里置 _gameStarted——host 的置位在 MenuAction 里，客户端没有那条路径，
	/// 不置位的话 Tick 的"连上了就进房间"判定会在 MatchStarted 之后把房间页盖回游戏上（实测）。</summary>
	void OnMatchStartedLocal()
	{
		_lobbyWaiting = false;
		_lobbyHeartbeatArmed = false;   // 出房间：房间心跳让位给对局心跳（MatchTick）
		_lobby?.Hide();
		StartGame();
		_gameStarted = true;
		_joinRequested = false;
	}

	/// <summary> 客户端：房主点了 START（host 回声用 IsAuthority 挡掉——host 已直调） </summary>
	public void OnMatchStartedRemote()
	{
		if ( NetworkManager.IsAuthority ) return;
		GameLog.Info( "[match] host started the game" );

		// 用户定稿（v0.6.4.1）：在房间里的客户端**不自动进局**——房间页亮起 JOIN GAME 按钮，
		// 由玩家点击后走 3 秒加入倒计时再进入；不在房间（异常态）则直接进
		if ( _lobbyWaiting )
		{
			_matchRunning = true;
			_lobby?.SetMatchRunning();
			return;
		}
		OnMatchStartedLocal();
	}

	/// <summary> host 取消对局（P 键快速退局的广播落地，仅客户端）：回房间等待页 </summary>
	public void OnMatchCancelledRemote()
	{
		if ( NetworkManager.IsAuthority ) return;   // host 自己已直接处理
		GameLog.Info( "[match] host cancelled the match — back to lobby" );
		ResetToLobby();
	}

	/// <summary> 客户端点击房间页的 JOIN GAME：对局进行中 → 3 秒加入倒计时 → 进入 </summary>
	public void ClientJoinMatch()
	{
		if ( !_matchRunning || _gameStarted || _joinCountdown >= 0f ) return;
		if ( !_lobbyWaiting ) return;

		_joinCountdown = GameConfig.JoinCountdownSeconds;
		_lobby?.ShowJoinCountdown( GameConfig.JoinCountdownSeconds );
		GameLog.Info( "[match] client joining via button" );
	}

	/// <summary> 自动重连 watchdog 发起重连（NetworkManager 调用）：重新走标准加入流 </summary>
	public void NotifyReconnectStarted()
	{
		_joinRequested = true;
		_sinceJoinRequest = 0;
	}

	// ---- 加入流辅助（房间列表页 v0.6.7.0）----

	/// <summary> 客户端发起加入（点房间/快速加入/本地直连共用）：置请求标，Tick 捕获会话激活收尾 </summary>
	public void OnJoinAttemptStarted()
	{
		_joinRequested = true;
		_sinceJoinRequest = 0;
	}

	/// <summary> 加入失败提示：浏览器页开着显示在那，主菜单状态行是兜底 </summary>
	public void OnJoinAttemptFailed( string message ) => _browser?.SetStatus( message );

	/// <summary> 同机调试直连（房间列表页 JOIN LOCAL）：local 地址 = 本机 host </summary>
	public void JoinLocalRoom()
	{
		OnJoinAttemptStarted();
		_net?.StartJoin( GameConfig.DefaultJoinAddress );
		_browser?.SetStatus( "CONNECTING TO LOCAL HOST ..." );
	}

	/// <summary> 房间列表页返回主菜单（BACK） </summary>
	public void CloseBrowser()
	{
		_browser?.Hide();
		_menu?.Show();

		// JOIN 打开列表时 StopMenuDemo 清掉了背景演示赛——回菜单必须重新开起来，否则背景不动（用户实测）
		if ( !_gameStarted && !_lobbyOpen && NetworkManager.IsAuthority && !Networking.IsActive )
			StartMenuDemo();
	}

	/// <summary> 设置页收起（SettingsPanel BACK 调用）：回主菜单 </summary>
	public void CloseSettings()
	{
		_settings?.Hide();
		_menu?.Show();
	}

	/// <summary> 排行页收起（RankingsPanel BACK 调用）：回主菜单 </summary>
	public void CloseRankings()
	{
		_rankings?.Hide();
		_menu?.Show();
	}

	/// <summary> 客户端掉线，自动重连开始（NetworkManager 调用）：界面留在原地 + 房间页状态行提示 </summary>
	public void OnDisconnectRecoveryStarted()
	{
		if ( _lobbyWaiting ) _lobby?.SetConnectionLost();
		Log.Warning( "[net] connection lost — staying put, auto reconnecting" );
	}

	/// <summary> 自动重连耗尽（NetworkManager 调用）：回主菜单（v0.6.4.6 用户定稿） </summary>
	public void OnReconnectFailed()
	{
		ResetToMenu();
		_menu?.SetStatus( "CONNECTION LOST — COULD NOT REACH HOST" );
	}

	// ---- 房间信息同步（host → 等待中的客户端）----

	/// <summary> 房间玩家名单：host 自己 + 所有活跃连接（顺序即列表顺序，0 号是房主） </summary>
	public List<string> LobbyPlayerNames()
	{
		var list = new List<string>();
		try
		{
			if ( Connection.Local is not null ) list.Add( Connection.Local.DisplayName );

			foreach ( var c in Connection.All )
			{
				if ( c is null || !c.IsActive ) continue;
				if ( Connection.Local is not null && c.Id == Connection.Local.Id ) continue;
				list.Add( c.DisplayName );
			}
		}
		catch
		{
			// 会话未就绪时给空名单，界面显示占位
		}
		return list;
	}

	/// <summary> 名单/参数变了（网络线程只置脏标，主线程 Tick 统一广播） </summary>
	public void MarkLobbyDirty() => _lobbyListDirty = true;

	void BroadcastLobbyState()
	{
		NetworkManager.LobbyState( (byte)MatchState.PendingMode, (int)MatchState.PendingDuration,
			MatchState.PendingPlayerTarget, LobbyPlayerNames().ToArray() );
	}

	/// <summary> 客户端应用房主推送的房间信息（host 回声用 IsAuthority 挡掉） </summary>
	public void OnLobbyStateRemote( byte mode, int duration, int players, string[] names )
	{
		if ( NetworkManager.IsAuthority ) return;

		// 房间心跳：host 每 3s 推一次（名单没变也推），收到即归零
		if ( _lobbyWaiting )
		{
			_lobbyHeartbeatArmed = true;
			_sinceLobbyHeartbeat = 0;
		}

		_lobbyMode = mode;
		_lobbyDuration = duration;
		_lobbyPlayers = players;
		_lobbyNames = names ?? Array.Empty<string>();
		_lobby?.UpdateFromHost( mode, duration, players, names );
	}

	/// <summary> 客户端：主球被吃，最大分身已晋升成新主球——把球瞬移到晋升位置继续模拟
	/// （质量经 [Sync] 到达；host 回声用 IsAuthority 挡掉）。 </summary>
	public void OnMainPromotedRemote( float x, float y )
	{
		if ( NetworkManager.IsAuthority ) return;

		var ball = LocalBall;
		if ( !ball.IsValid() ) return;

		ball.TeleportTo( new Vector3( x, y, 0f ) );
		GameLog.Info( $"[game] main promoted -> continue at ({x:0},{y:0})" );
	}

	/// <summary> host：标记一次合法瞬移（位移校验锚到新位置 + 开宽限期）——指挥官技能/晋升共用语义 </summary>
	public void MarkTeleport( Ball ball, Vector3 pos )
	{
		_lastPos[ball.Id] = pos;
		_teleportGrace[ball.Id] = 0;
	}

	/// <summary> 静态 RPC 落地：指挥官技能传送——本机球瞬移到队友身边（host 已先锚定校验位） </summary>
	public void OnClassTeleportRemote( long steamId, float x, float y )
	{
		if ( NetworkManager.IsAuthority ) return;
		if ( Game.SteamId.Value != steamId ) return;

		var ball = LocalBall;
		if ( !ball.IsValid() ) return;

		ball.TeleportTo( new Vector3( x, y, 0f ) );
		GameLog.Info( $"[class] commander teleport -> ({x:0},{y:0})" );
	}

	/// <summary> host：为指定玩家施放职业技能（客户端 RPC 入口；冷却/条件在管理器内校验） </summary>
	public void CastClassSkillFor( long steamId )
	{
		foreach ( var b in _balls )
		{
			if ( b.IsValid() && !b.IsBot && b.Alive && b.OwnerSteamId == steamId )
			{
				ClassSkillManager.Cast( b );
				return;
			}
		}
	}

	/// <summary> 静态 RPC 落地：为重连/复活的连接原地复活其球（按 OwnerSteamId 匹配） </summary>
	public void RespawnConnection( Connection conn, bool frontLine = false )
	{
		if ( conn is null || !conn.IsActive ) return;

		var steamId = conn.SteamId.Value;
		foreach ( var b in _balls )
		{
			if ( b.IsValid() && !b.IsBot && !b.Alive && b.OwnerSteamId == steamId )
			{
				RespawnBall( b, frontLine: frontLine );
				return;
			}
		}
		GameLog.Info( $"[net] respawn request: no dead ball for '{conn.DisplayName}'" );
	}

	// ---- 食物远端事件应用（仅客户端；host 的广播回声用 IsAuthority 挡掉）----

	public void OnFoodEatenRemote( int index, long eaterSteamId )
	{
		if ( NetworkManager.IsAuthority ) return;

		// 应用前先取位置/颜色放火花（ApplyEaten 会置 Alive=false，但数据仍在——取更保险）
		var fs = _food?.Foods;
		if ( fs is not null && (uint)index < (uint)fs.Length )
		{
			var pos = new Vector3( fs[index].Pos.x, fs[index].Pos.y, 0f );
			NeonRenderer.Spark( pos, fs[index].ColorIndex );
			if ( GameSfx.IsMine( eaterSteamId ) )
				GameSfx.EatFood( pos );
		}

		_food?.ApplyEaten( index );
	}

	public void OnFoodRespawnedRemote( int index, FoodData food )
	{
		if ( NetworkManager.IsAuthority ) return;
		_food?.ApplyRespawned( index, food );
	}

	public void OnFoodFullRemote( FoodData[] foods )
	{
		if ( NetworkManager.IsAuthority ) return;
		_food?.ApplyFull( foods );
		GameLog.Info( $"[game] food full sync received: {foods.Length}" );
	}

	/// <summary> 客户端应用绿刺全量（静态数据，收到即用） </summary>
	public void OnSpikesFullRemote( Vector2[] spikes )
	{
		if ( NetworkManager.IsAuthority ) return;
		_spikes = spikes;
		_spikeFed = new float[spikes is null ? 0 : spikes.Length];   // 喂食量镜像归零（后续 SpikeFed RPC 逐步同步）
		_spines.Clear();
	}

	// ---- 喂刺/刺爆远端事件（v0.7.8.24）----

	/// <summary> 客户端镜像：某朵刺的累计喂食量（host 已直接写入，回声挡掉） </summary>
	public void OnSpikeFedRemote( int index, float fed )
	{
		if ( NetworkManager.IsAuthority ) return;
		if ( _spikeFed is not null && (uint)index < (uint)_spikeFed.Length ) _spikeFed[index] = fed;
	}

	/// <summary> 全端爆效；客户端用与 host 相同的确定性角度生成视觉弹幕（host 的权威弹幕在 BurstSpike 已生成） </summary>
	public void OnSpikeBurstRemote( int index )
	{
		if ( _spikes is null || (uint)index >= (uint)_spikes.Length ) return;
		var pos = _spikes[index];

		if ( NetworkManager.IsAuthority )
		{
			GameSfx.Pop( new Vector3( pos.x, pos.y, 0f ) );   // host 爆效声（权威弹幕已在 BurstSpike 生成，渲染直接读）
			return;
		}

		if ( _spikeFed is not null && (uint)index < (uint)_spikeFed.Length ) _spikeFed[index] = 0f;
		var n = GameConfig.SpikeBurstCount;
		for ( int k = 0; k < n; k++ )
		{
			_spines.Add( new SpikeSpine
			{
				Pos = new Vector2( pos.x, pos.y ),
				Vel = SpineDir( k, n, index ) * GameConfig.SpineSpeed,
				SinceSpawn = 0,   // 同 BurstSpike：TimeSince 必须显式归零
			} );
		}
		GameSfx.Pop( new Vector3( pos.x, pos.y, 0f ) );
	}

	/// <summary> 刺爆弹幕命中特效（全端：Broadcast 本地也执行，host 不预播） </summary>
	public void OnSpineHitRemote( float x, float y, byte colorIndex )
	{
		var pos = new Vector3( x, y, 0f );
		NeonRenderer.Puff( pos, colorIndex );
		GameSfx.Pop( pos );
	}

	// ---- 道具远端事件应用（仅客户端；host 的广播回声用 IsAuthority 挡掉）----

	public void OnPowerUpFullRemote( PowerUpWire[] wires )
	{
		if ( NetworkManager.IsAuthority ) return;
		_power?.ApplyFull( wires );
	}

	public void OnPowerUpPickedRemote( int slot, byte kind, long pickerSteamId )
	{
		if ( NetworkManager.IsAuthority ) return;

		// 应用前先取位置/种类放火花（ApplyPicked 会置空槽）
		if ( _power?.Ready ?? false )
		{
			var slots = _power.Slots;
			if ( (uint)slot < (uint)slots.Count )
			{
				var s = slots[slot];
				NeonRenderer.Spark( new Vector3( s.Pos.x, s.Pos.y, 0f ), PowerUpManager.ColorOf( kind ) );
				if ( GameSfx.IsMine( pickerSteamId ) )
				{
					GameSfx.EatFood( new Vector3( s.Pos.x, s.Pos.y, 0f ) );
					GameAchievements.PowerStored();   // 成就：累计捡 5 个道具入包（客户端侧计数）
				}
			}
		}

		_power?.ApplyPicked( slot );
	}

	public void OnPowerUpRespawnedRemote( int slot, byte kind, float x, float y )
	{
		if ( NetworkManager.IsAuthority ) return;
		_power?.ApplyRespawned( slot, kind, new Vector2( x, y ) );
	}

	/// <summary> 客户端个人高光横幅落地（host 已本地播过，回声挡掉；只播"是我的"那条） </summary>
	public void OnPowerBannerRemote( byte kind, long ownerSteamId, byte mode )
	{
		if ( NetworkManager.IsAuthority ) return;
		if ( !GameSfx.IsMine( ownerSteamId ) ) return;

		var text = mode == 2 ? $"{PowerUpManager.NameOf( kind )} READY (Q)"   // Q 充能到点（v0.7.8.31）
			: mode == 0 ? $"{PowerUpManager.NameOf( kind )} STORED" : GameHud.ActivationText( kind );
		_hud?.AddBanner( text, GameHud.KindColor( kind ) );
		GameSfx.Pickup();

		if ( mode == 1 ) GameAchievements.FeedTriggered( ownerSteamId );   // 成就：首次喂食触发（mode1=激活/喂养）
	}

	/// <summary> 客户端应用死亡爆裂特效（host 已在本地生成过，回声用 IsAuthority 挡掉）。
	/// 视觉全场播；爆裂音只播自己死亡/爆裂的（ownerSteamId 匹配本机） </summary>
	public void OnPoppedEffectRemote( Vector3 pos, byte colorIndex, float radius, long ownerSteamId )
	{
		if ( NetworkManager.IsAuthority ) return;
		NeonRenderer.Pop( pos, colorIndex, radius );
		if ( GameSfx.IsMine( ownerSteamId ) )
			GameSfx.Pop( pos );
	}

	/// <summary> 客户端吞球事件（host 回声用 IsAuthority 挡掉——host 已在 NotifyBallEaten 直调过） </summary>
	public void OnBallEatenRemote( long eaterSteamId, long eatenSteamId, float massGained )
	{
		if ( NetworkManager.IsAuthority ) return;
		OnBallEaten( eaterSteamId, eatenSteamId, massGained );
	}

	void CreateBackdrop()
	{
		var go = new GameObject( true, "GridBackdrop" );
		go.Parent = _root;
		go.AddComponent<GridBackdrop>();
		go.AddComponent<TerritoryRenderer>();   // 领土格底色/边界/旗标（M7；非领土模式自清空不画）
	}

	/// <summary> 全场景一个的像素批量画笔：每帧把所有球/分身写进各批次顶点（v0.7.8.40 分身头像也并入） </summary>
	void CreateNeon()
	{
		var go = new GameObject( true, "Neon" );
		go.Parent = _root;
		go.AddComponent<NeonRenderer>();
	}

	void CreateCamera()
	{
		// 防御：热重载/重入可能残留旧相机——两个 IsMainCamera 会抢渲染，而旧的那个
		// 姿态已死（NeonCamera 缓存的 _target 失效早退），渲染选中它就是画面僵死不跟随
		foreach ( var c in Scene.GetAllComponents<CameraComponent>().ToArray() )
		{
			if ( c.IsValid() && c.GameObject.IsValid() )
			{
				GameLog.Info( "[game] destroying stale camera" );
				c.GameObject.Destroy();
			}
		}

		var go = new GameObject( true, "Camera" );
		go.Parent = _root;

		var cam = go.AddComponent<CameraComponent>();
		cam.IsMainCamera = true;
		cam.Priority = -100;
		cam.Orthographic = true;
		cam.OrthographicHeight = GameConfig.CameraBaseView;
		cam.ClearFlags = ClearFlags.All;
		cam.BackgroundColor = new Color( 0.918f, 0.957f, 0.988f );   // #EAF4FC 浅蓝底（像素风，v0.7.8.36）
		cam.EnablePostProcessing = true;

		// Bloom 保留组件（cr_bloom convar/设置页还管它），默认关——加色泛光会糊掉糖果色与像素颗粒
		var bloom = go.AddComponent<Bloom>();
		bloom.Mode = SceneCamera.BloomAccessor.BloomMode.Additive;
		bloom.Strength = 1.5f;
		bloom.Threshold = 0.55f;

		// 像素风去掉 ACES 色调映射（会把马卡龙色压灰），保持贴图原色
		_camera = go.AddComponent<NeonCamera>();
		_camera.Cam = cam;
	}

	void CreateHud()
	{
		var go = new GameObject( true, "Hud" );
		go.Parent = _root;
		go.AddComponent<ScreenPanel>();
		_hud = go.AddComponent<GameHud>();

		// 结算面板（M5）：独立 ScreenPanel 叠在 HUD 之上，平时隐藏
		var ego = new GameObject( true, "EndBoard" );
		ego.Parent = _root;
		ego.AddComponent<ScreenPanel>();
		_endBoard = ego.AddComponent<EndBoard>();
	}

	/// <summary> 主菜单（开局后由 StartGame 禁用整个 GameObject） </summary>
	void CreateMenu()
	{
		var go = new GameObject( true, "MainMenu" );
		go.Parent = _root;
		go.AddComponent<ScreenPanel>();
		_menu = go.AddComponent<MainMenu>();
	}

	/// <summary> 游戏房间（M5 二级界面）：HOST/VS BOTS 开房后显示，START/进局后隐藏 </summary>
	void CreateLobbyPanel()
	{
		var go = new GameObject( true, "LobbyPanel" );
		go.Parent = _root;
		go.AddComponent<ScreenPanel>();
		_lobby = go.AddComponent<LobbyPanel>();
	}

	/// <summary> 设置页（v0.7.8.16 二级界面）：主菜单 [4] 进入——泛光开关/音乐音量，BACK 回菜单 </summary>
	void CreateSettingsPanel()
	{
		var go = new GameObject( true, "SettingsPanel" );
		go.Parent = _root;
		go.AddComponent<ScreenPanel>();
		_settings = go.AddComponent<SettingsPanel>();
	}

	/// <summary> 排行页（v0.7.8.19 二级界面）：主菜单 [5] 进入——全球 XP 榜，BACK 回菜单 </summary>
	void CreateRankingsPanel()
	{
		var go = new GameObject( true, "RankingsPanel" );
		go.Parent = _root;
		go.AddComponent<ScreenPanel>();
		_rankings = go.AddComponent<RankingsPanel>();
	}

	/// <summary> 房间列表页（v0.6.7.0）：主菜单 [2] JOIN GAME 打开，列出 Steam 公开大厅 </summary>
	void CreateRoomBrowser()
	{
		var go = new GameObject( true, "RoomBrowser" );
		go.Parent = _root;
		go.AddComponent<ScreenPanel>();
		_browser = go.AddComponent<RoomBrowser>();
	}

	/// <summary>
	/// 生成一颗球。M0 直接代码建实体；M2 起真人球优先用 Ball 预制体
	/// （用户在编辑器创建，代码按路径 Clone，缺失时回退代码构建）。
	/// </summary>
	/// <summary>
	/// 生成一颗球（仅权威端调用）：优先克隆用户创建的 ball.prefab（根节点需挂 Ball），
	/// 缺失回退纯代码构建。owner=null → host 拥有（房主自己/bot）；
	/// 传连接 → 该客户端拥有，移动由其本地模拟（owner 模拟架构）。
	/// dormant=true：房间蛰伏球（Alive=false 不可见，开局由 StartMatchFromLobby 激活）——
	/// 引擎"进行中 NetworkSpawn 不复制到已连接客户端"，球必须在各端快照前存在。
	/// </summary>
	public Ball SpawnBall( Vector3 position, bool isBot, Connection owner = null, bool dormant = false )
	{
		// 给远程连接生成的球必须网络化才有意义：会话未激活时 NetworkSpawn 会被跳过，
		// 球沦为 host 本地幽灵球（不广播 → 客户端收不到；proxy=false → 干扰本机球判定）——实测大坑。
		// OnActive 竞态（大厅重建中触发回调）就落在这里，直接拒绝生成。
		if ( owner is not null && !Networking.IsActive )
		{
			Log.Warning( "[game] SpawnBall: session not active, refuse ghost ball for remote owner" );
			return null;
		}

		GameObject go = null;
		Ball ball = null;

		var prefab = PrefabFile.Load( GameConfig.BallPrefabPath );
		if ( prefab is not null )
		{
			go = GameObject.Clone( GameConfig.BallPrefabPath, new Transform( position ), _root );
			ball = go.Components.Get<Ball>();
			if ( ball is null )
			{
				go.Destroy();   // 预制体根节点没挂 Ball，视为无效配置，回退代码构建
				go = null;
			}
		}

		if ( go is null )
		{
			go = new GameObject( true, isBot ? "Bot" : "Player" );
			go.Parent = _root;
			go.WorldPosition = position;
			ball = go.AddComponent<Ball>();
		}

		ball.PlayerName = isBot ? NextBotName() : ( owner?.DisplayName ?? LocalPlayerName() );
		ball.OwnerSteamId = owner?.SteamId.Value ?? ( isBot ? NextBotSteamId() : Game.SteamId.Value );
		ball.IsRemoteOwned = owner is not null;   // host 侧：远程连接的球不参与本机球判定
		// 队伍分配：蛰伏球（房间阶段）不占队号，开局激活时统一按块分队；
		// 对局中途加入的球（dormant=false 且比赛已开始）补分到下一个块（团队赛，v0.6.5.0）
		ball.Init( isBot, !dormant && _gameStarted && MatchState.IsTeam ? NextTeamIndex() : -1 );
		ball.Dormant = dormant;
		if ( dormant ) ball.Alive = false;   // 必须在 NetworkSpawn 之前置 false，随初始快照下发

		if ( isBot ) go.AddComponent<BotBrain>();

		// host 侧直接注册（Ball.OnStart 在网络对象上不触发，见 ScanBalls 兜底注释）
		_balls.Add( ball );
		if ( !isBot && owner is null )
			SetLocalBall( ball );

		go.NetworkMode = NetworkMode.Object;
		if ( Networking.IsActive )
			go.NetworkSpawn( owner );

		return ball;
	}

	/// <summary> 取一个未被占用的 bot 名（从游标起扫，跳过场上任何球——含蛰伏/尸体——正用着的；
	/// 48 满编小于 64 名字池，正常永远取得到；池子真被占满才循环复用兜底） </summary>
	string NextBotName()
	{
		for ( int i = 0; i < GameConfig.BotNames.Length; i++ )
		{
			var name = GameConfig.BotNames[( _botNameIndex + i ) % GameConfig.BotNames.Length];
			if ( FindBallByPlayerName( name ) is null )
			{
				_botNameIndex += i + 1;
				return name;
			}
		}
		return GameConfig.BotNames[_botNameIndex++ % GameConfig.BotNames.Length];
	}

	/// <summary> 按名字找球（bot 取名查重用） </summary>
	public Ball FindBallByPlayerName( string name )
	{
		foreach ( var b in _balls )
		{
			if ( b.IsValid() && b.PlayerName == name ) return b;
		}
		return null;
	}

	int _botSeq;   // bot 独立假 SteamId 序列（配合 BotSteamIdBase）

	/// <summary> 分配一个未被占用的 bot 假 SteamId（热重载会重置 _botSeq，扫描查重防碰撞） </summary>
	long NextBotSteamId()
	{
		var id = GameConfig.BotSteamIdBase + _botSeq++;
		while ( FindBallBySteamId( id ) is not null )
			id = GameConfig.BotSteamIdBase + _botSeq++;
		return id;
	}

	/// <summary> 按 SteamId 找球（分身头像/名牌归属解析用；bot 现在各有独立假 SteamId） </summary>
	public Ball FindBallBySteamId( long steamId )
	{
		foreach ( var b in _balls )
		{
			if ( b.IsValid() && b.OwnerSteamId == steamId ) return b;
		}
		return null;
	}

	/// <summary> 全场总质量（主球+分身）最高的活球——菜单演示赛观战镜头/死亡观战用 </summary>
	public Ball TopMassBall()
	{
		Ball best = null;
		float bestMass = -1f;
		foreach ( var b in _balls )
		{
			if ( !b.IsValid() || !b.Alive ) continue;

			var m = TotalMassFor( b );
			if ( m <= bestMass ) continue;

			bestMass = m;
			best = b;
		}
		return best;
	}

	/// <summary> 球销毁时由 Ball.OnDestroy 回调注销 </summary>
	public void RemoveBall( Ball ball )
	{
		_balls.Remove( ball );
		_lastPos.Remove( ball.Id );
		_teleportGrace.Remove( ball.Id );
		_steerDir.Remove( ball.Id );
		_sinceEject.Remove( ball.Id );
		if ( LocalBall == ball ) LocalBall = null;
	}

	/// <summary>
	/// 结算结束（自动 10 秒 / 空格提前）：多人回 **host 的房间**（会话保留，等下一局），
	/// 单机回主菜单。
	/// </summary>
	public void AfterSettlement()
	{
		if ( Networking.IsActive )
			ResetToLobby();
		else
			ResetToMenu();
	}

	/// <summary>
	/// 回到房间（多人，v0.6.4.2 用户定稿）：**会话保留不清**——host 回房间控制端
	/// （可调参、再 START 开下一局），客户端回房间等待页（RequestMatchState 拉玩家列表）。
	/// 上一局的球留着当背景（下局 START 时统一销毁重建），模拟已冻结不会继续吃。
	/// </summary>
	public void ResetToLobby()
	{
		MatchState.Reset();
		_matchRunning = false;
		_joinCountdown = -1f;
		_gameStarted = false;

		_endBoard?.Hide();
		if ( _hud.IsValid() ) _hud.GameObject.Enabled = false;
		_menu?.Hide();

		if ( NetworkManager.IsAuthority )
		{
			_lobbyOpen = true;
			_lobby?.ShowHost();
			_net?.ResetSession();
			MarkLobbyDirty();   // 广播房间玩家列表
		}
		else
		{
			EnterClientWaiting();
			_browser?.Hide();
			_lobby?.ShowClient();
			NetworkManager.RequestMatchState();
		}

		GameMusic.PlayMenu();   // 回大厅回到菜单曲
		GameLog.Info( $"[match] back to lobby (auth={NetworkManager.IsAuthority})" );
	}

	/// <summary>
	/// 结算后回主菜单（M5）：断网、清场（球销毁/分身清空/比赛态复位）、菜单重出；
	/// 世界壳（网格/相机）留着当菜单背景，下次开局沿用（StartGame 复用 HUD）。
	/// </summary>
	public void ResetToMenu()
	{
		try
		{
			if ( Networking.IsActive ) Networking.Disconnect();
		}
		catch
		{
			// 断不开也无妨：host 侧下局沿用旧会话照常能开
		}

		foreach ( var b in _balls.ToArray() )
		{
			try
			{
				if ( b.IsValid() ) b.GameObject.Destroy();
			}
			catch ( Exception e )
			{
				// 客户端销毁 host 拥有的对象可能被引擎拒绝——不能让它中断回菜单流程（实测过卡住不返回）
				Log.Warning( $"[menu] destroy ball failed: {e.Message}" );
			}
		}
		_balls.Clear();
		_cells.Clear();
		_lastPos.Clear();
		_teleportGrace.Clear();
		_steerDir.Clear();
		_sinceEject.Clear();
		_power?.Reset();
		_teamSeq = 0;
		LocalBall = null;

		_gameStarted = false;
		_lobbyOpen = false;
		_lobbyWaiting = false;
		_lobbyHeartbeatArmed = false;
		_lobbyListDirty = false;
		_joinRequested = false;
		_networkReady = false;
		_joinCountdown = -1f;
		_matchRunning = false;
		MatchState.Reset();

		_net?.ResetSession();
		_lobby?.Hide();
		_browser?.Hide();
		_endBoard?.Hide();
		if ( _hud.IsValid() ) _hud.GameObject.Enabled = false;
		_menu?.Show();
		GameMusic.PlayMenu();
		GameLog.Info( "[menu] back to menu" );

		// 菜单背景演示赛重新开起来
		if ( NetworkManager.IsAuthority && !Networking.IsActive )
			StartMenuDemo();
	}
}