using System;
using System.Collections.Generic;

/// <summary>
/// bot AI（host 侧模拟，代理端不跑）：有限状态机
/// 逃跑（范围内能吃我的威胁）&gt; 追猎（能吃的近距离小球/分身）&gt; 靠近喂养目标 &gt; 觅食 &gt; 游荡。
/// 每 BotDecisionInterval 秒决策一次，方向写入 Ball.SetBotDirection。
/// 进阶能力（v0.6.6.0 起，v0.7.8.32 本轮加强）：
/// - **感知敌方分身**（v0.7.8.32）：分身能吞主球也能被吞——威胁/猎物/分裂风险评估全覆盖敌方分身（此前是纯盲区）；
/// - **分裂捕猎**（v0.6.6.0）：贴身且分出去的一半仍够吃时朝猎物分裂（agar 秒杀手法）；
///   v0.7.8.32：猎物太小不浪费分裂冷却；减半后附近有能吃我的敌人就放弃（送命分裂）；
/// - **分裂逃命**（v0.7.8.7）：威胁到嘴边时朝逃跑方向分裂（冲量前窜+变轻提速），逃命优先于捕猎；
/// - **多威胁合成逃跑**（v0.7.8.32）：被包夹时反向按威胁远近加权合成，不往第二个威胁怀里撞；
/// - **拦截预判**（v0.7.8.32）：追猎按猎物当前速度打提前量（新手直线追被风筝，王牌几乎完美拦截）；
/// - **觅食粘性**（v0.7.8.32）：食物/孢子目标活着就追到底（距离折扣竞争），不在两个目标间来回锯齿；
/// - **躲刺爆弹幕**（v0.7.8.32）：刺爆弹幕也是真伤害（-15% 质量），近距离侧向闪避；
/// - **吐孢子喂队友**（团队赛）：向范围内更大的队友喂孢子（养大哥），喂到自己不再明显小于对方为止；
/// - **顺路捡道具**（v0.7.8.7）：附近场上有道具就冲过去（bot 拾取即用）；
/// - **孢子觅食**（v0.7.8.7）：散落孢子按性价比折算进觅食目标，不再无视；
/// - **团队集结**：团队赛游荡目标偏向队友所在区域，抱团行动；
/// - 猎物/威胁判定跳过队友（队友免伤，追了白追）；猎物按"质量/距离"评分选性价比最高的。
/// M2 起 bot 由 host 拥有，IsProxy 端不执行。
/// </summary>
public sealed class BotBrain : Component
{
	/// <summary> 一次决策收集的态势（威胁/猎物可能是球也可能是敌方分身——分身时 Ball 引用为 null，
	/// 位置/半径/质量存独立字段，方向决策与动作层都只用这些字段取数据） </summary>
	sealed class BotContext
	{
		public Ball Threat;                     // 最近的威胁球（威胁是分身时为 null）
		public Vector3 ThreatPos;               // 最近威胁位置（球或敌方分身）
		public float ThreatRadius;
		public Ball Prey;                       // 价值最高的猎物球（猎物是分身时为 null）
		public Vector3 PreyPos;
		public float PreyRadius;
		public float PreyMass;                  // >0 = 有猎物（球或分身）
		public Vector3 PreyVel;                 // 猎物当前速度（拦截预判用）
		public Ball FeedTarget;                 // 团队赛：值得喂的队友（范围内最大且明显比我大）
		public readonly List<Ball> Mates = new();   // 存活队友（集结用）
		public Vector3 FleeDir;                 // 全部威胁的反向加权和（多威胁合成逃跑）
		public int ThreatCount;
		public bool DangerAfterSplit;           // 分裂减半后附近仍有够吃"减半我"的敌人
	}

	Ball _self;
	TimeSince _sinceDecision;
	TimeSince _sinceSplit;
	TimeSince _sinceFeed;
	Vector3 _wanderTarget;
	bool _hasWander;

