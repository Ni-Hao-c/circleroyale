using System;
using System.Collections.Generic;

/// <summary>
/// 道具管理（v0.7.3.0）：**host 权威 + 静态 RPC 同步**，与 FoodManager 同款模式
/// （纯数据实体不走网络对象——运行时 live create 不复制到客户端，实测）。
/// 固定槽位制：场上恒 PowerUpOnField 个槽，被捡后同槽延时重刷（换种类换位置），
/// 全量同步只有一个小数组，中途加入/热重载恢复都便宜。
/// 拾取主球与分身都算（v0.7.8.6，存主人背包）；效果在拾取瞬间应用到 Ball（buff 挂玩家，质量罐即时加质量）。
/// Init/Tick 仅权威端调用；Apply* 仅供客户端应用远端事件。
/// </summary>
public sealed class PowerUpManager
{
	/// <summary> 道具种类（HUD/渲染/效果 switch 共用；None=255 作 Ball.BuffKind 的"无 buff"哨兵） </summary>
	public enum Kind : byte
	{
		Speed = 0,    // ⚡ 加速：8s 移速 ×1.4（黄）
		Magnet = 1,   // 🧲 磁铁：8s 吸食半径翻倍（青）
		Mass = 2,     // 💊 质量罐：+300 即时（品红）
		Shield = 3,   // 🛡 护盾：5s 不可被大球吃（蓝）
		Spike = 4,    // 💥 尖刺分身：生成跟随主人 20s 的尖刺（紫绿），撞破比自己大的球奖 3s 无敌
	}

	public const byte None = 255;

	/// <summary> 槽位渲染色（与霓虹调色板独立——道具要一眼和食物区分开） </summary>
	public static Color ColorOf( byte kind )
	{
		switch ( kind )
		{
			case (byte)Kind.Speed: return new Color( 1.00f, 0.85f, 0.25f );
			case (byte)Kind.Magnet: return new Color( 0.30f, 0.90f, 1.00f );
			case (byte)Kind.Mass: return new Color( 1.00f, 0.40f, 0.70f );
			case (byte)Kind.Shield: return new Color( 0.40f, 0.55f, 1.00f );
			case (byte)Kind.Spike: return new Color( 0.55f, 1.00f, 0.45f );   // 亮绿（野刺紫=障碍，绿色=可操纵的武器）
			default: return new Color( 0.6f, 0.6f, 0.6f );
		}
	}

	/// <summary> HUD/图例用的道具名 </summary>
	public static string NameOf( byte kind )
	{
		switch ( kind )
		{
			case (byte)Kind.Speed: return "SPEED";
			case (byte)Kind.Magnet: return "MAGNET";
			case (byte)Kind.Shield: return "SHIELD";
			case (byte)Kind.Spike: return "SPIKE";
			case (byte)Kind.Mass: return "+MASS";
			default: return "";
		}
	}

	/// <summary> 道具槽（渲染与 HUD 读；host 权威，客户端为事件镜像） </summary>
	public sealed class Slot
	{
		public bool Alive;
		public byte Kind;
		public Vector2 Pos;
		public TimeSince SincePicked;   // 仅 host：重刷计时
	}

	readonly List<Slot> _slots = new();

	/// <summary> 喂食共享积分池（按槽，仅 host）：谁吐丝都往里充，积分 ≥ 本次喂食者体重×PowerUpFeedRatio 即触发给喂食者——
	/// 大球喂到一半会被小球几口抢走（虎口夺食，v0.7.4.0 用户定稿） </summary>
	float[] _feedScores = Array.Empty<float>();

	/// <summary> Q 技能充能计时（按 SteamId，仅 host；v0.7.8.30 Q 改随时间充能，E 仍拾取） </summary>
	readonly Dictionary<long, TimeSince> _qCharge = new();

	/// <summary> Q 槽当前持有技能的球（冷却暂停标记，检测"放走"的转变） </summary>
	readonly HashSet<long> _qHeld = new();

	/// <summary> 全部槽位（NeonRenderer 每帧读，各端内容一致） </summary>
	public IReadOnlyList<Slot> Slots => _slots;

	/// <summary> 客户端/热重载后本地列表未就绪（不渲染也不误判） </summary>
	public bool Ready => _slots.Count > 0;

