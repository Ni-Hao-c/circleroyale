using Sandbox.UI;

/// <summary>
/// 主菜单（M4；2026-09-09 参考手游布局重排，用户定稿）：中央大 logo + 三按钮横排
/// （CREATE GAME / JOIN GAME / VS BOTS），左上玩家资料卡（头像/名字/等级），
/// 右上设置/排行榜小按钮；QUIT 按需求移除（编辑器停 Play 即退出，standalone 再加回）。
/// 点击 + 键盘快捷键双通道（编辑器内嵌视口可能收不到鼠标点击，键盘保底）：
/// 1=建服 2=加入 3=人机 4=设置页 5(数字键)=排行榜。
/// 样式在同目录 MainMenu.cs.scss（按"类名.cs.scss"自动加载）。
/// </summary>
public sealed class MainMenu : PanelComponent
{
	Label _status;
	Label _level;
	Image _avatar;
	TimeSince _sinceAvatarTry = 1f;
	bool _built;

	protected override void OnStart()
	{
		base.OnStart();
		BuildUi();
	}

	void BuildUi()
	{
		var root = Panel;
		if ( root is null ) return;

		// 已建好且引用有效 → 不重复建（热重载半残时整体清空重建）
		if ( _built && _status.IsValid() && root.ChildrenCount > 0 ) return;

		root.DeleteChildren( true );

		// 左上：玩家资料卡（头像 / 名字 / 等级）
		var profile = new Panel() { Classes = "profile" };
		root.AddChild( profile );

		_avatar = new Image() { Classes = "profile-avatar" };
		profile.AddChild( _avatar );

		var info = new Panel() { Classes = "profile-info" };
		profile.AddChild( info );

		var name = new Label() { Classes = "profile-name" };
		name.Text = Sandbox.Utility.Steam.PersonaName;
		info.AddChild( name );

		_level = new Label() { Classes = "profile-level" };
		info.AddChild( _level );

		// 右上：设置 / 排行榜（图标小按钮：Gear / Trophy）
		var actions = new Panel() { Classes = "top-actions" };
		root.AddChild( actions );
		AddIconButton( actions, "ui/skin/ic_gear.png", "settings" );
		AddIconButton( actions, "ui/skin/ic_trophy.png", "rankings" );

		// 中央大 logo（副标题已按需求移除）
		var title = new Label() { Classes = "title" };
		title.Text = "CIRCLEROYALE";
		root.AddChild( title );

		// 三个主按钮横排（v0.7.8.43 参考手游布局）
		var row = new Panel() { Classes = "menu-row" };
		root.AddChild( row );
		AddButton( row, "CREATE GAME", "host" );
		AddButton( row, "JOIN GAME", "join" );
		AddButton( row, "VS BOTS", "bots" );

		_status = new Label() { Classes = "status" };
		root.AddChild( _status );

		var ver = new Label() { Classes = "version" };
		ver.Text = GameConfig.Version;
		root.AddChild( ver );

		var hint = new Label() { Classes = "hint" };
		hint.Text = "WASD MOVE — SPACE SPLIT — R FEED";
		root.AddChild( hint );

		TryLoadAvatar();
		_built = true;
	}

	void AddButton( Panel parent, string text, string id )
	{
		var b = new Label() { Classes = $"btn {id}" };
		b.Text = text;
		// lambda 必须捕获 this（调实例方法）——无捕获的静态 lambda 在热重载后无法重映射，
		// 点击会抛 NoMatchLambda 异常（实测：按钮全变"点了没反应"）
		b.AddEventListener( "onclick", () => OnMenuButton( id ) );
		parent.AddChild( b );
	}

