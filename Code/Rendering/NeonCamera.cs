using System;

/// <summary>
/// 俯视跟随相机：正交投影、指数平滑跟随本地球；视口高度随质量增大（镜头拉远）。
/// 相机朝向固定垂直向下，鼠标→世界方向的映射见 BallInput。
/// </summary>
public sealed class NeonCamera : Component
{
	const float FollowSpeed = 6f;
	const float ZoomSpeed = 2.5f;

	/// <summary> 由 CircleroyaleGame 注入（避免每帧 GetComponent） </summary>
	public CameraComponent Cam { get; set; }

	Ball _target;
	Ball _spectate;      // 死亡观战目标（本地球活着时恒为 null）
	Vector3 _focus;
	float _shakeAmp;     // 屏幕震动幅度（击杀/放技能触发，指数衰减，v0.7.7.0）
	Bloom _bloom;        // 泛光组件（cr_bloom convar 即时开关，v0.7.8.15）

	/// <summary> 触发屏幕轻微震动（击杀连播/放技能的高光反馈；幅度按事件定，自动指数衰减） </summary>
	public void Shake( float amplitude ) => _shakeAmp = MathF.Max( _shakeAmp, amplitude );

	protected override void OnStart()
	{
		base.OnStart();

		// 立即摆好默认俯视姿态（焦点=场地中心）。没有本机球之前 OnUpdate 会因
		// !_target.IsValid() 直接 return——相机保持引擎默认姿态（原点水平看），
		// 整个场地平面侧视塌成一条线：主菜单背景实测就是这样（客户端竞态期同样中招）
		ApplyPose();
		if ( Cam.IsValid() )
		{
			Cam.OrthographicHeight = GameConfig.CameraBaseView;
			_bloom = Cam.GetComponent<Bloom>();
		}
	}

	public void SetTarget( Ball ball )
	{
		if ( _target == ball ) return;   // 同一颗球：不重复 SnapTo（扫描会反复断言）
		_target = ball;
		SnapTo();
	}

	public void SnapTo()
	{
		if ( !_target.IsValid() ) return;
		_focus = _target.WorldPosition;
		_focus.z = 0f;
		ApplyPose();
		if ( Cam.IsValid() ) Cam.OrthographicHeight = ViewForMass( _target.Mass );
	}

	protected override void OnUpdate()
	{
		base.OnUpdate();

		// 泛光开关（v0.7.8.15）：控制台 cr_bloom 0/1 即时生效（幂等赋值，无抖动）；
		// 热重载换程序集后缓存失效 → 惰性重取
		if ( Cam.IsValid() )
		{
			if ( !_bloom.IsValid() ) _bloom = Cam.GetComponent<Bloom>();
			if ( _bloom.IsValid() ) _bloom.Enabled = GameConfig.ConvarBloom;
		}

		// 自愈：热重载会把场景组件换成新程序集对象，缓存的 _target 可能变成失效引用——
		// OnUpdate 首行早退会让相机永久冻结（实测：客户端复活着、分裂着，镜头僵死不跟）。
		// 从游戏系统重新认领本机球；连本机球都没有也保持俯视姿态，绝不僵死
		if ( !_target.IsValid() )
		{
			var local = CircleroyaleGame.Current?.LocalBall;
			if ( local.IsValid() )
			{
				SetTarget( local );
			}
			else
			{
				// 菜单背景演示赛（v0.6.3.0）：没有本机球时观战全场最大身体的 bot。
				// 粘滞同死亡观战：当前对象还活着且挑战者没领先 30% → 不换镜头
				var g = CircleroyaleGame.Current;
				var best = g?.TopMassBall();

				if ( _spectate.IsValid() && _spectate.Alive
					&& best.IsValid() && best != _spectate && g is not null )
				{
					if ( g.TotalMassFor( best ) <= g.TotalMassFor( _spectate ) * GameConfig.SpectateOvertakeRatio )
						best = _spectate;
				}

				if ( best.IsValid() )
				{
					_spectate = best;
					var tp = 1f - MathF.Exp( -FollowSpeed * Time.Delta );
					var fp = g.FocusPositionOf( best, out var fm );
					_focus = Vector3.Lerp( _focus, fp, tp );
					_focus.z = 0f;
					ApplyPose();
					UpdateTagPositions( g );
					if ( Cam.IsValid() )
					{
						Cam.OrthographicHeight = MathX.Lerp( Cam.OrthographicHeight, ViewForMass( fm ),
							1f - MathF.Exp( -ZoomSpeed * Time.Delta ) );
						UpdateTagPositions( g );
					}
				}
				else
				{
					ApplyPose();
				}
				return;
			}
		}

		// 死亡观战（M4）：本地球阵亡后镜头跟住当前榜首，重生瞬间弹回自己
		var cur = SpectateTarget();

		// 视角跟随最大的分身（v0.6.2.0 用户定稿）：焦点与变焦基准都取
		// "主球 + 自己分身"中质量最大的身体
		var game = CircleroyaleGame.Current;
		Vector3 focusPos = cur.WorldPosition;
		float followMass = cur.Mass;
		if ( game is not null )
			focusPos = game.FocusPositionOf( cur, out followMass );

		var t = 1f - MathF.Exp( -FollowSpeed * Time.Delta );
		_focus = Vector3.Lerp( _focus, focusPos, t );
		_focus.z = 0f;
		ApplyPose();
		UpdateTagPositions( game );

		if ( !Cam.IsValid() ) return;
		var tz = 1f - MathF.Exp( -ZoomSpeed * Time.Delta );
		Cam.OrthographicHeight = MathX.Lerp( Cam.OrthographicHeight, ViewWithCells( cur, followMass ), tz );
		UpdateTagPositions( game );   // 变焦也改了相机参数，重投一次保证本帧一致
	}

