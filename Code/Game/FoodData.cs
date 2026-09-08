/// <summary>
/// 单个食物的同步单元（M2 起进 <c>[Sync(FromHost)] NetList&lt;FoodData&gt;</c>，host 写、客户端读）。
/// 只放需要同步的状态；重生计时等本地状态由 FoodManager 并行存放，不进网络。
/// </summary>
public struct FoodData
{
	/// <summary> 世界坐标（z 恒 0） </summary>
	public Vector2 Pos;

	/// <summary> 霓虹配色索引（GameConfig.Palette） </summary>
	public byte ColorIndex;

	/// <summary> 是否存活（false = 等待重生，不渲染不可吃） </summary>
	public bool Alive;

	/// <summary> 类型：0=普通食物，1..n=道具（M4+ 预留，按 Type 画不同形状） </summary>
	public byte Type;
}