	Vector2 _stickyFood;            // 觅食粘性目标（食物点，防相邻目标间来回锯齿）
	TimeSince _sinceStickyFood;
	int _stickyBlobId = -1;         // 觅食粘性目标（散落孢子，按 CellPiece.Id 锁定）
	TimeSince _sinceStickyBlob;

	Vector3 _flagTarget;            // 领土目标旗点（M7.5，_hasFlagTarget 时有效）
	bool _hasFlagTarget;
	TimeSince _sinceFlagPick = 99f; // 选旗节流（BotFlagPickInterval 重估一次）
	TimeSince _sinceFlagFeed;       // 喂旗节流（BotFeedFlagCooldown，套在 DoEject 内置节流之上）

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

	/// <summary> 拦截提前量系数（v0.7.8.32）：新手直线追（被风筝），老手打半拍，王牌几乎完美拦截 </summary>
	float LeadFactor() => Difficulty switch
	{
		BotDifficulty.Rookie => 0f,
		BotDifficulty.Ace => 0.75f,
		_ => 0.45f,
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

		// 领土目标层（M7.5）：节流重估目标旗（选旗/喂旗规则见 PickFlagTarget）
		if ( _sinceFlagPick >= GameConfig.BotFlagPickInterval ) PickFlagTarget();
		if ( _hasFlagTarget )
		{
			// 目标格已满驻军 = 任务完成，放下目标回归觅食
			TerritoryManager.CellOf( _flagTarget, out var tcx, out var tcy );
			TerritoryManager.CellState( tcx, tcy, out var o, out _, out var sc );
			if ( o == _self.TeamIndex && sc >= GameConfig.TerritoryCaptureScore )
				_hasFlagTarget = false;
		}

		_self.SetBotDirection( Aim( AvoidSpines( AvoidSpikes( DecideDir( ctx ) ) ) ) );

		// 表情档（v0.7.8.36 像素风）：逃跑=惊恐，追猎=兴奋（王牌=得意），闲逛=开心。
		// 写 Ball.Mood [Sync(FromHost)]，各端 [Change] 换脸；值不变不发包
		byte mood = 0;
		if ( ctx.ThreatCount > 0 ) mood = 2;
		else if ( ctx.PreyMass > 0f ) mood = Difficulty == BotDifficulty.Ace ? (byte)5 : (byte)1;
		if ( _self.Mood != mood ) _self.Mood = mood;

		// 动作层（方向之外的能力）：分裂逃命 > 分裂捕猎 / 喂队友 / 喂旗 / 职业 Q
		TrySplitEscape( ctx );
		TrySplitHunt( ctx );
		TryFeedMate( ctx );
		TryFeedFlag( ctx );
		TryCastClassSkill( ctx );
	}

	/// <summary>
	/// 职业技能施放（M7.5b）：按职业情境化使用，全部走 ClassSkillManager.Cast 权威入口
	/// （冷却/校验内置，冷却按球 Id 记——bot 各球独立）。难度概率 gate，新兵不放。
	/// 指挥官=被威胁时传到最大队友身边（逃生+集结）；工兵=远距离赶旗疾行/被追加速；
	/// 护卫=被威胁时原地落尖刺断追兵；坦克=被威胁时质量爆发硬刚。
	/// </summary>
	void TryCastClassSkill( BotContext ctx )
	{
		if ( !MatchState.IsTerritory ) return;
		if ( _self.TerritoryClass > 3 ) return;
		if ( Difficulty == BotDifficulty.Rookie ) return;
		if ( Game.Random.Float( 0f, 1f ) > ActionChance() ) return;

		var use = _self.TerritoryClass switch
		{
			0 => ctx.ThreatCount > 0,
			1 => ( _hasFlagTarget && _self.WorldPosition.Distance( _flagTarget ) > 1200f ) || ctx.ThreatCount > 0,
			2 => ctx.ThreatCount > 0,
			3 => ctx.ThreatCount > 0,
			_ => false,
		};
		if ( !use ) return;

		ClassSkillManager.Cast( _self );   // 冷却未好静默拒绝，无副作用
	}

