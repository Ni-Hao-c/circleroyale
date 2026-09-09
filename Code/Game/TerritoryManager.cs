using System;

/// <summary>
/// 领土战争模式（M7，用户定稿 V2.1）：4×4 格，四角大本营开局属己方且永不沦陷。
/// 驻军模型占领：吐球进旗圈计分——中立格喂满 100 变色占领（驻军满 100）；
/// 敌方吐球先啃驻军，啃光回中立（溢出转为自己进度）。无自动衰减、无补给线；
/// 只能抢与己方领土 4 邻接的格（大本营免疫敌方喂食）。
/// host 权威结算（TickCells 吐球命中即记 + 5Hz 变化快照广播）；客户端 ApplyRemote 只读。
/// 格子几何由 ArenaHalfSize 决定（Replicated 下发，双端一致），无额外网络状态。
/// </summary>
public static class TerritoryManager
{
	const int TeamCount = 4;

	struct Cell
	{
		public int Owner;      // 占领队（-1 = 中立）
		public int ScoreTeam;  // 分数归属队（中立格的争夺进度方；-1 = 无）
		public float Score;    // 中立格 = 该队的占领进度；己方格 = 驻军值
	}

	static readonly Cell[] _cells = new Cell[GameConfig.TerritoryGrid * GameConfig.TerritoryGrid];
	static readonly int[] _owned = new int[TeamCount];
	static readonly float[] _points = new float[TeamCount];   // 占旗积分（每旗每 3s +1，用户定稿）
	static float _pointTimer;                                 // 积分发放节拍
	static int _conquerTeam = -1;   // 先到征服胜利的队（TickTerritory 读，EndMatch 后随 Reset 清）
	static int _sentVersion;        // 已广播过的版本号（host 节流：变了才发）

	public static bool Active => MatchState.IsTerritory;

	/// <summary> 状态版本号：任何格变化 +1（TerritoryRenderer 据此重建几何） </summary>
	public static int StateVersion { get; private set; }

	/// <summary> host：自上次快照后状态有变（TickTerritory 节流广播用） </summary>
	public static bool NeedsSync => StateVersion != _sentVersion;

	/// <summary> 规则生效点调用（MatchState.Apply）：全端复位为开局态——四角大本营属己方满驻军，其余全中立 </summary>
	public static void Reset()
	{
		for ( int i = 0; i < _cells.Length; i++ )
		{
			_cells[i].Owner = -1;
			_cells[i].ScoreTeam = -1;
			_cells[i].Score = 0f;
		}

		for ( var t = 0; t < TeamCount; t++ )
		{
			var ( cx, cy ) = HqCell( t );
			ref var c = ref _cells[cy * GameConfig.TerritoryGrid + cx];
			c.Owner = t;
			c.ScoreTeam = t;
			c.Score = GameConfig.TerritoryCaptureScore;
		}

		_conquerTeam = -1;
		_sentVersion = 0;
		_pointTimer = 0f;
		for ( var t = 0; t < TeamCount; t++ ) _points[t] = 0f;
		Recount();
		StateVersion++;
	}

	/// <summary> 队伍的大本营格（固定角映射：0 左上、1 右上、2 右下、3 左下） </summary>
	public static ( int Cx, int Cy ) HqCell( int team ) => team switch
	{
		0 => ( 0, 0 ),
		1 => ( GameConfig.TerritoryGrid - 1, 0 ),
		2 => ( GameConfig.TerritoryGrid - 1, GameConfig.TerritoryGrid - 1 ),
		_ => ( 0, GameConfig.TerritoryGrid - 1 ),
	};

	static bool IsHq( int cx, int cy )
	{
		for ( var t = 0; t < TeamCount; t++ )
		{
			var hq = HqCell( t );
			if ( hq.Cx == cx && hq.Cy == cy ) return true;
		}
		return false;
	}

	// ---- 几何（双端一致，只依赖 ArenaHalfSize）----

	public static float CellSize => GameConfig.ArenaHalfSize * 2f / GameConfig.TerritoryGrid;

	public static Vector3 CellCenter( int cx, int cy )
	{
		var half = GameConfig.ArenaHalfSize;
		var s = CellSize;
		return new Vector3( -half + ( cx + 0.5f ) * s, -half + ( cy + 0.5f ) * s, 0f );
	}

