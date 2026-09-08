using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// 成绩上传与全服榜读取（M5）。云端走 Sandbox.Services：
/// 上传 = Stats.SetValue("cr_match_mass", 本局总质量) + Flush；
/// 读取 = Leaderboards.GetFromStat 聚合 Max（全服最高单局）。
/// ⚠️ 未发布的本地包后端没有包身份，Refresh 内部吞 404（Entries 保持空）——
/// 展示层据此自动降级到 LocalBoard 的本地持久化榜。
/// </summary>
public static class ScoreUploader
{
	/// <summary> 排行榜统计名（聚合 Max = 单局最高总质量） </summary>
	public const string StatName = "cr_match_mass";

	/// <summary> 本机成绩上云（按登录用户记账；拿不到本机球就静默跳过） </summary>
	public static void SubmitOwn( ScoreWire[] standings, CircleroyaleGame game )
	{
		try
		{
			var local = game?.LocalBall;
			if ( !local.IsValid() ) return;

			var mass = game.TotalMassFor( local );
			Sandbox.Services.Stats.SetValue( StatName, mass );
			Sandbox.Services.Stats.Flush();
			GameLog.Info( $"[score] uploaded {mass:0} ({StatName})" );
		}
		catch ( Exception e )
		{
			GameLog.Info( $"[score] upload failed: {e.Message}" );
		}
	}

	/// <summary> 拉全服榜 Top10（异步；空/失败回调 null——展示层降级本地榜） </summary>
	public static async void FetchGlobal( Action<List<( string Name, float Mass )>> done )
	{
		try
		{
			var board = Sandbox.Services.Leaderboards.GetFromStat( StatName );
			board.SetAggregationMax();
			board.SetSortDescending();
			board.FilterByNone();
			board.MaxEntries = 10;
			await board.Refresh();

			var list = new List<( string Name, float Mass )>();
			foreach ( var e in board.Entries )
				list.Add( ( e.DisplayName, (float)e.Value ) );

			done( list.Count > 0 ? list : null );
		}
		catch ( Exception e )
		{
			GameLog.Info( $"[score] global board fetch failed: {e.Message}" );
			done( null );
		}
	}
}