	/// <summary>
	/// 选旗（M7.5 领土目标层）：16 格里挑"能动手"的旗——己方格（邻接已含）或邻接己方领土的
	/// 中立/敌格（CanActOnCell 与 Feed 的入侵门同一规则）。价值÷距离评分：
	/// 满驻军不碰；己方残血=回防加固；中立=无守军优先；敌格=驻军越薄越值得抢（王牌 ×1.5 更爱进攻）。
	/// 新兵不参与（照旧觅食游荡）；每 BotFlagPickInterval 重估一次，带队随机抖动分散扎堆。
	/// </summary>
	void PickFlagTarget()
	{
		_sinceFlagPick = 0;
		_hasFlagTarget = false;

		if ( !MatchState.IsTerritory || _self.TeamIndex < 0 ) return;
		if ( Difficulty == BotDifficulty.Rookie ) return;

		var myPos = _self.WorldPosition;
		var bestScore = 0f;
		var grid = GameConfig.TerritoryGrid;

		for ( int cy = 0; cy < grid; cy++ )
		{
			for ( int cx = 0; cx < grid; cx++ )
			{
				TerritoryManager.CellState( cx, cy, out var owner, out _, out var score );
				if ( owner == _self.TeamIndex && score >= GameConfig.TerritoryCaptureScore ) continue;   // 满驻军没活干
				if ( !TerritoryManager.CanActOnCell( cx, cy, _self.TeamIndex ) ) continue;              // 够不着不空想

				float value;
				if ( owner == _self.TeamIndex )
					value = 1f + ( GameConfig.TerritoryCaptureScore - score ) / GameConfig.TerritoryCaptureScore;      // 回防加固
				else if ( owner < 0 )
					value = 1.3f;                                                                                      // 中立无守军
				else
					value = 0.6f + ( GameConfig.TerritoryCaptureScore - score ) / GameConfig.TerritoryCaptureScore;    // 敌格看残度

				if ( Difficulty == BotDifficulty.Ace && owner >= 0 && owner != _self.TeamIndex ) value *= 1.5f;

				var d = myPos.Distance( TerritoryManager.CellCenter( cx, cy ) );
				var s = value * 1000f / ( 200f + d ) * Game.Random.Float( 0.8f, 1.2f );   // 价值÷距离 + 抖动防全队扎堆
				if ( s > bestScore )
				{
					bestScore = s;
					_flagTarget = TerritoryManager.CellCenter( cx, cy );
					_hasFlagTarget = true;
				}
			}
		}
	}

	/// <summary>
	/// 吐孢子喂旗（M7.5 领土目标层）：在目标旗圈内朝旗心吐——走 DoEject 同一条权威路径
	/// （内部自带 0.15s 节流；孢子进旗圈即计分消耗）。质量低于阈值不喂（饿瘦=白给），
	/// 保命/吃优先，本方法冷却再套一层控节奏。
	/// </summary>
	void TryFeedFlag( BotContext ctx )
	{
		if ( !GameConfig.EnableEject || !_hasFlagTarget ) return;
		if ( ctx.ThreatCount > 0 || ctx.PreyMass > 0f ) return;
		if ( _self.Mass < GameConfig.BotFeedFlagMinMass ) return;
		if ( _self.WorldPosition.Distance( _flagTarget ) > GameConfig.TerritoryFlagRadius * 1.2f ) return;   // 没到位不空吐
		if ( _sinceFlagFeed < GameConfig.BotFeedFlagCooldown ) return;

		_sinceFlagFeed = 0;
		CircleroyaleGame.Current?.DoEject( _self, new Vector2( _flagTarget.x, _flagTarget.y ) );
	}

