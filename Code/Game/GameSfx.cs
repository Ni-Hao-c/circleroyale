using System;

/// <summary>
/// 游戏音效（M4）：**全部 2D 平面声（.sound UI 标志，用户定稿——不随距离衰减）**。
/// 吃食物这类高频音只播"本机耳朵"自己的（IsMine 判定），死亡/爆裂全场都播（低频事件）。
/// 资产未就绪（导入中/客户端未下载）时静默跳过，绝不让音效调用打断游戏循环。
/// （曾名 GameSound：创建时与编辑器写入竞态把编译缓存搞花，换名重建——内容与旧版一致）
/// </summary>
public static class GameSfx
{
	const string SfxEat = "sounds/cr_eat.sound";
	const string SfxEatBall = "sounds/cr_eat_ball.sound";
	const string SfxPop = "sounds/cr_pop.sound";
	const string SfxSplit = "sounds/cr_split.sound";
	const string SfxEject = "sounds/cr_eject.sound";
	const string SfxRespawn = "sounds/cr_respawn.sound";
	const string SfxCombo1 = "sounds/cr_combo1.sound";   // 同一段上扬琶音三档升调（combo 越高音越高）
	const string SfxCombo2 = "sounds/cr_combo2.sound";
	const string SfxCombo3 = "sounds/cr_combo3.sound";
	const string SfxPickup = "sounds/cr_pickup.sound";
	const string SfxUiClick = "sounds/ui_click.sound";   // dobou BUTTON_05（用户供，v0.7.8.66）
	const string SfxUiSwipe = "sounds/ui_swipe.sound";   // dobou Tablet_Swipe_01（用户供，翻页/滚动）

	static int _eatCount;   // 本局吃食物计数（每 10 分播一次音，重生清零）

	// ---- 击杀连播（v0.7.7.0）：10 秒窗口内连续击杀累计 combo，横幅文案/音效/震动幅度都随档位走 ----
	static TimeSince _sinceKill = 999f;
	static int _comboCount;

	/// <summary> 本机击杀登记：返回本连击是第几杀（10 秒窗口外从 1 重计） </summary>
	public static int RegisterKill()
	{
		_comboCount = _sinceKill < 10f ? _comboCount + 1 : 1;
		_sinceKill = 0;
		return _comboCount;
	}

	/// <summary> 重生清零连击（新的一条命重新起算） </summary>
	public static void ResetCombo()
	{
		_comboCount = 0;
		_sinceKill = 999f;
	}

	/// <summary> combo 升调音（≥2 杀才播；档位越高音越高，五杀封顶三档） </summary>
	public static void ComboSound( int combo )
	{
		if ( combo < 2 ) return;
		Play( combo >= 5 ? SfxCombo3 : combo >= 3 ? SfxCombo2 : SfxCombo1, Vector3.Zero );
	}

	/// <summary> 道具入包/激活的"叮咚"（横幅出现配套音） </summary>
	public static void Pickup() => Play( SfxPickup, Vector3.Zero );

	/// <summary> UI 按钮点击（药丸按钮 / 主菜单 / 房间设置牌共用） </summary>
	public static void UiClick() => Play( SfxUiClick, Vector3.Zero );

	/// <summary> UI 翻页/滚动（玩家列表滚动容器用，ScrollSoundPanel 触发） </summary>
	public static void UiSwipe() => Play( SfxUiSwipe, Vector3.Zero );

	/// <summary> 该 owner 的事件是否归"本机耳朵"管：host 比对 SteamId；客户端比对
	/// 本机球的 OwnerSteamId（双开合成 ID 与 Game.SteamId 对不上，只能走球上同步值） </summary>
	public static bool IsMine( long ownerSteamId )
	{
		if ( NetworkManager.IsAuthority )
			return ownerSteamId == Game.SteamId.Value;

		var b = CircleroyaleGame.Current?.LocalBall;
		return b.IsValid() && b.OwnerSteamId == ownerSteamId;
	}

	/// <summary> 吃食物：每累计 10 分播一次（不再每颗都播，避免机关枪）。重生时 ResetEatCount 清零 </summary>
	public static void EatFood( Vector3 pos )
	{
		if ( ++_eatCount % 10 != 0 ) return;
		Play( SfxEat, pos );
	}

	/// <summary> 重生时清零吃食物计数（下一条命重新从 0 计） </summary>
	public static void ResetEatCount() => _eatCount = 0;

	public static void EatBall( Vector3 pos ) => Play( SfxEatBall, pos );

	public static void Pop( Vector3 pos ) => Play( SfxPop, pos );

	public static void Split( Vector3 pos ) => Play( SfxSplit, pos );

	public static void Eject( Vector3 pos ) => Play( SfxEject, pos );

	public static void Respawn( Vector3 pos ) => Play( SfxRespawn, pos );

	static void Play( string path, Vector3 pos )
	{
		try
		{
			Sound.Play( path, pos );
		}
		catch
		{
			// 资源未就绪：静默跳过（不刷日志）
		}
	}
}
