using System;
using System.Threading;
using System.Threading.Tasks;
using Sandbox.UI;

/// <summary>
/// 排行页（v0.7.8.19 用户需求）：主菜单 [5] RANKINGS——全球 XP 榜。
/// Leaderboards.GetFromStat("cr_xp") 默认 Sum 降序全时段，每次打开即刷新；
/// 底部自己的等级行（GameXp 本地缓存）。BACK / [4] 回主菜单（与 RoomBrowser 同键位）。
/// 纯 C# Panel + 同目录 RankingsPanel.cs.scss。
/// </summary>
public sealed class RankingsPanel : PanelComponent
{
	const int RowCount = 30;   // 榜容量：装进滚动容器，云端给多少显示多少，滚轮看完整榜

	// 排版预览（2026-09-09 用户调样式用）：不查云端，直接摆假榜行，看完改回 false
	// （static readonly 不用 const：const true 会把后面的云查询判成不可达代码，报 CS0162 警告）
	static readonly bool ShowSampleBoard = false;
	static readonly string[] SampleBoard =
	{
		"#1   FLYBIRD   —   125400 XP",
		"#2   SNACKLORD   —   118220 XP",
		"#3   大球王   —   98050 XP",
		"#4   KITTEN_92   —   87310 XP",
		"#5   ProEater   —   76004 XP",
		"#6   汤圆   —   64988 XP",
		"#7   Wobble   —   58310 XP",
		"#8   小小鸟   —   52777 XP",
		"#9   Mochi   —   46002 XP",
		"#10   pixel_cat   —   41388 XP",
		"#11   GULP   —   35240 XP",
		"#12   布丁   —   30117 XP",
		"#13   Doodle   —   26455 XP",
		"#14   泥鳅   —   21980 XP",
		"#15   Bubbles   —   18302 XP",
		"#16   阿翔   —   15044 XP",
	};

	Label _status;
	Label[] _rows;
	Panel _list;               // 排行榜滚动容器（原生网页式滚动）
	Label _self;
	bool _built;
	bool _shown;
	bool _retryOpen;
	int _refreshSeq;   // 每次查询自增：关页/重开后旧响应直接丢弃，防旧数据覆盖新状态
	readonly string[] _lastRows = new string[RowCount];   // 上次成功渲染的榜行快照：第二次打开秒显旧榜，后台刷新拿到新数据才整批替换
	bool _hasSnapshot;

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

		var title = new Label() { Classes = "rk-title" };
		title.Text = "RANKINGS";
		root.AddChild( title );

		var head = new Label() { Classes = "rk-head" };
		head.Text = "RANK                    PLAYER                          XP";
		root.AddChild( head );

		// 排行榜滚动列表（原生 overflow-y + 滚轮）：行牌流内单列，宽/起点/行距的用户调校值挪进 scss .rk-list
		_list = new Panel() { Classes = "rk-list" };
		root.AddChild( _list );

		_rows = new Label[RowCount];
		for ( int i = 0; i < _rows.Length; i++ )
		{
			var row = new Label() { Classes = "rk-row" };
			_list.AddChild( row );
			_rows[i] = row;
		}

		_self = new Label() { Classes = "rk-self" };
		root.AddChild( _self );

		// 状态条（读取中/无数据/失败）必须后于行牌创建——绘制顺序压在空牌上面，否则被 note 卡盖住
		_status = new Label() { Classes = "rk-status" };
		root.AddChild( _status );

		UiKit.PillButton( root, "BACK", "rk-back", () => OnBackClicked() );   // 捕获 this：热重载可重映射

