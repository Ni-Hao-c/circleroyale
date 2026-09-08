using System;

/// <summary>
/// 霓虹批渲染：每帧重写两组顶点（线段 / 三角形），全场实体一帧两个 DrawCall。
/// 球的构图按用户要求：<b>玩家头像为圆心，科幻线条为外侧</b>——
/// M0 内芯用低透明度色盘占位，M3 换成 Steam 头像四边形（Texture.LoadAvatar，bot 用几何图案）。
/// </summary>
	public sealed class NeonRenderer : Component
	{
		SceneDynamicObject _lines;
		SceneDynamicObject _tris;

		/// <summary> 全局引用（特效生成入口 NeonRenderer.Pop/Spark/Puff 走静态调用） </summary>
		public static NeonRenderer Instance { get; private set; }

		protected override void OnStart()
		{
			base.OnStart();

			Instance = this;
			_lines = CreateBatch();
			_tris = CreateBatch();
		}

		protected override void OnDestroy()
		{
			base.OnDestroy();
			if ( Instance == this ) Instance = null;
		}

	SceneDynamicObject CreateBatch()
	{
		var dyno = new SceneDynamicObject( Scene.SceneWorld );
		dyno.Material = Material.FromShader( "shaders/line.shader" );   // 顶点色 + 加色混合
		dyno.Flags.CastShadows = false;
		dyno.RenderLayer = SceneRenderLayer.OverlayWithDepth;
		dyno.Attributes.SetCombo( "D_BLEND", 1 );                       // SrcAlpha/One 加色，发光感
		return dyno;
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !Game.IsPlaying ) return;

		var game = CircleroyaleGame.Current;
		if ( game is null || _lines is null || _tris is null ) return;

		_lines.Clear();
		_lines.Init( Graphics.PrimitiveType.Lines );
		_tris.Clear();
		_tris.Init( Graphics.PrimitiveType.Triangles );

		UpdateParticles();

		foreach ( var ball in game.Balls )
		{
			if ( !ball.IsValid() || !ball.Alive ) continue;
			WriteBall( game, ball );

			// 瞄准箭头（v0.7.0.0 用户需求）：本机球外缘小三角指向光标——分裂/吐孢子方向指示。
			// 仅对局中显示（房间页停泊的球不画，箭头方向已冻结没有意义）
			if ( game.IsMatchStarted && ball == game.LocalBall && ball.AimDir.Length > 0.01f )
				WriteAimArrow( ball );
		}

		// 分身/孢子（纯数据实体，非 GameObject）：分身=无头像迷你球，孢子=小盘
		foreach ( var c in game.Cells )
		{
			var color = GameConfig.Palette[Math.Clamp( c.ColorIndex, 0, GameConfig.Palette.Length - 1 )];
			var pos = new Vector3( c.DrawPos.x, c.DrawPos.y, 0f );
			var r = c.Radius;

			if ( c.PieceKind == CellPiece.Kind.EjectedMass )
			{
				AddDisc( _tris, pos, r * 0.85f, color * 0.5f, 0.9f, 10 );
				AddRing( _lines, pos, r, color * 0.7f, 14 );
			}
			else if ( c.PieceKind == CellPiece.Kind.SpikeMinion )
			{
				// 尖刺分身（v0.7.4.0）：紫星（野刺同族=是刺）+ 主人色描边环（=我方武器）
				var purple = new Color( 0.85f, 0.45f, 1f );
				WriteStar( pos + Vector3.Up * 0.9f, MathF.Max( r, 40f ), purple, purple * 0.8f );
				AddRing( _lines, pos, MathF.Max( r, 40f ) * 1.25f, color * 0.8f, 20 );
			}
			else
			{
				AddDisc( _tris, pos, r * 0.92f, color * 0.32f, 0.9f, GameConfig.DiscSegments );
				AddRing( _lines, pos, r, color * 0.9f, GameConfig.RingSegments );
			}
		}

		// 尖刺：12 角星形（撞刺炸裂的障碍；小的从底下穿过）。**紫色——用户定稿**：
		// 绿色顶点色在此管线里帧间表现不稳定（同一代码一会儿绿一会儿品红），紫/品红族始终稳定
		var spikes = game.Spikes;
		if ( spikes is not null )
		{
			var purple = new Color( 0.85f, 0.45f, 1f );
			foreach ( var s in spikes )
			{
				// 实心（用户定稿）：填充拉满，加色混合下星形内部完整发亮，不再只是描边+淡芯
				WriteStar( new Vector3( s.x, s.y, 0f ), GameConfig.SpikeRadius, purple, purple * 0.85f );
			}
		}

		// 道具（v0.7.3.0）：旋转菱形 + 呼吸光晕，种类色区分（尺寸 26 远大于食物 8，一眼可辨）。
		// 形状微差辅助色弱区分：护盾双环 / 磁铁十字芯 / 质量罐实心大芯 / 加速拖尾尖角
		var power = game.PowerUps;
		if ( power is not null && power.Ready )
		{
			var spin = Time.Now * 1.6f;
			var breathe = 1f + MathF.Sin( Time.Now * 3f ) * 0.12f;

			foreach ( var s in power.Slots )
			{
				if ( !s.Alive ) continue;

				var pos = new Vector3( s.Pos.x, s.Pos.y, 0f );
				var kindColor = PowerUpManager.ColorOf( s.Kind );
				var r = GameConfig.PowerUpRadius * breathe;

				WriteDiamond( pos, r, spin, kindColor );

				switch ( (PowerUpManager.Kind)s.Kind )
				{
					case PowerUpManager.Kind.Shield:
						AddRing( _lines, pos, r * 1.45f, kindColor * 0.45f, 18 );   // 双环=防护
						break;
					case PowerUpManager.Kind.Magnet:
						WriteCross( pos, r * 0.55f, kindColor * 0.9f );             // 十字=吸附
						break;
					case PowerUpManager.Kind.Mass:
						AddDisc( _tris, pos, r * 0.5f, kindColor * 0.85f, 1.2f, 10 );   // 实心大芯=增重
						break;
					case PowerUpManager.Kind.Speed:
						WriteDiamond( pos + new Vector3( -r * 0.9f, 0f, 0f ), r * 0.45f, spin, kindColor * 0.4f );   // 拖尾=冲刺
						break;
					case PowerUpManager.Kind.Spike:
						WriteStar( pos + Vector3.Up * 1.4f, r * 0.5f, kindColor, kindColor * 0.6f );   // 内星=刺
						break;
				}
			}
		}

		// 食物：低模小圆盘（道具 M4+ 按 Type 换形状/图标）
		var foods = game.Food?.Foods;
		if ( foods is not null )
		{
			// 视口剔除（v0.6.3.0）：3 倍场地 4140 颗食物只有 ~20% 在画面里——
			// 用相机世界包围盒（正交半高 + 竖向余量按宽高比折算）跳过画面外的
			var palette = GameConfig.Palette;
			var fr = GameConfig.FoodRadius;
			var cullCx = Scene.Camera.WorldPosition.x;
			var cullCy = Scene.Camera.WorldPosition.y;
			var cullHw = Scene.Camera.OrthographicHeight * 0.5f * 2.4f + 64f;   // 宽余量按最大宽高比
			var cullHh = Scene.Camera.OrthographicHeight * 0.5f + 64f;

			for ( int i = 0; i < foods.Length; i++ )
			{
				if ( !foods[i].Alive ) continue;

				var fx = foods[i].Pos.x;
				var fy = foods[i].Pos.y;
				if ( MathF.Abs( fx - cullCx ) > cullHw || MathF.Abs( fy - cullCy ) > cullHh ) continue;

				var c = palette[foods[i].ColorIndex % palette.Length] * 0.55f;
				AddDisc( _tris, new Vector3( fx, fy, 0f ), fr, c, 0.75f, GameConfig.FoodSegments );
			}
		}

		DrawParticles();
		DrawEdgeArrows();
	}

	// ---- 屏幕边缘队友指示箭头（v0.7.8.12）：GameHud 名牌重投时写入请求，本组件 OnUpdate 末尾消费 ----

	sealed class EdgeArrowReq
	{
		public Vector3 Pos;      // 贴边点（世界）
		public Vector2 Dir;      // 指向队友真实方位（世界，单位向量）
		public Color Color;
		public float Size;       // 世界单位（按每像素世界单位折算，保持像素大小恒定）
	}

	static readonly List<EdgeArrowReq> _edgeArrows = new();

	/// <summary> 请求一支边缘箭头（贴边世界点 + 指向 + 颜色）。OnUpdate 消费后清空，晚一帧无感 </summary>
	public static void EdgeArrow( Vector3 pos, Vector2 dir, Color color, float size )
	{
		_edgeArrows.Add( new EdgeArrowReq { Pos = pos, Dir = dir, Color = color, Size = size } );
	}

	/// <summary> 实心三角箭头 + 短尾杆，朝向 Dir；加色批次下与霓虹风格一致 </summary>
	void DrawEdgeArrows()
	{
		for ( int i = 0; i < _edgeArrows.Count; i++ )
		{
			var r = _edgeArrows[i];
			var d = new Vector3( r.Dir.x, r.Dir.y, 0f );
			var perp = new Vector3( -r.Dir.y, r.Dir.x, 0f );
			var len = r.Size;
			var halfW = len * 0.5f;

			var tip = r.Pos + d * len;
			var b1 = r.Pos + perp * halfW;
			var b2 = r.Pos - perp * halfW;

			AddTri( _tris, b1, b2, tip, r.Color * 0.9f );

			// 尾杆：从箭头向屏内延伸一小段，提示"方向"而非"位置点"
			var t0 = r.Pos - d * len * 0.6f;
			_lines.AddVertex( new Vertex( t0, r.Color * 0.5f ) );
			_lines.AddVertex( new Vertex( r.Pos, r.Color * 0.5f ) );
		}
		_edgeArrows.Clear();
	}

	// ---- CPU 粒子（M3 表现力）：走同一霓虹加色批次，风格统一、零资产依赖 ----

	/// <summary> 瞄准箭头（v0.7.0.0）：本机球外缘的实心小三角，指向光标（分裂/吐孢子方向）。
	/// agar 式贴着球缘外侧，白色半透明；尺寸随球半径微缩放，不随球转 </summary>
	void WriteAimArrow( Ball ball )
	{
		var dir = ball.AimDir;
		var c = ball.WorldPosition;
		var r = ball.Radius;

		var len = Math.Clamp( r * 0.22f, 18f, 64f );   // 箭头长度随球径微缩放
		var halfW = len * 0.42f;
		var gap = r * 1.04f;

		var dir3 = new Vector3( dir.x, dir.y, 0f );
		var perp = new Vector3( -dir.y, dir.x, 0f );

		var baseC = c + dir3 * gap;
		var tip = c + dir3 * ( gap + len );
		var p1 = baseC + perp * halfW;
		var p2 = baseC - perp * halfW;

		AddTri( _tris, p1, p2, tip, new Color( 1f, 1f, 1f, 0.7f ) );
	}

	/// <summary> 单个实心三角 </summary>
	void AddTri( SceneDynamicObject batch, Vector3 a, Vector3 b, Vector3 c, Color color )
	{
		batch.AddVertex( new Vertex( a, color ) );
		batch.AddVertex( new Vertex( b, color ) );
		batch.AddVertex( new Vertex( c, color ) );
	}

	sealed class Particle
	{
		public Vector3 Pos;
		public Vector3 Vel;
		public float Life;
		public float MaxLife;
		public float Size;
		public Color Color;
	}

	readonly List<Particle> _particles = new();

	/// <summary> 爆裂（死亡/撞刺）：中大粒子径向飞散 </summary>
	public static void Pop( Vector3 pos, int colorIndex, float radius )
	{
		Spawn( pos, colorIndex, 26, 90f + radius * 0.9f, 0.45f, 0.9f, 3f, 9f );
	}

	/// <summary> 火花（吃食物）：几颗小碎屑一闪 </summary>
	public static void Spark( Vector3 pos, int colorIndex )
	{
		Spawn( pos, colorIndex, 5, 100f, 0.2f, 0.4f, 2f, 4f );
	}

	/// <summary> 火花（自定义色，道具拾取用）：种类色不在调色板里，直接给 Color </summary>
	public static void Spark( Vector3 pos, Color color )
	{
		SpawnColored( pos, color, 10, 140f, 0.25f, 0.5f, 2f, 5f );
	}

	/// <summary> 喷发（分裂/吐孢子生成瞬间） </summary>
	public static void Puff( Vector3 pos, int colorIndex )
	{
		Spawn( pos, colorIndex, 8, 150f, 0.25f, 0.5f, 2f, 5f );
	}

	static void Spawn( Vector3 pos, int colorIndex, int count, float maxSpeed, float minLife, float maxLife, float minSize, float maxSize )
	{
		SpawnColored( pos, GameConfig.Palette[Math.Clamp( colorIndex, 0, GameConfig.Palette.Length - 1 )],
			count, maxSpeed, minLife, maxLife, minSize, maxSize );
	}

	static void SpawnColored( Vector3 pos, Color color, int count, float maxSpeed, float minLife, float maxLife, float minSize, float maxSize )
	{
		var inst = Instance;
		if ( inst is null || !inst.IsValid() ) return;

		for ( int i = 0; i < count; i++ )
		{
			if ( inst._particles.Count >= GameConfig.FxMaxParticles )
				inst._particles.RemoveAt( 0 );   // 挤掉最老的，保帧率优先

			var a = Game.Random.Float( 0f, MathF.PI * 2f );
			var sp = maxSpeed * Game.Random.Float( 0.35f, 1f );
			var life = Game.Random.Float( minLife, maxLife );
			inst._particles.Add( new Particle
			{
				Pos = pos + new Vector3( Game.Random.Float( -6f, 6f ), Game.Random.Float( -6f, 6f ), 0f ),
				Vel = new Vector3( MathF.Cos( a ) * sp, MathF.Sin( a ) * sp, 0f ),
				Life = life,
				MaxLife = life,
				Size = Game.Random.Float( minSize, maxSize ),
				Color = color,
			} );
		}
	}

	void UpdateParticles()
	{
		var dt = Time.Delta;
		for ( int i = _particles.Count - 1; i >= 0; i-- )
		{
			var p = _particles[i];
			p.Life -= dt;
			if ( p.Life <= 0f )
			{
				_particles.RemoveAt( i );
				continue;
			}
			p.Pos += p.Vel * dt;
			p.Vel *= MathF.Exp( -3.5f * dt );   // 阻尼，飞散后减速悬浮消散
		}
	}

	void DrawParticles()
	{
		foreach ( var p in _particles )
		{
			var a = p.Life / p.MaxLife;
			AddDisc( _tris, p.Pos, p.Size * ( 0.5f + a * 0.5f ), p.Color * ( 0.25f + a * 0.75f ), 1.2f, 6 );
		}
	}

	void WriteBall( CircleroyaleGame game, Ball ball )
	{
		var c = ball.WorldPosition;
		var r = ball.Radius;
		var color = ball.NeonColor;

		// 出生保护：整球一闪一闪（用户定稿）——方波闪烁，暗相保留 35% 不至于看不见
		var blink = 1f;
		if ( ball.IsProtected )
			blink = MathF.Sin( Time.Now * GameConfig.SpawnBlinkHz * MathF.PI * 2f ) > 0f ? 1f : 0.35f;

		// 内芯：实心圆背景填充，颜色取自玩家头像主色（bot 用霓虹配色）。
		// z 抬高保证盖在网格之上、外环之下
		AddDisc( _tris, c, r * 0.92f, ball.BackgroundColor * ( ball.IsProtected ? 0.25f * blink : 0.45f ), 1f, GameConfig.DiscSegments );

		// 外环：霓虹线圈
		AddRing( _lines, c, r, color * 0.9f * ( ball.IsProtected ? blink : 1f ), GameConfig.RingSegments );

		// 护盾虚环：出生保护 = 闪烁亮环（v0.6.2.0 起分身不再护体，护体暗环移除）
		if ( ball.IsProtected )
		{
			DrawShieldRing( c, r, color * 0.5f * blink );
		}
	}

	void DrawShieldRing( Vector3 c, float r, Color shield )
	{
		var r2 = r * 1.25f;
		var seg = GameConfig.RingSegments;
		for ( int i = 0; i < seg; i += 2 )
		{
			var a0 = i * MathF.PI * 2f / seg;
			var a1 = ( i + 1 ) * MathF.PI * 2f / seg;
			_lines.AddVertex( new Vertex( RingPoint( c, r2, a0 ), shield ) );
			_lines.AddVertex( new Vertex( RingPoint( c, r2, a1 ), shield ) );
		}
	}

	/// <summary> 道具菱形：旋转的正方形（实心填充 + 亮描边） </summary>
	void WriteDiamond( Vector3 c, float r, float spin, Color color )
	{
		var pts = new Vector3[4];
		for ( int i = 0; i < 4; i++ )
		{
			var a = spin + i * MathF.PI * 0.5f;
			pts[i] = c + new Vector3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, 1.2f );
		}

		for ( int i = 0; i < 4; i++ )
		{
			AddTri( _tris, c + Vector3.Up * 1.2f, pts[i], pts[( i + 1 ) % 4], color * 0.35f );
			_lines.AddVertex( new Vertex( pts[i], color ) );
			_lines.AddVertex( new Vertex( pts[( i + 1 ) % 4], color ) );
		}
	}

	/// <summary> 道具十字芯（磁铁）：两条交叉线 </summary>
	void WriteCross( Vector3 c, float r, Color color )
	{
		_lines.AddVertex( new Vertex( c + new Vector3( -r, -r, 1.4f ), color ) );
		_lines.AddVertex( new Vertex( c + new Vector3( r, r, 1.4f ), color ) );
		_lines.AddVertex( new Vertex( c + new Vector3( -r, r, 1.4f ), color ) );
		_lines.AddVertex( new Vertex( c + new Vector3( r, -r, 1.4f ), color ) );
	}

	/// <summary> 绿刺星形：12 角内尖星，描边 + 低透明度填充（固定相位不旋转） </summary>
	void WriteStar( Vector3 c, float r, Color edge, Color fill )
	{
		const int points = 12;
		const float inner = 0.55f;
		var n = points * 2;
		var center = c + Vector3.Up * 0.85f;
		var pts = new Vector3[n];
		for ( int i = 0; i < n; i++ )
		{
			var rr = ( i % 2 == 0 ) ? r : r * inner;
			var a = i * MathF.PI * 2f / n + 0.26f;
			pts[i] = c + new Vector3( MathF.Cos( a ) * rr, MathF.Sin( a ) * rr, 0.85f );
		}

		for ( int i = 0; i < n; i++ )
		{
			var p2 = pts[( i + 1 ) % n];
			_tris.AddVertex( new Vertex( center, fill ) );
			_tris.AddVertex( new Vertex( pts[i], fill ) );
			_tris.AddVertex( new Vertex( p2, fill ) );

			_lines.AddVertex( new Vertex( pts[i], edge ) );
			_lines.AddVertex( new Vertex( p2, edge ) );
		}
	}

	static Vector3 RingPoint( Vector3 c, float r, float a )
	{
		return c + new Vector3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, 2f );
	}

	/// <summary> 霓虹圆环（线段批）：球外环 / 分身外环 / 孢子描边公用 </summary>
	static void AddRing( SceneDynamicObject dyno, Vector3 c, float r, Color color, int segments )
	{
		segments = Math.Max( 3, segments );
		var prev = RingPoint( c, r, 0f );
		for ( int i = 1; i <= segments; i++ )
		{
			var p = RingPoint( c, r, i * MathF.PI * 2f / segments );
			dyno.AddVertex( new Vertex( prev, color ) );
			dyno.AddVertex( new Vertex( p, color ) );
			prev = p;
		}
	}

	static void AddDisc( SceneDynamicObject dyno, Vector3 c, float r, Color color, float z, int segments )
	{
		segments = Math.Max( 3, segments );
		var center = c + Vector3.Up * z;
		var prev = c + new Vector3( r, 0f, z );
		for ( int i = 1; i <= segments; i++ )
		{
			var p = RingPoint( c, r, i * MathF.PI * 2f / segments );
			p.z = z;
			dyno.AddVertex( new Vertex( center, color ) );
			dyno.AddVertex( new Vertex( prev, color ) );
			dyno.AddVertex( new Vertex( p, color ) );
			prev = p;
		}
	}
}