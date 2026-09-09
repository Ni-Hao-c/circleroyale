using System;

/// <summary>
/// 像素批渲染（v0.7.8.36 整体替换霓虹线框风）：每帧重写各批次顶点，全场实体约 7 个 DrawCall。
/// 贴图批次走 line.shader 的 UV 采样 + 顶点色 tint（D_BLEND=0 普通 alpha）：
/// 球/分身/孢子 = ball_body.png 白模 tint；食物 = food.png 六色条带；野刺/尖刺分身 = spike.png；
/// 刺爆弹幕 = spine.png（按速度旋转）；道具 = powerups.png 图标叠程序化菱形底座。
/// 表情/头像（v0.7.8.40 从 SpriteRenderer 迁入——sprite 通道画在 OverlayWithDepth 批次之前，
/// 会被实心身体 quad 盖住）：bot = face_0..5 按 Mood 选批，真人 = SetAvatar 登记的每 SteamId 贴图批次。
/// 线/三角批次（普通 alpha）：椭圆投影、CPU 粒子方块、瞄准/边缘箭头。
/// 特效入口（Pop/Spark/Puff/Swallow）签名不变，调用方零改动。
/// </summary>
public sealed class NeonRenderer : Component
{
	SceneDynamicObject _shapeLines;   // 线段（普通 alpha）：道具底座描边/边缘箭头尾杆
	SceneDynamicObject _shapeTris;    // 三角形（普通 alpha）：投影/粒子/瞄准箭头/菱形填充
	SceneDynamicObject _bodies;       // ball_body.png：球/分身/孢子身体（顶点色 tint）
	SceneDynamicObject _food;         // food.png：糖豆（UV 取色格）
	SceneDynamicObject _spikes;       // spike.png：野刺 + 尖刺分身
	SceneDynamicObject _spines;       // spine.png：刺爆弹幕（旋转）
	SceneDynamicObject _icons;        // powerups.png：道具图标（UV 取格）
	SceneDynamicObject _rings;        // shield_ring.png：护盾 buff 虚线环（白模 tint，绕球慢转，v0.7.8.79）
	SceneDynamicObject[] _faces;      // face_0..5：bot 表情（Mood 选批，v0.7.8.40）

	/// <summary> ball_body.png 里球体内容占画布比例（process.py fit margin 0.06）——贴图直径→世界直径换算用 </summary>
	const float BodyContentFraction = 0.9375f;

	/// <summary> face_N.png 里脸部内容占画布比例——头像直径→世界直径换算用 </summary>
	const float FaceContentFraction = 0.9f;

	sealed class AvatarBatch
	{
		public SceneDynamicObject Dyno;
		public Texture Tex;
	}

	/// <summary> 真人 Steam 头像批次（每 SteamId 一个独立贴图批次） </summary>
	readonly Dictionary<long, AvatarBatch> _avatars = new();

	/// <summary> 头像登记表（Ball.SetAvatar 写，OnUpdate 消费；渲染器未就绪/热重载期间在此待命） </summary>
	static readonly Dictionary<long, Texture> _pendingAvatars = new();

	/// <summary> 全局引用（特效生成入口 NeonRenderer.Pop/Spark/Puff 走静态调用） </summary>
	public static NeonRenderer Instance { get; private set; }

	protected override void OnStart()
	{
		base.OnStart();

		Instance = this;
		_shapeLines = CreateShapeBatch();
		_shapeTris = CreateShapeBatch();
		_bodies = CreateTexBatch( "ui/pixel/ball_body.png" );
		_food = CreateTexBatch( "ui/pixel/food.png" );
		_spikes = CreateTexBatch( "ui/pixel/spike.png" );
		_spines = CreateTexBatch( "ui/pixel/spine.png" );
		_icons = CreateTexBatch( "ui/pixel/powerups.png" );
		_rings = CreateTexBatch( "ui/pixel/shield_ring.png" );
		_faces = new SceneDynamicObject[6];
		for ( int i = 0; i < 6; i++ )
			_faces[i] = CreateTexBatch( $"ui/pixel/face_{i}.png", topmost: true );   // 0开心 1兴奋 2惊恐 3眩晕 4瞌睡 5得意
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();
		if ( Instance == this ) Instance = null;
	}

