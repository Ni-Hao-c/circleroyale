using System;
using System.Collections.Generic;

/// <summary>
/// 本地持久化排行榜（M5）：全服榜的离线兜底。每局结束把结算榜并入
/// FileSystem.Data 的 JSON（同 SteamId 只留最好成绩），读 Top10 展示。
/// 云端（ScoreUploader）拿不到数据时自动用它——未发布包后端无身份是常态（实测）。
/// ⚠️ Entry 必须是"公共属性"：Sandbox.Json 序列化不吃公共字段
/// （私有嵌套类 + 字段实测写出全空榜，Top10 全是 0）。
/// </summary>
public static class LocalBoard
{
	public sealed class Entry
	{
		public long SteamId { get; set; }
		public string Name { get; set; }
		public float Mass { get; set; }
	}

	const string SavePath = "cr_local_board.json";

	/// <summary> 把本局结算榜并入本地榜（同 SteamId 保留最好成绩；封顶 50 条） </summary>
	public static void Record( ScoreWire[] rows )
	{
		try
		{
			if ( rows is null || rows.Length == 0 ) return;

			var list = Load();
			foreach ( var r in rows )
			{
				var e = list.Find( x => x.SteamId == r.SteamId );
				if ( e is null )
				{
					list.Add( new Entry { SteamId = r.SteamId, Name = r.Name, Mass = r.Mass } );
				}
				else if ( r.Mass > e.Mass )
				{
					e.Mass = r.Mass;
					e.Name = r.Name;
				}
			}

			list.Sort( ( x, y ) => y.Mass.CompareTo( x.Mass ) );
			if ( list.Count > 50 ) list.RemoveRange( 50, list.Count - 50 );

			Write( list );
		}
		catch ( Exception e )
		{
			GameLog.Info( $"[score] local board save failed: {e.Message}" );
		}
	}

	/// <summary> 本地榜 TopN（读不到/为空返回 null） </summary>
	public static List<( string Name, float Mass )> Top( int count )
	{
		try
		{
			var list = Load();
			if ( list.Count == 0 ) return null;

			list.Sort( ( x, y ) => y.Mass.CompareTo( x.Mass ) );

			var result = new List<( string, float )>();
			for ( int i = 0; i < list.Count && i < count; i++ )
				result.Add( ( list[i].Name, list[i].Mass ) );
			return result;
		}
		catch
		{
			return null;
		}
	}

	static List<Entry> Load()
	{
		var fs = FileSystem.Data;
		if ( fs is null ) return new List<Entry>();   // 早期可能为 null（实测坑）

		if ( !fs.FileExists( SavePath ) ) return new List<Entry>();
		return Json.Deserialize<List<Entry>>( fs.ReadAllText( SavePath ) ) ?? new List<Entry>();
	}

	static void Write( List<Entry> list )
	{
		var fs = FileSystem.Data;
		if ( fs is null ) return;
		fs.WriteAllText( SavePath, Json.Serialize( list ) );
	}
}