	/// <summary> 收集态势：威胁/猎物（球 + 敌方分身，跳过队友）+ 喂养目标 + 队友列表 </summary>
	BotContext Survey()
	{
		var ctx = new BotContext();
		var game = CircleroyaleGame.Current;
		if ( game is null ) return ctx;

		float threatDist = float.MaxValue;
		float preyScore = -1f;      // 猎物按"质量回报/距离"评分，不再单纯取最近
		float feedMass = -1f;
		var myPos = _self.WorldPosition;
		var myMass = _self.Mass;
		var shielded = _self.HasBuff( PowerUpManager.Kind.Shield );   // 护盾期连分身都吞不了我

		foreach ( var b in game.Balls )
		{
			if ( !b.IsValid() || !b.Alive || b == _self ) continue;

			bool mate = MatchState.IsTeam && b.TeamIndex >= 0 && b.TeamIndex == _self.TeamIndex;
			if ( mate ) ctx.Mates.Add( b );

			var d = myPos.Distance( b.WorldPosition );

			// 分裂风险评估（v0.7.8.32，独立于威胁分支）：减半后的主体会不会被它近身吃掉——
			// 能吃"减半我"但吃不了"现在的我"的中等敌人恰恰是分裂后的头号杀手
			if ( !mate && b.Mass >= myMass * 0.5f * GameConfig.EatRatio
				&& d < b.Radius + _self.Radius * 0.75f + GameConfig.BotSplitRiskRange )
			{
				ctx.DangerAfterSplit = true;
			}

			// 威胁：质量够吃我（≥ EatRatio，与权威吃取同式）、不是队友、我不在保护/护盾期
			if ( !mate && b.Mass >= myMass * GameConfig.EatRatio && !_self.IsProtected && !shielded )
			{
				var range = b.Radius + _self.Radius + GameConfig.BotFleeRange * RangeScale();
				if ( d < range )
				{
					ctx.ThreatCount++;
					ctx.FleeDir += ( myPos - b.WorldPosition ).Normal * ( 1f - d / range );   // 越近推力越大
					if ( d < threatDist )
					{
						threatDist = d;
						ctx.Threat = b;
						ctx.ThreatPos = b.WorldPosition;
						ctx.ThreatRadius = b.Radius;
					}
				}
			}
			// 猎物：我够吃它、不是队友、双方都不在保护期（护盾期免战，追了也白追）；
			// 道具护盾同理——追也吃不到，跳过不浪费时间
			else if ( !mate && myMass >= b.Mass * GameConfig.EatRatio && !b.IsProtected && !_self.IsProtected
				&& !b.HasBuff( PowerUpManager.Kind.Shield ) )
			{
				var range = GameConfig.BotHuntRange * RangeScale();
				if ( d < range )
				{
					var score = b.Mass / ( 100f + d );
					if ( score > preyScore )
					{
						ctx.Prey = b;
						ctx.PreyPos = b.WorldPosition;
						ctx.PreyRadius = b.Radius;
						ctx.PreyMass = b.Mass;
						ctx.PreyVel = b.Velocity;
						preyScore = score;
					}
				}
			}

			// 喂养目标（团队赛，养大哥）：范围内最大的、明显比我大的队友
			if ( mate && GameConfig.EnableEject && myMass >= GameConfig.BotFeedMinMass
				&& b.Mass > myMass * 1.25f
				&& d < GameConfig.BotFeedRange * RangeScale()
				&& b.Mass > feedMass )
			{
				ctx.FeedTarget = b;
				feedMass = b.Mass;
			}
		}

		// 敌方分身感知（v0.7.8.32）：分身能吞无盾主球也能被吞——此前 bot 完全看不见它们。
		// 阵营按主人球判（TickCells 自愈保证 OwnerBall 有效；主人真死时分身本来也会被清）
		var cells = game.Cells;
		if ( cells is not null )
		{
			foreach ( var c in cells )
			{
				if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;
				if ( c.OwnerSteamId == _self.OwnerSteamId ) continue;
				var ob = c.OwnerBall;
				if ( MatchState.IsTeam && ob.IsValid() && ob.TeamIndex >= 0 && ob.TeamIndex == _self.TeamIndex ) continue;

				var d = myPos.Distance( c.Pos );

				// 分裂风险评估（同球规则，独立于威胁分支）
				if ( c.Mass >= myMass * 0.5f * GameConfig.EatRatio
					&& d < c.Radius + _self.Radius * 0.75f + GameConfig.BotSplitRiskRange )
				{
					ctx.DangerAfterSplit = true;
				}

				if ( c.Mass >= myMass * GameConfig.EatRatio && !_self.IsProtected && !shielded )
				{
					var range = c.Radius + _self.Radius + GameConfig.BotFleeRange * RangeScale();
					if ( d < range )
					{
						ctx.ThreatCount++;
						ctx.FleeDir += ( myPos - c.Pos ).Normal * ( 1f - d / range );
						if ( d < threatDist )
						{
							threatDist = d;
							ctx.Threat = null;
							ctx.ThreatPos = c.Pos;
							ctx.ThreatRadius = c.Radius;
						}
					}
				}
				else if ( myMass >= c.Mass * GameConfig.EatRatio )
				{
					var range = GameConfig.BotHuntRange * RangeScale();
					if ( d < range )
					{
						var score = c.Mass / ( 100f + d );
						if ( score > preyScore )
						{
							ctx.Prey = null;
							ctx.PreyPos = c.Pos;
							ctx.PreyRadius = c.Radius;
							ctx.PreyMass = c.Mass;
							ctx.PreyVel = c.Impulse + c.SteerVel;   // 分身当前速度（冲量+跟随转向）
							preyScore = score;
						}
					}
				}
			}
		}
		return ctx;
	}

