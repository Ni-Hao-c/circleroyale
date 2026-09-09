using System;

/// <summary>
/// 背景音乐（M4；2026-09-09 用户定稿：主菜单/大厅 = journey，战斗 = town，
/// 旧 chiptune1/battle.music 弃用）。走 .sound SoundEvent（UI 标志 = 2D 平面声）。
/// **循环 = 播完自动重播**（SoundHandle 没有循环标志，Tick 里检测曲终重开）。切曲旧曲 1s 淡出。
/// </summary>
public static class GameMusic
{
	const string MenuTrack = "sounds/music/journey.sound";
	const string BattleTrack = "sounds/music/town.sound";

	static SoundHandle _handle;
	static string _track;
	static bool _wasPlaying;   // 上一帧 handle 是否有效且在播（曲终重播的判据，见 Tick）
	static Sandbox.Audio.Mixer _musicMixer;   // Music 混音器缓存（找不到就留在默认 Game，不影响出声）

	/// <summary> 音乐音量 0-1（控制台实时可调）。走 Music 混音器，别和 UI 音效一锅炖 </summary>
	[ConVar( "cr_music_volume", Help = "Music volume 0..1 (default 0.22)" )]
	public static float Volume { get; set; } = 0.22f;

	/// <summary> 主菜单/大厅曲（已在放就无事发生） </summary>
	public static void PlayMenu() => Play( MenuTrack );

	/// <summary> 开局切战斗曲（已在放就无事发生） </summary>
	public static void PlayBattle() => Play( BattleTrack );

	/// <summary> 停止音乐（保留备用，当前菜单/大厅/战斗三态都有各自的曲子） </summary>
	public static void Stop()
	{
		_track = null;
		_wasPlaying = false;
		if ( _handle.IsValid() )
		{
			_handle.Stop( 1f );
			_handle = default;
		}
	}

	/// <summary> 每帧调（CircleroyaleGame.Tick，菜单阶段也要跑）：音量跟随 convar + 曲终自动重播 </summary>
	public static void Tick()
	{
		if ( _track is null ) return;

		// 曲终重播：引擎在声音播完后会 Dispose 掉 handle（SoundHandle.PreTick: Finished→Dispose），
		// 所以 IsValid 会变 false——**不能**用 `if (!_handle.IsValid()) return;` 早退，否则永远到不了重播。
		// 只在"上一帧还在播、现在失效/Finished/IsStopped"时重开，避免资源缺失时每帧空转重试。
		if ( _wasPlaying && ( !_handle.IsValid() || _handle.Finished || _handle.IsStopped ) )
		{
			var path = _track;
			_handle = default;
			_track = null;
			_wasPlaying = false;
			Play( path );
			return;
		}

		if ( !_handle.IsValid() ) return;

		_handle.Volume = Volume;
		_wasPlaying = _handle.IsPlaying;
	}

	static void Play( string path )
	{
		if ( _track == path && _handle.IsValid() && _handle.IsPlaying ) return;   // 已在放这首

		try
		{
			if ( _handle.IsValid() ) _handle.Stop( 1f );   // 旧曲淡出让位

			_handle = Sound.Play( path );   // 曲子 .sound 带 UI 标志 = 2D 平面声
			_track = path;

			if ( !_handle.IsValid() )
			{
				Log.Warning( $"[music] Sound.Play returned invalid handle: {path} (asset not imported?)" );
				_track = null;   // 别让 Tick 每帧重试
				return;
			}

			_wasPlaying = true;

			// 路由到内置 Music 混音器（Master→Music/Game/UI/Voice，引擎 ResetToDefault 建）
			_musicMixer ??= Sandbox.Audio.Mixer.FindMixerByName( "Music" );
			if ( _musicMixer is not null )
			{
				// 用户要求：Music 混音器音量减半（引擎默认 1.0 → 0.5；绝对值赋值，热重载不叠加）
				_musicMixer.Volume = 0.5f;
				_handle.TargetMixer = _musicMixer;
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"[music] play '{path}' failed: {e.Message}" );
			_track = null;
		}
	}
}
