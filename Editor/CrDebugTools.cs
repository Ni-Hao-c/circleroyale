using System.Text;
using Editor.Mcp;

/// <summary>
/// 临时调试工具集（排查分队/分身异常，v0.7.8.25）：编辑器 MCP 的 call_tool 直接调用。
/// cr_balls = 全部球的权威状态；cr_cells = 场上分身/孢子概览。排查完可整文件删除。
/// </summary>
[McpToolset( "crdebug", "circleroyale debug: dump balls / cells live state" )]
public static class CrDebugTools
{
	/// <summary>Dump 每颗球的名字/队伍/生死/质量/bot/SteamId + MatchState 概要</summary>
	[McpTool( "cr_balls" )]
	public static string CrBalls()
	{
		var g = CircleroyaleGame.Current;
		if ( g is null ) return "no game";

		var sb = new StringBuilder();
		int i = 0;
		foreach ( var b in g.Balls )
		{
			if ( b is null || !b.IsValid() )
			{
				sb.AppendLine( $"{i}: <invalid>" );
				i++;
				continue;
			}
			sb.AppendLine( $"{i}: '{b.PlayerName}' T={b.TeamIndex} alive={b.Alive} mass={b.Mass:0} bot={b.IsBot} sid={b.OwnerSteamId}" );
			i++;
		}
		sb.AppendLine( $"MatchState: IsTeam={MatchState.IsTeam} TeamSize={MatchState.TeamSize} mode={MatchState.CurrentMode} target={MatchState.PlayerTarget} gameStarted={g.IsMatchStarted}" );
		return sb.ToString();
	}

	/// <summary>Dump 场上分身/孢子（kind/主人/质量/冷却/年龄），最多 200 行</summary>
	[McpTool( "cr_cells" )]
	public static string CrCells()
	{
		var g = CircleroyaleGame.Current;
		if ( g is null ) return "no game";

		var sb = new StringBuilder();
		int i = 0;
		foreach ( var c in g.Cells )
		{
			sb.AppendLine( $"{i}: kind={c.PieceKind} owner={c.OwnerSteamId} mass={c.Mass:0} cd={c.MergeCooldown:0.0} age={c.SinceSpawn:0.0}" );
			i++;
			if ( i >= 200 )
			{
				sb.AppendLine( "..." );
				break;
			}
		}
		return sb.ToString();
	}
}