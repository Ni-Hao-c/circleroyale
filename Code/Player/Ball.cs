using System;

/// <summary>
/// 球（真人/bot 共用）：**owner 本地模拟移动**（真人=键盘输入，bot=host 的 BotBrain），
/// 权威数值（Mass/IsBot/ColorIndex/PlayerName/OwnerSteamId）由 host 经 [Sync(FromHost)] 下发，
/// 位置随网络 transform 自动同步（owner → 其他端）。
/// 视觉：霓虹线条由 NeonRenderer 批量绘制；头像为本地 SpriteRenderer（各端按 OwnerSteamId 自取）。
/// </summary>
public sealed class Ball : Component
{
	[Sync( SyncFlags.FromHost )] public float Mass { get; set; } = GameConfig.StartMass;

	[Sync( SyncFlags.FromHost )] public bool IsBot { get; set; }

	[Sync( SyncFlags.FromHost )] public int ColorIndex { get; set; }

	[Sync( SyncFlags.FromHost )] public string PlayerName { get; set; } = "Player";

	/// <summary> 拥有者 SteamId（bot 为 0）；host 写入，各端据此加载对应头像 </summary>
	[Sync( SyncFlags.FromHost )] public long OwnerSteamId { get; set; }

	/// <summary> 队伍号（团队赛 0..N-1，普通赛 -1）；host 开局轮转分配，重生不变 </summary>
	[Sync( SyncFlags.FromHost )] public int TeamIndex { get; set; } = -1;

	// ---- 道具 buff（v0.7.3.0）：host 权威写入，各端展示/效果共用。
	// Kind 残留旧值没关系，HasBuff 只看 _sinceBuff < BuffDuration；
	// serial 自增保证"同类 buff 再捡一次"也触发 [Change]（客户端重置本地计时） ----

	[Sync( SyncFlags.FromHost )] public byte BuffKind { get; set; } = 255;

	[Sync( SyncFlags.FromHost )] public float BuffDuration { get; set; }

	[Sync( SyncFlags.FromHost ), Change( nameof( OnBuffSynced ) )] public byte BuffSerial { get; set; }

	TimeSince _sinceBuff = 999f;

	// ---- 道具背包（v0.7.5.0 主动使用）：拾取存入 Q/E 两格，按键释放效果。
	// 255 = 空槽；死亡/重生清空（机会不跨命携带） ----

	[Sync( SyncFlags.FromHost )] public byte PowerA { get; set; } = 255;

	[Sync( SyncFlags.FromHost )] public byte PowerB { get; set; } = 255;

	/// <summary> 各端收到 buff 变化（含 host 本地写入）即重置本地计时——客户端据此倒算剩余时间 </summary>
	void OnBuffSynced() => _sinceBuff = 0;

	/// <summary> host：给这颗球上 buff（质量罐等即时效果不走这里） </summary>
	public void ApplyBuff( byte kind, float duration )
	{
		BuffKind = kind;
		BuffDuration = duration;
		BuffSerial++;
		_sinceBuff = 0;
	}

	/// <summary> buff 是否生效中（host 权威判定与客户端展示同一公式，客户端自然过期无需通知） </summary>
	public bool HasBuff( PowerUpManager.Kind kind ) =>
		BuffKind == (byte)kind && _sinceBuff < BuffDuration;

	/// <summary> 当前 buff 剩余秒数（HUD 倒计时用；无 buff 为 0） </summary>
	public float BuffLeft => BuffKind == 255 ? 0f : MathF.Max( 0f, BuffDuration - _sinceBuff );

	/// <summary> 背包第 slot 格（0=Q 槽 1=E 槽）的道具种类；255=空 </summary>
	public byte PowerSlot( int slot ) => slot == 0 ? PowerA : PowerB;

	/// <summary> host：道具入背包（找空 Q/E 槽，返回槽号 0/1；满返回 -1——道具留在场上） </summary>
	public int StorePower( byte kind )
	{
		if ( PowerA == 255 ) { PowerA = kind; return 0; }
		if ( PowerB == 255 ) { PowerB = kind; return 1; }
		return -1;
	}

	/// <summary> host：使用背包道具——清槽并触发效果（PowerUpManager.ApplyEffect） </summary>
	public void UsePower( int slot )
	{
		var kind = PowerSlot( slot );
		if ( kind == 255 ) return;

		if ( slot == 0 ) PowerA = 255;
		else PowerB = 255;

		PowerUpManager.ApplyEffect( this, kind );
	}