	public static void CellOf( Vector3 pos, out int cx, out int cy )
	{
		var half = GameConfig.ArenaHalfSize;
		var s = CellSize;
		cx = Math.Clamp( (int)MathF.Floor( ( pos.x + half ) / s ), 0, GameConfig.TerritoryGrid - 1 );
		cy = Math.Clamp( (int)MathF.Floor( ( pos.y + half ) / s ), 0, GameConfig.TerritoryGrid - 1 );
	}

	/// <summary> 队伍大本营内的出生采样范围（贴边内缩，别出生到旗圈边上挨打） </summary>
	public static void HqBounds( int team, out float minX, out float maxX, out float minY, out float maxY )
	{
		var ( cx, cy ) = HqCell( team );
		var half = GameConfig.ArenaHalfSize;
		var s = CellSize;
		var inset = s * 0.28f;
		minX = -half + cx * s + inset;
		maxX = -half + ( cx + 1 ) * s - inset;
		minY = -half + cy * s + inset;
		maxY = -half + ( cy + 1 ) * s - inset;
	}

	/// <summary> 球是否在己方大本营内（大体型衰减豁免判定，Ball 衰减处调用） </summary>
	public static bool IsOwnHq( int team, Vector3 pos )
	{
		if ( !Active || team < 0 ) return false;
		CellOf( pos, out var cx, out var cy );
		var hq = HqCell( team );
		return cx == hq.Cx && cy == hq.Cy;
	}

	/// <summary> 球是否在敌营大本营内（M7.2 安全屋：减速+掉重；无队/非领土恒 false） </summary>
	public static bool InHostileHq( int team, Vector3 pos )
	{
		if ( !Active || team < 0 ) return false;
		CellOf( pos, out var cx, out var cy );
		ref var c = ref _cells[cy * GameConfig.TerritoryGrid + cx];
		return IsHq( cx, cy ) && c.Owner >= 0 && c.Owner != team;
	}

	/// <summary> 是否存在前线格（大本营以外的己方占领格——死亡面板 FRONT 按钮亮暗用） </summary>
	public static bool HasFrontCell( int team ) => TryFrontSpawn( team, default, out _ );

	/// <summary> 该位置的旗是否允许该队动作（己方格，或邻接己方领土的非大本营格）——UI 引导用；
	/// 与 Feed 的入侵门同一规则 </summary>
	public static bool CanActOn( int team, Vector3 pos )
	{
		CellOf( pos, out var cx, out var cy );
		return CanActOnCell( cx, cy, team );
	}

	public static bool CanActOnCell( int cx, int cy, int team )
	{
		if ( !Active || team < 0 ) return false;
		ref var c = ref _cells[cy * GameConfig.TerritoryGrid + cx];
		return c.Owner == team || ( !IsHq( cx, cy ) && AdjacentToOwned( cx, cy, team ) );
	}

	/// <summary> 前线复活点（M7.2 复活二选一）：离 nearPos 最近的己方非大本营占领格内随机点；
	/// 没有前线格返回 false（只剩大本营可复活） </summary>
	public static bool TryFrontSpawn( int team, Vector3 nearPos, out Vector3 pos )
	{
		pos = default;
		if ( !Active || team < 0 ) return false;

		var best = float.MaxValue;
		var found = false;
		var grid = GameConfig.TerritoryGrid;
		for ( int cy = 0; cy < grid; cy++ )
		{
			for ( int cx = 0; cx < grid; cx++ )
			{
				ref var c = ref _cells[cy * grid + cx];
				if ( c.Owner != team || IsHq( cx, cy ) ) continue;
				var d = CellCenter( cx, cy ).Distance( nearPos );
				if ( d < best )
				{
					best = d;
					pos = CellCenter( cx, cy );
					found = true;
				}
			}
		}
		if ( !found ) return false;

		var s = CellSize;
		pos += new Vector3( Game.Random.Float( -s * 0.18f, s * 0.18f ), Game.Random.Float( -s * 0.18f, s * 0.18f ), 0f );
		return true;
	}

	// ---- 喂旗结算（host 权威）----

