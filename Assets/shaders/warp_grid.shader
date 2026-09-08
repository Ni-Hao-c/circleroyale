HEADER
{
	Description = "Circleroyale Warped Neon Grid";
}

MODES
{
	Forward();
}

FEATURES
{
}

COMMON
{
	#define CUSTOM_MATERIAL_INPUTS
	#include "common/shared.hlsl"
}

struct VertexInput
{
	float3 vPositionWs : POSITION < Semantic( PosXyz ); >;
	uint nInstanceTransformID : TEXCOORD13 < Semantic( InstanceTransformUv ); >;
};

struct PixelInput
{
	float3 vPositionWs : TEXCOORD0;

	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs : SV_Position;
	#endif

	#if ( PROGRAM == VFX_PROGRAM_PS )
		float4 vPositionSs : SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionWs = i.vPositionWs;
		o.vPositionPs = Position3WsToPs( i.vPositionWs );
		return o;
	}
}

PS
{
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, ONE );   // 加色，和 line.shader 同款发光感

	// 参数由 GridBackdrop 每帧经 SceneDynamicObject.Attributes.Set 传入
	float CellSize < Attribute( "CellSize" ); Default( 128.0 ); >;
	float WarpTime < Attribute( "WarpTime" ); Default( 0.0 ); >;
	float WarpAmp < Attribute( "WarpAmp" ); Default( 22.0 ); >;
	float LineWidth < Attribute( "LineWidth" ); Default( 1.3 ); >;
	float4 GridTint < Attribute( "GridTint" ); Default4( 0.10, 0.22, 0.42, 0.50 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 p = i.vPositionWs.xy;

		// 双层正弦扭曲：一层快波带方向感 + 一层慢波大面积揉动
		float2 warp;
		warp.x = sin( p.y * 0.0080 + WarpTime * 0.90 ) * WarpAmp
		       + sin( p.y * 0.0021 - WarpTime * 0.53 ) * WarpAmp * 0.6;
		warp.y = sin( p.x * 0.0075 - WarpTime * 0.80 ) * WarpAmp
		       + sin( p.x * 0.0017 + WarpTime * 0.41 ) * WarpAmp * 0.6;

		float2 cellUv = ( p + warp ) / CellSize;

		// 线宽随屏幕导数自适应（远看不糊、近看不断），同 snap_grid 的抗锯齿写法
		float2 deriv = max( abs( ddx( cellUv ) ), abs( ddy( cellUv ) ) );
		float2 wrapped = abs( frac( cellUv ) - 0.5 );
		float2 lw = LineWidth * deriv;
		float2 cov = saturate( ( wrapped - ( 0.5 - lw ) ) / max( lw, 0.0001 ) );
		float grid = max( cov.x, cov.y );

		// 呼吸脉冲：从场地中心向外扩散的亮度环
		float breathe = 1.0 + 0.35 * sin( length( p ) * 0.0012 - WarpTime * 1.4 );

		float4 col = GridTint * grid * breathe;
		if ( col.a < 0.004 ) discard;

		return col;
	}
}
