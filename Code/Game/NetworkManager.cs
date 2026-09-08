using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 联机管理（Component.INetworkListener）：**本机本地组件，自身不联网**（此前挂在网络对象上
/// 复制不到客户端，导致食物/重生全废——实测）。食物与重生同步走**静态 RPC**（不依赖任何对象）。
/// 权威端（编辑器直接 Play 或房主）：创建公开大厅（24 人）、审批连接、为每个连入的客户端
/// 生成归它所有的球（NetworkSpawn(channel) → 该端本地 IsProxy=false，owner 本地模拟）；
/// OnActive 时把当前食物全量定向发给新连接；断线时销毁其球。
/// 客户端：通过静态 RequestRespawn RPC 请求重生（host 用 Rpc.Caller 找回连接）。
/// </summary>
public sealed class NetworkManager : Component, Component.INetworkListener
{
	/// <summary> 全局引用 </summary>
	public static NetworkManager Instance { get; private set; }

	/// <summary> 本机是否权威端：未联网的编辑器 Play 或房主 </summary>
	public static bool IsAuthority => !Networking.IsActive || Networking.IsHost;

	bool _hosted;

	protected override void OnStart()
	{
		base.OnStart();
		Instance = this;
		Log.Info( $"[net] OnStart authority={IsAuthority} active={Networking.IsActive} host={Networking.IsHost}" );
	}

	/// <summary>
	/// 未联网则创建公开大厅并生成房主球 + bot；已联网（作为客户端加入）则什么都不生成。
	/// 由 CircleroyaleGame.EnsureWorld 显式调用。
	/// createLobby=false：**人机对战**——不建大厅直接离线开局（IsAuthority 恒 true，一切照旧）。
	/// ⚠️ CreateLobby 异步生效：必须等 Networking.IsActive 翻转后再生成对象，
	/// 否则 NetworkSpawn 被跳过、球变成纯本地对象永不复制（实测大坑）。
	/// ⚠️ 客户端（已联网非房主）不做任何 host 流程——此前没拦，客户端也会打出
	/// "host ball + bots spawned" 一整套误导日志（实测）。
	/// </summary>
	public void RunHostFlow( bool createLobby = true )
	{
		if ( !IsAuthority )
		{
			Log.Info( "[net] client mode — skip host flow" );
			return;
		}

		if ( _hosted ) return;   // 防重入
		_hosted = true;

		RunHostFlowAsync( createLobby );
	}

	async void RunHostFlowAsync( bool createLobby )
	{
		try
		{
			if ( createLobby && !Networking.IsActive )
			{
				Networking.CreateLobby( new global::Sandbox.Network.LobbyConfig
				{
					Privacy = global::Sandbox.Network.LobbyPrivacy.Public,
					MaxPlayers = GameConfig.MaxPlayers,
					// 房间名带房主名——房间列表页展示用（v0.6.7.0）
					Name = LobbyDisplayName(),
				} );
				Log.Info( "[net] lobby creating (Public/24)..." );

				// 等会话真正激活（CreateLobby 异步生效），最多 30 秒
				int wait = 0;
				while ( !Networking.IsActive && wait < 300 )
				{
					await Task.DelayRealtimeSeconds( 0.1f );
					wait++;
				}
				Log.Info( $"[net] session active={Networking.IsActive} (waited {wait * 0.1f:0.0}s)" );
			}
			else if ( !createLobby )
			{
				Log.Info( "[net] offline room (no lobby)" );
			}

			// M5：开局不再自动——进房间（蛰伏球就位），host 点 START 才 StartMatchFromLobby
			CircleroyaleGame.Current?.OnLobbyOpened();
			Log.Info( "[net] room ready — waiting for host to start" );
		}
		catch ( Exception e )
		{
			Log.Info( $"[net] host flow failed: {e.Message} — 离线兜底" );
			CircleroyaleGame.Current?.OnLobbyOpened();
		}
	}

