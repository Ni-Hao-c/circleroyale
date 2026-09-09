using Sandbox.UI;

/// <summary>
/// 滚动时播翻页音的滚动容器（v0.7.8.66）：玩家列表用。
/// OnMouseWheel 只在滚轮真滚到面板上时触发（惯性阶段不触发），0.1s 节流防快速连滚机枪化。
/// </summary>
public sealed class ScrollSoundPanel : Panel
{
	TimeSince _sinceSwipe = 999f;

	public override void OnMouseWheel( Vector2 value )
	{
		base.OnMouseWheel( value );

		if ( _sinceSwipe > 0.1f )
		{
			GameSfx.UiSwipe();
			_sinceSwipe = 0f;
		}
	}
}
