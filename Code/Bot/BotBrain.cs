using System;
using System.Collections.Generic;

/// <summary>
/// bot AI（host 侧模拟，代理端不跑）：有限状态机
/// 逃跑（范围内能吃我的威胁）&gt; 追猎（能吃的近距离小球）&gt; 靠近喂养目标 &gt; 觅食 &gt; 游荡。
/// 每 BotDecisionInterval 秒决策一次，方向写入 Ball.SetBotDirection。
/// 进阶能力（v0.6.6.0，用户需求）：
/// - **分裂捕猎**：贴身且分出去的一半仍够吃时，朝猎物分裂（agar 标准秒杀手法），有冷却与难度门槛；
/// - **吐孢子喂队友**（团队赛）：向范围内更大的队友喂孢子（养大哥），喂到自己不再明显小于对方为止；
/// - **团队集结**：团队赛游荡目标偏向队友所在区域，抱团行动；
/// - 猎物/威胁判定跳过队友（队友免伤，追了白追）。
/// M2 起 bot 由 host 拥有，IsProxy 端不执行。
/// </summary>
public sealed class BotBrain : Component
{
	/// <summary> 一次决策收集的态势 </summary>
	sealed class BotContext
	{
		public Ball Threat;
		public Ball Prey;
		public Ball FeedTarget;                 // 团队赛：值得喂的队友（范围内最大且明显比我大）
		public readonly List<Ball> Mates = new();   // 存活队友（集结用）
	}

	Ball _self;
	TimeSince _sinceDecision;
	TimeSince _sinceSplit;
	TimeSince _sinceFeed;
	Vector3 _wanderTarget;
	bool _hasWander;

	/// <summary> 难度档（OnStart 随机定档；决策节奏/感知范围/准度都按档取值） </summary>
	public BotDifficulty Difficulty { get; private set; } = BotDifficulty.Veteran;

	protected override void OnStart()
	{
		base.OnStart();
		_self = GetComponent<Ball>();
		Difficulty = RollDifficulty();
		_sinceDecision = DecisionInterval();   // 首帧立即决策
	}

	BotDifficulty RollDifficulty()
	{
		var r = Game.Random.Float( 0f, 1f );
		if ( r < GameConfig.BotAceFraction ) return BotDifficulty.Ace;
		if ( r < GameConfig.BotAceFraction + GameConfig.BotVeteranFraction ) return BotDifficulty.Veteran;
		return BotDifficulty.Rookie;
	}

	float DecisionInterval() => Difficulty switch
	{
		BotDifficulty.Rookie => GameConfig.BotRookieDecisionInterval,
		BotDifficulty.Ace => GameConfig.BotAceDecisionInterval,
		_ => GameConfig.BotDecisionInterval,
	};

	float RangeScale() => Difficulty switch
	{
		BotDifficulty.Rookie => GameConfig.BotRookieRangeScale,
		BotDifficulty.Ace => GameConfig.BotAceRangeScale,
		_ => 1f,
	};

	float AimError() => Difficulty switch
	{
		BotDifficulty.Rookie => GameConfig.BotRookieAimError,
		BotDifficulty.Ace => GameConfig.BotAceAimError,
		_ => GameConfig.BotVeteranAimError,
	};

	/// <summary> 进阶动作的施展概率（新兵笨拙少用，王牌几乎必放） </summary>
	float ActionChance() => Difficulty switch
	{
		BotDifficulty.Rookie => 0.18f,
		BotDifficulty.Ace => 0.85f,
		_ => 0.5f,
	};

	protected override void OnFixedUpdate()
	{
		base.OnFixedUpdate();
		if ( IsProxy ) return;
		if ( !_self.IsValid() || !_self.Alive ) return;   // 死亡状态不决策
		if ( MatchState.MatchOver ) return;               // 结算期全场冻结

		var interval = DecisionInterval();
		if ( _sinceDecision < interval ) return;
		_sinceDecision = 0;

		var ctx = Survey();

		_self.SetBotDirection( Aim( AvoidSpikes( DecideDir( ctx ) ) ) );

		// 动作层（方向之外的能力）：分裂捕猎 / 喂队友
		TrySplitHunt( ctx );
		TryFeedMate( ctx );
	}

