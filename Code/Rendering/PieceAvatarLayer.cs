using System;
using System.Collections.Generic;

/// <summary>
/// 分身头像层（M4，用户需求：分身带头像便于区分归属）。
/// 分身是纯数据实体，头像用**本地非联网的 GameObject 群**跟随 DrawPos 渲染——
/// 各端已有的分身快照镜像（15Hz）足够驱动，零额外同步，也没有 live create 可靠性问题。
/// 贴图按主人解析：真人 = Steam 头像（按 OwnerSteamId，缓存 + 1s 重试），
/// bot = 名字哈希几何图案（BotAvatar，bot 已有独立假 SteamId 可精确归属）。
/// 槽按分身 Id 稳定绑定（同名牌池模式），分身消失即回收。
/// </summary>
public sealed class PieceAvatarLayer : Component
{
	const int PoolSize = 96;   // 分身上限 16×真人，bot 撞刺碎片另计；超出部分本轮不画头像

	sealed class Slot
	{
		public GameObject Go;
		public SpriteRenderer Sprite;
		public Texture Texture;     // 当前贴图（变了才重设 Sprite）
		public int PieceId = -1;
		public bool Used;
	}

	sealed class AvatarEntry
	{
		public Texture Texture;
		public TimeSince SinceTry;
		public int Tries;
	}

	readonly List<Slot> _pool = new();
	readonly Dictionary<long, AvatarEntry> _avatars = new();

	protected override void OnUpdate()
	{
		base.OnUpdate();
		if ( !Game.IsPlaying ) return;

		var game = CircleroyaleGame.Current;
		if ( game is null ) return;

		foreach ( var s in _pool ) s.Used = false;

		foreach ( var c in game.Cells )
		{
			if ( c.PieceKind != CellPiece.Kind.SplitPiece ) continue;

			var slot = GetSlot( c.Id );
			if ( slot is null ) break;   // 池满：宁缺毋滥，下帧再试

			slot.Used = true;

			if ( !slot.Go.IsValid() ) continue;

			// 跟随分身渲染位置；头像直径 = 半径（直径的 50%，与主球同规格）
			slot.Go.WorldPosition = new Vector3( c.DrawPos.x, c.DrawPos.y, 0f );
			var d = c.Radius;
			slot.Sprite.Size = new Vector2( d, d );

			var tex = GetAvatarTexture( game, c );
			if ( tex is null )
			{
				slot.Sprite.Enabled = false;
				continue;
			}

			if ( slot.Texture != tex )
			{
				slot.Texture = tex;
				slot.Sprite.Sprite = Sprite.FromTexture( tex );
			}
			slot.Sprite.Enabled = true;
		}

		// 本帧没用到的槽隐藏回收
		foreach ( var s in _pool )
		{
			if ( !s.Used && s.Go.IsValid() )
			{
				s.Go.Enabled = false;
				s.PieceId = -1;
				s.Texture = null;
			}
		}
	}

	/// <summary> 按 Id 取槽：已有直接用，否则从空闲槽取（惰性建 GO） </summary>
	Slot GetSlot( int pieceId )
	{
		foreach ( var s in _pool )
		{
			if ( s.Used && s.PieceId == pieceId ) return s;
		}

		foreach ( var s in _pool )
		{
			if ( s.Used ) continue;

			if ( !s.Go.IsValid() )
			{
				// 顶层本地对象（不挂 Parent）：客户端上 Neon GO 是 host 拥有的网络对象，
				// 往它下面挂子物体可能静默失效（M2 reparent 教训），顶层本地产物最稳
				s.Go = new GameObject( true, "PieceAvatar" );

				s.Sprite = s.Go.AddComponent<SpriteRenderer>();
				s.Sprite.Lighting = false;
				s.Sprite.Shadows = false;
				s.Texture = null;
			}

			s.Go.Enabled = true;
			s.PieceId = pieceId;
			return s;
		}
		return null;
	}

	/// <summary> 主人头像：bot 用几何图案（即时）；真人拉 Steam 头像（缓存，1s 重试，15 次后占位图） </summary>
	Texture GetAvatarTexture( CircleroyaleGame game, CellPiece c )
	{
		if ( _avatars.TryGetValue( c.OwnerSteamId, out var entry ) )
		{
			if ( entry.Texture is not null ) return entry.Texture;
			if ( entry.SinceTry < 1f ) return null;   // 重试节流
		}
		else
		{
			entry = new AvatarEntry { SinceTry = 1f };   // 首帧立即尝试
			_avatars[c.OwnerSteamId] = entry;
		}

		entry.SinceTry = 0;

		var owner = game.FindBallBySteamId( c.OwnerSteamId );
		Texture tex = null;

		if ( owner.IsValid() && owner.IsBot )
			tex = BotAvatar.GetOrCreate( owner.PlayerName, c.ColorIndex );
		else if ( c.OwnerSteamId != 0 )
			tex = Texture.LoadAvatar( c.OwnerSteamId, 128 );

		if ( tex is null )
		{
			// 15 次拿不到（头像不存在/下载失败/双开合成 ID 拉不到）→ 占位图兜底，别无限重试
			if ( ++entry.Tries >= 15 )
			{
				tex = Texture.Load( GameConfig.AvatarPlaceholderPath, false );
			}
			if ( tex is null ) return null;
		}

		entry.Texture = tex;
		return tex;
	}
}