	/// <summary> host：生成全部槽位并广播全量（开局/演示赛调用） </summary>
	public void Init()
	{
		_slots.Clear();
		for ( int i = 0; i < GameConfig.PowerUpOnField; i++ )
		{
			_slots.Add( new Slot
			{
				Alive = true,
				Kind = RandomKind(),
				Pos = RandomPos(),
			} );
		}

		if ( Networking.IsActive ) NetworkManager.PowerUpFull( ToWire() );
		_feedScores = new float[_slots.Count];
	}

	/// <summary> 清空（回主菜单；客户端经 PowerUpFull 空数组同步） </summary>
	public void Reset()
	{
		_slots.Clear();
		_feedScores = Array.Empty<float>();
	}

	/// <summary> host：拾取判定（主球 + 分身共用）+ 到期槽位重刷。仅权威端每帧调用。
	/// v0.7.5.0 起拾取**存入背包**（bot 例外：拾取即用）——背包满则道具留在场上不被捡；
	/// v0.7.8.6 起分身（SplitPiece）也走同一判定，捡到存主人背包 </summary>
	public void Tick( List<Ball> balls, List<CellPiece> cells = null )
	{
		if ( _slots.Count == 0 ) return;

		foreach ( var ball in balls )
		{
			if ( !ball.IsValid() || !ball.Alive ) continue;
			PickupPass( ball, ball.WorldPosition, ball.Radius );
		}

		// 分身拾取（v0.7.8.6）：捡到存主人背包（同一 Q/E 背包）；孢子不捡（中性食物团），
		// 尖刺分身不捡（武器不兼职取货）。主人死亡的分身下一帧 TickCells 才清，这里顺带过滤。
		// ⚠️ 倒序 for：bot 拿着（20s 后转正的）尖刺分身踩到尖刺道具 → 拾取即用 →
		// SpawnSpikeMinion 往 _cells 追加——foreach 枚举中改集合会炸（v0.7.8.20 修）
		if ( cells is not null )
		{
			for ( int i = cells.Count - 1; i >= 0; i-- )
			{
				var c = cells[i];
				if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				if ( !c.OwnerBall.IsValid() || !c.OwnerBall.Alive ) continue;
				PickupPass( c.OwnerBall, c.Pos, c.Radius );
			}
		}

		// 空槽重刷
		for ( int i = 0; i < _slots.Count; i++ )
		{
			var s = _slots[i];
			if ( s.Alive || s.SincePicked < GameConfig.PowerUpRefillSeconds ) continue;

			s.Alive = true;
			s.Kind = RandomKind();
			s.Pos = RandomPos();
			if ( i < _feedScores.Length ) _feedScores[i] = 0f;   // 重刷积分清零（培养进度不作数）
			if ( Networking.IsActive ) NetworkManager.PowerUpRespawned( i, s.Kind, s.Pos.x, s.Pos.y );
		}

		// Q 技能随时间充能（v0.7.8.30 用户定稿）：真人球 Q 槽空时按**当前质量**计时充能
		// （15~60s，越轻越快），到点自动发一个随机技能。bot 拾取即用无背包，不参与；
		// 持有未放时冷却暂停（技能躺在槽里等按键）。计时按 SteamId 记（跨热重载稳定）。
		// 领土模式（M7.4）：Q 键让给职业技能——充能 pass 冻结清零，PowerA 保持空槽
		if ( MatchState.IsTerritory )
		{
			_qCharge.Clear();
			_qHeld.Clear();
			return;
		}
		foreach ( var b in balls )
		{
			if ( !b.IsValid() || !b.Alive || b.IsBot ) continue;

			var sid = b.OwnerSteamId;
			if ( b.PowerA != None )
			{
				_qHeld.Add( sid );   // 技能待放：冷却暂停
				continue;
			}
			if ( _qHeld.Remove( sid ) )
			{
				_qCharge[sid] = 0;   // 刚放走/复活清空：冷却从现在起算
				continue;
			}
			if ( !_qCharge.TryGetValue( sid, out var ts ) )
			{
				_qCharge[sid] = 0;   // 首见（入局）
				continue;
			}
			if ( ts < GameConfig.QSkillCooldownFor( b.Mass ) ) continue;

			_qCharge[sid] = 0;
			b.PowerA = RandomKind();
			if ( GameSfx.IsMine( b.OwnerSteamId ) )
			{
				GameSfx.Pickup();
				CircleroyaleGame.Current?.Hud?.AddBanner( $"{NameOf( b.PowerA )} READY (Q)", ColorOf( b.PowerA ) );
			}
			// 客户端玩家的 Q 到点：横幅+音效走广播（mode2），host 回声由 OnPowerBannerRemote 的 IsAuthority 挡掉（v0.7.8.31）
			if ( Networking.IsActive ) NetworkManager.PowerBanner( b.PowerA, b.OwnerSteamId, 2 );
			GameLog.Info( $"[game] q skill granted: {NameOf( b.PowerA )} -> {b.PlayerName}" );
		}
	}

