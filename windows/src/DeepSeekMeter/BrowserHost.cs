using System.Windows;
using DeepSeekMeter.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeepSeekMeter;

/// <summary>
/// 常驻的屏幕外 WebView2：平台请求全部经由它发出，复用浏览器登录态。
///
/// 窗口必须真正 Show —— WebView2 在不可见窗口里不会渲染，脚本执行会被节流。
/// 因此把它放在屏幕外、不抢焦点、不进任务栏。
/// </summary>
public sealed class BrowserHost : IDisposable
{
    private const string Home = "https://platform.deepseek.com/";

    private readonly Window _window;
    private readonly WebView2 _webView = new();
    private readonly Task _ready;

    public IPlatformTransport Transport { get; }

    public BrowserHost()
    {
        _window = new Window
        {
            Width = 480,
            Height = 360,
            Left = -4000,
            Top = -4000,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            Title = "DeepSeekMeter session host",
        };
        _window.Content = _webView;

        Transport = new WebViewTransport(() => _webView.CoreWebView2);

        // 必须 Show：否则 WebView2 不初始化
        _window.Show();
        _ready = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var env = await WebViewEnvironment.GetAsync();
            await _webView.EnsureCoreWebView2Async(env);

            var core = _webView.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;

            // 等首次导航完成，页面有同源 document 后 fetch 才可用
            var navigated = new TaskCompletionSource();
            void OnNav(object? s, CoreWebView2NavigationCompletedEventArgs e) => navigated.TrySetResult();
            core.NavigationCompleted += OnNav;
            core.Navigate(Home);
            await navigated.Task;
            core.NavigationCompleted -= OnNav;
            Diag.Log($"BrowserHost ready, source={core.Source}");
        }
        catch (Exception ex)
        {
            // 初始化失败不阻塞启动：请求时会抛 Network 错误并显示在界面上
            Diag.Log($"BrowserHost init failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 从当前页面的 localStorage 读取平台 Token。
    ///
    /// 登录态由 Cookie + localStorage.userToken 共同构成：只带 Cookie 请求
    /// /auth-api/v0/users/current 会返回 200 但 biz_code 报 Token 无效。
    /// 这里直接在已登录页面里取值，避免离开浏览器上下文。
    /// </summary>
    public async Task<string?> ReadUserTokenAsync()
    {
        try
        {
            await _ready;
            var core = _webView.CoreWebView2;
            if (core is null) return null;

            // 同步脚本（不带 Promise，ExecuteScriptAsync 不等待 Promise）
            const string js =
                "(function(){try{" +
                "var keys=Object.keys(localStorage);" +
                "var raw=localStorage.getItem('userToken');" +
                "var tok=null;" +
                "if(raw){try{var o=JSON.parse(raw);tok=(o&&typeof o.value==='string')?o.value:null;}catch(e){tok=null;}}" +
                "return JSON.stringify({keys:keys,tok:tok});" +
                "}catch(e){return JSON.stringify({keys:[],tok:null,err:String(e)});}})()";

            var wire = await core.ExecuteScriptAsync(js);
            var inner = System.Text.Json.JsonSerializer.Deserialize<string>(wire) ?? "";
            if (inner.Length == 0) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(inner);
            var keys = doc.RootElement.TryGetProperty("keys", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.Array
                ? string.Join(",", k.EnumerateArray().Select(e => e.GetString()))
                : "";
            var token = doc.RootElement.TryGetProperty("tok", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                ? t.GetString()
                : null;

            Diag.Log($"localStorage keys=[{keys}] userToken={(token is null ? "absent" : $"{token.Length} chars")}");
            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch (Exception ex)
        {
            Diag.Log($"ReadUserTokenAsync failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>清空浏览器登录态（退出登录用）。</summary>
    public async Task<bool> ClearSessionAsync()
    {
        try
        {
            await _ready;
            var core = _webView.CoreWebView2;
            if (core is null) return false;
            core.CookieManager.DeleteAllCookies();
            core.Navigate(Home);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        try { _window.Close(); } catch { /* 忽略 */ }
    }
}