	/// <summary> 方向决策：逃跑 &gt; 追猎 &gt; 靠近喂养目标 &gt; 觅食 &gt; 游荡（团队赛偏集结） </summary>
	Vector2 DecideDir( BotContext ctx )
	{
		var myPos = _self.WorldPosition;

		// 逃跑：全部威胁反向合成（越近权重越大，v0.7.8.32）+ 朝场地中心分量（防被逼进角落）
		if ( ctx.ThreatCount > 0 )
		{
			var flee = ctx.FleeDir.Length > 0.01f
				? Norm( ctx.FleeDir )
				: new Vector2( myPos.x - ctx.ThreatPos.x, myPos.y - ctx.ThreatPos.y );
			var toCenter = new Vector2( -myPos.x, -myPos.y );
			var len = toCenter.Length;
			toCenter = len > 0.001f ? toCenter / len : Vector2.Zero;
			return Norm( new Vector3( flee.x + toCenter.x * 0.45f, flee.y + toCenter.y * 0.45f, 0f ) );
		}

		// 追猎：按猎物当前速度打拦截提前量（v0.7.8.32）——直线追永远追不上会跑的猎物
		if ( ctx.PreyMass > 0f )
		{
			var target = ctx.PreyPos;
			var lead = LeadFactor();
			if ( lead > 0f && ctx.PreyVel.LengthSquared > 1f )
			{
				var mySpeed = GameConfig.BaseSpeed
					* MathF.Pow( GameConfig.StartMass / MathF.Max( 1f, _self.Mass ), GameConfig.SpeedCurve );
				var d = myPos.Distance( ctx.PreyPos );
				var leadVec = ctx.PreyVel * ( d / MathF.Max( 60f, mySpeed ) ) * lead;
				var leadLen = leadVec.Length;
				if ( leadLen > _self.Radius * 1.5f ) leadVec *= _self.Radius * 1.5f / leadLen;   // 封顶别冲过头
				target = ctx.PreyPos + leadVec;
			}
			return Norm( target - myPos );
		}

		// 领土目标导航（M7.5）：无威胁无猎物时朝目标旗赶路——压过喂大哥/捡道具/觅食
		// （旗是胜利条件）；饿瘦到喂旗阈值以下就放下目标，落回觅食层恢复
		if ( _hasFlagTarget )
		{
			if ( _self.Mass < GameConfig.BotFeedFlagMinMass )
			{
				_hasFlagTarget = false;
			}
			else
			{
				return Norm( _flagTarget - myPos );
			}
		}

		// 喂养：朝大哥移动（孢子沿路径滑过去；喂瘦到不满足条件自动回归觅食）
		if ( ctx.FeedTarget.IsValid() )
			return Norm( ctx.FeedTarget.WorldPosition - myPos );

		// 顺路捡道具（v0.7.8.7）：无威胁无猎物时附近场上有道具就冲过去（bot 拾取即用，
		// 捡到立刻生效）；喂大哥/觅食都让位给这个
		var pu = NearestPowerUpPos( myPos );
		if ( pu.HasValue )
			return Norm( new Vector3( pu.Value.x, pu.Value.y, 0f ) - myPos );

		// 觅食：先看散落孢子（质量远大于食物点，值得绕路），没有再找最近食物；
		// 两层各自带粘性（v0.7.8.32）：目标活着就追到底，不在两个目标间来回锯齿
		var blob = NearestBlob( myPos );
		var food = NearestFoodPos( myPos );
		Vector2? target2 = null;
		bool blobChosen = false;

		if ( blob is not null && food is null )
		{
			target2 = new Vector2( blob.Pos.x, blob.Pos.y );
			blobChosen = true;
		}
		else if ( blob is not null && food.HasValue )
		{
			// 孢子比食物点值钱好几倍：绕路 2.5 倍距离以内都值得去拿
			var bd = myPos.Distance( blob.Pos );
			var fd = myPos.Distance( new Vector3( food.Value.x, food.Value.y, 0f ) );
			if ( bd < fd * 2.5f + 160f )
			{
				target2 = new Vector2( blob.Pos.x, blob.Pos.y );
				blobChosen = true;
			}
		}
		if ( !blobChosen && food.HasValue ) target2 = food.Value;

		if ( target2.HasValue )
		{
			if ( blobChosen )
			{
				_stickyBlobId = blob.Id;
				_sinceStickyBlob = 0;
				_sinceStickyFood = 99f;   // 失效化
			}
			else
			{
				_stickyFood = target2.Value;
				_sinceStickyFood = 0;
				_stickyBlobId = -1;
			}
			return Norm( new Vector3( target2.Value.x, target2.Value.y, 0f ) - myPos );
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
	/// 分裂逃命（v0.7.8.7，agar 保命手法）：威胁即将咬到（距离小于双方半径之和 + 一口余量）且
	/// 自身分裂后仍是像样的身体 → 朝逃跑方向分裂——分身带冲量瞬间前窜、主体变轻提速，
	/// 脱险后冷却一到自然合体。与捕猎共用 _sinceSplit 冷却，逃命优先判定。
	/// v0.7.8.32：威胁可以是敌方分身（它们同样能吞主球）。
	/// </summary>
	void TrySplitEscape( BotContext ctx )
	{
		if ( !GameConfig.EnableSplit || ctx.ThreatCount == 0 ) return;
		if ( _self.Mass < GameConfig.BotSplitMinMass * 2f ) return;          // 太小分裂=白送两份
		if ( _sinceSplit < GameConfig.BotSplitCooldown ) return;

		var to = _self.WorldPosition - ctx.ThreatPos;
		var d = MathF.Max( to.Length, 0.001f );
		if ( d > ctx.ThreatRadius + _self.Radius + 60f ) return;            // 还没到嘴边不浪费质量
		if ( Game.Random.Float( 0f, 1f ) > ActionChance() ) return;

		_sinceSplit = 0;
		// 逃跑方向 = 反向 + 朝场地中心分量（与 DecideDir 逃跑同一套）；传"瞄准点"语义
		var away = Norm( to + ( Vector3.Zero - _self.WorldPosition ).Normal * 0.45f );
		var aimPoint = new Vector2( _self.WorldPosition.x + away.x * 2048f,
			_self.WorldPosition.y + away.y * 2048f );
		CircleroyaleGame.Current?.DoSplit( _self, aimPoint );
		GameLog.Info( $"[bot] split-escape '{_self.PlayerName}' <- '{ctx.Threat?.PlayerName ?? "cell"}'" );
	}

	/// <summary>
	/// 分裂捕猎（v0.6.6.0）：贴身（半径+打击距离内）、分出去的一半仍够吃猎物、过冷却与
	/// 难度概率门槛 → 朝猎物分裂。分身带冲量飞出去，之后跟随主人转向继续压猎物。
	/// v0.7.8.7：传猎物位置为瞄准点——每颗身体各自朝猎物扇形合围。
	/// v0.7.8.32：猎物太小不烧冷却（BotSplitPreyFraction）；减半后 nearby 有够吃我的敌人就放弃
	/// （送命分裂，DangerAfterSplit）；猎物可以是敌方分身。
	/// </summary>
	void TrySplitHunt( BotContext ctx )
	{
		if ( !GameConfig.EnableSplit || ctx.PreyMass <= 0f ) return;
		if ( _self.Mass < GameConfig.BotSplitMinMass ) return;                       // 太小分裂=白送质量
		if ( ctx.PreyMass < _self.Mass * GameConfig.BotSplitPreyFraction ) return;   // 太小的猎物不值得烧冷却
		if ( _self.Mass * 0.5f < ctx.PreyMass * GameConfig.EatRatio ) return;        // 分身必须还能吃掉猎物
		if ( ctx.DangerAfterSplit ) return;                                          // 减半=送命，不干
		if ( _sinceSplit < GameConfig.BotSplitCooldown ) return;

		var d = _self.WorldPosition.Distance( ctx.PreyPos );
		if ( d > _self.Radius + GameConfig.BotSplitRange * RangeScale() ) return;
		if ( Game.Random.Float( 0f, 1f ) > ActionChance() ) return;

		_sinceSplit = 0;
		CircleroyaleGame.Current?.DoSplit( _self, new Vector2( ctx.PreyPos.x, ctx.PreyPos.y ) );
		GameLog.Info( $"[bot] split-hunt '{_self.PlayerName}' -> '{ctx.Prey?.PlayerName ?? "cell"}'" );
	}

	/// <summary>
	/// 吐孢子喂队友（团队赛，v0.6.6.0 养大哥）：向范围内最大的队友吐孢子，
	/// 走 DoEject 同一条权威路径（内部自带 0.15s 节流；多身体一起吐）。
	/// 决策间隔 + 本方法冷却 + 难度概率三重节流，喂瘦到对方不足我 1.25 倍自动停。
	/// </summary>
	void TryFeedMate( BotContext ctx )
	{
		if ( !GameConfig.EnableEject || ctx.FeedTarget is null || !ctx.FeedTarget.IsValid() ) return;
		if ( ctx.ThreatCount > 0 || ctx.PreyMass > 0f ) return;   // 保命/吃优先
		if ( _sinceFeed < GameConfig.BotFeedCooldown ) return;
		if ( Game.Random.Float( 0f, 1f ) > ActionChance() ) return;

		_sinceFeed = 0;
		// 传大哥位置为瞄准点（v0.7.8.7）：每颗身体各自朝大哥吐——远处的分身不再沿
		// 主球方向平行走空，孢子真正落进大哥嘴里
		CircleroyaleGame.Current?.DoEject( _self,
			new Vector2( ctx.FeedTarget.WorldPosition.x, ctx.FeedTarget.WorldPosition.y ) );
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

	/// <summary> 躲刺爆弹幕（v0.7.8.32）：飞行中的刺也是真伤害（命中 -15% 质量），近距离侧向闪避 </summary>
	Vector2 AvoidSpines( Vector2 dir )
	{
		var spines = CircleroyaleGame.Current?.Spines;
		if ( spines is null || spines.Count == 0 ) return dir;

		var myPos = _self.WorldPosition;
		var aware = _self.Radius + 96f;
		foreach ( var s in spines )
		{
			if ( s.SinceSpawn > 0.5f ) continue;   // 后半程动能耗尽，不管

			var dx = myPos.x - s.Pos.x;
			var dy = myPos.y - s.Pos.y;
			var d = MathF.Sqrt( dx * dx + dy * dy );
			if ( d >= aware || d < 0.001f ) continue;

			dir = Norm( new Vector3( dir.x + dx / d * 1.2f, dir.y + dy / d * 1.2f, 0f ) );
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

		// 粘性折扣（v0.7.8.32）：上次的目标还活着就当它更近（圈内匹配），防相邻食物间来回锯齿
		var sticky = _sinceStickyFood < 2.5f ? (Vector2?)_stickyFood : null;

		for ( int i = 0; i < foods.Length; i++ )
		{
			if ( !foods[i].Alive ) continue;

			var dx = foods[i].Pos.x - myPos.x;
			var dy = foods[i].Pos.y - myPos.y;
			var dSq = dx * dx + dy * dy;
			if ( sticky.HasValue )
			{
				var sx = foods[i].Pos.x - sticky.Value.x;
				var sy = foods[i].Pos.y - sticky.Value.y;
				if ( sx * sx + sy * sy < 64f * 64f ) dSq *= 0.45f;
			}
			if ( dSq > seekSq || dSq >= best ) continue;

			best = dSq;
			bestPos = foods[i].Pos;
		}
		return bestPos;
	}

	/// <summary> 感知范围内最近的场上道具（v0.7.8.7：bot 会专程去捡，拾取即用） </summary>
	Vector2? NearestPowerUpPos( Vector3 myPos )
	{
		var mgr = CircleroyaleGame.Current?.PowerUps;
		if ( mgr is null || !mgr.Ready ) return null;

		var best = float.MaxValue;
		Vector2? bestPos = null;
		var seek = GameConfig.BotSeekFoodRange * 0.75f * RangeScale();
		var seekSq = seek * seek;

		foreach ( var s in mgr.Slots )
		{
			if ( !s.Alive ) continue;

			var dx = s.Pos.x - myPos.x;
			var dy = s.Pos.y - myPos.y;
			var dSq = dx * dx + dy * dy;
			if ( dSq > seekSq || dSq >= best ) continue;

			best = dSq;
			bestPos = s.Pos;
		}
		return bestPos;
	}

	/// <summary> 感知范围内最值得的散落孢子（v0.7.8.7 起不再无视；v0.7.8.32：按质量折算距离 + 目标粘性） </summary>
	CellPiece NearestBlob( Vector3 myPos )
	{
		var cells = CircleroyaleGame.Current?.Cells;
		if ( cells is null || cells.Count == 0 ) return null;

		CellPiece best = null;
		var bestScore = float.MaxValue;
		var seek = GameConfig.BotSeekFoodRange * RangeScale();
		var seekSq = seek * seek;

		// 粘性：还在追同一团（Id 未变）就打距离折扣
		var stickyId = _sinceStickyBlob < 3f ? _stickyBlobId : -1;

		foreach ( var c in cells )
		{
			if ( c.PieceKind != CellPiece.Kind.EjectedMass ) continue;

			var dx = c.Pos.x - myPos.x;
			var dy = c.Pos.y - myPos.y;
			var dSq = dx * dx + dy * dy;

			// 大孢子值得绕路：按质量折算等效距离（30 质量≈距离减半）
			dSq /= 1f + c.Mass / 30f;
			if ( c.Id == stickyId ) dSq *= 0.5f;

			if ( dSq > seekSq || dSq >= bestScore ) continue;

			bestScore = dSq;
			best = c;
		}
		return best;
	}

	static Vector2 Norm( Vector3 v )
	{
		var v2 = new Vector2( v.x, v.y );
		var len = v2.Length;
		return len > 0.001f ? v2 / len : Vector2.Zero;
	}
}