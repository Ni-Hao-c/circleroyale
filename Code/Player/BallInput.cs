using System;

/// <summary>
/// 输入辅助：**仅键盘 + 手柄**（2026-09-05 用户决策，鼠标操控已移除）。
/// 数据源 Input.AnalogMove——引擎已把 WASD（模板 Input.config 预定义的
/// Forward/Backward/Left/Right）与手柄左摇杆叠加为 x=前(+)/后(-)、y=左(+)/右(-)。
/// 顶视相机下：前 → 相机 Up（屏幕上），左 → -相机 Right（屏幕左）。
/// M2 起真人客户端本地计算（owner 模拟），bot 由 BotBrain 直接给方向，不走这里。
/// </summary>
public static class BallInput
{
	/// <summary> 摇杆死区（防模拟量中心漂移） </summary>
	const float DeadZone = 0.2f;

	public static Vector2 GetMoveDir( CameraComponent camera )
	{
		if ( !camera.IsValid() ) return Vector2.Zero;

		var analog = Input.AnalogMove;
		if ( analog.Length < DeadZone ) return Vector2.Zero;

		var rot = camera.GameObject.WorldRotation;
		var world = rot * ( Vector3.Right * ( -analog.y ) + Vector3.Up * analog.x );

		var v = new Vector2( world.x, world.y );
		var len = v.Length;
		return len > 0.001f ? new Vector2( v.x / len, v.y / len ) : Vector2.Zero;
	}

	/// <summary>
	/// 瞄准方向（v0.7.0.0 用户需求）：光标世界位置相对球的单位方向——分裂/吐孢子指向这里。
	/// 正交俯视相机下 ScreenPixelToRay 的射线原点 XY 就是光标的世界坐标。
	/// 光标正贴球心时返回 Zero（调用方回退移动方向）。
	/// </summary>
	public static Vector2 GetAimDir( CameraComponent camera, Vector3 ballPos )
	{
		if ( !camera.IsValid() ) return Vector2.Zero;

		var ray = camera.ScreenPixelToRay( Mouse.Position );
		var dx = ray.Position.x - ballPos.x;
		var dy = ray.Position.y - ballPos.y;
		var len = MathF.Sqrt( dx * dx + dy * dy );
		if ( len < 0.001f ) return Vector2.Zero;

		return new Vector2( dx / len, dy / len );
	}
}