	/// <summary> 按键释放背包槽（pressed 为 true 时处理）：本地先播激活横幅+音效+轻震，
	/// 再走权威/RPC 路径 </summary>
	void UsePowerSlot( int slot, bool pressed )
	{
		if ( !pressed ) return;

		var kind = PowerSlot( slot );
		if ( kind == 255 ) return;   // 空槽：按键无效（不播横幅不发请求）

		CircleroyaleGame.Current?.Hud?.AddBanner( GameHud.ActivationText( kind ), GameHud.KindColor( kind ) );
		GameSfx.Pickup();
		CircleroyaleGame.Current?.Camera?.Shake( 5f );   // 放技能轻震（击杀震更猛，随 combo 加码）

		if ( NetworkManager.IsAuthority )
			UsePower( slot );
		else
			RequestUsePower( slot );
	}

	/// <summary>
	/// 是否存活。**球对象永不销毁**：死亡 = Alive=false（同步字段，渲染/AI/判定全部跳过），
	/// 重生 = host 置回 true。绕开"进行中 NetworkSpawn 不复制到已连接客户端"的引擎行为（实测大坑）。
	/// </summary>
	[Sync( SyncFlags.FromHost )]
	public bool Alive { get; set; } = true;

	public float Radius => GameConfig.StartRadius * MathF.Sqrt( Mass / GameConfig.StartMass );

	/// <summary> 本球的霓虹色（由 ColorIndex 从调色板取） </summary>
	public Color NeonColor => GameConfig.Palette[Math.Clamp( ColorIndex, 0, GameConfig.Palette.Length - 1 )];

	/// <summary> 移速：质量曲线 × 加速 buff（⚡ 时 ×1.4；Speed 属性各端同算，owner 模拟与 host 校验一致） </summary>
	public float Speed
	{
		get
		{
			var s = MathF.Max( GameConfig.MinSpeed,
				GameConfig.BaseSpeed * MathF.Pow( GameConfig.StartMass / Mass, GameConfig.SpeedCurve ) );
			return HasBuff( PowerUpManager.Kind.Speed ) ? s * GameConfig.PowerUpSpeedBoost : s;
		}
	}

	/// <summary> 是否出生保护中（虚环护盾表现，双向免战） </summary>
	public bool IsProtected => _sinceSpawn < GameConfig.SpawnProtectSeconds;

	/// <summary> 球体背景填充色：真人=头像主色取样，bot=霓虹配色 </summary>
	public Color BackgroundColor => _avatarColor ?? NeonColor;

	Vector3 _velocity;
	TimeSince _sinceSpawn;
	SpriteRenderer _avatar;
	Color? _avatarColor;
	Vector2 _botDir;
	Vector2 _lastMoveDir = Vector2.Right;   // 静止时分裂/吐孢子的方向兜底
	Vector2 _lastAimDir = Vector2.Zero;     // 光标瞄准方向（分裂/吐孢子用，v0.7.0.0）

	/// <summary> 当前瞄准方向（本机球：渲染外缘箭头用；bot 恒 Zero 不画） </summary>
	public Vector2 AimDir => _lastAimDir;
	TimeSince _sinceEject;                  // 客户端按住连吐的节流（host 还有同款校验）
	TimeSince _sinceAvatarRetry = 1f;   // 首帧立即尝试
	int _avatarRetryCount;
	bool _avatarReady;
	bool _wasAlive = true;

	/// <summary> 死亡时刻（host 记录，用于 bot 原地复活的延时） </summary>
	public TimeSince SinceDeath { get; private set; }

	/// <summary>
	/// host 侧标记：这颗球是为远程连接生成的（仅权威端有意义，不联网）。
	/// ScanBalls/SetLocalBall 用它把远程球（含竞态下未网络化的幽灵球）排除出本机球候选，
	// 否则同账号双开时幽灵球会顶掉 host 自己的球、相机来回跳（实测）。
	/// </summary>
	public bool IsRemoteOwned { get; set; }

	/// <summary> host 侧标记死亡（对象保留，原地复用） </summary>
	public void MarkDead()
	{
		Alive = false;
		SinceDeath = 0;
		_velocity = Vector3.Zero;
	}

	/// <summary> 重置出生保护计时（host 在玩家真正入局时调用——pre-snapshot 球的
	/// 保护期在握手期间就空跑完了，实测加入 1.6s 即被吃；IsProtected 按各端本地计时判定，
	/// host 重置即恢复权威判定，客户端自己的计时随快照应用另算） </summary>
	public void RefreshSpawnProtection() => _sinceSpawn = 0;

