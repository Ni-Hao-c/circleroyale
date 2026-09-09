using System;

/// <summary>
/// 等级系统（v0.7.8.19）：XP 存云端 Stats（cr_xp，Increment 累加、跨机器持久），
/// 等级=**本地曲线从 XP 总值现算**（改曲线不用迁移数据）。
/// 局内他人等级：每台机器算好自己的等级写进自己 Ball 的 [Sync] AccountLevel（owner 可写），
/// 全场名牌可见；bot 恒 0 不显示。排行页走 Leaderboards.GetFromStat("cr_xp") 查全球榜。
/// 边界：Stats 是客户端上报（休闲向，别拿去当竞技天梯依据）；写后立刻读要 FlushAndWaitAsync。
/// 进程内计数（本局击杀）重启清零只影响当局经验。
/// </summary>
public static class GameXp
{
	public const string StatName = "cr_xp";

	const double PlayedXp = 10;   // 参与奖
	const double KillXp = 20;     // 每击杀
	const double TopTenXp = 25;   // 第 4~10 名
	const double PodiumXp = 60;   // 第 2~3 名
	const double FirstXp = 100;   // 个人第一名
	const double WinXp = 50;      // 团队夺冠

	static double _localXp = -1;         // -1 = 云端未加载
	static TimeSince _sinceFetch = 999f; // 拉取节流（失败也 5s 再试）
	static int _matchKills;              // 本局击杀（结算消费后清零）

	/// <summary> 本机等级（云端未加载时 0 = 不显示） </summary>
	public static int LocalLevel => _localXp < 0 ? 0 : LevelFromXp( _localXp );

	/// <summary> 菜单/HUD 用的一行文本（未加载返回空串，Label 留白） </summary>
	public static string LevelText() =>
		_localXp < 0 ? "" : $"LEVEL {LocalLevel}  —  {(int)_localXp} XP";

	/// <summary> 等级曲线：升到 level 级累计需要 150 × (level-1)^1.5（L2=150，L5≈1200，L10≈4300，L20≈25500） </summary>
	public static double XpForLevel( int level ) => 150.0 * Math.Pow( Math.Max( 0, level - 1 ), 1.5 );

	public static int LevelFromXp( double xp )
	{
		int lvl = 1;
		while ( lvl < 999 && xp >= XpForLevel( lvl + 1 ) ) lvl++;
		return lvl;
	}

	/// <summary> 拉取本机云端 XP（节流 5s，失败自愈重试）。菜单 OnUpdate / 结算后调用。
	/// ⚠️ 只升不降合并（v0.7.8.29）：Increment 上传后会本地 Predict（缓存立即含新值），
	/// 但云端 ingest 有延迟——结算后立刻 Refresh 会拿回旧值覆盖预测，观感"没加经验"；
	/// XP 单调递增，取 max 兼顾"另一台机器玩出了更高值"与"本地预测领先"两种情况 </summary>
	public static async void EnsureLocalXp()
	{
		if ( Application.IsDedicatedServer || _sinceFetch < 5f ) return;
		_sinceFetch = 0;

		try
		{
			var stats = Sandbox.Services.Stats.LocalPlayer;
			await stats.Refresh();
			_localXp = Math.Max( _localXp, stats.Get( StatName ).Sum );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[xp] local stat refresh failed: {e.Message}" );
		}
	}

	/// <summary> 本机击杀记账（GameAchievements.LocalKill 顺带调） </summary>
	public static void OnLocalKill() => _matchKills++;

	/// <summary>
	/// 结算给经验（各端各自上报本机）：参与 10 + 击杀×20 + 名次（1st=100 / 2~3rd=60 / 4~10th=25）+ 团队夺冠 50。
	/// 随后立即 Flush 并重新拉取——下一局的等级展示就是新值。
	/// </summary>
	public static void Settlement( ScoreWire[] standings, long localSteamId, bool won )
	{
		if ( standings is null || standings.Length == 0 ) return;

		double xp = PlayedXp + _matchKills * KillXp;

		for ( int i = 0; i < standings.Length; i++ )
		{
			if ( standings[i].SteamId != localSteamId ) continue;
			if ( i == 0 ) xp += FirstXp;
			else if ( i < 3 ) xp += PodiumXp;
			else if ( i < 10 ) xp += TopTenXp;
			break;
		}
		if ( won ) xp += WinXp;

		var kills = _matchKills;
		_matchKills = 0;

		Sandbox.Services.Stats.Increment( StatName, xp );   // 引擎内部已本地 Predict：本地缓存立即含新值
		Sandbox.Services.Stats.Flush();                     // 后台上传（云端 ingest 有延迟，别等它再读）
		_sinceFetch = 999f;                                 // 立刻允许重拉
		RefreshLocalNow();
		GameLog.Info( $"[xp] settlement +{xp:0} (kills={kills}, won={won})" );
	}

	/// <summary> 结算后的读回：**不 Refresh**（会用云端未 ingest 的旧值覆盖本地预测，观感"没加经验"），
	/// 直接读本地 Predict 后的 Sum；下次 5s 节流的 EnsureLocalXp 再对齐云端（只升不降） </summary>
	static void RefreshLocalNow()
	{
		try
		{
			var stats = Sandbox.Services.Stats.LocalPlayer;
			_localXp = Math.Max( _localXp, stats.Get( StatName ).Sum );
		}
		catch ( Exception e )
		{
			Log.Warning( $"[xp] post-settlement refresh failed: {e.Message}" );
		}
	}

	/// <summary> 每帧（GameHud.OnUpdate 调）：把本机等级写进自己的球——[Sync] owner 写，全场名牌可见 </summary>
	public static void MatchTick( CircleroyaleGame game )
	{
		var local = game.LocalBall;
		if ( !local.IsValid() ) return;

		var lvl = LocalLevel;
		if ( lvl > 0 && local.AccountLevel != lvl ) local.AccountLevel = lvl;
	}
}