	/// <summary> 收集态势：威胁/猎物（跳过队友）+ 喂养目标 + 队友列表 </summary>
	BotContext Survey()
	{
		var ctx = new BotContext();
		var game = CircleroyaleGame.Current;
		if ( game is null ) return ctx;

		float threatDist = float.MaxValue;
		float preyDist = float.MaxValue;
		float feedMass = -1f;

		foreach ( var b in game.Balls )
		{
			if ( !b.IsValid() || !b.Alive || b == _self ) continue;

			bool mate = MatchState.IsTeam && b.TeamIndex >= 0 && b.TeamIndex == _self.TeamIndex;
			if ( mate ) ctx.Mates.Add( b );

			var d = _self.WorldPosition.Distance( b.WorldPosition );

			// 威胁：质量够吃我、不是队友（队友免伤谈不上威胁）、我不在出生保护内
			if ( !mate && b.Mass > _self.Mass * GameConfig.EatRatio && !_self.IsProtected )
			{
				var range = b.Radius + _self.Radius + GameConfig.BotFleeRange * RangeScale();
				if ( d < range && d < threatDist )
				{
					ctx.Threat = b;
					threatDist = d;
				}
			}
			// 猎物：我够吃它、不是队友、双方都不在保护期（护盾期免战，追了也白追）；
			// 道具护盾（v0.7.3.0）同理——追也吃不到，跳过不浪费时间
			else if ( !mate && _self.Mass > b.Mass * GameConfig.EatRatio && !b.IsProtected && !_self.IsProtected
				&& !b.HasBuff( PowerUpManager.Kind.Shield ) )
			{
				if ( d < GameConfig.BotHuntRange * RangeScale() && d < preyDist )
				{
					ctx.Prey = b;
					preyDist = d;
				}
			}

			// 喂养目标（团队赛，养大哥）：范围内最大的、明显比我大的队友
			if ( mate && GameConfig.EnableEject && _self.Mass >= GameConfig.BotFeedMinMass
				&& b.Mass > _self.Mass * 1.25f
				&& d < GameConfig.BotFeedRange * RangeScale()
				&& b.Mass > feedMass )
			{
				ctx.FeedTarget = b;
				feedMass = b.Mass;
			}
		}
		return ctx;
	}

	/// <summary> 方向决策：逃跑 &gt; 追猎 &gt; 靠近喂养目标 &gt; 觅食 &gt; 游荡（团队赛偏集结） </summary>
	Vector2 DecideDir( BotContext ctx )
	{
		var myPos = _self.WorldPosition;

		// 逃跑：反向 + 朝场地中心的分量（防被逼进角落）
		if ( ctx.Threat.IsValid() )
		{
			var away = ( myPos - ctx.Threat.WorldPosition ).Normal;
			var toCenter = ( Vector3.Zero - myPos ).Normal;
			return Norm( away + toCenter * 0.45f );
		}

		// 追猎
		if ( ctx.Prey.IsValid() )
			return Norm( ctx.Prey.WorldPosition - myPos );

		// 喂养：朝大哥移动（孢子沿路径滑过去；喂瘦到不满足条件自动回归觅食）
		if ( ctx.FeedTarget.IsValid() )
			return Norm( ctx.FeedTarget.WorldPosition - myPos );

		// 觅食：最近的食物
		var food = NearestFoodPos( myPos );
		if ( food.HasValue )
		{
			var fp = food.Value;
			return Norm( new Vector3( fp.x, fp.y, 0f ) - myPos );
		}

		// 游荡：团队赛偏向队友所在区域（集结抱团，v0.6.6.0），否则随机巡游点
		if ( !_hasWander || myPos.Distance( _wanderTarget ) < 120f )
		{
			float half = GameConfig.ArenaHalfSize - 256f;
			if ( MatchState.IsTeam && ctx.Mates.Count > 0 )
			{
				var mate = ctx.Mates[Game.Random.Int( 0, ctx.Mates.Count - 1 )];
				var mh = GameConfig.ArenaHalfSize - 512f;
				_wanderTarget = new Vector3(
					Math.Clamp( mate.WorldPosition.x + Game.Random.Float( -900f, 900f ), -mh, mh ),
					Math.Clamp( mate.WorldPosition.y + Game.Random.Float( -900f, 900f ), -mh, mh ), 0f );
			}
			else
			{
				_wanderTarget = new Vector3( Game.Random.Float( -half, half ), Game.Random.Float( -half, half ), 0f );
			}
			_hasWander = true;
		}
		return Norm( _wanderTarget - myPos );
	}