	SceneDynamicObject CreateShapeBatch( bool topmost = false )
	{
		var dyno = new SceneDynamicObject( Scene.SceneWorld );
		dyno.Material = LineMaterial.Create();
		dyno.Flags.CastShadows = false;
		// 头像/表情批次放 OverlayWithoutDepth（比 OverlayWithDepth 晚一段绘制，无深度测试）：
		// OverlayWithDepth 内部对各 dyno 的排序不受顶点 z 控制（实测表情被 z=1.0 的身体盖住）
		dyno.RenderLayer = topmost ? SceneRenderLayer.OverlayWithoutDepth : SceneRenderLayer.OverlayWithDepth;
		dyno.Attributes.SetCombo( "D_BLEND", 0 );   // 普通 alpha 混合（像素风无加色发光）
		return dyno;
	}

	sealed class TexBatch
	{
		public SceneDynamicObject Dyno;
		public string Path;
		public Texture Tex;
	}

	/// <summary> 贴图批次登记（路径留底——热重载重建世界时 Texture.Load 可能瞬时失败，
	/// OnUpdate 里 1s 节流补载自愈，否则整场渲染成错误红纹理，实测） </summary>
	readonly List<TexBatch> _texBatches = new();

	TimeSince _sinceTexHeal = 1f;

	SceneDynamicObject CreateTexBatch( string texturePath, bool topmost = false )
	{
		var dyno = CreateShapeBatch( topmost );

		var batch = new TexBatch { Dyno = dyno, Path = texturePath };
		_texBatches.Add( batch );
		LoadTex( batch );

		return dyno;
	}

	void LoadTex( TexBatch b )
	{
		b.Tex = Texture.Load( b.Path, false );
		if ( b.Tex is null )
			Log.Warning( $"[pixel] texture not ready: {b.Path} (1s 后自动重试)" );

		b.Dyno.Material = MakeTexMaterial( b.Tex );   // SceneDynamicObject.Material 只写，先备好材质再赋值
	}

	/// <summary> 贴图批次材质：独立实例 + point 采样（像素贴图放大必须最近邻，双线性会把像素颗粒糊掉）；
	/// SamplerIndex 是 line.shader 的材质属性（Bindless::GetSampler），失败也不致命。
	/// static：TerritoryRenderer 的旗/城堡贴图批次同款复用 </summary>
	public static Material MakeTexMaterial( Texture tex )
	{
		var mat = LineMaterial.Create();
		try { mat.Set( "SamplerIndex", 0 ); } catch { }
		if ( tex is not null ) mat.Set( "g_tColor", tex );
		return mat;
	}

	/// <summary> 真人头像登记（Ball 各端自取 Steam 头像后调；每 SteamId 一个独立贴图批次） </summary>
	public static void SetAvatar( long steamId, Texture tex )
	{
		if ( steamId == 0 || tex is null ) return;
		_pendingAvatars[steamId] = tex;
	}

	/// <summary> 消化头像登记：缺批次补建、贴图变了换材质（渲染器未就绪时的登记留到下一帧生效） </summary>
	void SyncAvatars()
	{
		foreach ( var kv in _pendingAvatars )
		{
			if ( _avatars.TryGetValue( kv.Key, out var av ) && av.Tex == kv.Value ) continue;

			if ( av is null )
			{
				av = new AvatarBatch { Dyno = CreateShapeBatch( topmost: true ) };
				_avatars[kv.Key] = av;
			}
			av.Dyno.Material = MakeTexMaterial( kv.Value );
			av.Tex = kv.Value;
		}
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !Game.IsPlaying ) return;

		var game = CircleroyaleGame.Current;
		if ( game is null || _shapeTris is null || _bodies is null || _faces is null ) return;

