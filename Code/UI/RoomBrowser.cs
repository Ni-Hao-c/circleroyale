using System;
using System.Collections.Generic;
using Sandbox.Network;
using Sandbox.UI;

/// <summary>
/// 房间列表（v0.6.7.0 用户需求）：主菜单 [2] JOIN GAME 打开——列出 Steam 上本游戏的公开大厅，
/// 点击行加入；REFRESH 手动刷新（显示中每 10s 自动刷一次）；JOIN LOCAL 同机调试直连兜底
/// （双开时大厅查询搜不到同账号房间，靠这条进）；BACK 回主菜单。
/// 纯 C# Panel + 同目录 RoomBrowser.cs.scss（显隐走 Style.Display，与 LobbyPanel 同款）。
/// </summary>
public sealed class RoomBrowser : PanelComponent
{
	Label _status;
	Label[] _rows;
	Label _refresh;
	Label _local;
	Label _back;

	readonly List<LobbyInformation> _lobbies = new();
	bool _built;
	bool _shown;
	bool _joining;          // 已点了一个房间正在连（防重复点击；成功由 Tick 加入流收尾）
	bool _retryOpen;        // EnsureWorld 先于 OnStart 调 Show 时重试
	bool _querying;
	TimeSince _sinceQuery = 999f;

	const int RowCount = 10;

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
		if ( _built && _status.IsValid() && root.ChildrenCount > 0 ) return;

		root.DeleteChildren( true );

		root.Style.Position = PositionMode.Absolute;
		root.Style.Left = 0f;
		root.Style.Top = 0f;
		root.Style.Right = 0f;
		root.Style.Bottom = 0f;
		root.Style.BackgroundColor = new Color( 0.008f, 0.012f, 0.05f, 0.55f );
		root.Style.PointerEvents = PointerEvents.All;

		var title = new Label() { Classes = "rb-title" };
		title.Text = "ROOM BROWSER";
		root.AddChild( title );

		_status = new Label() { Classes = "rb-status" };
		root.AddChild( _status );

		var head = new Label() { Classes = "rb-head" };
		head.Text = "ROOM                          PLAYERS";
		root.AddChild( head );

		_rows = new Label[RowCount];
		for ( int i = 0; i < _rows.Length; i++ )
		{
			var row = new Label() { Classes = "rb-row" };
			row.Style.Position = PositionMode.Absolute;
			row.Style.Left = 460f;
			row.Style.Width = 1000f;
			row.Style.Top = 300f + i * 36f;
			int index = i;
			// lambda 捕获 this（实例方法）——无捕获静态 lambda 热重载后无法重映射（实测点击死锁）
			row.AddEventListener( "onclick", () => OnRowClicked( index ) );
			root.AddChild( row );
			_rows[i] = row;
		}

		_refresh = new Label() { Classes = "rb-refresh" };
		_refresh.Text = "REFRESH";
		_refresh.AddEventListener( "onclick", () => OnRefreshClicked() );
		root.AddChild( _refresh );

		_local = new Label() { Classes = "rb-local" };
		_local.Text = "JOIN LOCAL — SAME MACHINE";
		_local.AddEventListener( "onclick", () => OnLocalClicked() );
		root.AddChild( _local );

		_back = new Label() { Classes = "rb-back" };
		_back.Text = "BACK — [4]";
		_back.AddEventListener( "onclick", () => OnBackClicked() );
		root.AddChild( _back );

