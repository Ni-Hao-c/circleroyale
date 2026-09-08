using System;
using System.Collections.Generic;

/// <summary>
/// 成就对接（v0.7.8.14，共 25 条）：成就本体在 sbox.game 游戏包页面 Services > Achievements 配置
/// （ident/标题/描述/图标/分值/解锁模式），代码只按 ident 触发 Manual 成就。
/// 引擎行为（源码 AchievementCollection.ManualUnlock）：ident 未配置/已解锁/非 Manual 时
/// 静默跳过——先写代码后配页面，配好即生效；每次事件都调 Unlock，由引擎去重。
/// Unlock 只对**本机玩家**生效（专用服务器自动 no-op）——所有触发点必须先 GameSfx.IsMine
/// 判定"是不是本机赚到的"，host 不替客户端解锁。
/// 累计型成就（吃食物总数等）走包页面 Stat 绑定（Stats.Increment 即可），不走这里。
/// 进程内计数器（击杀数/捡包数）重启会清零——一次性成就解锁由云端去重，只影响"晚几局解锁"。
/// </summary>
public static class GameAchievements
{
	// ---- 首批 5 条（v0.7.8.13）----
	public const string FirstBlood = "first_blood";     // 首次吃掉一名玩家
	public const string Godlike = "godlike";            // 单局五连杀
	public const string SpikeBlast = "spike_blast";     // 尖刺分身炸掉比自己大的球
	public const string TeamWinner = "team_winner";     // 团队赛所在队夺冠
	public const string Champion = "champion";          // 个人第一名结算

	// ---- 第二批 20 条（v0.7.8.14）----
	public const string DoubleKill = "double_kill";     // 单局双连杀
	public const string TripleKill = "triple_kill";     // 单局三连杀
	public const string Rampage = "rampage";            // 单局四连杀
	public const string BigPrey = "big_prey";           // 单口吞掉大球
	public const string Revenge = "revenge";            // 吃掉上次吃掉自己的玩家
	public const string Hunter10 = "hunter_10";         // 累计吃 10 名玩家
	public const string Hunter50 = "hunter_50";         // 累计吃 50 名玩家
	public const string HalfTon = "half_ton";           // 一条命总质量 500
	public const string OneTon = "one_ton";             // 一条命总质量 1000
	public const string TwoTon = "two_ton";             // 一条命总质量 2000
	public const string FiveTon = "five_ton";           // 一条命总质量 5000
	public const string FirstTool = "first_tool";       // 首次使用道具
	public const string FullBag = "full_bag";           // 两格背包同时有货
	public const string PowerHoarder = "power_hoarder"; // 累计捡 5 个道具入包
	public const string FirstFeed = "first_feed";       // 首次喂食触发道具
	public const string SpikeOp = "spike_op";           // 首次放出尖刺分身
	public const string OctoSplit = "octo_split";       // 同时拥有 8 颗身体
	public const string Untouchable = "untouchable";    // 一条命存活 5 分钟
	public const string FirstDeath = "first_death";     // 第一次被吃
	public const string FirstWin = "first_win";         // 首次获胜

	const float BigPreyMass = 800f;   // 单口吞入质量门槛（返还前的口径，对应 ~1000 质量的球）

	/// <summary> 一条命内的质量里程碑（与 MassSteps 一一对应解锁 HalfTon/OneTon/TwoTon/FiveTon） </summary>
	static readonly float[] MassSteps = { 500f, 1000f, 2000f, 5000f };

	static int _lifetimeKills;     // 进程内累计击杀
	static int _lifetimePickups;   // 进程内累计道具入包
	static long _lastKiller;       // 最近吃掉本机的玩家（复仇名单）

	/// <summary> 本机击杀（每次吃球都调，引擎对已解锁静默去重）；
	/// combo = 本局连杀数（GameSfx 10s 窗口），massGained = 本口吞入质量 </summary>
	public static void LocalKill( int combo, float massGained, long eatenSteamId )
	{
		Unlock( FirstBlood );
		if ( combo >= 2 ) Unlock( DoubleKill );
		if ( combo >= 3 ) Unlock( TripleKill );
		if ( combo >= 4 ) Unlock( Rampage );
		if ( combo >= 5 ) Unlock( Godlike );

		if ( massGained >= BigPreyMass ) Unlock( BigPrey );
		if ( eatenSteamId != 0 && eatenSteamId == _lastKiller ) Unlock( Revenge );

		_lifetimeKills++;
		if ( _lifetimeKills >= 10 ) Unlock( Hunter10 );
		if ( _lifetimeKills >= 50 ) Unlock( Hunter50 );
	}