		_shapeLines.Clear();
		_shapeLines.Init( Graphics.PrimitiveType.Lines );
		_shapeTris.Clear();
		_shapeTris.Init( Graphics.PrimitiveType.Triangles );
		_bodies.Clear();
		_bodies.Init( Graphics.PrimitiveType.Triangles );
		_food.Clear();
		_food.Init( Graphics.PrimitiveType.Triangles );
		_spikes.Clear();
		_spikes.Init( Graphics.PrimitiveType.Triangles );
		_spines.Clear();
		_spines.Init( Graphics.PrimitiveType.Triangles );
		_icons.Clear();
		_icons.Init( Graphics.PrimitiveType.Triangles );
		_rings.Clear();
		_rings.Init( Graphics.PrimitiveType.Triangles );

		SyncAvatars();

		// 贴图自愈（1s 节流）：热重载/资产刷新期间加载失败的批次补载
		if ( _sinceTexHeal > 1f )
		{
			_sinceTexHeal = 0;
			foreach ( var b in _texBatches )
			{
				if ( b.Tex is not null ) continue;
				LoadTex( b );
			}
		}

		foreach ( var f in _faces )
		{
			f.Clear();
			f.Init( Graphics.PrimitiveType.Triangles );
		}
		foreach ( var av in _avatars.Values )
		{
			av.Dyno.Clear();
			av.Dyno.Init( Graphics.PrimitiveType.Triangles );
		}

		UpdateParticles();
		UpdateWallPulses( Time.Delta );

		// ---- 球：椭圆投影 + tint 白模 + 出生保护闪烁 ----
		foreach ( var ball in game.Balls )
		{
			if ( !ball.IsValid() || !ball.Alive ) continue;

			var c = ball.WorldPosition;
			var r = ball.Radius;

			// 投影画在脚下：屏幕下方 = 世界 -X（相机 90° 滚转），横长竖扁
			AddShadowEllipse( c + new Vector3( -r * 0.12f, 0f, 0.25f ), r * 0.32f, r * 0.72f );

			// 出生保护：整球方波闪烁（暗相 35%），闪烁在身体 tint 上，头像保持可见
			var blink = 1f;
			if ( ball.IsProtected )
				blink = MathF.Sin( Time.Now * GameConfig.SpawnBlinkHz * MathF.PI * 2f ) > 0f ? 1f : 0.35f;
			var tint = new Color( ball.NeonColor.r, ball.NeonColor.g, ball.NeonColor.b, blink );

			AddQuad( _bodies, c, r / BodyContentFraction, r / BodyContentFraction, 0f,
				new Vector4( 0f, 0f, 1f, 1f ), tint, 1.0f );

			// 护盾 buff（M6.5）：白模虚线环染道具蓝，绕球慢转（v0.7.8.79 接 shield_ring.png）
			if ( ball.HasBuff( PowerUpManager.Kind.Shield ) )
				AddQuad( _rings, c, r * 1.32f, r * 1.32f, Time.Now * 0.7f,
					new Vector4( 0f, 0f, 1f, 1f ), PowerUpManager.ColorOf( (byte)PowerUpManager.Kind.Shield ), 1.05f );

			// 头像（v0.7.8.40）：bot=表情脸（Mood 选批），真人=Steam 头像；直径=半径，画在身体之上
			DrawAvatar( c, r, ball.IsBot, ball.Mood, ball.OwnerSteamId, 1.1f );

			// 瞄准箭头（v0.7.0.0）：本机球外缘白色小三角指向光标，仅对局中显示
			if ( game.IsMatchStarted && ball == game.LocalBall && ball.AimDir.Length > 0.01f )
				WriteAimArrow( ball );
		}

