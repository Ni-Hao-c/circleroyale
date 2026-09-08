using System;

/// <summary>
/// 游戏日志（v0.7.8.18）：**只在编辑器模式下打印**（Game.IsEditor）——
/// 发布版/专用服务器不再刷生命周期日志（玩家反馈日志噪音）。
/// Warning/Error 不受限（真实问题，玩家报障要靠日志文件）。
/// 全项目 Log.Info( 已统一替换为 GameLog.Info(，新代码也请走 GameLog。
/// </summary>
public static class GameLog
{
	/// <summary> 编辑器（开发/联调）= 打印；发布版/专用服务器 = 静默 </summary>
	public static bool Verbose => Game.IsEditor;

	public static void Info( string message )
	{
		if ( Verbose ) Log.Info( message );
	}
}