	/// <summary> 瞬移（分身晋升主球时用）：位置归 owner 模拟，owner 端必须自己搬过去；
	/// 不触发保护期/重播音效——这是身份延续，不是重生 </summary>
	public void TeleportTo( Vector3 pos )
	{
		WorldPosition = new Vector3( pos.x, pos.y, 0f );
		_velocity = Vector3.Zero;
		_lastMoveDir = Vector2.Zero;
	}

	/// <summary> host 侧初始化（仅权威端调用；客户端字段全部走网络同步）。
	/// teamIndex ≥ 0 时同时写入队伍号；团队赛颜色锁队伍色，普通赛随机霓虹色 </summary>
	public void Init( bool isBot, int teamIndex = -1 )
	{
		IsBot = isBot;
		if ( teamIndex >= 0 ) TeamIndex = teamIndex;

		ColorIndex = MatchState.IsTeam
			? ( ( TeamIndex % GameConfig.Palette.Length ) + GameConfig.Palette.Length ) % GameConfig.Palette.Length
			: (int)Game.Random.Float( 0f, GameConfig.Palette.Length - 0.001f );

		_sinceSpawn = 0;
		_velocity = Vector3.Zero;

		// 道具 buff 与背包随重生清掉（机会不跨命携带）
		BuffKind = 255;
		BuffDuration = 0f;
		PowerA = 255;
		PowerB = 255;
	}

	/// <summary> bot 由 BotBrain 每帧写入的移动方向（真人走 BallInput，不用这个） </summary>
	public void SetBotDirection( Vector2 dir ) => _botDir = dir;

	protected override void OnStart()
	{
		base.OnStart();

		// 所有机器统一从网络对象的 OnStart 注册（host 生成 / 客户端随复制出现）
		CircleroyaleGame.Current?.RegisterBall( this );
		if ( !IsProxy && !IsBot )
			CircleroyaleGame.Current?.SetLocalBall( this );

		// 头像圆心（用户决策：头像作圆心，霓虹线条作外侧）
		var go = new GameObject( true, "Avatar" );
		go.Parent = GameObject;

		_avatar = go.AddComponent<SpriteRenderer>();
		_avatar.Lighting = false;
		_avatar.Shadows = false;

		TryLoadAvatar();
	}

	/// <summary>
	/// 加载头像：bot 用占位图；真人按 OwnerSteamId 拉 Steam 头像
	/// （下载有延迟，失败每秒重试，15 次后转占位图）。
	/// </summary>
	void TryLoadAvatar()
	{
		if ( IsBot )
		{
			// 几何图案头像：按名字+色号确定性生成（各端算出同一张，零同步）
			if ( string.IsNullOrEmpty( PlayerName ) ) return;   // 名字同步未到，等重试

			Texture tex = null;
			try
			{
				tex = BotAvatar.GetOrCreate( PlayerName, ColorIndex );
			}
			catch ( Exception e )
			{
				Log.Warning( $"bot avatar generate failed for '{PlayerName}': {e.Message}" );
			}
			ApplyAvatarTexture( tex ?? Texture.Load( GameConfig.AvatarPlaceholderPath, false ) );
			_avatarReady = true;
			return;
		}

		if ( OwnerSteamId == 0 ) return;   // 同步未到，等下一轮重试

		var avatar = Texture.LoadAvatar( OwnerSteamId, 128 );
		if ( avatar is null ) return;

		ApplyAvatarTexture( avatar );
		_avatarReady = true;
	}

	void ApplyAvatarTexture( Texture tex )
	{
		if ( tex is null )
		{
			Log.Warning( $"avatar load failed: '{PlayerName}' bot={IsBot} steam={OwnerSteamId}" );
			_avatar.Enabled = false;
			return;
		}

		_avatar.Sprite = Sprite.FromTexture( tex );   // Texture setter 已废弃，必须走 Sprite 资源
		_avatarColor = IsBot ? null : SampleAverageColor( tex );
	}

