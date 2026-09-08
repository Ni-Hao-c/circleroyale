using System;
using System.Collections.Generic;
using Sandbox.UI;

/// <summary>
/// 结算面板（M5）：比赛结束全屏结算——本局排行（逐行滑入动画）+ 全服排行
/// （云端优先 ScoreUploader，空了降级 LocalBoard 本地榜）+ 回菜单按钮。
/// 纯 C# Panel + 同目录 EndBoard.cs.scss；显示期间全场模拟冻结（MatchState.MatchOver）。
/// </summary>
public sealed class EndBoard : PanelComponent
{
	Label _title;
	Label _winner;
	Label _matchHead;
	Label[] _matchRows;
	Label _selfRow;
	Label _globalHead;
	Label[] _globalRows;
	Label _globalNote;
	Label _back;

	bool _built;
	bool _shown;
	TimeSince _sinceShow;

	ScoreWire[] _standings;
	long _mySteamId;

	// 逐行 reveal 动画：行索引 → 出场时刻（秒），透明度/位移在 0.25s 内渐入
	const float MatchRowDelay = 0.45f;
	const float MatchRowStagger = 0.22f;
	const float GlobalDelay = 3.4f;
	const float GlobalStagger = 0.16f;

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
		if ( _built && _title.IsValid() && root.ChildrenCount > 0 ) return;

		root.DeleteChildren( true );

		root.Style.Position = PositionMode.Absolute;
		root.Style.Left = 0f;
		root.Style.Top = 0f;
		root.Style.Right = 0f;
		root.Style.Bottom = 0f;
		root.Style.BackgroundColor = new Color( 0.008f, 0.012f, 0.05f, 0.88f );
		root.Style.PointerEvents = PointerEvents.All;

		// 标题/冠军/回菜单居中；本局排行（左列）与全服排行（右列）并排（v0.6.3.0 用户定稿）
		_title = NewRow( root, 88f, 360f, 1200f, "eb-title" );
		_title.Text = "MATCH OVER";

		_winner = NewRow( root, 166f, 360f, 1200f, "eb-winner" );

		_matchHead = NewRow( root, 244f, 260f, 580f, "eb-head" );
		_matchHead.Text = "MATCH RANKING";

		// 本局排行：10 行 + 榜外自身行（左列）
		_matchRows = new Label[GameConfig.LeaderboardRows];
		for ( int i = 0; i < _matchRows.Length; i++ )
		{
			_matchRows[i] = NewRow( root, 276f + i * 28f, 260f, 580f, "eb-row" );
		}

		_selfRow = NewRow( root, 276f + _matchRows.Length * 28f, 260f, 580f, "eb-row self" );
		_selfRow.Style.Display = DisplayMode.None;

		_globalHead = NewRow( root, 244f, 1060f, 580f, "eb-head global" );
		_globalHead.Text = "GLOBAL TOP 10";

		_globalRows = new Label[10];
		for ( int i = 0; i < _globalRows.Length; i++ )
		{
			_globalRows[i] = NewRow( root, 276f + i * 28f, 1060f, 580f, "eb-row small" );
		}

		_globalNote = NewRow( root, 276f + _globalRows.Length * 28f, 1060f, 580f, "eb-note" );

		_back = NewRow( root, 620f, 360f, 1200f, "eb-back" );
		_back.Text = Networking.IsActive ? "RETURN TO LOBBY — [SPACE]" : "BACK TO MENU — [SPACE]";
		_back.AddEventListener( "onclick", () => OnBackClicked() );