	/// <summary>
	/// 一个"拾取体"（主球或分身）扫一遍槽位。bot 拾取即用；真人存入 Q/E 背包
	/// （效果等按键释放），背包满则道具留在场上。判定圈封顶反马太（大球要刻意碾上去）。
	/// </summary>
	void PickupPass( Ball receiver, Vector3 pos, float radius )
	{
		// 拾取判定封顶（v0.7.4.0 反马太）：大球不再"路过顺走"——判定圈按封顶半径算，
		// 必须刻意碾到道具正上方才吃得到；小球反而轻松
		var rr = MathF.Min( radius, GameConfig.PowerUpPickRadiusCap ) + GameConfig.PowerUpRadius;

		for ( int i = 0; i < _slots.Count; i++ )
		{
			var s = _slots[i];
			if ( !s.Alive ) continue;

			var dx = s.Pos.x - pos.x;
			var dy = s.Pos.y - pos.y;
			if ( dx * dx + dy * dy > rr * rr ) continue;

			if ( receiver.IsBot )
			{
				Consume( i, receiver );   // bot 不囤：拾取即触发效果（banner 由 Consume 内 IsMine 挡掉）
			}
			else if ( receiver.StorePower( s.Kind ) >= 0 )
			{
				// 真人：存入 Q/E 背包槽（效果等玩家按键释放），道具槽重刷
				s.Alive = false;
				s.SincePicked = 0;

				NeonRenderer.Spark( new Vector3( s.Pos.x, s.Pos.y, 0f ), ColorOf( s.Kind ) );
				if ( GameSfx.IsMine( receiver.OwnerSteamId ) )
				{
					GameSfx.EatFood( new Vector3( s.Pos.x, s.Pos.y, 0f ) );
					GameAchievements.PowerStored();   // 成就：累计捡 5 个入包（host 本机侧）
				}
				if ( Networking.IsActive ) NetworkManager.PowerUpPicked( i, s.Kind, receiver.OwnerSteamId );

				ShowBanner( receiver, s.Kind, stored: true );
				GameLog.Info( $"[game] powerup stored: {NameOf( s.Kind )} -> {receiver.PlayerName}" );
			}
			// 背包满：不消耗，道具留在场上（下一颗球/下个帧再判）
		}
	}

	/// <summary> 消耗道具并触发效果（bot 拾取即用 / 喂食触发共用路径；槽重刷） </summary>
	void Consume( int index, Ball receiver )
	{
		if ( (uint)index >= (uint)_slots.Count ) return;
		var s = _slots[index];
		if ( !s.Alive ) return;

		s.Alive = false;
		s.SincePicked = 0;
		if ( index < _feedScores.Length ) _feedScores[index] = 0f;

		ApplyEffect( receiver, s.Kind );
		NeonRenderer.Spark( new Vector3( s.Pos.x, s.Pos.y, 0f ), ColorOf( s.Kind ) );
		if ( GameSfx.IsMine( receiver.OwnerSteamId ) )
			GameSfx.EatFood( new Vector3( s.Pos.x, s.Pos.y, 0f ) );

		ShowBanner( receiver, s.Kind, stored: false );
		if ( Networking.IsActive ) NetworkManager.PowerUpPicked( index, s.Kind, receiver.OwnerSteamId );
	}

	/// <summary>
	/// 个人高光横幅（v0.7.6.0）：host 本地播自己的 + 广播给其他端（客户端按 IsMine 再显示）。
	/// stored=true "XXX STORED"（捡到入背包）/ false 激活文案（喂养触发/bot 拾取即用）。
	/// v0.7.7.0：横幅配套"叮咚"音（本机耳朵）。
	/// </summary>
	void ShowBanner( Ball receiver, byte kind, bool stored )
	{
		var text = stored ? $"{NameOf( kind )} STORED" : GameHud.ActivationText( kind );
		if ( GameSfx.IsMine( receiver.OwnerSteamId ) )
		{
			CircleroyaleGame.Current?.Hud?.AddBanner( text, ColorOf( kind ) );
			GameSfx.Pickup();
		}

		if ( Networking.IsActive ) NetworkManager.PowerBanner( kind, receiver.OwnerSteamId, (byte)( stored ? 0 : 1 ) );
	}

