using System;

/// <summary>
/// 分裂细胞 / 吐出的孢子（**纯数据实体，不是 GameObject**）。
/// 为什么不走网络对象：运行时 live create 广播到已连接客户端的可靠性未验证（M2 遗留），
/// 而食物同款"静态 RPC 快照"是本项目实测 100% 可靠的唯一同步通道。
/// host 权威模拟（冲量衰减 + 跟随主人转向），状态经 CellsState 静态 RPC 按 CellSyncInterval
/// 广播；各端本地镜像列表只负责渲染与平滑（DrawPos 追 Pos，客户端把 15Hz 快照插顺）。
/// </summary>
public sealed class CellPiece
{
	public enum Kind : byte
	{
		SplitPiece = 0,   // 分裂出的细胞（跟随主人方向移动，冷却后并回）
		EjectedMass = 1,  // 吐出的孢子（中性食物团，谁都能吃）
		SpikeMinion = 2,  // 尖刺分身（v0.7.4.0 道具生成）：跟随主人 20s，到期变回普通分身；敌方按尖刺规则判定
	}

	public int Id;

	public Kind PieceKind;

	/// <summary> 主人 SteamId（bot 为 0；bot 不分裂，此字段仅真人有值） </summary>
	public long OwnerSteamId;

	/// <summary> 主人队伍号（-1 无队；host 出生时记一次，领土喂旗计分用，免逐帧查表） </summary>
	public int TeamIndex = -1;

	/// <summary> 锚定不动（M7.4 护卫固守尖刺）：不跟随主人、无转向 </summary>
	public bool Anchored;

	/// <summary> 存在时长覆盖（秒；小于 0 = 用 Kind 默认寿命）——护卫尖刺 8s / 道具尖刺 20s（M7.4） </summary>
	public float MaxLife = -1f;

	public int ColorIndex;

	public float Mass;

	/// <summary> 权威位置（host 每帧推进；客户端 = 最近一次快照值，15Hz 跳变） </summary>
	public Vector3 Pos;

	/// <summary> 渲染位置：向 Pos 指数平滑（host 逐帧权威直接贴齐；客户端插值） </summary>
	public Vector3 DrawPos;

	/// <summary> 弹射冲量（分裂/吐出瞬间），随时间指数衰减 </summary>
	public Vector3 Impulse;

	/// <summary> 跟随主人移动方向的速度分量（仅 host 模拟） </summary>
	public Vector3 SteerVel;

	/// <summary> 出生时刻：合并冷却（分身）与可食延迟（孢子）都看它 </summary>
	public TimeSince SinceSpawn;

	/// <summary> 仅 host：主人球引用（分身清理、转向方向来源）；孢子恒为 null（中性存活） </summary>
	public Ball OwnerBall;

	/// <summary> 半径沿用球的质量定律：r = StartRadius × √(mass/StartMass) </summary>
	public float Radius => GameConfig.StartRadius * MathF.Sqrt( MathF.Max( 1f, Mass ) / GameConfig.StartMass );

	/// <summary> 移动速度沿用球的速度曲线：越大越慢 </summary>
	public float Speed => MathF.Max( GameConfig.MinSpeed,
		GameConfig.BaseSpeed * MathF.Pow( GameConfig.StartMass / MathF.Max( 1f, Mass ), GameConfig.SpeedCurve ) );

	/// <summary> 合并冷却时长（秒）：主动分裂按质量曲线（MergeCooldownFor）；撞刺碎片短冷却（SpikePieceMergeCooldown） </summary>
	public float MergeCooldown = GameConfig.MergeCooldownBase;

	/// <summary> 合并冷却是否已过（仅分身有意义） </summary>
	public bool IsMergeReady => SinceSpawn > MergeCooldown;
}

/// <summary>
/// 刺爆弹幕（v0.7.8.24）：喂刺喂满后从刺射出的尖刺弹。host 权威模拟（含命中判定），
/// 客户端为纯视觉弹道镜像（SpineDir 公式双端一致，视觉弹道=权威弹道）。
/// </summary>
public sealed class SpikeSpine
{
	public Vector2 Pos;

	public Vector2 Vel;

	/// <summary> 出生时刻（SpineLife 到期消散） </summary>
	public TimeSince SinceSpawn;
}

/// <summary> CellsState RPC 的线格式（全 blittable 字段，同 FoodData 模式） </summary>
public struct CellWire
{
	public int Id;
	public byte Kind;        // CellPiece.Kind
	public byte ColorIndex;
	public long Owner;       // OwnerSteamId
	public float X;
	public float Y;
	public float Mass;
}