		_built = true;
	}

	/// <summary> 主菜单 [2] 进入（CircleroyaleGame.MenuAction 调用）：开门即查询 </summary>
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
		_joining = false;
		Panel.Style.Display = DisplayMode.Flex;
		_sinceQuery = 999f;   // 首帧立即查询
		SetStatus( "" );
		RenderRows();
	}

	/// <summary> 收起（加入成功 / BACK / 回菜单） </summary>
	public void Hide()
	{
		_shown = false;
		_retryOpen = false;
		_joining = false;
		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.None;
	}

	/// <summary> 状态行（加入失败/超时提示也走这里） </summary>
	public void SetStatus( string text )
	{
		if ( _status.IsValid() ) _status.Text = text;
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

		// 显示中每 10s 自动刷新一次房间列表（手动 REFRESH 也可）
		if ( !_querying && _sinceQuery > 10f ) Refresh();

		if ( Input.Pressed( "Slot4" ) ) OnBackClicked();
	}

	/// <summary> 查询 Steam 大厅（异步；回来时若已关闭则丢弃） </summary>
	async void Refresh()
	{
		if ( _querying ) return;

		_querying = true;
		_sinceQuery = 0;
		SetStatus( "FINDING ROOMS ..." );

		try
		{
			var lobbies = await Networking.QueryLobbies();

			if ( !_shown ) return;   // 查询回来时页面已经关了

			// 人数多的排前面（满员也列出但置灰不可点）
			_lobbies.Clear();
			_lobbies.AddRange( lobbies );
			_lobbies.Sort( ( a, b ) => b.Members.CompareTo( a.Members ) );

			if ( _lobbies.Count == 0 )
				SetStatus( "NO OPEN ROOMS — HOST ONE FROM ANOTHER MACHINE, OR JOIN LOCAL BELOW" );
			else
				SetStatus( $"{_lobbies.Count} ROOM{(_lobbies.Count == 1 ? "" : "S")} FOUND — CLICK TO JOIN" );

			RenderRows();
		}
		catch ( Exception e )
		{
			Log.Warning( $"[net] room list query failed: {e.Message}" );
			if ( _shown ) SetStatus( "QUERY FAILED — TRY REFRESH" );
		}
		finally
		{
			_querying = false;
		}
	}

	void RenderRows()
	{
		if ( _rows is null || _rows[0] is null || !_rows[0].IsValid() ) return;

		for ( int i = 0; i < _rows.Length; i++ )
		{
			var row = _rows[i];
			if ( !row.IsValid() ) continue;

			if ( i >= _lobbies.Count )
			{
				row.Text = "";
				continue;
			}

			var l = _lobbies[i];
			var name = string.IsNullOrWhiteSpace( l.Name ) ? "CIRCLEROYALE ROOM" : l.Name;
			row.Text = $"{name}   —   {l.Members}/{l.MaxMembers}{( l.IsFull ? "  (FULL)" : "" )}";
			row.Style.FontColor = l.IsFull
				? new Color( 1f, 1f, 1f, 0.25f )
				: new Color( 1f, 1f, 1f, 0.8f );
		}
	}

	void OnRowClicked( int index )
	{
		if ( !_shown || _joining ) return;
		if ( index >= _lobbies.Count ) return;

		var lobby = _lobbies[index];
		if ( lobby.IsFull )
		{
			SetStatus( "THAT ROOM IS FULL — PICK ANOTHER" );
			return;
		}

		_joining = true;
		SetStatus( $"JOINING '{( string.IsNullOrWhiteSpace( lobby.Name ) ? "ROOM" : lobby.Name )}' ..." );
		NetworkManager.Instance?.JoinLobby( lobby );
	}

	/// <summary> 手动刷新 </summary>
	void OnRefreshClicked()
	{
		if ( !_shown || _joining ) return;
		Refresh();
	}

	/// <summary> 同机调试直连（双开搜不到同账号大厅，走这条） </summary>
	void OnLocalClicked()
	{
		if ( !_shown || _joining ) return;

		_joining = true;
		SetStatus( "CONNECTING TO LOCAL HOST ..." );
		CircleroyaleGame.Current?.JoinLocalRoom();
	}

	/// <summary> 回主菜单 </summary>
	void OnBackClicked()
	{
		if ( !_shown ) return;
		CircleroyaleGame.Current?.CloseBrowser();
	}
}