		_built = true;
	}

	/// <summary> 建一行 Label：列内居中（left..left+width），行号定 top </summary>
	Label NewRow( Panel root, float top, float left, float width, string classes )
	{
		var l = new Label() { Classes = classes };
		l.Style.Position = PositionMode.Absolute;
		l.Style.Left = left;
		l.Style.Width = width;
		l.Style.Top = top;
		root.AddChild( l );
		return l;
	}

	/// <summary> 比赛结束展示结算（CircleroyaleGame.OnMatchOverRemote 调用，双端） </summary>
	public void Show( ScoreWire[] standings, long mySteamId, string myName )
	{
		_standings = standings;
		_mySteamId = mySteamId;

		BuildUi();
		if ( !_built ) return;

		_sinceShow = 0;
		_shown = true;
		Panel.Style.Display = DisplayMode.Flex;

		_winner.Text = WinnerText( standings );

		FillMatchRows();
		FillGlobalPlaceholder();
		ScoreUploader.FetchGlobal( ApplyGlobal );
	}

	/// <summary> 回大厅/主菜单（实例方法：热重载可重映射） </summary>
	void OnBackClicked() => CircleroyaleGame.Current?.AfterSettlement();

	/// <summary> 回大厅时收起（面板随 GameObject 复用，下次开局再展示） </summary>
	public void Hide()
	{
		_shown = false;
		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.None;
	}

	// ---- 内容填充 ----

	string WinnerText( ScoreWire[] rows )
	{
		if ( rows is null || rows.Length == 0 ) return "";

		if ( !MatchState.IsTeam )
			return $"WINNER — {rows[0].Name}   ({rows[0].Mass:0})";

		// 团队赛：胜负按队伍 3 人总分
		var totals = new Dictionary<int, float>();
		foreach ( var r in rows )
		{
			totals.TryGetValue( r.Team, out var m );
			totals[r.Team] = m + r.Mass;
		}

		int best = -1;
		float bestMass = -1f;
		foreach ( var kv in totals )
		{
			if ( kv.Value > bestMass )
			{
				bestMass = kv.Value;
				best = kv.Key;
			}
		}
		return $"TEAM {best + 1} WINS   ({bestMass:0})";
	}

	void FillMatchRows()
	{
		var rows = _standings;
		int selfRank = -1;
		for ( int i = 0; i < rows.Length; i++ )
		{
			if ( rows[i].SteamId == _mySteamId && !rows[i].Bot )
			{
				selfRank = i;
				break;
			}
		}

		for ( int i = 0; i < _matchRows.Length; i++ )
		{
			var row = _matchRows[i];
			if ( i >= rows.Length )
			{
				row.Text = "";
				continue;
			}

			var r = rows[i];
			row.Text = RowText( $"{i + 1}.", r.Name, $"{r.Mass:0}", r.Team );

			bool isSelf = r.SteamId == _mySteamId && !r.Bot;
			row.Style.FontColor = isSelf ? new Color( 0.21f, 0.94f, 1f ) : new Color( 1f, 1f, 1f, 0.78f );
		}

		// 榜外自身行
		bool showSelf = selfRank >= _matchRows.Length;
		_selfRow.Style.Display = showSelf ? DisplayMode.Flex : DisplayMode.None;
		if ( showSelf )
		{
			var r = rows[selfRank];
			_selfRow.Text = RowText( $"#{selfRank + 1}.", r.Name, $"{r.Mass:0}", r.Team );
		}
	}

	/// <summary> 行文本（团队赛带 T# 前缀；普通赛只有名次/名字/分数） </summary>
	string RowText( string rank, string name, string mass, byte team )
	{
		return MatchState.IsTeam ? $"{rank} [T{team + 1}] {name}   {mass}" : $"{rank}  {name}   {mass}";
	}

	/// <summary> 全服榜先占位"加载中"，云端回来后填充（空了降级本地榜） </summary>
	void FillGlobalPlaceholder()
	{
		_globalRows[0].Text = "LOADING ...";
		for ( int i = 1; i < _globalRows.Length; i++ )
			_globalRows[i].Text = "";
		_globalNote.Text = "";
	}

	void ApplyGlobal( List<( string Name, float Mass )> entries )
	{
		if ( !_shown ) return;
		if ( _globalRows is null || _globalRows[0] is null || !_globalRows[0].IsValid() ) return;

		if ( entries is null )
		{
			// 云端不可用（未发布包是常态）：降级本机持久化榜
			entries = LocalBoard.Top( _globalRows.Length );
			_globalNote.Text = entries is not null ? "OFFLINE BOARD (THIS MACHINE)" : "NO RECORDS YET";
		}
		else
		{
			_globalNote.Text = "BEST SINGLE-MATCH SCORE";
		}

		for ( int i = 0; i < _globalRows.Length; i++ )
		{
			if ( entries is null || i >= entries.Count )
			{
				_globalRows[i].Text = "";
				continue;
			}
			_globalRows[i].Text = $"{i + 1}.  {entries[i].Name}   {entries[i].Mass:0}";
		}
	}

	// ---- 逐帧：reveal 动画 + 光标 + 空格回菜单 ----

	protected override void OnUpdate()
	{
		base.OnUpdate();

		BuildUi();
		if ( !_shown ) return;

		if ( Panel.IsValid() ) Panel.Style.Display = DisplayMode.Flex;
		Mouse.Visibility = MouseVisibility.Visible;   // 结算期按钮要能点

		float t = _sinceShow;
		Animate( _title, t, 0.05f );
		Animate( _winner, t, 0.2f );
		Animate( _matchHead, t, MatchRowDelay - 0.2f );

		for ( int i = 0; i < _matchRows.Length; i++ )
			Animate( _matchRows[i], t, MatchRowDelay + i * MatchRowStagger );

		Animate( _selfRow, t, MatchRowDelay + _matchRows.Length * MatchRowStagger );
		Animate( _globalHead, t, GlobalDelay - 0.2f );

		for ( int i = 0; i < _globalRows.Length; i++ )
			Animate( _globalRows[i], t, GlobalDelay + i * GlobalStagger );

		Animate( _globalNote, t, GlobalDelay + _globalRows.Length * GlobalStagger );
		Animate( _back, t, GlobalDelay + _globalRows.Length * GlobalStagger + 0.3f );

		// 自动返回（v0.6.4.2）：结算显示 10 秒后回大厅（多人）/主菜单（单机），空格可提前
		var remain = GameConfig.SettlementAutoReturnSeconds - _sinceShow;
		if ( remain <= 0f )
		{
			CircleroyaleGame.Current?.AfterSettlement();
			return;
		}

		if ( _back.IsValid() )
			_back.Text = Networking.IsActive
				? $"RETURN TO LOBBY IN {MathF.Ceiling( remain ):0} — [SPACE NOW]"
				: $"BACK TO MENU IN {MathF.Ceiling( remain ):0} — [SPACE NOW]";
	}

	/// <summary> 单行渐入：出场前 Opacity 0；0.25s 内透明度 0→1、PaddingLeft 36px→0（视觉上从右滑到位） </summary>
	void Animate( Label row, float now, float revealAt )
	{
		if ( row is null || !row.IsValid() ) return;

		float k = Math.Clamp( ( now - revealAt ) / 0.25f, 0f, 1f );
		if ( k <= 0f )
		{
			row.Style.Opacity = 0f;
			return;
		}

		row.Style.Opacity = k;
		row.Style.PaddingLeft = ( 1f - k ) * 36f;
	}
}
