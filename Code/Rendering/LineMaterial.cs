/// <summary>
/// line.shader 独立材质工厂。引擎的 Material.FromShader 按 shader 路径缓存共享实例
/// （Material.Static.cs "will return the same material"），谁 Set g_tColor 全场生效——
/// 实测 5 个贴图批次互相覆盖，球体被最后设置的 powerups.png 染成道具条带（v0.7.8.39）。
/// 纯色线/三角批次也必须独立：默认 TexCoord0=(0,0) 会采样到别人设置的贴图。
/// </summary>
public static class LineMaterial
{
	static int _seq;

	public static Material Create()
	{
		return Material.Create( $"cr_line_{_seq++}", "shaders/line.shader" );
	}
}