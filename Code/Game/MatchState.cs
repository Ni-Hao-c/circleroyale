using System;

/// <summary>
/// 比赛规则与进行状态（M5）：host 在主菜单选规则，开局生效并经静态 RPC 下发；
/// 倒计时 host 权威（1Hz 校时 + 各端本地续走）；比赛结束由 host 判定并广播结算榜。
/// 全端只读 Current*，写入只来自权威端与本文件（RPC 应用幂等）。
/// </summary>
public static class MatchState
{
	public enum Mode
	{
		Ffa = 0,        // 普通赛：各自为战
		Team2 = 1,      // 团队赛：2 人一队
		Team3 = 2,      // 团队赛：3 人一队
		Team4 = 3,      // 团队赛：4 人一队
		Territory = 4,  // 领土战争（M7）：4 队 × 4 人，4×4 格驻军模型占点
	}

	/// <summary> 团队赛每队人数（查表，v0.7.8.34：Territory 加入后不能再从枚举值直推——Team4/Territory 都是 4 人队） </summary>
	public static int TeamSize => CurrentMode switch
	{
		Mode.Ffa => 1,
		Mode.Team2 => 2,
		Mode.Team3 => 3,
		_ => 4,
	};

	// ---- 菜单里选的待生效设置（host 侧；开局 ActivateForMatch 落地）----

	/// <summary> 菜单当前选择的模式（下一局生效；默认领土战争，v0.7.8.85 用户定稿） </summary>
	public static Mode PendingMode { get; set; } = Mode.Territory;

	/// <summary> 菜单当前选择的时长（秒） </summary>
	public static float PendingDuration { get; set; } = GameConfig.DefaultMatchSeconds;

	/// <summary> 菜单当前选择的场上目标人数（真人 + bot） </summary>
	public static int PendingPlayerTarget { get; set; } = GameConfig.DefaultPlayerTarget;

	// ---- 进行中的规则（全端一致；host 写，客户端经 RPC 收）----

	public static Mode CurrentMode { get; private set; } = Mode.Ffa;
	public static float Duration { get; private set; } = GameConfig.DefaultMatchSeconds;
	public static int PlayerTarget { get; private set; } = GameConfig.DefaultPlayerTarget;

	/// <summary> 剩余秒数（host 权威递减；客户端 1Hz 校时 + 本地每帧续走） </summary>
	public static float TimeLeft { get; internal set; }

	/// <summary> host 心跳（v0.6.8.0）：Apply/ApplyTick 到达即归零。对局中 host 以 1Hz 广播校时，
	/// 客户端据此检测"host 静默挂了"——host 进程被杀时引擎会话可能不自动失效（僵尸态），
	/// 只能靠应用层心跳发现（CircleroyaleGame.Tick 超时强制断线） </summary>
	public static TimeSince SinceLastTick;

	/// <summary> 本局是否已结束（结算面板展示中，全场模拟冻结） </summary>
	public static bool MatchOver { get; internal set; }

	public static bool IsTeam => CurrentMode >= Mode.Team2;

	/// <summary> 是否领土战争模式（M7） </summary>
	public static bool IsTerritory => CurrentMode == Mode.Territory;

	/// <summary> 队伍显示色（团队赛：球色/比分条/播报按队伍取色；队伍最多 24 个，调色板循环取用） </summary>
	public static Color TeamColor( int teamIndex ) =>
		GameConfig.Palette[( ( teamIndex % GameConfig.Palette.Length ) + GameConfig.Palette.Length ) % GameConfig.Palette.Length];

	/// <summary> 权威端开局生效并广播（OnNetworkReady 调用；菜单选择原样落地）。
	/// 无会话（菜单背景演示赛）时跳过广播——Rpc.Broadcast 需要激活的会话。 </summary>
	public static void ActivateForMatch()
	{
		Apply( (byte)PendingMode, (int)PendingDuration, PendingPlayerTarget );
		if ( Networking.IsActive )
			NetworkManager.MatchSettings( (byte)PendingMode, (int)PendingDuration, PendingPlayerTarget );
	}

	/// <summary> 全端应用规则（host 本地直调 / 客户端经 RPC；重复应用幂等）。
	/// mode 字节经网络到达，钳到合法枚举域——TeamSize 由 CurrentMode 推导，坏值不能漏进来（v0.7.8.33） </summary>
	public static void Apply( byte mode, int durationSeconds, int playerTarget )
	{
		CurrentMode = (Mode)Math.Clamp( (int)mode, (int)Mode.Ffa, (int)Mode.Territory );
		Duration = MathF.Max( 10f, durationSeconds );
		PlayerTarget = Math.Clamp( playerTarget, 2, 64 );
		if ( CurrentMode == Mode.Territory )
			PlayerTarget = GameConfig.TerritoryGrid * TeamSize;   // 领土固定 4 队 × 4 人 = 16（人数菜单对本模式无效，v0.7.8.35——32 目标曾分出 8 队）
		TimeLeft = Duration;
		MatchOver = false;
		SinceLastTick = 0;   // 规则下发也算 host 活着（武装心跳，防开局瞬间误判）

		// 规则生效点 = 领土格复位点（全端一致：host 直调与客户端 RPC 都走这里；非领土模式置空态）
		TerritoryManager.Reset();
	}

	/// <summary> 1Hz 校时（客户端；本地每帧继续递减，误差远小于 1s 观感平滑） </summary>
	public static void ApplyTick( float timeLeft )
	{
		TimeLeft = MathF.Max( 0f, timeLeft );
		SinceLastTick = 0;   // host 心跳
	}

	/// <summary> 回主菜单：清比赛进行态，保留菜单里选的规则 </summary>
	public static void Reset()
	{
		MatchOver = false;
		TimeLeft = 0f;
		CurrentMode = PendingMode;
		Duration = PendingDuration;
		PlayerTarget = PendingPlayerTarget;
	}
}
