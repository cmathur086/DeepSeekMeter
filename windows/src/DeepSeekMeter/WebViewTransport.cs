using System.Text.Json;
using DeepSeekMeter.Core;
using Microsoft.Web.WebView2.Core;

namespace DeepSeekMeter;

/// <summary>
/// 用 WebView2 页面内的 fetch() 发平台请求。
///
/// 为什么不用裸 HttpClient：platform.deepseek.com 前置了 AWS WAF，
/// 无浏览器会话的客户端一律被 202 + x-amzn-waf-action 挑战（与 Token 是否有效无关）。
/// 在页面里 fetch 则自动带上 Cookie、WAF 放行票据与真实浏览器指纹。
///
/// 注意：ExecuteScriptAsync 不会等待 Promise（返回的会是 "{}"），
/// 所以这里先把结果写进 window.__dsm，再轮询取值。
/// </summary>
internal sealed class WebViewTransport : IPlatformTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);
    private readonly Func<CoreWebView2?> _core;

    public WebViewTransport(Func<CoreWebView2?> core) => _core = core;

    public async Task<PlatformHttpResult> GetAsync(string url, string? bearerToken, CancellationToken ct)
    {
        var core = _core();
        if (core is null)
            throw new PlatformException(PlatformErrorKind.Network, "Browser session is not ready");

        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "application/json, text/plain, */*",
        };
        if (!string.IsNullOrEmpty(bearerToken))
            headers["Authorization"] = "Bearer " + bearerToken;

        // Referer / Origin 由浏览器自己填，JS 里设不了（forbidden headers）
        var key = "r" + Guid.NewGuid().ToString("N");
        var urlJson = JsonSerializer.Serialize(url);
        var headersJson = JsonSerializer.Serialize(headers);

        var kick =
            "window.__dsm = window.__dsm || {};\n" +
            $"window.__dsm.{key} = null;\n" +
            "(function () {\n" +
            $"  fetch({urlJson}, {{ credentials: 'include', headers: {headersJson} }})\n" +
            "    .then(function (r) {\n" +
            "      return r.text().then(function (t) {\n" +
            $"        window.__dsm.{key} = JSON.stringify({{ s: r.status, b: t }});\n" +
            "      });\n" +
            "    })\n" +
            "    .catch(function (e) {\n" +
            $"      window.__dsm.{key} = JSON.stringify({{ s: 0, b: String(e) }});\n" +
            "    });\n" +
            "})();";

        try
        {
            await core.ExecuteScriptAsync(kick);
        }
        catch (Exception ex)
        {
            Diag.Log($"fetch {url} kick failed: {ex.GetType().Name}: {ex.Message}");
            throw new PlatformException(PlatformErrorKind.Network, ex.Message);
        }

        var wire = await PollAsync(core, key, ct);

        var inner = JsonSerializer.Deserialize<string>(wire) ?? "";
        if (inner.Length == 0)
            throw new PlatformException(PlatformErrorKind.Network, "Browser returned an empty result");

        using var doc = JsonDocument.Parse(inner);
        var status = doc.RootElement.GetProperty("s").GetInt32();
        var body = doc.RootElement.TryGetProperty("b", out var b) ? b.GetString() ?? "" : "";
        Diag.Log($"fetch {url} -> HTTP {status} ({body.Length} bytes)");
        if (status == 0)
            throw new PlatformException(PlatformErrorKind.Network, body);

        return new PlatformHttpResult(status, body);
    }

    /// <summary>轮询 window.__dsm.&lt;key&gt;，直到 fetch 回填结果或超时。</summary>
    private static async Task<string> PollAsync(CoreWebView2 core, string key, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            var probe = await core.ExecuteScriptAsync($"window.__dsm.{key} ?? null");
            // 未完成时是 JSON 字面量 null；完成后是带引号的 JSON 字符串
            if (probe.Length > 4 && probe != "null")
                return probe;

            await Task.Delay(50, ct);
        }
        throw new PlatformException(PlatformErrorKind.Network, "Browser request timed out");
    }
}
