using System;
using Sandbox.UI;

/// <summary>
/// 设置页（v0.7.8.16 用户需求）：主菜单 [4] SETTINGS 进入的二级界面。
/// 点击行循环取值（同 LobbyPanel 设置区交互）：
/// - BLOOM 泛光开关（cr_bloom，NeonCamera.OnUpdate 每帧幂等应用，即时生效）
/// - MUSIC VOLUME 音乐音量五档循环（写 cr_music_volume，GameMusic.Tick 每帧应用到播放句柄）
/// BACK / [4] 回主菜单（与 RoomBrowser 同款显隐与键位）。
/// 纯 C# Panel + 同目录 SettingsPanel.cs.scss。后续新设置往 BuildUi 加行即可。
/// </summary>
public sealed class SettingsPanel : PanelComponent
{
	Label _bloom;
	Label _music;
	bool _built;
	bool _shown;
	bool _retryOpen;   // EnsureWorld 先于 OnStart 调 Show 时重试

	protected override void OnStart()
	{
		base.OnStart();
		BuildUi();
		Hide();
	}

	void BuildUi()
	{
		var root = Panel;
		if ( root is null ) return;
		if ( _built && _bloom.IsValid() && root.ChildrenCount > 0 ) return;

		root.DeleteChildren( true );

		root.Style.Position = PositionMode.Absolute;
		root.Style.Left = 0f;
		root.Style.Top = 0f;
		root.Style.Right = 0f;
		root.Style.Bottom = 0f;
		root.Style.BackgroundColor = new Color( 0.008f, 0.012f, 0.05f, 0.55f );
		root.Style.PointerEvents = PointerEvents.All;

		UiKit.AttachStyles( root );   // 共享部件样式（.cr-pill 药丸按钮）

		var title = new Label() { Classes = "sp-title" };
		title.Text = "SETTINGS";
		root.AddChild( title );

		var hint = new Label() { Classes = "sp-hint" };
		hint.Text = "CLICK A SETTING TO CHANGE IT";
		root.AddChild( hint );

		_bloom = new Label() { Classes = "sp-row" };
		_bloom.Style.Position = PositionMode.Absolute;
		_bloom.Style.Left = Length.Percent( 50f );   // 600 宽行居中（宽在 scss），任意分辨率自适应
		_bloom.Style.MarginLeft = -300f;
		_bloom.Style.Width = 600f;
		_bloom.Style.Top = 360f;
		_bloom.AddEventListener( "onclick", () => OnBloomClicked() );   // 捕获 this：热重载可重映射
		root.AddChild( _bloom );

		_music = new Label() { Classes = "sp-row" };
		_music.Style.Position = PositionMode.Absolute;
		_music.Style.Left = Length.Percent( 50f );
		_music.Style.MarginLeft = -300f;
		_music.Style.Width = 600f;
		_music.Style.Top = 430f;
		_music.AddEventListener( "onclick", () => OnMusicClicked() );
		root.AddChild( _music );

		UiKit.PillButton( root, "BACK", "sp-back", () => OnBackClicked() );   // 捕获 this：热重载可重映射

		_built = true;
	}

	/// <summary> 主菜单 [4] 进入 </summary>
	public void Show()
	{
		BuildUi();
		if ( !_built )
		{
			_retryOpen = true;   // Panel 未就绪（EnsureWorld 同帧调用），OnUpdate 补开
			return;
		}

		_retryOpen = false;
		_shown = true;
		Panel.Style.Display = DisplayMode.Flex;
		RefreshValues();
	}

	/// <summary> 收起（BACK / 回菜单） </summary>
	public void Hide()
	{
		_shown = false;
		_retryOpen = false;
		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.None;
	}

	/// <summary> 页面是否开着（ReconcileUi 终态判定用：开着时主菜单让位） </summary>
	public bool IsOpen => _shown;

	protected override void OnUpdate()
	{
		base.OnUpdate();

		BuildUi();

		if ( _retryOpen && !_shown )
		{
			Show();
			return;
		}

		if ( !_shown ) return;

		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.Flex;
		Mouse.Visibility = MouseVisibility.Visible;   // 行/按钮要能点

		RefreshValues();

		if ( Input.Pressed( "Slot4" ) ) OnBackClicked();   // 与 RoomBrowser 同键位
	}

	/// <summary> 行文本取当前值（SetText 防抖） </summary>
	void RefreshValues()
	{
		if ( _bloom.IsValid() )
			SetText( _bloom, $"BLOOM / GLOW  —  {( GameConfig.ConvarBloom ? "ON" : "OFF" )}" );
		if ( _music.IsValid() )
			SetText( _music, $"MUSIC VOLUME  —  {(int)MathF.Round( GameMusic.Volume * 100f )}%" );
	}

	static void SetText( Label l, string text )
	{
		if ( l is null || !l.IsValid() || l.Text == text ) return;
		l.Text = text;
	}

	void OnBloomClicked()
	{
		GameConfig.ConvarBloom = !GameConfig.ConvarBloom;
		RefreshValues();
	}

	/// <summary> 音乐音量五档循环：0 → 25% → 50% → 75% → 100% → 0 </summary>
	void OnMusicClicked()
	{
		GameMusic.Volume = ( MathF.Round( GameMusic.Volume * 4f ) + 1f ) % 5f / 4f;
		RefreshValues();
	}

	void OnBackClicked()
	{
		if ( !_shown ) return;
		CircleroyaleGame.Current?.CloseSettings();
	}
}