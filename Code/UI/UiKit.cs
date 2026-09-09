using System;
using Sandbox.UI;

/// <summary>
/// 通用 UI 小部件库（v0.7.8.49）：跨面板复用的按钮工厂 + 共享样式表。
/// 面板用法：BuildUi 里根面板就绪后调 UiKit.AttachStyles( Panel )，再用 UiKit.PillButton(...) 造按钮。
/// 外观（九宫格/配色/字号）统一在 Assets/ui/cruikit.scss；位置、宽度等摆放用 extraClass 写在面板自己的 scss 里。
/// </summary>
public static class UiKit
{
	// ⚠️ 必须放 Assets/ 下用小写资源路径——Code/ 下的文件只有编译器登记的源文件能被
	// StyleSheet.Load 找到（手写 Code/UI/UiKit.cs.scss 实测 File not found，见控制台）
	const string StylesPath = "ui/cruikit.scss";

	/// <summary> 把共享样式表挂到面板根（重复挂只是同样式多生效一份，无副作用） </summary>
	public static void AttachStyles( Panel root )
	{
		root?.StyleSheet.Load( StylesPath );
	}

	/// <summary> 药丸文字按钮（主菜单三按钮同款：btn_white 九宫格 + hover 粉）。
	/// extraClass 传摆放类（如 "sp-back"）；lambda 捕获委托实例，热重载后可重映射 </summary>
	public static Label PillButton( Panel parent, string text, string extraClass, Action onclick )
	{
		var b = new Label() { Classes = $"cr-pill {extraClass}" };
		b.Text = text;
		b.AddEventListener( "onclick", () => { GameSfx.UiClick(); onclick(); } );
		parent.AddChild( b );
		return b;
	}
}