		_built = true;
	}

	/// <summary> 主菜单 [5] 进入：开门即查榜 </summary>
	public void Show()
	{
		BuildUi();
		if ( !_built )
		{
			_retryOpen = true;
			return;
		}

		_retryOpen = false;
		_shown = true;
		Panel.Style.Display = DisplayMode.Flex;
		RefreshBoard();
	}

	public void Hide()
	{
		_shown = false;
		_retryOpen = false;
		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.None;
	}

	/// <summary> 页面是否开着（ReconcileUi 终态判定用） </summary>
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
		Mouse.Visibility = MouseVisibility.Visible;

		// 自己的等级行（云端未加载时 GameXp 内部 5s 节流重试）
		GameXp.EnsureLocalXp();
		if ( _self.IsValid() )
		{
			var t = GameXp.LevelText();
			_self.Text = t.Length > 0 ? t : "STATS LOADING ...";
		}

		if ( Input.Pressed( "Slot4" ) ) OnBackClicked();
	}

	static void SetText( Label l, string text )
	{
		if ( l is null || !l.IsValid() || l.Text == text ) return;
		l.Text = text;
	}

	void SetStatus( string text ) => SetText( _status, text );

	/// <summary>
	/// 查全球 XP 榜（默认 Sum 降序全时段）。
	/// 三个实测坑：①"Refresh 完成"和"数据真填进 Entries"不同时刻到，常先回空结果——0 条原地重试（共 3 次）；
	/// ②同一榜对象第二次 Refresh 可能返回空/失败——**所以绝不先清旧行**，有快照先摆快照秒显，新数据到了才替换；
	/// ③从没成功渲染过才允许显示 NO SCORES。
	/// </summary>
	async void RefreshBoard()
	{
		_refreshSeq++;
		int seq = _refreshSeq;

		// 排版预览：摆假榜行就走，不查云端（看完关掉 ShowSampleBoard）
		if ( ShowSampleBoard )
		{
			for ( int i = 0; i < _rows.Length; i++ )
			{
				var text = i < SampleBoard.Length ? SampleBoard[i] : "";
				SetText( _rows[i], text );
				_lastRows[i] = text;
			}
			_hasSnapshot = true;
			SetStatus( "" );
			return;
		}

		// 有旧榜先摆旧榜（第二次打开不再空白），LOADING 只是角标，新数据到了才整批替换
		if ( _hasSnapshot )
			for ( int i = 0; i < _rows.Length; i++ ) SetText( _rows[i], _lastRows[i] );

		SetStatus( "LOADING LEADERBOARD ..." );

		for ( int attempt = 0; attempt < 3; attempt++ )
		{
			try
			{
				var board = Sandbox.Services.Leaderboards.GetFromStat( GameXp.StatName );
				board.SetAggregationSum();
				board.FilterByNone();
				await board.Refresh( CancellationToken.None );

				if ( !_shown || seq != _refreshSeq ) return;   // 关页/重开：响应作废

				var entries = board.Entries;
				GameLog.Info( $"[rank] refresh attempt {attempt + 1}: {entries?.Length ?? 0} entries" );

				if ( entries is not null && entries.Length > 0 )
				{
					RenderRows( entries );
					SetStatus( "" );
					return;
				}

				// 空结果：等 2 秒再拉一次（数据经常晚于"完成"到达），旧榜/LOADING 保持
				if ( attempt < 2 ) await Task.Delay( 2000 );
			}
			catch ( Exception e )
			{
				Log.Warning( $"[rank] leaderboard refresh failed: {e.Message}" );
				break;   // 出错不再重试，落到兜底提示
			}
		}

		// 全部落空：成功渲染过 → 保留旧榜撤提示；从没成功过 → 无数据提示
		if ( _shown && seq == _refreshSeq )
			SetStatus( _hasSnapshot ? "" : "NO SCORES YET — COME BACK LATER" );
	}

	void RenderRows( Sandbox.Services.Leaderboards.Board2.Entry[] entries )
	{
		if ( entries is null ) entries = new Sandbox.Services.Leaderboards.Board2.Entry[0];

		for ( int i = 0; i < _rows.Length; i++ )
		{
			if ( i >= entries.Length )
			{
				SetText( _rows[i], "" );
				_lastRows[i] = "";
				continue;
			}

			var e = entries[i];
			var text = $"#{e.Rank}   {e.DisplayName}   —   {e.Value} XP";
			SetText( _rows[i], text );
			_lastRows[i] = text;
		}

		_hasSnapshot = true;
	}

	void OnBackClicked()
	{
		if ( !_shown ) return;
		CircleroyaleGame.Current?.CloseRankings();
	}
}