using System;

/// <summary>
/// 领土战争地面渲染（M7，v0.7.8.79 像素风重写）：占领格 = 队色底 + 粗边框 + 旗点旗子（flag.png
/// 白模染队色），大本营 = 城堡（castle.png 白模染队色）；争夺中的中立格 = 白边框 + 争夺方色进度条；
/// 分数越高底色越实。状态版本变化才重建几何（16 格顶点量极小）；贴图批次 1s 自愈补载
/// （NeonRenderer 同款）。CreateBackdrop 先于 Neon 建本组件 → 同层绘制在球/食物之下。非领土模式清空。
/// ⚠️ 相机 90° 滚转：旗/城堡这类有方向的贴图必须 rot=π/2 才屏幕正立（表情/头像同款教训）。
/// </summary>
public sealed class TerritoryRenderer : Component
{
	SceneDynamicObject _shape;    // 格底/粗边框/进度条（普通 alpha 三角形）
	SceneDynamicObject _flags;    // flag.png（白模 tint）
	SceneDynamicObject _castles;  // castle.png（白模 tint）
	int _builtVersion = -1;
	float _builtHalf = -1f;
	TimeSince _sinceRebuild = 999f;   // 重建节流（喂旗高峰 host 5Hz 快照，全清重写三批几何过频会闪）

	sealed class TexBatch
	{
		public SceneDynamicObject Dyno;
		public string Path;
		public Texture Tex;
	}

	readonly List<TexBatch> _texBatches = new();
	TimeSince _sinceTexHeal = 1f;

	const float BorderThickness = 18f;         // 格边框粗细（世界单位，像素粗边观感）
	const float FlagWorldHeight = 400f;        // 旗子世界高度（约 1/7.5 格宽）
	const float CastleWorldFraction = 0.30f;   // 城堡占格宽比例
	const float BarWidth = 380f;               // 进度条宽
	const float BarHeight = 36f;               // 进度条高
	const float FlagTexAspect = 32f / 52f;     // flag.png 宽高比

	protected override void OnStart()
	{
		base.OnStart();
		_shape = CreateShapeBatch();
		_flags = CreateTexBatch( "ui/pixel/flag.png" );
		_castles = CreateTexBatch( "ui/pixel/castle.png" );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !Game.IsPlaying ) return;

		var half = GameConfig.ArenaHalfSize;

		if ( TerritoryManager.Active )
		{
			// 重建节流（≥0.25s）：喂旗高峰 host 5Hz 快照，全清重写三批几何过频会闪；
			// 错过的版本不追，下一拍自然画上（进度差 1/5 格分不清）
			if ( ( _builtVersion != TerritoryManager.StateVersion || MathF.Abs( _builtHalf - half ) > 0.5f )
				&& _sinceRebuild > 0.25f )
				Rebuild( half );
		}
		else if ( _builtVersion >= 0 )
		{
			Clear();
		}

