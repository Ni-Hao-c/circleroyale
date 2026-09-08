/// <summary>
/// 道具全量同步单元（v0.7.3.0）：槽位序号 = 数组下标，Kind=None(255) 表示空槽。
/// 顶层 struct——RPC 参数序列化对顶层类型最稳（FoodData 同款先例）。
/// </summary>
public struct PowerUpWire
{
	public byte Kind;
	public float X;
	public float Y;
}