	/// <summary> 本机被吃：参与奖 + 记仇（之后吃掉他解锁 Revenge） </summary>
	public static void LocalDeath( long killerSteamId )
	{
		_lastKiller = killerSteamId;
		Unlock( FirstDeath );
	}

	/// <summary>
	/// 每帧体检（GameHud.OnUpdate 调，全端）：一条命质量里程碑 / 8 颗身体 / 存活 5 分钟 / 背包双满。
	/// 门槛判定每帧重复触发由引擎去重，无需本地状态。
	/// </summary>
	public static void MatchTick( CircleroyaleGame game )
	{
		if ( !game.IsMatchStarted || MatchState.MatchOver ) return;

		var local = game.LocalBall;
		if ( !local.IsValid() || !local.Alive ) return;

		var mass = game.TotalMassFor( local );
		if ( mass >= MassSteps[0] ) Unlock( HalfTon );
		if ( mass >= MassSteps[1] ) Unlock( OneTon );
		if ( mass >= MassSteps[2] ) Unlock( TwoTon );
		if ( mass >= MassSteps[3] ) Unlock( FiveTon );

		int pieces = 0;
		foreach ( var c in game.Cells )
		{
			if ( c.PieceKind == CellPiece.Kind.SplitPiece && c.OwnerSteamId == local.OwnerSteamId ) pieces++;
		}
		if ( pieces >= 8 ) Unlock( OctoSplit );

		if ( local.LifeSeconds > 300f ) Unlock( Untouchable );

		// 背包双满（PowerA/B 是 [Sync]，255=空）——同步到位才触发，无时序竞态
		if ( local.PowerA != 255 && local.PowerB != 255 ) Unlock( FullBag );
	}

	/// <summary> 本机按 Q/E 释放道具（UsePowerSlot 横幅处调）：首次用道具 / 首次放尖刺 </summary>
	public static void PowerUsed( byte kind )
	{
		Unlock( FirstTool );
		if ( kind == (byte)PowerUpManager.Kind.Spike ) Unlock( SpikeOp );
	}

	/// <summary> 本机捡到道具入包（host 本机拾取与客户端广播落地两条路都会调）：累计 5 个 </summary>
	public static void PowerStored()
	{
		if ( ++_lifetimePickups >= 5 ) Unlock( PowerHoarder );
	}

	/// <summary> 喂食触发道具（host 的 Feed 判定 + 客户端 PowerBanner 落地，两路都过 IsMine） </summary>
	public static void FeedTriggered( long feederSteamId )
	{
		if ( GameSfx.IsMine( feederSteamId ) )
			Unlock( FirstFeed );
	}

	/// <summary> 尖刺分身炸掉比主人更大的球（host 判定；owner=尖刺主人，广播到各端后本机判定） </summary>
	public static void SpikeMinionBlast( long ownerSteamId )
	{
		if ( GameSfx.IsMine( ownerSteamId ) )
			Unlock( SpikeBlast );
		if ( Networking.IsActive )
			NetworkManager.SpikeReward( ownerSteamId );
	}

	/// <summary> 尖刺奖励广播落地（客户端；host 已在 SpikeMinionBlast 判过，挡回声） </summary>
	public static void OnSpikeRewardRemote( long ownerSteamId )
	{
		if ( NetworkManager.IsAuthority ) return;
		if ( GameSfx.IsMine( ownerSteamId ) )
			Unlock( SpikeBlast );
	}

	/// <summary> 比赛结算（全端各自判定）：团队赛=本队总质量第一（并列也算）；普通赛=个人第一名 </summary>
	public static void Settlement( ScoreWire[] standings, long localSteamId )
	{
		if ( standings is null || standings.Length == 0 ) return;

		bool won = false;

		if ( MatchState.IsTeam )
		{
			var local = CircleroyaleGame.Current?.LocalBall;
			if ( local.IsValid() && local.TeamIndex >= 0 )
			{
				var totals = new Dictionary<int, float>();
				foreach ( var row in standings )
				{
					var t = totals.TryGetValue( row.Team, out var m ) ? m : 0f;
					totals[row.Team] = t + row.Mass;
				}

				var mine = totals.GetValueOrDefault( local.TeamIndex );
				var best = -1f;
				foreach ( var kv in totals )
				{
					if ( kv.Value > best ) best = kv.Value;
				}

				if ( mine >= best )
				{
					Unlock( TeamWinner );
					won = true;
				}
			}
		}
		else if ( standings[0].SteamId == localSteamId )
		{
			Unlock( Champion );
			won = true;
		}

		if ( won ) Unlock( FirstWin );
	}

	static void Unlock( string ident ) => Sandbox.Services.Achievements.Unlock( ident );
}