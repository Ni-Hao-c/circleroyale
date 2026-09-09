using System;

/// <summary>
/// 食物管理（**每台机器一份本地实例，不走对象复制**——NetList 挂对象无法复制到客户端，实测）：
/// host 权威生成/吃/重生；状态变化经 NetworkManager 的静态 RPC 广播，各端应用到本地列表。
/// Init/TryEat/Tick 仅权威端调用；Apply* 仅供客户端应用远端事件。
/// 空间哈希（v0.6.3.0）：3 倍场地 4140 颗食物后，吃判定 O(球数×食物)≈20 万次/帧成为瓶颈——
/// 512 单位桶查询把每球检查量从 4140 降到几十。
/// </summary>
public sealed class FoodManager
{
	FoodData[] _foods;
	TimeSince[] _eatenAt;                           // 仅 host（重生计时）
	readonly List<int> _pendingRespawn = new();     // 仅 host

	// ---- 空间哈希（仅 host 吃判定用；客户端无吃判定不建桶）----
	const float BucketSize = 512f;
	Dictionary<int, List<int>> _buckets;
	int _cols;
	readonly List<int> _nearResults = new();

	/// <summary> 存活食物数 </summary>
	public int AliveCount { get; private set; }

	/// <summary> 全量食物（NeonRenderer 每帧读；各端内容一致） </summary>
	public FoodData[] Foods => _foods;

	int BucketKey( Vector2 p )
	{
		var half = GameConfig.ArenaHalfSize;
		int bx = Math.Clamp( (int) ( ( p.x + half ) / BucketSize ), 0, _cols - 1 );
		int by = Math.Clamp( (int) ( ( p.y + half ) / BucketSize ), 0, _cols - 1 );
		return by * _cols + bx;
	}

	void BucketAdd( int index ) => BucketAddAt( index, _foods[index].Pos );

	void BucketAddAt( int index, Vector2 p )
	{
		int key = BucketKey( p );
		if ( !_buckets.TryGetValue( key, out var list ) )
		{
			list = new List<int>( 16 );
			_buckets[key] = list;
		}
		list.Add( index );
	}

	void BucketMove( int index, Vector2 from )
	{
		int oldKey = BucketKey( from );
		if ( _buckets.TryGetValue( oldKey, out var list ) )
			list.Remove( index );

		BucketAdd( index );
	}

	/// <summary> 圆形范围内的存活食物下标（近似：按桶粗筛，调用方需精确复核距离）。
	/// 返回内部复用列表，下一次调用即失效。仅 host（吃判定权威端）。 </summary>
	public List<int> Nearby( Vector2 pos, float radius )
	{
		_nearResults.Clear();
		if ( _buckets is null || _foods is null ) return _nearResults;

		int minBx = Math.Clamp( (int) ( ( pos.x - radius + GameConfig.ArenaHalfSize ) / BucketSize ), 0, _cols - 1 );
		int maxBx = Math.Clamp( (int) ( ( pos.x + radius + GameConfig.ArenaHalfSize ) / BucketSize ), 0, _cols - 1 );
		int minBy = Math.Clamp( (int) ( ( pos.y - radius + GameConfig.ArenaHalfSize ) / BucketSize ), 0, _cols - 1 );
		int maxBy = Math.Clamp( (int) ( ( pos.y + radius + GameConfig.ArenaHalfSize ) / BucketSize ), 0, _cols - 1 );

		for ( int by = minBy; by <= maxBy; by++ )
		{
			for ( int bx = minBx; bx <= maxBx; bx++ )
			{
				if ( !_buckets.TryGetValue( by * _cols + bx, out var list ) ) continue;

				foreach ( var idx in list )
				{
					if ( _foods[idx].Alive ) _nearResults.Add( idx );
				}
			}
		}
		return _nearResults;
	}

	/// <summary> host 投食（权威端；随后经 FoodFull RPC 发给新连接） </summary>
	public void Init()
	{
		_foods = new FoodData[GameConfig.FoodCount];
		_eatenAt = new TimeSince[GameConfig.FoodCount];
		_pendingRespawn.Clear();
		_buckets = new Dictionary<int, List<int>>( 1024 );
		_cols = Math.Max( 1, (int) ( GameConfig.ArenaHalfSize * 2f / BucketSize ) + 1 );

		for ( int i = 0; i < _foods.Length; i++ )
		{
			_foods[i] = MakeFood();
			BucketAdd( i );
		}
		AliveCount = _foods.Length;
	}

	/// <summary> 每帧：把到期的重生位重新投食并广播（仅权威端） </summary>
	public void Tick()
	{
		for ( int i = _pendingRespawn.Count - 1; i >= 0; i-- )
		{
			int idx = _pendingRespawn[i];
			if ( _eatenAt[idx] < GameConfig.FoodRespawnSeconds ) continue;

			var oldPos = _foods[idx].Pos;
			_foods[idx] = MakeFood();
			_pendingRespawn.RemoveAt( i );
			AliveCount++;
			BucketMove( idx, oldPos );
			if ( Networking.IsActive ) NetworkManager.FoodRespawned( idx, _foods[idx] );   // 无会话（菜单演示赛）不广播
		}
	}

	/// <summary> 尝试吃掉指定食物；成功返回 true（调用方据此加质量）。仅权威端，内部广播。
	/// eaterSteamId 随广播下发，客户端据此判断是不是自己的嘴（播吃音） </summary>
	public bool TryEat( int index, long eaterSteamId = 0 )
	{
		if ( (uint)index >= (uint)_foods.Length || !_foods[index].Alive ) return false;

		_foods[index].Alive = false;
		_eatenAt[index] = 0;
		_pendingRespawn.Add( index );
		AliveCount--;
		if ( Networking.IsActive ) NetworkManager.FoodEaten( index, eaterSteamId );   // 无会话不广播
		return true;
	}

	FoodData MakeFood()
	{
		var half = GameConfig.ArenaHalfSize - GameConfig.FoodMargin;
		return new FoodData
		{
			Pos = new Vector2( Game.Random.Float( -half, half ), Game.Random.Float( -half, half ) ),
			ColorIndex = (byte)Game.Random.Float( 0f, GameConfig.Palette.Length - 0.001f ),
			Alive = true,
			Type = 0,
		};
	}

	// ---- 客户端应用（远端事件 → 本地列表）----

	public void ApplyFull( FoodData[] foods )
	{
		_foods = foods;
		AliveCount = 0;
		foreach ( var f in _foods )
		{
			if ( f.Alive ) AliveCount++;
		}
	}

	public void ApplyEaten( int index )
	{
		if ( _foods is null ) return;   // 热重载后新实例未同步全量前，忽略增量
		if ( (uint)index >= (uint)_foods.Length || !_foods[index].Alive ) return;

		var f = _foods[index];
		f.Alive = false;
		_foods[index] = f;
		AliveCount--;
	}

	public void ApplyRespawned( int index, FoodData food )
	{
		if ( _foods is null ) return;
		if ( (uint)index >= (uint)_foods.Length ) return;

		// 已活着（重复/乱序投递）也更新数据但不重复计数——AliveCount 只在真复燃时 +1
		if ( !_foods[index].Alive ) AliveCount++;
		_foods[index] = food;
	}
}