	/// <summary>
	/// host：孢子喂道具（"血量池"）——共享积分池按槽累计（谁吐丝都往里充），
	/// 积分 ≥ 本次喂食者当前体重 × PowerUpFeedRatio 即触发效果给喂食者（**立即生效，不入背包**）。
	/// 大球要喂到 5%×大体重才轮到自己达标，小球几口就够——中途截胡即"虎口夺食"。
	/// feeder 无效（断线/死亡瞬间）只充能不判定。
	/// </summary>
	public void Feed( Ball feeder, int index, float blobMass )
	{
		if ( (uint)index >= (uint)_slots.Count || !_slots[index].Alive ) return;
		if ( index >= _feedScores.Length ) Array.Resize( ref _feedScores, _slots.Count );

		_feedScores[index] += blobMass;

		if ( feeder is null || !feeder.IsValid() || !feeder.Alive ) return;
		if ( _feedScores[index] < feeder.Mass * GameConfig.PowerUpFeedRatio ) return;

		var kind = _slots[index].Kind;
		Consume( index, feeder );
		GameAchievements.FeedTriggered( feeder.OwnerSteamId );   // 成就：首次喂食触发（host 本机侧）
		GameLog.Info( $"[game] powerup fed to trigger: {NameOf( kind )} -> {feeder.PlayerName}" );
	}

	/// <summary> 道具效果（host 权威；质量罐即时到账，其余挂 buff，尖刺分身在游戏系统生成）。
	/// 拾取存背包（v0.7.5.0）：真人按键 UsePower 时才走到这里 </summary>
	public static void ApplyEffect( Ball ball, byte kind )
	{
		switch ( kind )
		{
			case (byte)Kind.Speed:
				ball.ApplyBuff( kind, GameConfig.PowerUpSpeedSeconds );
				break;
			case (byte)Kind.Magnet:
				ball.ApplyBuff( kind, GameConfig.PowerUpMagnetSeconds );
				break;
			case (byte)Kind.Shield:
				ball.ApplyBuff( kind, GameConfig.PowerUpShieldSeconds );
				break;
			case (byte)Kind.Mass:
				ball.Mass = MathF.Min( GameConfig.MaxMass, ball.Mass + GameConfig.PowerUpMassBonus );
				break;
			case (byte)Kind.Spike:
				CircleroyaleGame.Current?.SpawnSpikeMinion( ball );
				break;
		}

		GameLog.Info( $"[game] powerup picked: {NameOf( kind )} -> {ball.PlayerName}" );
	}

	byte RandomKind() => (byte)Game.Random.Float( 0f, 4.999f );

	Vector2 RandomPos()
	{
		var half = GameConfig.ArenaHalfSize - GameConfig.PowerUpMargin;
		return new Vector2( Game.Random.Float( -half, half ), Game.Random.Float( -half, half ) );
	}

	/// <summary> 全量快照（开局补发/新连接定向用；host 侧调用） </summary>
	public PowerUpWire[] ToWireForSync() => ToWire();

	PowerUpWire[] ToWire()
	{
		var wires = new PowerUpWire[_slots.Count];
		for ( int i = 0; i < _slots.Count; i++ )
		{
			var s = _slots[i];
			wires[i] = new PowerUpWire
			{
				Kind = s.Alive ? s.Kind : None,
				X = s.Pos.x,
				Y = s.Pos.y,
			};
		}
		return wires;
	}

	// ---- 客户端应用（远端事件 → 本地槽位）----

	public void ApplyFull( PowerUpWire[] wires )
	{
		_slots.Clear();
		if ( wires is null ) return;

		foreach ( var w in wires )
		{
			_slots.Add( new Slot
			{
				Alive = w.Kind != None,
				Kind = w.Kind,
				Pos = new Vector2( w.X, w.Y ),
			} );
		}
	}

	public void ApplyPicked( int index )
	{
		if ( (uint)index >= (uint)_slots.Count ) return;   // 热重载后未同步全量前，忽略增量
		_slots[index].Alive = false;
	}

	public void ApplyRespawned( int index, byte kind, Vector2 pos )
	{
		if ( (uint)index >= (uint)_slots.Count ) return;

		var s = _slots[index];
		s.Alive = true;
		s.Kind = kind;
		s.Pos = pos;
	}
}