	/// <summary>
	/// 死亡观战目标：跟全场总质量榜首。带粘滞（v0.6.3.1）——几个 bot 交替领先时
	/// 不再每次反超都切镜头，挑战者须领先 SpectateOvertakeRatio 才接管。
	/// </summary>
	Ball SpectateTarget()
	{
		if ( _target.Alive )
		{
			if ( _spectate.IsValid() )
			{
				_spectate = null;
				SnapTo();   // 重生：镜头瞬移回自己（不从榜首位置长距离滑过来）
			}
			return _target;
		}

		var game = CircleroyaleGame.Current;
		var best = game?.TopMassBall();

		// 粘滞：当前观战对象还活着，且挑战者没领先到阈值 → 不换镜头
		if ( _spectate.IsValid() && _spectate.Alive
			&& best.IsValid() && best != _spectate && game is not null )
		{
			if ( game.TotalMassFor( best ) <= game.TotalMassFor( _spectate ) * GameConfig.SpectateOvertakeRatio )
				return _spectate;
		}

		_spectate = best.IsValid() ? best : _target;
		return _spectate;
	}

	/// <summary>
	/// 视口高度：按"最大身体"质量取基准；有自家分身时拉远到能盖住最远的那个
	/// （分裂后细胞冲出屏幕外就看不见了，取景要把自家分身包进来）。
	/// 客户端匹配用 OwnerSteamId：分身与主球带同一个同步值，双开合成 ID 也能对上。
	/// </summary>
	float ViewWithCells( Ball cur, float baseMass )
	{
		var view = ViewForMass( baseMass );

		var game = CircleroyaleGame.Current;
		if ( game is null ) return view;

		var sid = cur.OwnerSteamId;
		float maxDist = 0f;
		foreach ( var c in game.Cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece || c.OwnerSteamId != sid ) continue;
			maxDist = MathF.Max( maxDist, cur.WorldPosition.Distance( c.DrawPos ) );
		}

		if ( maxDist > 0f )
			view = Math.Clamp( ( cur.Radius + maxDist ) * 2.6f, view, GameConfig.CameraViewMax );

		return view;
	}

	void ApplyPose()
	{
		// 屏幕震动（v0.7.7.0）：每次摆位叠加一个随机方向偏移，幅度指数衰减到零——
		// 摆位本身逐帧重写（不含残量），震完自动归位；名牌投影用同一相机位置，抖动同步一致
		var shake = Vector3.Zero;
		if ( _shakeAmp > 0.1f )
		{
			var a = Game.Random.Float( 0f, MathF.PI * 2f );
			shake = new Vector3( MathF.Cos( a ), MathF.Sin( a ), 0f )
				* _shakeAmp * Game.Random.Float( 0.4f, 1f );
			_shakeAmp *= MathF.Exp( -6f * Time.Delta );
		}

		GameObject.WorldPosition = _focus + Vector3.Up * GameConfig.CameraHeight + shake;
		GameObject.WorldRotation = Rotation.LookAt( Vector3.Down, Vector3.Forward );
	}

	/// <summary> 相机姿态刚写完，立刻让 HUD 用同一份相机数据重投名牌（零帧间差） </summary>
	void UpdateTagPositions( CircleroyaleGame game )
	{
		if ( game is null ) return;
		var ortho = Cam.IsValid() ? Cam.OrthographicHeight : 0f;
		game.Hud?.UpdateTagPositions( game, GameObject.WorldPosition, ortho );
	}

	static float ViewForMass( float mass )
	{
		return Math.Clamp( GameConfig.CameraBaseView * MathF.Sqrt( mass / GameConfig.StartMass ),
			GameConfig.CameraViewMin, GameConfig.CameraViewMax );
	}
}