	/// <summary> 头像主色：全图不透明像素的均值（用作球底填充色） </summary>
	static Color? SampleAverageColor( Texture tex )
	{
		try
		{
			var pixels = tex.GetPixels( 0 );
			if ( pixels is null || pixels.Length == 0 ) return null;

			float r = 0f, g = 0f, b = 0f;
			int n = 0;
			for ( int i = 0; i < pixels.Length; i++ )
			{
				var p = pixels[i];
				if ( p.a < 32 ) continue;
				r += p.r;
				g += p.g;
				b += p.b;
				n++;
			}
			if ( n == 0 ) return null;

			return new Color( r / n / 255f, g / n / 255f, b / n / 255f );
		}
		catch
		{
			return null;
		}
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		// 头像直径 = 半径（占玩家形状直径的 50%，用户指定），居中；
		// 死亡时一并隐藏——此前只藏了霓虹环，头像方块留在原地像"尸体"（实测）
		if ( _avatar.IsValid() )
		{
			var d = Radius;
			_avatar.Size = new Vector2( d, d );
			_avatar.Enabled = Alive;
		}

		// 头像重试：每秒一次，15 次未拿到转占位图（bot 也走：名字/色号同步可能晚于 OnStart）
		if ( !_avatarReady && _sinceAvatarRetry > 1f )
		{
			_sinceAvatarRetry = 0;
			if ( _avatarRetryCount++ >= 15 )
			{
				ApplyAvatarTexture( Texture.Load( GameConfig.AvatarPlaceholderPath, false ) );
				_avatarReady = true;
			}
			else
			{
				TryLoadAvatar();
			}
		}

		// ---- 分裂/吐孢子输入（M4.11：每帧路径，仅本机真人、活球）----
		// 权威端直接执行；客户端发实例 RPC（[Rpc.Host]，host 侧校验后执行，分身是 host 权威数据）。
		// 音效在按键瞬间即播（本地即时反馈；host 校验不过顶多空响一次）
		if ( IsProxy || IsRemoteOwned || IsBot || !Alive ) return;
		if ( MatchState.MatchOver ) return;   // 比赛结束：全场冻结，输入不响应

		// 停在等待页（P 退局/掉线恢复）且不在对局时停止操控：球留在原地，防隔屏盲开。
		// ⚠️ 必须带 !IsMatchStarted：只要还在对局（_gameStarted）就绝不禁输入——
		// _lobbyWaiting 若因异常残留为 true，会把对局中的移动/分裂/吐孢子/瞄准全锁死
		// （实测：不能分身/吐丝、瞄准箭头冻在旧方向）
		{
			var g = CircleroyaleGame.Current;
			if ( g is not null && g.IsClientWaiting && !g.IsMatchStarted )
			{
				_lastMoveDir = Vector2.Zero;
				return;
			}
		}

		var inputDir = BallInput.GetMoveDir( Scene.Camera );
		if ( inputDir.Length > 0.001f ) _lastMoveDir = inputDir;

		// 瞄准（v0.7.0.0 用户需求）：分裂/吐孢子指向光标，光标正贴球心时回退移动方向
		var aimDir = BallInput.GetAimDir( Scene.Camera, WorldPosition );
		if ( aimDir.Length > 0.001f ) _lastAimDir = aimDir;
		var actionDir = _lastAimDir.Length > 0.001f ? _lastAimDir : _lastMoveDir;

		// 鼠标键并行（v0.7.4.1 用户需求）：右键分裂、左键吐丝（"Attack1"/"Attack2" 为引擎内建按钮，
		// 官方项目同款用法）；空格/R 保留，两套键位共存。左键吐丝天然朝光标（actionDir 即瞄准方向）
		if ( GameConfig.EnableSplit && ( Input.Pressed( "Jump" ) || Input.Pressed( "Attack2" ) ) )
		{
			var splitDir = actionDir;
			if ( Mass >= GameConfig.SplitMinMass ) GameSfx.Split( WorldPosition );

			if ( NetworkManager.IsAuthority )
				CircleroyaleGame.Current?.DoSplit( this, splitDir );
			else
				RequestSplit( splitDir );
		}

		if ( GameConfig.EnableEject
			&& ( Input.Down( "Reload" ) || Input.Down( "Attack1" ) )
			&& _sinceEject > GameConfig.EjectCooldownSeconds )
		{
			_sinceEject = 0;
			var ejectDir = actionDir;
			if ( Mass >= 2f ) GameSfx.Eject( WorldPosition );   // 至少能吐最小一颗（下限 1 + 消耗 1）

			if ( NetworkManager.IsAuthority )
				CircleroyaleGame.Current?.DoEject( this, ejectDir );
			else
				RequestEject( ejectDir );
		}

		// 道具主动使用（v0.7.5.0）：Q/E 释放背包里的道具——权威端直接触发，客户端发 RPC。
		// banner 按键即播（本地即时反馈，不等 host 往返）；槽位空则什么都不发生
		UsePowerSlot( 0, Input.Keyboard.Pressed( "Q" ) );
		UsePowerSlot( 1, Input.Keyboard.Pressed( "E" ) );
	}

