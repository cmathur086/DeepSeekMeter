using Microsoft.Web.WebView2.Core;

namespace DeepSeekMeter;

/// <summary>
/// 全进程唯一的 WebView2 环境。
/// 同一个用户数据目录只能存在一个环境实例，而登录窗口与常驻 BrowserHost
/// 必须共享同一份 Cookie，因此二者都从这里取环境，不能各自 CreateAsync。
/// </summary>
internal static class WebViewEnvironment
{
    /// <summary>WebView2 用户数据目录（退出登录时清理 Cookie/localStorage）。</summary>
    public static readonly string UserDataFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeepSeekMeter", "WebView2");

    private static Task<CoreWebView2Environment>? _create;

    public static Task<CoreWebView2Environment> GetAsync()
        => _create ??= CoreWebView2Environment.CreateAsync(null, UserDataFolder);
}
