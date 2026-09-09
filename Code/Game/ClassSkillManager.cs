using System;
using System.Collections.Generic;

/// <summary>
/// 职业技能管理器（M7.4，领土模式专属）：Q 键施放的四职业冷却技能，host 权威。
/// 指挥官=传送到最大队友身边（远程玩家经 ClassTeleport 广播瞬移）；工兵=6 秒大加速（复用 Speed buff）；
/// 护卫=原地锚定尖刺 8 秒（复用尖刺分身碰撞判定）；坦克=+40% 当前质量 10 秒，到期回收、期间禁分裂。
/// 冷却按球 Id 记（真人=本球；bot 各球独立）。客户端冷却为本地估算，
/// host 冷却未好时静默拒绝（无副作用）。坦克回收表持有 Ball 引用，热重载换程序集对象会失效——
/// 届时该次回收静默消失（对局本身也会被热重载打断），可接受的边界。
/// </summary>
public static class ClassSkillManager
{
	sealed class Surge
	{
		public Ball Ball;
		public float Added;
		public TimeSince Since;
	}

	static readonly List<Surge> _surges = new();
	static readonly Dictionary<Guid, TimeSince> _cd = new();   // 球 Id → 上次施放（按球不按 SteamId——bot 的 SteamId 全 0 会全队共享一个冷却）

	public static string NameOf( byte cls ) => cls switch
	{
		0 => "COMMANDER",
		1 => "ENGINEER",
		2 => "GUARD",
		3 => "TANK",
		_ => "",
	};

	/// <summary> HUD 短标（Q 钮就绪态显示） </summary>
	public static string TagOf( byte cls ) => cls switch
	{
		0 => "CMD",
		1 => "ENG",
		2 => "GRD",
		3 => "TNK",
		_ => "",
	};

	public static float CooldownOf( byte cls ) => cls switch
	{
		0 => GameConfig.ClassCdCommander,
		1 => GameConfig.ClassCdEngineer,
		2 => GameConfig.ClassCdGuard,
		3 => GameConfig.ClassCdTank,
		_ => 999f,
	};

	/// <summary> 坦克爆发期内禁分裂（临时质量分裂=套现永久质量） </summary>
	public static bool IsTankSurging( Ball ball )
	{
		if ( ball is null || !ball.IsValid() ) return false;
		foreach ( var s in _surges )
		{
			if ( s.Ball == ball ) return true;
		}
		return false;
	}

	/// <summary> host 权威施放：冷却/存活/模式校验后执行；返回是否真的放了（没队友的指挥官不进冷却） </summary>
	public static bool Cast( Ball ball )
	{
		if ( !MatchState.IsTerritory ) return false;
		if ( ball is null || !ball.IsValid() || !ball.Alive ) return false;
		if ( ball.TerritoryClass > 3 ) return false;

		var sid = ball.Id;   // 冷却按球身记（真人=本球；bot 各球独立，不受 SteamId 全 0 影响）
		if ( _cd.TryGetValue( sid, out var since ) && since < CooldownOf( ball.TerritoryClass ) ) return false;

		switch ( ball.TerritoryClass )
		{
			case 0:
				if ( !CastCommander( ball ) ) return false;
				break;
			case 1:
				ball.ApplyBuff( (byte)PowerUpManager.Kind.Speed, GameConfig.ClassEngineerSeconds );
				break;
			case 2:
				CircleroyaleGame.Current?.SpawnGuardSpike( ball );
				break;
			case 3:
				CastTank( ball );
				break;
		}

		_cd[sid] = 0;
		GameLog.Info( $"[class] {NameOf( ball.TerritoryClass )} cast by '{ball.PlayerName}'" );
		return true;
	}

	static bool CastCommander( Ball ball )
	{
		var game = CircleroyaleGame.Current;
		if ( game is null ) return false;

		Ball best = null;
		foreach ( var b in game.Balls )
		{
			if ( !b.IsValid() || !b.Alive || b == ball ) continue;
			if ( b.TeamIndex < 0 || b.TeamIndex != ball.TeamIndex ) continue;
			if ( best is null || b.Mass > best.Mass ) best = b;
		}
		if ( best is null ) return false;   // 孤身无队友：技能不放、不进冷却

		var pos = best.WorldPosition;
		if ( ball.IsBot || ball.OwnerSteamId == Game.SteamId.Value )
		{
			ball.TeleportTo( pos );           // host 本机球/bot：直接瞬移
			game.MarkTeleport( ball, pos );
		}
		else
		{
			game.MarkTeleport( ball, pos );   // 远程球：host 先锚定校验位，owner 端收广播自行瞬移
			NetworkManager.ClassTeleport( ball.OwnerSteamId, pos.x, pos.y );
		}
		return true;
	}

	static void CastTank( Ball ball )
	{
		var added = MathF.Max( GameConfig.ClassTankMassMin, ball.Mass * GameConfig.ClassTankMassFraction );
		ball.Mass = MathF.Min( GameConfig.MaxMass, ball.Mass + added );
		_surges.Add( new Surge { Ball = ball, Added = added, Since = 0 } );
	}

	/// <summary> host 每帧（TickTerritory 调）：坦克临时质量到期回收（下限出生质量；球没了就不补） </summary>
	public static void Tick()
	{
		for ( int i = _surges.Count - 1; i >= 0; i-- )
		{
			var s = _surges[i];
			if ( s.Since < GameConfig.ClassTankSeconds ) continue;

			var b = s.Ball;
			if ( b.IsValid() && b.Alive )
				b.Mass = MathF.Max( GameConfig.StartMass, b.Mass - s.Added );
			_surges.RemoveAt( i );
		}
	}
}