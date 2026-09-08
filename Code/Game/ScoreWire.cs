/// <summary>
/// 比赛结算榜行（MatchOver RPC 载荷，纯数据结构同 CellWire/FoodData 模式）。
/// Mass = 总质量（主球 + 分身），结束瞬间由 host 排好序。
/// </summary>
public struct ScoreWire
{
	public long SteamId;
	public string Name;
	public float Mass;
	public byte Team;    // 队伍号（普通赛恒 0，展示层看 MatchState.IsTeam 决定是否显示）
	public bool Bot;
}