		// ---- 分身/孢子/尖刺分身（纯数据实体）：tint 白模 / 刺贴图，出生弹跳保留 ----
		foreach ( var c in game.Cells )
		{
			var color = GameConfig.Palette[Math.Clamp( c.ColorIndex, 0, GameConfig.Palette.Length - 1 )];
			var pos = new Vector3( c.DrawPos.x, c.DrawPos.y, 0f );
			var r = c.Radius;

			// 出生弹跳（v0.7.8.28 juice）：新 id 首帧记时刻，0.3s 内缩放脉冲。客户端镜像用"首见 id"判定，双端统一
			if ( !_seenCells.Contains( c.Id ) )
			{
				_seenCells.Add( c.Id );
				_cellBorn[c.Id] = 0;
				if ( _seenCells.Count > 512 ) PruneCellFx( game );
			}
			if ( _cellBorn.TryGetValue( c.Id, out var born ) && born < 0.3f )
			{
				var phase = born / 0.3f;
				r *= phase < 0.3f
					? MathX.Lerp( 0.55f, 1.14f, phase / 0.3f )
					: MathX.Lerp( 1.14f, 1f, ( phase - 0.3f ) / 0.7f );
			}

			if ( c.PieceKind == CellPiece.Kind.SpikeMinion )
			{
				// 尖刺分身（v0.7.4.0）：刺族贴图（紫色=是刺），尺寸不小于野刺观感
				var sr = MathF.Max( r, 40f );
				AddQuad( _spikes, pos, sr, sr, 0f, new Vector4( 0f, 0f, 1f, 1f ), Color.White, 0.65f );
			}
			else
			{
				// 分身/孢子：同一张白模 tint（孢子略缩，观感=小糖豆团）
				var scale = c.PieceKind == CellPiece.Kind.EjectedMass ? 0.85f : 1f;
				var half = r * scale / BodyContentFraction;
				var z = c.PieceKind == CellPiece.Kind.EjectedMass ? 0.8f : 0.9f;
				AddQuad( _bodies, pos, half, half, 0f, new Vector4( 0f, 0f, 1f, 1f ), color, z );

				// 分身头像（v0.7.8.40 自 PieceAvatarLayer 并入）：只给 SplitPiece，直径=分身半径
				if ( c.PieceKind == CellPiece.Kind.SplitPiece )
				{
					var owner = game.FindBallBySteamId( c.OwnerSteamId );
					DrawAvatar( pos, r, owner.IsValid() && owner.IsBot,
						owner.IsValid() ? owner.Mood : (byte)0, c.OwnerSteamId, 0.95f );
				}
			}
		}

		// ---- 野刺：喂食长大后直接缩放贴图 ----
		var spikes = game.Spikes;
		if ( spikes is not null )
		{
			for ( int i = 0; i < spikes.Length; i++ )
			{
				var grow = 1f + GameConfig.SpikeGrowVisual * Math.Clamp( game.SpikeFedAt( i ) / GameConfig.SpikeGrowTarget, 0f, 1f );
				var half = GameConfig.SpikeRadius * grow;
				AddQuad( _spikes, new Vector3( spikes[i].x, spikes[i].y, 0f ), half, half, 0f,
					new Vector4( 0f, 0f, 1f, 1f ), Color.White, 0.6f );
			}
		}

		// ---- 刺爆弹幕（v0.7.8.24）：刺弹贴图按速度方向旋转，随寿命淡出 ----
		foreach ( var sp in game.Spines )
		{
			var remain = 1f - Math.Clamp( sp.SinceSpawn / GameConfig.SpineLife, 0f, 1f );
			var dir = sp.Vel;
			if ( dir.Length < 0.001f ) continue;
			var rot = MathF.Atan2( dir.y, dir.x );
			var tint = new Color( 1f, 1f, 1f, 0.35f + 0.65f * remain );
			AddQuad( _spines, new Vector3( sp.Pos.x, sp.Pos.y, 0f ), 17f, 17f, rot,
				new Vector4( 0f, 0f, 1f, 1f ), tint, 1.2f );
		}

		// ---- 道具（v0.7.3.0）：旋转菱形底座（普通 alpha）+ 种类图标贴图 ----
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