	/// <summary>
	/// 分裂捕猎（v0.6.6.0）：贴身（半径+打击距离内）、分出去的一半仍够吃猎物、过冷却与
	/// 难度概率门槛 → 朝猎物分裂。分身带冲量飞出去，之后跟随主人转向继续压猎物。
	/// </summary>
	void TrySplitHunt( BotContext ctx )
	{
		if ( !GameConfig.EnableSplit || ctx.Prey is null || !ctx.Prey.IsValid() ) return;
		if ( _self.Mass < GameConfig.BotSplitMinMass ) return;                       // 太小分裂=白送质量
		if ( _self.Mass * 0.5f < ctx.Prey.Mass * GameConfig.EatRatio ) return;      // 分身必须还能吃掉猎物
		if ( _sinceSplit < GameConfig.BotSplitCooldown ) return;

		var to = ctx.Prey.WorldPosition - _self.WorldPosition;
		var d = to.Length;
		if ( d > _self.Radius + GameConfig.BotSplitRange * RangeScale() ) return;
		if ( Game.Random.Float( 0f, 1f ) > ActionChance() ) return;

		_sinceSplit = 0;
		CircleroyaleGame.Current?.DoSplit( _self, new Vector2( to.x / d, to.y / d ) );
		Log.Info( $"[bot] split-hunt '{_self.PlayerName}' -> '{ctx.Prey.PlayerName}'" );
	}

	/// <summary>
	/// 吐孢子喂队友（团队赛，v0.6.6.0 养大哥）：向范围内最大的队友吐孢子，
	/// 走 DoEject 同一条权威路径（内部自带 0.15s 节流；多身体一起吐）。
	/// 决策间隔 + 本方法冷却 + 难度概率三重节流，喂瘦到对方不足我 1.25 倍自动停。
	/// </summary>
	void TryFeedMate( BotContext ctx )
	{
		if ( !GameConfig.EnableEject || ctx.FeedTarget is null || !ctx.FeedTarget.IsValid() ) return;
		if ( ctx.Threat.IsValid() || ctx.Prey.IsValid() ) return;   // 保命/吃优先
		if ( _sinceFeed < GameConfig.BotFeedCooldown ) return;
		if ( Game.Random.Float( 0f, 1f ) > ActionChance() ) return;

		_sinceFeed = 0;
		var to = ctx.FeedTarget.WorldPosition - _self.WorldPosition;
		var d = MathF.Max( to.Length, 0.001f );
		CircleroyaleGame.Current?.DoEject( _self, new Vector2( to.x / d, to.y / d ) );
	}

	/// <summary> 避刺：任何体积都得绕（感知距离 = 自身半径 + SpikeRadius×2.2） </summary>
	Vector2 AvoidSpikes( Vector2 dir )
	{
		var spikes = CircleroyaleGame.Current?.Spikes;
		if ( spikes is null ) return dir;

		var myPos = _self.WorldPosition;
		var aware = _self.Radius + GameConfig.SpikeRadius * GameConfig.BotSpikeAwareness;
		foreach ( var s in spikes )
		{
			var away = new Vector2( myPos.x - s.x, myPos.y - s.y );
			var d = away.Length;
			if ( d >= aware || d < 0.001f ) continue;

			away /= d;
			dir = Norm( new Vector3( dir.x + away.x * 1.6f, dir.y + away.y * 1.6f, 0f ) );
		}
		return dir;
	}

	/// <summary> 瞄准噪声：低难度方向打不准（±弧度内随机偏转），王牌几乎指哪打哪 </summary>
	Vector2 Aim( Vector2 dir )
	{
		var err = AimError();
		if ( err <= 0f || dir.Length <= 0.001f ) return dir;

		var a = Game.Random.Float( -err, err );
		var cos = MathF.Cos( a );
		var sin = MathF.Sin( a );
		return new Vector2( dir.x * cos - dir.y * sin, dir.x * sin + dir.y * cos );
	}

	Vector2? NearestFoodPos( Vector3 myPos )
	{
		var foods = CircleroyaleGame.Current.Food?.Foods;
		if ( foods is null ) return null;

		var best = float.MaxValue;
		Vector2? bestPos = null;
		var seek = GameConfig.BotSeekFoodRange * RangeScale();
		var seekSq = seek * seek;

		for ( int i = 0; i < foods.Length; i++ )
		{
			if ( !foods[i].Alive ) continue;

			var dx = foods[i].Pos.x - myPos.x;
			var dy = foods[i].Pos.y - myPos.y;
			var dSq = dx * dx + dy * dy;
			if ( dSq > seekSq || dSq >= best ) continue;

			best = dSq;
			bestPos = foods[i].Pos;
		}
		return bestPos;
	}

	static Vector2 Norm( Vector3 v )
	{
		var v2 = new Vector2( v.x, v.y );
		var len = v2.Length;
		return len > 0.001f ? v2 / len : Vector2.Zero;
	}
}