	protected override void OnFixedUpdate()
	{
		if ( IsProxy ) return;   // 只有拥有者（真人）或 host（bot）模拟
		if ( IsRemoteOwned ) return;   // host 侧远程球由其主人模拟；未网络化的幽灵球绝不本地模拟

		// 死亡状态：停住不动（对象保留）；Alive 翻回 true 时本地重生（随机换位+保护期）
		if ( !Alive )
		{
			_velocity = Vector3.Zero;
			_wasAlive = false;
			return;
		}
		if ( !_wasAlive )
		{
			_wasAlive = true;
			_sinceSpawn = 0;          // 复活保护期（本地表现；权威保护期在 host 的 Init 里）
			_velocity = Vector3.Zero;
			WorldPosition = RandomSpawnPos();   // owner 自己随机换位（位置归 owner 模拟）
			GameSfx.Respawn( WorldPosition );
			GameSfx.ResetEatCount();   // 下一条命吃食物音从 0 重新计
			GameSfx.ResetCombo();      // 连击清零（新的一条命重新起算）
			Log.Info( $"[ball] local respawn '{PlayerName}'" );
		}

		// 比赛结束：全场冻结（结算面板接管，谁也不许再动）
		if ( MatchState.MatchOver )
		{
			_velocity = Vector3.Zero;
			return;
		}

		// 真人 = 本地输入（owner 模拟）；bot = BotBrain 写入的方向。
		// 分裂/吐孢子的输入判定已移到 OnUpdate（每帧路径）——Input 按键边沿按渲染帧刷新，
		// 固定步进的 FixedUpdate 可能错过 Pressed 边沿（按了没反应的隐患）
		var dir = IsBot ? _botDir : BallInput.GetMoveDir( Scene.Camera );
		if ( dir.Length > 0.001f ) _lastMoveDir = dir;

		// 速度与转向都随质量衰减（批18①）：体积越大极速越低、惯性越强转向越笨拙，
		// 大球必须提前预判走位。响应时间常数 = BaseTurnSpeed × (StartMass/Mass)^TurnCurve
		var turn = MathF.Max( GameConfig.MinTurnSpeed,
			GameConfig.BaseTurnSpeed * MathF.Pow( GameConfig.StartMass / Mass, GameConfig.TurnCurve ) );
		var t = 1f - MathF.Exp( -turn * Time.Delta );
		_velocity = Vector3.Lerp( _velocity, dir * Speed, t );

		var pos = WorldPosition + _velocity * Time.Delta;

		// 场地边界钳制（半径异常涨破场地时 half 兜底为 0，绝不让 Clamp 的 min>max 抛异常）
		var half = MathF.Max( 0f, GameConfig.ArenaHalfSize - Radius );
		pos.x = Math.Clamp( pos.x, -half, half );
		pos.y = Math.Clamp( pos.y, -half, half );
		pos.z = 0f;

		WorldPosition = pos;
	}

	/// <summary> 场地内出生点（owner 复活时本地选位）：走游戏系统的安全采样，无系统时纯随机兜底 </summary>
	static Vector3 RandomSpawnPos()
	{
		var game = CircleroyaleGame.Current;
		if ( game is not null ) return game.RandomSpawnPos();

		var half = GameConfig.ArenaHalfSize - 128f;
		return new Vector3( Game.Random.Float( -half, half ), Game.Random.Float( -half, half ), 0f );
	}

	// ---- 分裂/吐孢子 RPC（M3）：客户端 → host，host 校验归属后执行 ----

	[Rpc.Host]
	public void RequestSplit( Vector2 dir )
	{
		var caller = Rpc.Caller;
		if ( caller is not null && caller.SteamId.Value != OwnerSteamId ) return;   // 只能操作自己的球

		CircleroyaleGame.Current?.DoSplit( this, dir );
	}

	[Rpc.Host]
	public void RequestEject( Vector2 dir )
	{
		var caller = Rpc.Caller;
		if ( caller is not null && caller.SteamId.Value != OwnerSteamId ) return;

		CircleroyaleGame.Current?.DoEject( this, dir );
	}

	[Rpc.Host]
	public void RequestUsePower( int slot )
	{
		var caller = Rpc.Caller;
		if ( caller is not null && caller.SteamId.Value != OwnerSteamId ) return;   // 只能操作自己的球

		UsePower( slot );
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		CircleroyaleGame.Current?.RemoveBall( this );
	}
}