	/// <summary> 一颗孢子落进旗圈（TickCells 吐球命中调用）。
	/// 返回是否消耗该孢子：进旗圈且规则允许就消耗计分；没进圈或规则不允许（非邻接/大本营）
	/// 则 false，孢子继续当普通孢子飞，吐出的质量不白费 </summary>
	public static bool Feed( int team, Vector3 pos, float blobMass )
	{
		if ( !Active || team < 0 || blobMass <= 0f ) return false;

		CellOf( pos, out var cx, out var cy );
		if ( CellCenter( cx, cy ).Distance( pos ) >= GameConfig.TerritoryFlagRadius ) return false;   // 没进旗圈

		ref var c = ref _cells[cy * GameConfig.TerritoryGrid + cx];

		// 入侵规则：动别人/无主的格，必须与己方领土 4 邻接（稳扎稳打，无飞地）
		if ( c.Owner != team && ( !AdjacentToOwned( cx, cy, team ) || IsHq( cx, cy ) ) )
			return false;

		var pts = MathF.Min( blobMass * GameConfig.TerritoryScorePerMass, GameConfig.TerritoryScoreCapPerBlob );
		if ( pts <= 0f ) return false;

		var changed = false;

		if ( c.Owner == team )
		{
			// 己方格：补驻军（无自动衰减，只会被敌方啃掉后重占）
			if ( c.Score < GameConfig.TerritoryCaptureScore )
			{
				c.Score = MathF.Min( GameConfig.TerritoryCaptureScore, c.Score + pts );
				changed = true;
			}
		}
		else if ( c.Owner < 0 )
		{
			// 中立格：无主进度归自己累加；有别人的进度先啃，啃光转为自己累加（溢出计入）
			if ( c.ScoreTeam < 0 || c.ScoreTeam == team )
			{
				c.ScoreTeam = team;
				c.Score += pts;
			}
			else
			{
				c.Score -= pts;
				if ( c.Score <= 0f )
				{
					c.ScoreTeam = team;
					c.Score = -c.Score;
				}
			}

			if ( c.Score >= GameConfig.TerritoryCaptureScore )
			{
				c.Owner = team;
				c.Score = GameConfig.TerritoryCaptureScore;
				_owned[team]++;
				changed = true;
				NeonRenderer.Puff( CellCenter( cx, cy ), team );
				GameLog.Info( $"[territory] team {team + 1} captured cell ({cx},{cy}) — {_owned[team]}/{GameConfig.TerritoryWinCells}" );
			}
			else
			{
				changed = true;
			}
		}
		else
		{
			// 敌方格：先啃驻军，啃光回中立（溢出转为自己进度，接着喂就能顺势占领）
			c.Score -= pts;
			if ( c.Score <= 0f )
			{
				_owned[c.Owner]--;
				c.Owner = -1;
				c.ScoreTeam = team;
				c.Score = -c.Score;
				NeonRenderer.Puff( CellCenter( cx, cy ), team );
				GameLog.Info( $"[territory] team {team + 1} neutralized cell ({cx},{cy})" );
			}
			changed = true;
		}

		if ( changed )
		{
			StateVersion++;
			if ( _conquerTeam < 0 && _owned[team] >= GameConfig.TerritoryWinCells )
			{
				_conquerTeam = team;
				GameLog.Info( $"[territory] team {team + 1} reaches {GameConfig.TerritoryWinCells} cells — conquest!" );
			}
		}
		return true;
	}

	static bool AdjacentToOwned( int cx, int cy, int team )
	{
		return Owns( cx - 1, cy, team ) || Owns( cx + 1, cy, team )
			|| Owns( cx, cy - 1, team ) || Owns( cx, cy + 1, team );
	}

	static bool Owns( int cx, int cy, int team )
	{
		if ( cx < 0 || cy < 0 || cx >= GameConfig.TerritoryGrid || cy >= GameConfig.TerritoryGrid ) return false;
		return _cells[cy * GameConfig.TerritoryGrid + cx].Owner == team;
	}

	static void Recount()
	{
		for ( var i = 0; i < _owned.Length; i++ ) _owned[i] = 0;
		foreach ( var c in _cells )
		{
			if ( c.Owner >= 0 && c.Owner < _owned.Length ) _owned[c.Owner]++;
		}
	}

	/// <summary> 某队当前占领格数（HUD/结算/胜利判定共用） </summary>
	public static int OwnedCount( int team ) => team >= 0 && team < TeamCount ? _owned[team] : 0;