		// 贴图自愈（1s 节流）：热重载/资产刷新期间加载失败补载，否则整场错误红纹理（NeonRenderer 实测）
		if ( _sinceTexHeal > 1f )
		{
			_sinceTexHeal = 0;
			foreach ( var b in _texBatches )
			{
				if ( b.Tex is not null ) continue;
				LoadTex( b );
			}
		}
	}

	void Clear()
	{
		_builtVersion = -1;
		if ( _shape.IsValid() ) _shape.Clear();
		if ( _flags.IsValid() ) _flags.Clear();
		if ( _castles.IsValid() ) _castles.Clear();
	}

	SceneDynamicObject CreateShapeBatch()
	{
		var dyno = new SceneDynamicObject( Scene.SceneWorld );
		dyno.Material = LineMaterial.Create();
		dyno.Flags.CastShadows = false;
		dyno.RenderLayer = SceneRenderLayer.OverlayWithDepth;
		dyno.Attributes.SetCombo( "D_BLEND", 0 );   // 像素风普通 alpha（加色发光已随霓虹风废弃）
		return dyno;
	}

	SceneDynamicObject CreateTexBatch( string path )
	{
		var b = new TexBatch { Dyno = CreateShapeBatch(), Path = path };
		_texBatches.Add( b );
		LoadTex( b );
		return b.Dyno;
	}

	void LoadTex( TexBatch b )
	{
		b.Tex = Texture.Load( b.Path, false );
		if ( b.Tex is null )
			Log.Warning( $"[pixel] texture not ready: {b.Path} (1s 后自动重试)" );
		b.Dyno.Material = NeonRenderer.MakeTexMaterial( b.Tex );   // Material 只写：先备好再赋
	}

	void Rebuild( float half )
	{
		_builtVersion = TerritoryManager.StateVersion;
		_builtHalf = half;
		_sinceRebuild = 0;

		_shape.Clear();
		_shape.Init( Graphics.PrimitiveType.Triangles );
		_flags.Clear();
		_flags.Init( Graphics.PrimitiveType.Triangles );
		_castles.Clear();
		_castles.Init( Graphics.PrimitiveType.Triangles );

		var grid = GameConfig.TerritoryGrid;
		var s = TerritoryManager.CellSize;
		var flagHalfH = FlagWorldHeight * 0.5f;
		var flagHalfW = flagHalfH * FlagTexAspect;
		var castleHalf = s * CastleWorldFraction * 0.5f;

		for ( int cy = 0; cy < grid; cy++ )
		{
			for ( int cx = 0; cx < grid; cx++ )
			{
				TerritoryManager.CellState( cx, cy, out var owner, out var scoreTeam, out var score );
				var frac = Math.Clamp( score / GameConfig.TerritoryCaptureScore, 0f, 1f );
				var center = TerritoryManager.CellCenter( cx, cy );

				var x0 = -half + cx * s;
				var x1 = -half + ( cx + 1 ) * s;
				var y0 = -half + cy * s;
				var y1 = -half + ( cy + 1 ) * s;
				var isHq = IsHq( cx, cy );

				// 主色：归属方队色；争夺中中立=白边框+争夺方进度；无人问津=灰旗
				Color teamColor;
				bool contested = false;
				if ( owner >= 0 )
					teamColor = MatchState.TeamColor( owner );
				else if ( scoreTeam >= 0 && score > 0f )
				{
					teamColor = MatchState.TeamColor( scoreTeam );
					contested = true;
				}
				else
					teamColor = new Color( 0.62f, 0.65f, 0.73f );

				if ( owner >= 0 || contested )
				{
					// 格底：越满越实（争夺中更淡——还没到手）
					var fillA = owner >= 0 ? 0.16f + 0.18f * frac : 0.07f + 0.10f * frac;
					var fill = new Color( teamColor.r, teamColor.g, teamColor.b, fillA );
					AddQuadShape( x0 + BorderThickness, y0 + BorderThickness, x1 - BorderThickness, y1 - BorderThickness, 0.20f, fill );

					// 粗边框（像素领地感；争夺中=白色虚位以待）
					var lineC = owner >= 0
						? new Color( teamColor.r, teamColor.g, teamColor.b, 0.85f )
						: new Color( 1f, 1f, 1f, 0.55f );
					AddBorder( x0, y0, x1, y1, 0.30f, lineC );

					// 进度条（驻军/占领进度，旗/城堡下方）
					if ( frac > 0.01f )
					{
						var barTop = center.y + FlagWorldHeight * 0.62f + BarHeight;
						AddQuadShape( center.x - BarWidth * 0.5f, barTop - BarHeight, center.x + BarWidth * 0.5f, barTop, 0.35f,
							new Color( 0.16f, 0.15f, 0.22f, 0.5f ) );
						AddQuadShape( center.x - BarWidth * 0.5f, barTop - BarHeight, center.x - BarWidth * 0.5f + BarWidth * frac, barTop, 0.35f,
							new Color( teamColor.r, teamColor.g, teamColor.b, 0.95f ) );
					}
				}

				// 旗/城堡：大本营=城堡，普通格=旗子（rot=π/2 对齐屏幕正立）
				Color spriteTint = owner >= 0 ? new Color( teamColor.r, teamColor.g, teamColor.b, 1f )
					: contested ? new Color( 1f, 1f, 1f, 1f )
					: new Color( 0.72f, 0.74f, 0.82f, 0.45f );

				if ( isHq )
					AddTexQuad( _castles, center, castleHalf, castleHalf, MathF.PI * 0.5f, spriteTint, 0.52f );
				else
					AddTexQuad( _flags, center, flagHalfW, flagHalfH, MathF.PI * 0.5f, spriteTint, 0.52f );
			}
		}
	}

	static bool IsHq( int cx, int cy )
	{
		for ( var t = 0; t < 4; t++ )
		{
			var hq = TerritoryManager.HqCell( t );
			if ( hq.Cx == cx && hq.Cy == cy ) return true;
		}
		return false;
	}

	// ---- 顶点写入（shape=纯色 quad；tex=UV quad，写法同 NeonRenderer.AddQuad）----

	void AddQuadShape( float x0, float y0, float x1, float y1, float z, Color color )
	{
		var a = new Vertex( new Vector3( x0, y0, z ), color );
		var b = new Vertex( new Vector3( x1, y0, z ), color );
		var c = new Vertex( new Vector3( x1, y1, z ), color );
		var d = new Vertex( new Vector3( x0, y1, z ), color );
		_shape.AddVertex( a ); _shape.AddVertex( b ); _shape.AddVertex( c );
		_shape.AddVertex( a ); _shape.AddVertex( c ); _shape.AddVertex( d );
	}

	void AddBorder( float x0, float y0, float x1, float y1, float z, Color color )
	{
		// 四条粗边向内铺，不越过格界
		AddQuadShape( x0, y0, x1, y0 + BorderThickness, z, color );
		AddQuadShape( x0, y1 - BorderThickness, x1, y1, z, color );
		AddQuadShape( x0, y0 + BorderThickness, x0 + BorderThickness, y1 - BorderThickness, z, color );
		AddQuadShape( x1 - BorderThickness, y0 + BorderThickness, x1, y1 - BorderThickness, z, color );
	}

	void AddTexQuad( SceneDynamicObject batch, Vector3 center, float halfX, float halfY, float rot, Color color, float z )
	{
		var cos = MathF.Cos( rot );
		var sin = MathF.Sin( rot );
		var rx = cos * halfX; var ry = sin * halfX;      // right 轴
		var ux = -sin * halfY; var uy = cos * halfY;     // up 轴

		var v0 = new Vertex( new Vector3( center.x - rx - ux, center.y - ry - uy, z ), new Vector4( 0f, 0f, 0f, 0f ), color );
		var v1 = new Vertex( new Vector3( center.x + rx - ux, center.y + ry - uy, z ), new Vector4( 1f, 0f, 0f, 0f ), color );
		var v2 = new Vertex( new Vector3( center.x + rx + ux, center.y + ry + uy, z ), new Vector4( 1f, 1f, 0f, 0f ), color );
		var v3 = new Vertex( new Vector3( center.x - rx + ux, center.y - ry + uy, z ), new Vector4( 0f, 1f, 0f, 0f ), color );

		batch.AddVertex( v0 ); batch.AddVertex( v1 ); batch.AddVertex( v2 );
		batch.AddVertex( v0 ); batch.AddVertex( v2 ); batch.AddVertex( v3 );
	}
}