	/// <summary>
	/// 主菜单"快速加入"（v0.6.7.0 起为房间列表页服务）：查询本游戏的大厅 →
	/// 加入人数最多且未满的房间。失败/无房回报浏览器页提示，**不再自动回退连 local**
	/// （同机调试直连由页面上的 JOIN LOCAL 按钮显式负责）。
	/// 官方同款路径见 addons/menu/Code/MenuHelpers.cs（QueryLobbies + TryConnectSteamId）。
	/// </summary>
	public async void QuickJoin()
	{
		EnsureDisconnected();

		try
		{
			var lobbies = await Networking.QueryLobbies();

			global::Sandbox.Network.LobbyInformation? best = null;
			foreach ( var l in lobbies )
			{
				if ( l.IsFull ) continue;
				if ( best is null || l.Members > best.Value.Members ) best = l;
			}

			if ( best is not null )
			{
				var b = best.Value;
				Log.Info( $"[net] quick join '{b.Name}' members={b.Members}/{b.MaxMembers}" );
				_lastJoinedLobby = b;   // 记住目标：掉线自动重连走同一条 Steam 路径

				if ( await Networking.TryConnectSteamId( b.LobbyId ) )
					return;   // 成功：标准加入流接管（Tick 捕获会话激活 → 房间页）
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[net] quick join query failed: {e.Message}" );
		}

		CircleroyaleGame.Current?.OnJoinAttemptFailed( "NO ROOM JOINED — TRY AGAIN" );
	}

	/// <summary>
	/// 房间列表点击加入（v0.6.7.0）：按大厅 SteamId 直连（走 Steam 中继，跨机器可用）。
	/// 成功后标准加入流接管；失败回报浏览器页提示。
	/// </summary>
	public async void JoinLobby( global::Sandbox.Network.LobbyInformation lobby )
	{
		EnsureDisconnected();
		_lastJoinedLobby = lobby;   // 记住目标：掉线自动重连走同一条 Steam 路径
		CircleroyaleGame.Current?.OnJoinAttemptStarted();

		try
		{
			Log.Info( $"[net] joining lobby '{lobby.Name}' ({lobby.Members}/{lobby.MaxMembers})" );

			if ( await Networking.TryConnectSteamId( lobby.LobbyId ) )
				return;

			Log.Warning( "[net] join lobby failed" );
			CircleroyaleGame.Current?.OnJoinAttemptFailed( "COULDN'T JOIN THAT ROOM — TRY AGAIN" );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[net] join lobby error: {e.Message}" );
			CircleroyaleGame.Current?.OnJoinAttemptFailed( "JOIN ERROR — TRY AGAIN" );
		}
	}

	/// <summary> 连接前掐掉残留会话（否则 TryConnectSteamId/Connect 静默失败——实测坑） </summary>
	void EnsureDisconnected()
	{
		if ( !Networking.IsActive ) return;

		try
		{
			Networking.Disconnect();
		}
		catch
		{
			// 断不开也无妨：最坏就是这次加入失败，有超时兜底
		}
		Log.Info( "[net] dropped stale session before connect" );
	}

	/// <summary> 公开大厅：默认放行（返回 false 可拒绝连接） </summary>
	public bool AcceptConnection( Connection channel, string reason ) => true;

	/// <summary> 主菜单"加入"：连到指定地址（local = 本机房主）。失败由 CircleroyaleGame 的超时兜底显示 </summary>
	public void StartJoin( string address )
	{
		// 残留会话（上局当过 host/加入没断干净）：不掐掉会一直静默失败
		// （实测：编辑器当过一次 host 后再点 JOIN，反复刷 "session already active, skip connect"）
		EnsureDisconnected();

		try
		{
			Networking.Connect( address );
			Log.Info( $"[net] connecting to '{address}' ..." );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[net] connect to '{address}' failed: {e.Message}" );
		}
	}

	/// <summary> 大厅显示名：CIRCLEROYALE — 房主名（会话未激活时 Local 可能为空，回退常量名） </summary>
	string LobbyDisplayName()
	{
		try
		{
			var host = Connection.Local?.DisplayName;
			return string.IsNullOrWhiteSpace( host ) ? "CIRCLEROYALE" : $"CIRCLEROYALE — {host}";
		}
		catch
		{
			return "CIRCLEROYALE";
		}
	}

	/// <summary>
	/// 新客户端握手到 Welcome 阶段（仅 host 收到；早于该客户端请求初始快照）：
	/// 把生成请求**入队**，由主线程 OnUpdate 下一帧生成归它所有的球——球会随初始快照到达客户端。
	/// ⚠️ 本回调跑在网络线程：直接 SpawnBall（new GameObject/加载 prefab）会随机抛
	/// ".ctor must be called on the main thread!"（线程调度时好时坏，2026-09-05 实测炸穿握手，
	/// 客户端跟着在 MountedVPKs 阶段抛 UnauthorizedAccessException 断线）。
	/// ⚠️ 也不要放到 OnActive（快照拍完后）才生成：live create 广播到不了刚加入的连接，
	/// 而初始快照/定向 RPC 都通（2026-09-05 双开实测）。
	/// </summary>
	public void OnConnected( Connection channel )
	{
		Log.Info( $"[net] OnConnected '{channel.DisplayName}' id={channel.Id}" );

		// host 自己的本地连接：引擎在 ClientInfo 处直接 return，不会走到这里；双保险
		if ( Connection.Local is not null && channel.Id == Connection.Local.Id ) return;

		// 主线程下一帧生成（0.1s << 客户端请求快照的 ~2s，球必然进快照）。
		// 房间阶段生成的是蛰伏球（Alive=false）；名单变化由主线程统一广播 LobbyState
		bool lobby = CircleroyaleGame.Current?.IsLobbyOpen ?? false;
		_pendingJoins.Add( new PendingJoin( channel, 0f, 0.1f, false, lobby ) );
		CircleroyaleGame.Current?.MarkLobbyDirty();
		Log.Info( $"[net] pre-snapshot ball queued for '{channel.DisplayName}' (dormant={lobby})" );
	}

	/// <summary> 该连接是否已有一颗活球（host 侧查表） </summary>
	bool HasBallFor( Connection channel )
	{
		var game = CircleroyaleGame.Current;
		if ( game is null ) return false;

		var steamId = channel.SteamId.Value;
		foreach ( var b in game.Balls )
		{
			if ( b.IsValid() && !b.IsBot && b.OwnerSteamId == steamId ) return true;
		}
		return false;
	}

	/// <summary> 新客户端完全连入（仅 host 收到）：食物全量立即定向；无球时兜底延后生成。
	/// ⚠️ 实测坑：引擎把 host 自己的本地连接也走一遍完整握手（每场开场都触发本回调），
	/// 它对应的球已由 OnNetworkReady 生成，这里再生成就是一颗无人模拟、相机不跟、
	/// 死了不重生的多余球（实测：每局开场双 OnStart 'host名'）——直接跳过。
	/// ⚠️ 另一个实测坑：无论本回调内同步生成、还是延后 0.8s 由主线程生成，live create 广播
	/// 都到不了刚加入的连接（快照/定向 RPC 通、唯独 create 丢）——所以球的生成放在更早的
	/// OnConnected（进初始快照），这里只兜底竞态。 </summary>
	public async void OnActive( Connection channel )
	{
		Log.Info( $"[net] OnActive '{channel.DisplayName}' id={channel.Id}" );

		// host 自己的本地连接：球由 OnNetworkReady 生成，跳过（见类注释）
		if ( Connection.Local is not null && channel.Id == Connection.Local.Id )
		{
			Log.Info( "[net] OnActive: host's own connection — skip (host ball spawned by OnNetworkReady)" );
			return;
		}

		// 热重启竞态防护：等会话激活后再继续（见 RunHostFlow 注释）
		int wait = 0;
		while ( !Networking.IsActive && wait < 100 )
		{
			await Task.DelayRealtimeSeconds( 0.1f );
			wait++;
		}

		// 等待期间连接可能已断开；游戏系统也可能已重建
		var game = CircleroyaleGame.Current;
		if ( game is null )
		{
			Log.Info( "[net] OnActive: no game, abort" );
			return;
		}

		// 食物全量定向同步给新连接（静态 RPC 定向：实测可靠，立即发）
		var foods = game.Food?.Foods;
		if ( foods is not null )
		{
			using ( Rpc.FilterInclude( channel ) )
			{
				FoodFull( foods );
			}
		}

		// 绿刺位置（静态数据全量）
		var spikes = game.Spikes;
		if ( spikes is not null )
		{
			using ( Rpc.FilterInclude( channel ) )
			{
				SpikesFull( spikes );
			}
		}

		// 道具全量（对局进行中加入的客户端；房间阶段槽未生成则发空，开局另有定向补发）
		var powerWires = game.PowerUps?.ToWireForSync();
		if ( powerWires is not null )
		{
			using ( Rpc.FilterInclude( channel ) )
			{
				PowerUpFull( powerWires );
			}
		}

		// 房间/对局分支（M5）：
		// - 房间开着 → 推房间信息（参数 + 玩家名单），客户端亮等待界面
		// - 对局已开始 → 同步玩家列表 + 推规则 + 直接通知开局（客户端房间页显示 3 秒加入倒计时后进入）
		if ( game.IsLobbyOpen )
		{
			using ( Rpc.FilterInclude( channel ) )
			{
				LobbyState( (byte)MatchState.PendingMode, (int)MatchState.PendingDuration,
					MatchState.PendingPlayerTarget, game.LobbyPlayerNames().ToArray() );
			}
		}
		else if ( game.IsMatchStarted )
		{
			using ( Rpc.FilterInclude( channel ) )
			{
				// 玩家列表先同步（房间页展示），再推规则与开局（客户端走 3 秒加入倒计时）
				LobbyState( (byte)MatchState.PendingMode, (int)MatchState.PendingDuration,
					MatchState.PendingPlayerTarget, game.LobbyPlayerNames().ToArray() );

				MatchSettings( (byte)MatchState.CurrentMode, (int)MatchState.Duration, MatchState.PlayerTarget );
				MatchStarted();
			}
		}

		// 玩家真正入局：刷新其球的出生保护——pre-snapshot 球在握手期间生成，3s 保护期
		// 在没人操控时就空跑完了（实测：加入 1.6s 即被 bot 吃掉）
		foreach ( var b in game.Balls )
		{
			if ( b.IsValid() && !b.IsBot && b.OwnerSteamId == channel.SteamId.Value )
				b.RefreshSpawnProtection();
		}

		// 球通常已在 OnConnected（快照前）生成；这里只兜底"当时世界未就绪"的竞态
		if ( !HasBallFor( channel ) )
		{
			_pendingJoins.Add( new PendingJoin( channel, 0f, 0.8f, true ) );
			Log.Info( $"[net] join queued for '{channel.DisplayName}' (fallback) — no pre-snapshot ball" );
		}
	}

	// ---- 加入者球的延迟生成 ----

	readonly struct PendingJoin
	{
		public readonly Connection Channel;
		public readonly TimeSince Since;
		public readonly float Delay;
		public readonly bool CheckActive;
		public readonly bool Dormant;      // 入队时是否房间阶段（蛰伏球）
		public PendingJoin( Connection channel, float start, float delay, bool checkActive, bool dormant = false )
		{
			Channel = channel; Since = start; Delay = delay; CheckActive = checkActive; Dormant = dormant;
		}
	}

	readonly List<PendingJoin> _pendingJoins = new();

	// ---- 客户端自动重连（M3，兜引擎握手偶发非主线程断言）----
	string _serverAddress;          // 见过就记：握手早期 HostConnection 就有值，首连失败也拿得到
	global::Sandbox.Network.LobbyInformation? _lastJoinedLobby;   // 房间列表加入的目标（v0.6.8.1）：
	                                                              // Steam 中继连接的 Host.Address 可能为空，
	                                                              // 重连改用它记住的大厅目标走 TryConnectSteamId
	TimeSince _sinceJoinStuck;
	int _reconnectAttempts;

	protected override void OnUpdate()
	{
		base.OnUpdate();

		TickDisconnectRecovery();
		TickReconnectWatchdog();

		if ( _pendingJoins.Count == 0 ) return;

		for ( int i = _pendingJoins.Count - 1; i >= 0; i-- )
		{
			var p = _pendingJoins[i];
			if ( p.Since < p.Delay ) continue;

			_pendingJoins.RemoveAt( i );
			SpawnJoinBall( p.Channel, p.CheckActive, p.Dormant );
		}
	}

	// ---- 客户端掉线自动重连（v0.6.4.6 用户定稿）：掉线 → 按 ReconnectRetrySeconds 重试 →
	//      耗尽 ReconnectMaxAttempts 次 → 回主菜单 ----

	/// <summary> 掉线自动重连进行中（CircleroyaleGame.ReconcileUi 据此把房间页留在原地） </summary>
	public static bool IsReconnecting => Instance is not null && Instance._reconnecting;

	bool _reconnecting;
	TimeSince _sinceReconnectAttempt;
	TimeSince _sinceReconnectStart;

	/// <summary>
	/// 掉线恢复：从会话里掉出来（房间等待/对局中都算）→ 定时重连，耗尽回调 OnReconnectFailed。
	/// 重连成功走标准加入流（NotifyReconnectStarted → 房间页 + RequestMatchState）。
	/// 菜单上主动断开（LEAVE/结算回菜单）经 ResetSession 清标记，不会触发重连。
	/// </summary>
	void TickDisconnectRecovery()
	{
		if ( Instance != this ) return;   // 快照带过来的 host 副本不跑本机流程（防双份重连）

		if ( Networking.IsActive || Networking.IsHost )
		{
			if ( !_reconnecting ) return;
			_reconnecting = false;
			Log.Info( "[net] reconnect: session active again" );
			CircleroyaleGame.Current?.NotifyReconnectStarted();
			return;
		}

		if ( !_reconnecting )
		{
			// 只有"刚才还在会话里"的掉线才重连（菜单上从没连上的归 JOIN FAILED 提示流管）
			if ( _serverAddress is null ) return;
			if ( !( CircleroyaleGame.Current?.IsClientInSession ?? false ) ) return;

			_reconnecting = true;
			_reconnectAttempts = 0;
			_sinceReconnectStart = 0;
			_sinceReconnectAttempt = 999f;   // 立即发起首次尝试
			Log.Warning( "[net] disconnected from host — auto reconnect starting" );
			CircleroyaleGame.Current?.OnDisconnectRecoveryStarted();
			return;
		}

		// 总时长上限（v0.6.8.0）：单次 Connect 可能长时间挂在"正在握手"上，
		// 不管走到哪一步，超过上限一律放弃回主菜单（防个别尝试挂死导致永远不返回）
		if ( _sinceReconnectStart > GameConfig.ReconnectGiveUpSeconds )
		{
			_reconnecting = false;
			Log.Warning( "[net] reconnect window exceeded — giving up, back to menu" );
			CircleroyaleGame.Current?.OnReconnectFailed();
			return;
		}

		if ( Networking.IsConnecting ) return;   // 上一次连接还在握手，别叠加

		if ( _reconnectAttempts >= GameConfig.ReconnectMaxAttempts )
		{
			_reconnecting = false;
			Log.Warning( "[net] reconnect exhausted — giving up, back to menu" );
			CircleroyaleGame.Current?.OnReconnectFailed();
			return;
		}

		if ( _sinceReconnectAttempt < GameConfig.ReconnectRetrySeconds ) return;

		_reconnectAttempts++;
		_sinceReconnectAttempt = 0;

		// 首选房间列表记住的大厅目标（Steam 中继连接的 Host.Address 可能为空，实测重连没启动）；
		// local 直连的旧路径保留（Address 有值就用）
		if ( _lastJoinedLobby is not null )
		{
			var lobby = _lastJoinedLobby.Value;
			Log.Warning( $"[net] reconnect attempt {_reconnectAttempts}/{GameConfig.ReconnectMaxAttempts} → steam lobby '{lobby.Name}'" );
			_ = Networking.TryConnectSteamId( lobby.LobbyId );
			return;
		}

		Log.Warning( $"[net] reconnect attempt {_reconnectAttempts}/{GameConfig.ReconnectMaxAttempts} → {_serverAddress}" );
		try
		{
			Networking.Connect( _serverAddress );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[net] reconnect connect failed: {e.Message}" );
		}
	}

	/// <summary>
	/// 客户端握手卡死自动重连：连入后 ReconnectAfterSeconds 秒仍没有自己的球 →
	/// 拆掉僵尸连接（重连发起与"耗尽回主菜单"统一由 TickDisconnectRecovery 处理）。
	/// 仅客户端跑；成功入局（本机球就位）即复位计数。已入局后的掉线/僵尸态不归它管
	/// （host 停 Play 的 TCP 僵尸无代码可救，不在这里空转重试）。
	/// </summary>
	void TickReconnectWatchdog()
	{
		if ( Instance != this ) return;   // 快照带过来的 host 副本不跑本机流程
		if ( !Networking.IsActive || Networking.IsHost ) return;

		// 随手记服务器地址（连接对象一出现就有值）
		var addr = Connection.Host?.Address;
		if ( !string.IsNullOrEmpty( addr ) ) _serverAddress = addr;

		var joined = CircleroyaleGame.Current?.LocalBall.IsValid() ?? false;
		if ( joined )
		{
			if ( _reconnectAttempts > 0 ) Log.Info( "[net] auto-reconnect: joined OK" );
			_reconnectAttempts = 0;
			_sinceJoinStuck = 0;
			return;
		}

		if ( Networking.IsConnecting ) return;   // 正在握手，别打断
		if ( _serverAddress is null ) return;    // 没拿到过地址，无从重连
		if ( _reconnectAttempts >= GameConfig.ReconnectMaxAttempts ) return;
		if ( _sinceJoinStuck < GameConfig.ReconnectAfterSeconds ) return;

		var stuck = _sinceJoinStuck;
		_reconnectAttempts++;
		_sinceJoinStuck = 0;
		Log.Warning( $"[net] join stuck {stuck:0}s without own ball — tearing down for reconnect {_reconnectAttempts}/{GameConfig.ReconnectMaxAttempts}" );

		try
		{
			Networking.Disconnect();
		}
		catch
		{
			// 拆不掉也照办（TickDisconnectRecovery 的重试循环兜着）
		}
		// 重连发起与"耗尽回主菜单"统一交给 TickDisconnectRecovery（!IsActive 分支）
	}

	/// <summary> 出队后真正生成加入者的球（主线程、普通 tick 上下文）。
	/// checkActive：OnActive 兜底路径要查（连接真断了就别生成）；OnConnected 阶段
	/// **不能查**——Welcome 时 IsActive 本来就是 false，查了必误杀（2026-09-05 实测），
	/// 生成早了没关系，真断线由 OnDisconnected 按 SteamId 清理。
	/// dormant：房间阶段生成蛰伏球（Alive=false，开局激活），之后要在名单里亮出来。 </summary>
	void SpawnJoinBall( Connection channel, bool checkActive, bool dormant = false )
	{
		if ( checkActive && !channel.IsActive )
		{
			Log.Info( "[net] join ball: connection gone, skip" );
			return;
		}

		var game = CircleroyaleGame.Current;
		if ( game is null )
		{
			Log.Info( "[net] join ball: no game, skip" );
			return;
		}

		var ball = game.SpawnBall( game.RandomSpawnPos(), isBot: false, owner: channel, dormant: dormant );
		Log.Info( $"[net] join ball for '{channel.DisplayName}': ball={( ball is null ? "REFUSED" : "ok" )} dormant={dormant} netRoot={( ball?.GameObject.IsNetworkRoot ?? false )}" );

		if ( dormant ) game.MarkLobbyDirty();   // 名单亮出新人（本地面板轮刷也看得到，这里管推送）
	}

	/// <summary> 断线：销毁其球（销毁会同步到所有端），分身随葬，bot 由 TickBots 回填。
	/// 房间阶段还要把名单变化推给剩下的人（置脏标，主线程广播） </summary>
	public void OnDisconnected( Connection channel )
	{
		var game = CircleroyaleGame.Current;
		if ( game is null ) return;

		foreach ( var b in game.Balls.ToArray() )
		{
			if ( b.IsValid() && !b.IsBot && b.OwnerSteamId == channel.SteamId.Value )
			{
				b.GameObject.Destroy();
			}
		}
		game.PopPiecesOf( channel.SteamId.Value );

		if ( game.IsLobbyOpen ) game.MarkLobbyDirty();
	}

	/// <summary> 回主菜单：复位 host 流程与重连看门狗（下次开局重新走 RunHostFlow/OnNetworkReady） </summary>
	public void ResetSession()
	{
		_hosted = false;
		_pendingJoins.Clear();
		_reconnectAttempts = 0;
		_sinceJoinStuck = 0;
		_reconnecting = false;
		Log.Info( "[net] session reset" );
	}

	// ---- 食物与重生：静态 RPC（不依赖任何网络对象）----

	/// <summary> host 吃判定后广播：某食物被吃（eaterSteamId 供客户端判断是否自己的嘴，播吃音） </summary>
	[Rpc.Broadcast]
	public static void FoodEaten( int index, long eaterSteamId = 0 ) => CircleroyaleGame.Current?.OnFoodEatenRemote( index, eaterSteamId );

	/// <summary> host 重生食物后广播：新位置/颜色 </summary>
	[Rpc.Broadcast]
	public static void FoodRespawned( int index, FoodData food ) => CircleroyaleGame.Current?.OnFoodRespawnedRemote( index, food );

	/// <summary> 新连接加入时定向发送：食物全量 </summary>
	[Rpc.Broadcast]
	public static void FoodFull( FoodData[] foods ) => CircleroyaleGame.Current?.OnFoodFullRemote( foods );

	/// <summary> 绿刺位置（host 生成后广播 + 新连接定向补发；静态数据，此后不变） </summary>
	[Rpc.Broadcast]
	public static void SpikesFull( Vector2[] spikes ) => CircleroyaleGame.Current?.OnSpikesFullRemote( spikes );

	/// <summary> 道具全量（host 开局广播 + 新连接定向补发；固定槽位数组，幂等） </summary>
	[Rpc.Broadcast]
	public static void PowerUpFull( PowerUpWire[] slots ) => CircleroyaleGame.Current?.OnPowerUpFullRemote( slots );

	/// <summary> 道具被捡（host 拾取判定后广播；客户端放特效并置空槽） </summary>
	[Rpc.Broadcast]
	public static void PowerUpPicked( int slot, byte kind, long pickerSteamId ) => CircleroyaleGame.Current?.OnPowerUpPickedRemote( slot, kind, pickerSteamId );

	/// <summary> 道具重刷（host 延时补位后广播） </summary>
	[Rpc.Broadcast]
	public static void PowerUpRespawned( int slot, byte kind, float x, float y ) => CircleroyaleGame.Current?.OnPowerUpRespawnedRemote( slot, kind, x, y );

	/// <summary> 重生提示音广播（host 复活一颗球后）：各端按"队友才响"落地（v0.7.8.11） </summary>
	[Rpc.Broadcast]
	public static void RespawnCue( long steamId, int teamIndex ) => CircleroyaleGame.Current?.OnRespawnCueRemote( steamId, teamIndex );

	/// <summary> 尖刺分身炸大奖广播（host 判定后）：成就解锁在尖刺主人的机器上落地（v0.7.8.13） </summary>
	[Rpc.Broadcast]
	public static void SpikeReward( long ownerSteamId ) => GameAchievements.OnSpikeRewardRemote( ownerSteamId );

	/// <summary> 个人高光横幅广播（v0.7.6.0）：mode 0=存入背包 / 1=喂养触发激活——
	/// host 本地已播，客户端按 ownerSteamId 判"是不是自己的"再显示 </summary>
	[Rpc.Broadcast]
	public static void PowerBanner( byte kind, long ownerSteamId, byte mode ) => CircleroyaleGame.Current?.OnPowerBannerRemote( kind, ownerSteamId, mode );

	/// <summary> 死亡/爆裂特效广播（不可靠传输——丢一帧特效无所谓，不占可靠通道）。
	/// ownerSteamId = 死亡实体的主人，客户端据此判断是否自己死亡/爆裂（只播自己的爆裂音） </summary>
	[Rpc.Broadcast( NetFlags.Unreliable )]
	public static void PoppedEffect( Vector3 pos, byte colorIndex, float radius, long ownerSteamId ) => CircleroyaleGame.Current?.OnPoppedEffectRemote( pos, colorIndex, radius, ownerSteamId );

	/// <summary> 吞球广播（eater/eaten 的 SteamId 供客户端判断自己是否参与，播吞球音 + 击杀播报） </summary>
	[Rpc.Broadcast]
	public static void BallEaten( long eaterSteamId, long eatenSteamId, float massGained ) => CircleroyaleGame.Current?.OnBallEatenRemote( eaterSteamId, eatenSteamId, massGained );

	// ---- 比赛规则与进程（M5）----

	/// <summary> 比赛规则下发（host 开局广播 + 新连接定向补发；一次性、幂等） </summary>
	[Rpc.Broadcast]
	public static void MatchSettings( byte mode, int durationSeconds, int playerTarget ) => MatchState.Apply( mode, durationSeconds, playerTarget );

	/// <summary> 倒计时校时（1Hz 不可靠广播；客户端本地每帧续走） </summary>
	[Rpc.Broadcast( NetFlags.Unreliable )]
	public static void MatchTick( float timeLeft ) => MatchState.ApplyTick( timeLeft );

	/// <summary> 比赛结束 + 结算榜（host 排好序广播；各端展示并各自上传成绩） </summary>
	[Rpc.Broadcast]
	public static void MatchOver( ScoreWire[] standings ) => CircleroyaleGame.Current?.OnMatchOverRemote( standings );

	/// <summary> 房主点了 START：客户端出房间进游戏（OnActive 对中途加入者也有定向版） </summary>
	[Rpc.Broadcast]
	public static void MatchStarted() => CircleroyaleGame.Current?.OnMatchStartedRemote();

	/// <summary> host 按 P 快速退局（v0.6.9.0）：广播取消——所有客户端一起回房间等待页 </summary>
	[Rpc.Broadcast]
	public static void MatchCancelled() => CircleroyaleGame.Current?.OnMatchCancelledRemote();

	/// <summary> 房间信息推送（参数 + 玩家名单；名单/参数变化与新人 OnActive 定向共用）。
	/// names[0] 恒为房主。 </summary>
	[Rpc.Broadcast]
	public static void LobbyState( byte mode, int durationSeconds, int playerTarget, string[] names ) => CircleroyaleGame.Current?.OnLobbyStateRemote( mode, durationSeconds, playerTarget, names );

	/// <summary> 主球被吃后最大分身晋升（v0.6.2.0）：定向发给该玩家——把主球瞬移到晋升位置
	/// 继续 owner 模拟（质量经 [Sync] 自动同步）。host 自己的球不走此 RPC（本地已直接设位）。 </summary>
	[Rpc.Broadcast]
	public static void MainPromoted( float x, float y ) => CircleroyaleGame.Current?.OnMainPromotedRemote( x, y );

	/// <summary> 客户端请求当前对局状态（v0.6.3.4）：进房间/重连/热重载后向 host 要一次——
	/// 房间开着推房间信息，对局进行中直接推规则+开局。让任意时刻的客户端状态可幂等恢复。 </summary>
	[Rpc.Host]
	public static void RequestMatchState()
	{
		var conn = Rpc.Caller;
		if ( conn is null || !conn.IsActive ) return;

		var game = CircleroyaleGame.Current;
		if ( game is null ) return;

		using ( Rpc.FilterInclude( conn ) )
		{
			if ( game.IsLobbyOpen )
			{
				LobbyState( (byte) MatchState.PendingMode, (int) MatchState.PendingDuration,
					MatchState.PendingPlayerTarget, game.LobbyPlayerNames().ToArray() );
			}
			else if ( game.IsMatchStarted )
			{
				MatchSettings( (byte) MatchState.CurrentMode, (int) MatchState.Duration, MatchState.PlayerTarget );
				MatchStarted();
			}
			else
			{
				// host 也在菜单（会话残留）：按菜单设置推一局空房间信息
				LobbyState( (byte) MatchState.PendingMode, (int) MatchState.PendingDuration,
					MatchState.PendingPlayerTarget, game.LobbyPlayerNames().ToArray() );
			}
		}
	}

	/// <summary>
	/// host 按固定节拍广播：分身/孢子状态快照（纯数据实体，各端镜像渲染）。
	/// 广播对刚加入的连接也有效（与食物增量同款路径，实测可靠）——中途加入也能看到正在飞的分身。
	/// </summary>
	[Rpc.Broadcast]
	public static void CellsState( CellWire[] cells ) => CircleroyaleGame.Current?.OnCellsStateRemote( cells );

	/// <summary>
	/// 客户端请求重生（静态 RPC）：host 用 Rpc.Caller 取回调用者连接并为其原地复活。
	/// 不要让客户端自报 SteamId——同机双开时连接是合成 ID，与 Game.SteamId 对不上（实测踩坑）。
	/// </summary>
	[Rpc.Host]
	public static void RequestRespawn()
	{
		var conn = Rpc.Caller;
		Log.Info( $"[net] RequestRespawn from '{conn?.DisplayName ?? "null"}'" );   // 生命周期日志：定位客户端重生链路断点
		if ( conn is null || !conn.IsActive ) return;

		CircleroyaleGame.Current?.RespawnConnection( conn );
	}

	/// <summary> 客户端本地食物列表未就绪（热重载/新实例）时，向 host 请求全量（定向回发）；
	/// 绿刺顺带补发（客户端不清请求，重复收无害——数据幂等） </summary>
	[Rpc.Host]
	public static void RequestFoodFull()
	{
		var conn = Rpc.Caller;
		if ( conn is null || !conn.IsActive ) return;

		var game = CircleroyaleGame.Current;

		var foods = game?.Food?.Foods;
		if ( foods is not null )
		{
			using ( Rpc.FilterInclude( conn ) )
			{
				FoodFull( foods );
			}
		}

		var spikes = game?.Spikes;
		if ( spikes is not null )
		{
			using ( Rpc.FilterInclude( conn ) )
			{
				SpikesFull( spikes );
			}
		}
	}
}