	/// <summary> host 每帧：占旗积分发放（用户定稿——每占领一旗每 3 秒 +1 分；点数只在 host 累积，随快照下发） </summary>
	public static void TickPoints( float dt )
	{
		if ( !Active ) return;
		_pointTimer += dt;
		var step = GameConfig.TerritoryPointSeconds;
		while ( _pointTimer >= step )
		{
			_pointTimer -= step;
			for ( var t = 0; t < TeamCount; t++ ) _points[t] += _owned[t];
		}
	}

	/// <summary> 某队当前占旗积分（胜利判定/HUD/结算共用；客户端经 TerritoryState 快照同步） </summary>
	public static float Points( int team ) => team >= 0 && team < TeamCount ? _points[team] : 0f;

	/// <summary> 单格状态读取（TerritoryRenderer 每次重建逐格取） </summary>
	public static void CellState( int cx, int cy, out int owner, out int scoreTeam, out float score )
	{
		if ( cx < 0 || cy < 0 || cx >= GameConfig.TerritoryGrid || cy >= GameConfig.TerritoryGrid )
		{
			owner = -1;
			scoreTeam = -1;
			score = 0f;
			return;
		}
		ref var c = ref _cells[cy * GameConfig.TerritoryGrid + cx];
		owner = c.Owner;
		scoreTeam = c.ScoreTeam;
		score = c.Score;
	}

	/// <summary> 达成征服胜利的队伍（-1 = 尚无）；host 检查到即 EndMatch </summary>
	public static int ConquerWinner => _conquerTeam;

	// ---- 网络同步 ----

	/// <summary> host：当前四队占旗积分（与格子快照同拍下发） </summary>
	public static float[] PointsWire()
	{
		var arr = new float[TeamCount];
		for ( var t = 0; t < TeamCount; t++ ) arr[t] = _points[t];
		return arr;
	}

	/// <summary> host：当前 16 格状态快照（发出即记录版本，NeedsSync 归零） </summary>
	public static TerritoryWire[] Snapshot()
	{
		var wires = new TerritoryWire[_cells.Length];
		for ( int i = 0; i < _cells.Length; i++ )
		{
			wires[i] = new TerritoryWire
			{
				Owner = _cells[i].Owner < 0 ? (byte)255 : (byte)_cells[i].Owner,
				ScoreTeam = _cells[i].ScoreTeam < 0 ? (byte)255 : (byte)_cells[i].ScoreTeam,
				Score = _cells[i].Score,
			};
		}
		_sentVersion = StateVersion;
		return wires;
	}

	/// <summary> 客户端应用 host 快照（host 回声由 IsAuthority 挡掉；重复应用幂等）。
	/// points = 四队占旗积分（null 容错：旧包/空包只刷格子） </summary>
	public static void ApplyRemote( TerritoryWire[] wires, float[] points )
	{
		if ( NetworkManager.IsAuthority ) return;
		if ( wires is null || wires.Length != _cells.Length ) return;

		// 逐项比对，完全没变就跳过——host 每 2s 保活快照带相同数据，
		// 无脑 StateVersion++ 会让客户端领土几何频繁重建（闪，v0.7.8.86 修）
		var changed = false;
		for ( int i = 0; i < _cells.Length; i++ )
		{
			var owner = wires[i].Owner == 255 ? -1 : wires[i].Owner;
			var scoreTeam = wires[i].ScoreTeam == 255 ? -1 : wires[i].ScoreTeam;
			var score = wires[i].Score;
			ref var c = ref _cells[i];
			if ( c.Owner == owner && c.ScoreTeam == scoreTeam && c.Score == score ) continue;
			c.Owner = owner;
			c.ScoreTeam = scoreTeam;
			c.Score = score;
			changed = true;
		}
		if ( points is not null )
		{
			for ( var t = 0; t < TeamCount && t < points.Length; t++ )
			{
				if ( _points[t] != points[t] )
				{
					_points[t] = points[t];
					changed = true;
				}
			}
		}
		if ( !changed ) return;
		Recount();
		StateVersion++;
	}
}

/// <summary> 领土格网络快照线格式（255 = 中立/无归属；与 CellWire/ScoreWire 同顶层惯例） </summary>
public struct TerritoryWire
{
	public byte Owner;
	public byte ScoreTeam;
	public float Score;
}