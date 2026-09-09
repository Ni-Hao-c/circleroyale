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
	Panel _list;            // 房间列表滚动容器（原生网页式滚动）
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

	const int MaxRooms = 200;   // 列表容量：行壳一次建好（Display.None 零绘制），文本流式绑定
	const int BindChunk = 20;   // 每批绑定的行数（流式加载，滚近底部再绑下一批）
	int _boundCount;            // 当前已绑定显示的行数

		// 样式预览（2026-09-09 用户要求）：查不到真实房间时显示示例房看排版（含滚动条效果）。
		// 已调完关闭；下次预览再改 true——示例行点击无反应（OnRowClicked 对超出真实数量的索引直接忽略）
		const bool ShowSampleRows = false;

		static readonly ( string Name, int Members, int Max, bool Full )[] SampleRows =
		{
			( "FLYBIRD'S ROOM", 3, 16, false ),
			( "KITTEN CAFE", 16, 16, true ),
			( "PRO LOBBY", 7, 16, false ),
			( "中文房间测试", 5, 16, false ),
			( "SNACK TIME", 12, 16, false ),
			( "大球吃小球", 9, 16, false ),
			( "NOOB ZONE", 2, 16, false ),
			( "ROLLING THUNDER", 16, 16, true ),
			( "AFTERNOON CHILL", 6, 16, false ),
			( "EU DUEL CLUB", 11, 16, false ),
		};

		static readonly Color RowDim = new( 0.29f, 0.23f, 0.39f, 0.3f );
		static readonly Color RowNormal = new( 0.29f, 0.23f, 0.39f, 0.9f );

		/// <summary> 流式绑定要显示的总行数（样例模式开且无真实房间时 = 4 行示例） </summary>
		int VisibleTotal => ( _lobbies.Count == 0 && ShowSampleRows ) ? SampleRows.Length : _lobbies.Count;

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

		UiKit.AttachStyles( root );   // 共享部件样式（.cr-pill 药丸按钮）

		var title = new Label() { Classes = "rb-title" };
		title.Text = "ROOM BROWSER";
		root.AddChild( title );

		var head = new Label() { Classes = "rb-head" };
		head.Text = "ROOM                          PLAYERS";
		root.AddChild( head );

		// 房间列表：单列流内排列装进原生滚动容器（overflow-y + 滚轮）。
		// 行壳一次建好但全部 Display.None（零绘制成本），文本由 BindMoreRows 滚近底部时分批绑定
		_list = new Panel() { Classes = "rb-list" };
		root.AddChild( _list );

		_rows = new Label[MaxRooms];
		for ( int i = 0; i < _rows.Length; i++ )
		{
			var row = new Label() { Classes = "rb-row" };
			int index = i;
			// lambda 捕获 this（实例方法）——无捕获静态 lambda 热重载后无法重映射（实测点击死锁）
			row.AddEventListener( "onclick", () => OnRowClicked( index ) );
			row.Style.Display = DisplayMode.None;
			_list.AddChild( row );
			_rows[i] = row;
		}

		// 状态条后于行牌创建——绘制顺序压在空牌上面（读取中/无房间/失败提示都走这里）
		_status = new Label() { Classes = "rb-status" };
		root.AddChild( _status );

		_refresh = UiKit.PillButton( root, "REFRESH", "rb-refresh", () => OnRefreshClicked() );
		_local = UiKit.PillButton( root, "JOIN LOCAL", "rb-local", () => OnLocalClicked() );
		_back = UiKit.PillButton( root, "BACK", "rb-back", () => OnBackClicked() );

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

		// 流式加载：滚到距底 <400px 时绑下一批行牌（ScrollSize 是内容总高，随绑定增长）
		if ( _list.IsValid() && _boundCount < VisibleTotal && _list.ScrollOffset.y > _list.ScrollSize.y - 400f )
			BindMoreRows();

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

			// 刷新重绑：全部行壳先藏掉（空壳零绘制），再流式绑第一批
			_boundCount = 0;
			foreach ( var r in _rows )
				if ( r.IsValid() ) r.Style.Display = DisplayMode.None;

			BindMoreRows();

			if ( _list.IsValid() ) _list.ScrollOffset = Vector2.Zero;   // 回到顶部
		}

		/// <summary> 流式加载：把下一批行牌填文本显示出来（滚近底部时 OnUpdate 再调） </summary>
		void BindMoreRows()
		{
			if ( _rows is null || _rows[0] is null || !_rows[0].IsValid() ) return;

			bool samples = _lobbies.Count == 0 && ShowSampleRows;
			int end = Math.Min( VisibleTotal, _boundCount + BindChunk );

			for ( int i = _boundCount; i < end; i++ )
			{
				var row = _rows[i];
				if ( !row.IsValid() ) continue;

				string text;
				bool full;

				if ( samples )
				{
					var s = SampleRows[i];
					text = $"{s.Name}   —   {s.Members}/{s.Max}{( s.Full ? "  (FULL)" : "" )}";
					full = s.Full;
				}
				else
				{
					var l = _lobbies[i];
					var name = string.IsNullOrWhiteSpace( l.Name ) ? "CIRCLEROYALE ROOM" : l.Name;
					text = $"{name}   —   {l.Members}/{l.MaxMembers}{( l.IsFull ? "  (FULL)" : "" )}";
					full = l.IsFull;
				}

				row.Text = text;
				row.Style.FontColor = full ? RowDim : RowNormal;
				row.Style.Display = DisplayMode.Flex;
			}

			_boundCount = end;
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