				var k = Math.Clamp( s.Kind % 5, 0, 4 );
				var uv = new Vector4( k / 5f, 0f, ( k + 1 ) / 5f, 1f );
				AddQuad( _icons, pos + Vector3.Up * 1.3f, r * 1.35f, r * 1.35f, MathF.PI * 0.5f, uv, Color.White, 1.3f );
			}
		}

		// ---- 食物：糖豆贴图按色号取 UV 格（视口剔除保留） ----
		var foods = game.Food?.Foods;
		if ( foods is not null )
		{
			// 视口剔除（v0.6.3.0）：4140 颗食物只有 ~20% 在画面里。
			// 宽高比取 max(2.4, Screen.Aspect)：普通屏维持老余量，超宽屏（32:9）不再缺左右食物
			var fr = GameConfig.FoodRadius;
			var cullCx = Scene.Camera.WorldPosition.x;
			var cullCy = Scene.Camera.WorldPosition.y;
			var cullAspect = MathF.Max( 2.4f, Screen.Height > 0f ? Screen.Width / Screen.Height : 2.4f );
			var cullHw = Scene.Camera.OrthographicHeight * 0.5f * cullAspect + 64f;
			var cullHh = Scene.Camera.OrthographicHeight * 0.5f + 64f;

			for ( int i = 0; i < foods.Length; i++ )
			{
				if ( !foods[i].Alive ) continue;

				var fx = foods[i].Pos.x;
				var fy = foods[i].Pos.y;
				if ( MathF.Abs( fx - cullCx ) > cullHw || MathF.Abs( fy - cullCy ) > cullHh ) continue;

				var k = foods[i].ColorIndex % 6;
				var uv = new Vector4( k / 6f, 0f, ( k + 1 ) / 6f, 1f );
				AddQuad( _food, new Vector3( fx, fy, 0f ), fr / 0.95f, fr / 0.95f, 0f, uv, Color.White, 0.55f );
			}
		}

		DrawParticles();
		DrawWallPulses();
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

	/// <summary> 实心三角箭头 + 短尾杆，朝向 Dir </summary>
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

			AddTri( _shapeTris, b1, b2, tip, r.Color * 0.9f );

			// 尾杆：从箭头向屏内延伸一小段，提示"方向"而非"位置点"
			var t0 = r.Pos - d * len * 0.6f;
			_shapeLines.AddVertex( new Vertex( t0, r.Color * 0.5f ) );
			_shapeLines.AddVertex( new Vertex( r.Pos, r.Color * 0.5f ) );
		}
		_edgeArrows.Clear();
	}

	// ---- CPU 粒子（M3 表现力）：普通 alpha 批次方块粒子 ----

	/// <summary> 瞄准箭头（v0.7.0.0）：本机球外缘的白色实心小三角，指向光标（分裂/吐孢子方向） </summary>
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

		AddTri( _shapeTris, p1, p2, tip, new Color( 1f, 1f, 1f, 0.55f ) );
	}

	/// <summary> 单个实心三角 </summary>
	void AddTri( SceneDynamicObject batch, Vector3 a, Vector3 b, Vector3 c, Color color )
	{
		batch.AddVertex( new Vertex( a, color ) );
		batch.AddVertex( new Vertex( b, color ) );
		batch.AddVertex( new Vertex( c, color ) );
	}

	/// <summary> 贴图四边形（两个三角）：center 中心、half 半宽/半高、rot 弧度、uv=(x0,y0,x1,y1) </summary>
	static void AddQuad( SceneDynamicObject b, Vector3 center, float halfX, float halfY, float rot, Vector4 uv, Color color, float z )
	{
		var cos = MathF.Cos( rot );
		var sin = MathF.Sin( rot );
		var rx = cos * halfX; var ry = sin * halfX;      // right 轴
		var ux = -sin * halfY; var uy = cos * halfY;     // up 轴

		var v0 = new Vertex( new Vector3( center.x - rx - ux, center.y - ry - uy, z ), new Vector4( uv.x, uv.y, 0f, 0f ), color );
		var v1 = new Vertex( new Vector3( center.x + rx - ux, center.y + ry - uy, z ), new Vector4( uv.z, uv.y, 0f, 0f ), color );
		var v2 = new Vertex( new Vector3( center.x + rx + ux, center.y + ry + uy, z ), new Vector4( uv.z, uv.w, 0f, 0f ), color );
		var v3 = new Vertex( new Vector3( center.x - rx + ux, center.y - ry + uy, z ), new Vector4( uv.x, uv.w, 0f, 0f ), color );

		b.AddVertex( v0 ); b.AddVertex( v1 ); b.AddVertex( v2 );
		b.AddVertex( v0 ); b.AddVertex( v2 ); b.AddVertex( v3 );
	}

	/// <summary> 头像/表情：直径=球半径（占直径 50%，用户指定）；bot 从 face_N 批次取（Mood 选格），
	/// 真人从 Steam 头像批次取。批次在 OverlayWithoutDepth 层，天然画在身体（OverlayWithDepth）之上。
	/// rot=π/2：相机 90° 滚转下贴图 u 轴朝屏幕竖直，不转的话脸是横躺的（食物/球体等圆形看不出来） </summary>
	void DrawAvatar( Vector3 c, float radius, bool bot, byte mood, long steamId, float z )
	{
		var half = radius * 0.5f / FaceContentFraction;

		if ( bot )
		{
			var k = Math.Clamp( (int)mood, 0, 5 );
			AddQuad( _faces[k], c, half, half, MathF.PI * 0.5f, new Vector4( 0f, 0f, 1f, 1f ), Color.White, z );
		}
		else if ( _avatars.TryGetValue( steamId, out var av ) )
		{
			AddQuad( av.Dyno, c, half, half, MathF.PI * 0.5f, new Vector4( 0f, 0f, 1f, 1f ), Color.White, z );
		}
	}

	/// <summary> 椭圆投影（世界 XY 平面：ax=世界 X 半轴=屏幕竖向，ay=世界 Y 半轴=屏幕横向） </summary>
	void AddShadowEllipse( Vector3 center, float ax, float ay, int segments = 14 )
	{
		segments = Math.Max( 6, segments );
		var prev = center + new Vector3( ax, 0f, 0f );
		for ( int i = 1; i <= segments; i++ )
		{
			var a = i * MathF.PI * 2f / segments;
			var p = center + new Vector3( MathF.Cos( a ) * ax, MathF.Sin( a ) * ay, 0f );
			AddTri( _shapeTris, center, prev, p, new Color( 0.59f, 0.67f, 0.82f, 0.30f ) );
			prev = p;
		}
	}

	sealed class Particle
	{
		public Vector3 Pos;
		public Vector3 Vel;
		public float Life;
		public float MaxLife;
		public float Size;
		public Color Color;
		public Vector3 Target;   // Seek=true：吸引目标（吞球滑进吃者嘴里）
		public bool Seek;
	}

	readonly List<Particle> _particles = new();
	readonly HashSet<int> _seenCells = new();                    // 出生弹跳：见过的分身/孢子 id（客户端镜像不带 SinceSpawn，用"首见"双端统一判定）
	readonly Dictionary<int, TimeSince> _cellBorn = new();

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

	/// <summary> 吞球滑入（v0.7.8.28 juice）：被吃者缩成一团、加速滑进吃者嘴里，尺寸随寿命收缩归零 </summary>
	public static void Swallow( Vector3 pos, int colorIndex, Vector3 target, float radius )
	{
		var inst = Instance;
		if ( inst is null || !inst.IsValid() ) return;

		if ( inst._particles.Count >= GameConfig.FxMaxParticles )
			inst._particles.RemoveAt( 0 );

		var life = 0.3f;
		inst._particles.Add( new Particle
		{
			Pos = pos,
			Target = target,
			Seek = true,
			Life = life,
			MaxLife = life,
			Size = MathF.Max( 10f, radius * 0.9f ),
			Color = GameConfig.Palette[Math.Clamp( colorIndex, 0, GameConfig.Palette.Length - 1 )],
		} );
		Spawn( pos, colorIndex, 4, 120f, 0.15f, 0.3f, 2f, 4f );   // 少量碎屑点缀
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
			if ( p.Seek )
			{
				p.Pos = Vector3.Lerp( p.Pos, p.Target, 1f - MathF.Exp( -11f * dt ) );   // 指数吸向吃者嘴
			}
			else
			{
				p.Pos += p.Vel * dt;
				p.Vel *= MathF.Exp( -3.5f * dt );   // 阻尼，飞散后减速悬浮消散
			}
		}
	}

	/// <summary> 方块粒子（像素风）：轴对齐小方块，尺寸/透明度随寿命衰减 </summary>
	void DrawParticles()
	{
		foreach ( var p in _particles )
		{
			var a = p.Life / p.MaxLife;
			var size = p.Size * ( 0.5f + a * 0.5f );
			var col = new Color( p.Color.r, p.Color.g, p.Color.b, ( 0.25f + a * 0.75f ) * 0.9f );
			AddQuad( _shapeTris, p.Pos, size * 0.5f, size * 0.5f, 0f, new Vector4( 0f, 0f, 1f, 1f ), col, 1.4f );
		}
	}

	// ---- 撞墙脉冲（v0.7.8.78）：球被围墙钳住时，接触点一圈小方块向外扩散淡出——"墙挡住了"的反馈 ----

	const float WallPulseLife = 0.4f;

	sealed class WallPulseFx
	{
		public Vector3 Pos;
		public float Age;
		public Color Color;
	}

	readonly List<WallPulseFx> _wallPulses = new();

	/// <summary> 围墙受击脉冲：接触点 + 撞球颜色（程序化方块环；日后可换贴图环） </summary>
	public static void WallPulse( Vector3 pos, Color color )
	{
		var inst = Instance;
		if ( inst is null || !inst.IsValid() ) return;

		if ( inst._wallPulses.Count >= 24 ) inst._wallPulses.RemoveAt( 0 );
		inst._wallPulses.Add( new WallPulseFx { Pos = pos, Age = 0f, Color = color } );
	}

	void UpdateWallPulses( float dt )
	{
		for ( int i = _wallPulses.Count - 1; i >= 0; i-- )
		{
			_wallPulses[i].Age += dt;
			if ( _wallPulses[i].Age >= WallPulseLife ) _wallPulses.RemoveAt( i );
		}
	}

	void DrawWallPulses()
	{
		foreach ( var w in _wallPulses )
		{
			var t = w.Age / WallPulseLife;
			var r = 8f + t * 54f;                              // 半径外扩
			var alpha = ( 1f - t ) * 0.85f;                    // 渐隐
			var size = 8f - t * 3f;                            // 方块随扩散略缩
			var col = new Color( w.Color.r, w.Color.g, w.Color.b, alpha );

			for ( int s = 0; s < 14; s++ )
			{
				var a = s * MathF.PI * 2f / 14f;
				var p = w.Pos + new Vector3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, 0f );
				AddQuad( _shapeTris, p, size * 0.5f, size * 0.5f, 0f, new Vector4( 0f, 0f, 1f, 1f ), col, 1.4f );
			}
		}
	}

	/// <summary> 出生弹跳注册表超限时整体重建（当前活着的 id 重新立册，重置瞬间轻微再弹一次，罕见可接受） </summary>
	void PruneCellFx( CircleroyaleGame game )
	{
		_seenCells.Clear();
		_cellBorn.Clear();
		foreach ( var c in game.Cells )
			_seenCells.Add( c.Id );
	}

	/// <summary> 道具菱形底座：旋转的正方形（半透明填充 + 描边），像素风下作图标的衬底 </summary>
	void WriteDiamond( Vector3 c, float r, float spin, Color color )
	{
		var pts = new Vector3[4];
		for ( int i = 0; i < 4; i++ )
		{
			var a = spin + i * MathF.PI * 0.5f;
			pts[i] = c + new Vector3( MathF.Cos( a ) * r, MathF.Sin( a ) * r, 0.7f );
		}

		for ( int i = 0; i < 4; i++ )
		{
			AddTri( _shapeTris, c + Vector3.Up * 0.7f, pts[i], pts[( i + 1 ) % 4], color * 0.35f );
			_shapeLines.AddVertex( new Vertex( pts[i], color ) );
			_shapeLines.AddVertex( new Vertex( pts[( i + 1 ) % 4], color ) );
		}
	}
}