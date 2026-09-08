using System;
using System.Collections.Generic;

/// <summary>
/// bot 几何图案头像（M3）：按 bot 名字 + 色号哈希**确定性生成**——同一 bot 在 host 与
/// 各客户端算出完全相同的图案，零同步成本。纯数学图样（同心环/辐射条/点阵/波纹），
/// 经 Texture.Create 程序化生成，颜色取霓虹调色板；背景透明（透出球体的霓虹底色）。
/// </summary>
public static class BotAvatar
{
	const int Size = 128;

	static readonly Dictionary<string, Texture> _cache = new();

	/// <summary> 取 bot 头像：同 key 必得同一张；缓存超限整体清空（bot 数有限，实际到不了） </summary>
	public static Texture GetOrCreate( string botName, int colorIndex )
	{
		var key = $"{botName}#{colorIndex}";
		if ( _cache.TryGetValue( key, out var tex ) && tex.IsValid() ) return tex;

		if ( _cache.Count > 64 ) _cache.Clear();

		tex = Generate( botName ?? "", colorIndex );
		_cache[key] = tex;
		return tex;
	}

	static Texture Generate( string botName, int colorIndex )
	{
		// 稳定种子：FNV-1a 哈希（跨机器、跨进程一致）
		ulong hash = 14695981039346656037UL;
		foreach ( var ch in botName )
		{
			hash ^= ch;
			hash *= 1099511628211UL;
		}
		var rng = new System.Random( (int)( hash & 0x7fffffff ) );

		int pattern = rng.Next( 4 );
		int variant = rng.Next( 3 );   // 频率/相位微调，同款图样也不重样
		var baseColor = GameConfig.Palette[Math.Clamp( colorIndex, 0, GameConfig.Palette.Length - 1 )];

		var px = new byte[Size * Size * 4];
		int p = 0;
		for ( int y = 0; y < Size; y++ )
		{
			for ( int x = 0; x < Size; x++ )
			{
				float dx = ( x - Size * 0.5f ) / ( Size * 0.5f );
				float dy = ( y - Size * 0.5f ) / ( Size * 0.5f );
				float r = MathF.Sqrt( dx * dx + dy * dy );
				float a = MathF.Atan2( dy, dx );

				float v = pattern switch
				{
					0 => Rings( r, variant ),
					1 => Spokes( r, a, variant ),
					2 => Dots( dx, dy, variant ),
					_ => Waves( dx, dy, variant ),
				};

				// 圆形裁切 + 边缘 2px 抗锯齿；图样覆盖率 → 不透明度
				float alpha = Math.Clamp( ( 0.96f - r ) / 0.05f, 0f, 1f );
				float on = Math.Clamp( v, 0f, 1f ) * alpha;
				float shade = 1.15f - r * 0.5f;   // 中心亮边缘暗，带点立体感

				px[p++] = (byte)( Math.Clamp( baseColor.r * shade, 0f, 1f ) * on * 255f );
				px[p++] = (byte)( Math.Clamp( baseColor.g * shade, 0f, 1f ) * on * 255f );
				px[p++] = (byte)( Math.Clamp( baseColor.b * shade, 0f, 1f ) * on * 255f );
				px[p++] = (byte)( on * 255f );
			}
		}

		return Texture.Create( Size, Size, ImageFormat.RGBA8888 )
			.WithData( px )
			.WithName( $"botavatar_{(uint)hash:X8}" )
			.Finish();
	}

	/// <summary> 同心环：实心核心 + N 道环 </summary>
	static float Rings( float r, int variant )
	{
		if ( r < 0.18f ) return 1f;

		var freq = 2.6f + variant * 1.3f;
		var f = r * freq - variant * 0.33f;
		return Fract( f ) < 0.45f ? 1f : 0f;
	}

	/// <summary> 辐射条：从中心射出的辐条 + 实心核心 </summary>
	static float Spokes( float r, float a, int variant )
	{
		if ( r < 0.2f ) return 1f;

		var spokes = 6 + variant * 2;
		var f = ( a / ( MathF.PI * 2f ) + 1f ) * spokes;
		return Fract( f ) < 0.4f ? 1f : 0f;
	}

	/// <summary> 点阵：正弦调制的圆点网格 </summary>
	static float Dots( float dx, float dy, int variant )
	{
		var freq = 7f + variant * 3f;
		var phase = variant * 2.1f;
		var v = MathF.Sin( dx * freq + phase ) * MathF.Sin( dy * freq - phase );
		return v > 0.35f ? 1f : 0f;
	}

	/// <summary> 波纹：水平正弦波带 </summary>
	static float Waves( float dx, float dy, int variant )
	{
		var freq = 5f + variant * 2.5f;
		var phase = variant * 1.7f;
		var band = dy + 0.32f * MathF.Sin( dx * freq + phase );
		return MathF.Abs( band ) < 0.16f ? 1f : 0f;
	}

	static float Fract( float f ) => f - MathF.Floor( f );
}