	/// <summary> 图标小按钮（右上角设置/排行榜）：dobou 槽底图 + 像素图标。
	/// 图标不定位不设尺寸，全交给 scss（.icon-btn 的 flex 居中 + .icon-btn-img 的宽高）——
	/// 之前图标绝对定位，参照物是整个右上角容器而不是按钮，两个图标叠在一起（实测）；
	/// C# 内联样式会压过样式表，这里设了尺寸用户在 scss 里就改不动了 </summary>
	void AddIconButton( Panel parent, string iconPath, string id )
	{
		var b = new Panel() { Classes = "icon-btn" };
		var icon = new Image() { Classes = "icon-btn-img" };
		icon.Texture = Texture.Load( iconPath );
		icon.Style.PointerEvents = PointerEvents.None;   // 点击穿透到按钮
		b.AddChild( icon );
		b.AddEventListener( "onclick", () => OnMenuButton( id ) );   // 捕获 this：热重载可重映射
		parent.AddChild( b );
	}

	/// <summary> 本机玩家头像：Steam 拉取（下载异步，OnUpdate 1s 节流重试），失败落占位图 </summary>
	void TryLoadAvatar()
	{
		if ( _avatar is null || !IsValid ) return;

		var sid = (long)(ulong)Sandbox.Utility.Steam.SteamId;
		var tex = sid != 0 ? Texture.LoadAvatar( sid, 128 ) : null;
		_avatar.Texture = tex ?? Texture.Load( GameConfig.AvatarPlaceholderPath, false );
	}

	/// <summary> 菜单按钮点击（实例方法：热重载可重映射） </summary>
	void OnMenuButton( string id )
	{
		GameSfx.UiClick();
		CircleroyaleGame.Current?.MenuAction( id switch { "host" => 0, "join" => 1, "bots" => 2, "settings" => 4, "rankings" => 5, _ => 3 } );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		// 强制光标可见可交互（面板默认 PointerEvents=None，Auto 模式下没人"想要鼠标"光标就没了）；
		// Hide() 时恢复 Auto。放在 OnUpdate 每帧断言，任何样式级联意外都兜得住
		Mouse.Visibility = MouseVisibility.Visible;

		BuildUi();

		// 键盘保底（编辑器内嵌视口鼠标事件不可靠，键盘一定能进来）：
		// [4] 设置页；数字 5 直读（Slot5 未在 Input 配置里）= 排行榜；QUIT 已移除
		if ( Input.Pressed( "Slot1" ) ) CircleroyaleGame.Current?.MenuAction( 0 );
		if ( Input.Pressed( "Slot2" ) ) CircleroyaleGame.Current?.MenuAction( 1 );
		if ( Input.Pressed( "Slot3" ) ) CircleroyaleGame.Current?.MenuAction( 2 );
		if ( Input.Pressed( "Slot4" ) ) CircleroyaleGame.Current?.MenuAction( 4 );
		if ( Input.Keyboard.Pressed( "5" ) ) CircleroyaleGame.Current?.MenuAction( 5 );

		// 等级角标（云端 XP 异步加载，GameXp 内部节流自愈重试）
		GameXp.EnsureLocalXp();
		SetText( _level, GameXp.LevelText() );

		// 头像没拉到（Steam 下载异步）每秒重试一次，拉到即停
		if ( _avatar.IsValid() && _avatar.Texture is null && _sinceAvatarTry > 1f )
		{
			_sinceAvatarTry = 0;
			TryLoadAvatar();
		}
	}

	static void SetText( Label l, string text )
	{
		if ( l is null || !l.IsValid() || l.Text == text ) return;
		l.Text = text;
	}

	/// <summary> 状态行（连接中 / 失败重试提示） </summary>
	public void SetStatus( string text )
	{
		if ( _status.IsValid() ) _status.Text = text;
	}

	/// <summary> 开局后隐藏菜单（Panel 无 Enabled，直接禁掉整个 GameObject），并把光标还给游戏 </summary>
	public void Hide()
	{
		if ( !GameObject.Enabled ) return;   // 已隐藏：别动光标（ReconcileUi 每帧调，房间页要光标可点）
		Mouse.Visibility = MouseVisibility.Auto;
		GameObject.Enabled = false;
	}

	/// <summary> 从房间/结算回菜单（M5）：重新显示（OnUpdate 恢复光标断言） </summary>
	public void Show()
	{
		GameObject.Enabled = true;
		BuildUi();
		if ( _status.IsValid() ) _status.Text = "";
	}
}