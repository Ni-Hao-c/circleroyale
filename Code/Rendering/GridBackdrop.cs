using System;

/// <summary>
/// 背景网格 + 发光边界（静态几何，line.shader 直线网格）。
/// warp_grid.shader（正弦扭曲网格，Assets/shaders/ 下保留）曾实装并被用户喊停——先回退静态网格，
/// 该 shader 文件留着以后想再开时直接换回（见 PLAN.md M4 记录）。
/// 场地尺寸可被 cr_arena_size 改（Replicated 下发，可能晚于 OnStart 到达）——OnUpdate 检测变化重建几何。
/// </summary>
public sealed class GridBackdrop : Component
{
	SceneDynamicObject _lines;
	float _builtHalf;   // 当前几何对应的场地半宽（变了就重建）

	static readonly Color GridColor = new Color( 0.10f, 0.22f, 0.42f, 0.45f );
	static readonly Color BorderColor = new Color( 0.10f, 0.95f, 1.00f, 0.9f );

	protected override void OnStart()
	{
		base.OnStart();
		Rebuild();
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !Game.IsPlaying ) return;

		// 客户端收到 Replicated 的场地尺寸（可能晚于 OnStart）→ 重建几何
		if ( MathF.Abs( _builtHalf - GameConfig.ArenaHalfSize ) > 0.5f )
			Rebuild();
	}

	void Rebuild()
	{
		var half = GameConfig.ArenaHalfSize;
		_builtHalf = half;

		if ( !_lines.IsValid() )
		{
			_lines = new SceneDynamicObject( Scene.SceneWorld );
			_lines.Material = Material.FromShader( "shaders/line.shader" );
			_lines.Flags.CastShadows = false;
			_lines.RenderLayer = SceneRenderLayer.OverlayWithDepth;
			_lines.Attributes.SetCombo( "D_BLEND", 1 );
		}
		_lines.Clear();
		_lines.Init( Graphics.PrimitiveType.Lines );

		var step = GameConfig.GridStep;
		for ( var x = -half; x <= half + 1f; x += step )
		{
			AddLine( new Vector3( x, -half, 0f ), new Vector3( x, half, 0f ), GridColor );
		}
		for ( var y = -half; y <= half + 1f; y += step )
		{
			AddLine( new Vector3( -half, y, 0f ), new Vector3( half, y, 0f ), GridColor );
		}

		AddSquare( half - 2f, BorderColor );
		AddSquare( half + 10f, BorderColor * 0.55f );
	}

	void AddSquare( float half, Color color )
	{
		AddLine( new Vector3( -half, -half, 0.5f ), new Vector3( half, -half, 0.5f ), color );
		AddLine( new Vector3( half, -half, 0.5f ), new Vector3( half, half, 0.5f ), color );
		AddLine( new Vector3( half, half, 0.5f ), new Vector3( -half, half, 0.5f ), color );
		AddLine( new Vector3( -half, half, 0.5f ), new Vector3( -half, -half, 0.5f ), color );
	}

	void AddLine( Vector3 a, Vector3 b, Color color )
	{
		_lines.AddVertex( new Vertex( a, color ) );
		_lines.AddVertex( new Vertex( b, color ) );
	}
}
