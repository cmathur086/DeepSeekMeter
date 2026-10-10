namespace DeepSeekMeter.Core;

/// <summary>平台 GET 请求的结果：HTTP 状态码 + 原始响应体。</summary>
public readonly record struct PlatformHttpResult(int StatusCode, string Body);

/// <summary>
/// 平台请求的传输层抽象。
///
/// 默认（null）走裸 HttpClient。但 platform.deepseek.com 前置了 AWS WAF：
/// 无浏览器会话 Cookie 的裸客户端会被以 202 + x-amzn-waf-action 挑战，
/// 与 Token 是否有效无关。因此 WPF 侧注入「浏览器内 fetch」实现，
/// 让请求带上浏览器登录态与 WAF 放行 Cookie。
/// </summary>
public interface IPlatformTransport
{
    /// <summary>发起一次 GET；<paramref name="bearerToken"/> 为 null 时不带 Authorization 头。</summary>
    Task<PlatformHttpResult> GetAsync(string url, string? bearerToken, CancellationToken ct);
}
