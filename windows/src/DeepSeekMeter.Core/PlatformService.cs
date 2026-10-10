using System.Net;
using System.Text.Json;

namespace DeepSeekMeter.Core;

/// <summary>平台错误类型（对齐 macOS 版 PlatformError）。</summary>
public enum PlatformErrorKind
{
    EmptyToken,
    Network,
    Http,
    Api,
    Decoding,
}

/// <summary>
/// 平台接口错误（带用户可读的中文 message，对齐 macOS 版 PlatformError.message）。
/// </summary>
public sealed class PlatformException : Exception
{
    public PlatformErrorKind Kind { get; }
    public int? HttpCode { get; }
    public int? ApiCode { get; }

    public PlatformException(PlatformErrorKind kind, string message, int? httpCode = null, int? apiCode = null)
        : base(message)
    {
        Kind = kind;
        HttpCode = httpCode;
        ApiCode = apiCode;
    }

    /// <summary>平台 Token 无效或已过期（40002 / 40003）。</summary>
    public bool IsTokenExpired => ApiCode is 40002 or 40003;

    /// <summary>根据错误类型构造提示文案（对齐 Swift PlatformError.message）。</summary>
    public static string BuildMessage(PlatformErrorKind kind, string detail, int? httpCode = null, int? apiCode = null)
    {
        switch (kind)
        {
            case PlatformErrorKind.EmptyToken:
                return "Please enter your platform token in settings first";
            case PlatformErrorKind.Network:
                return $"Usage fetch failed: {detail}";
            case PlatformErrorKind.Http:
                return $"Usage fetch failed (HTTP {httpCode})";
            case PlatformErrorKind.Api:
                if (apiCode is 40002 or 40003)
                    return "Platform token is invalid or expired - please sign in again";
                return $"Platform API error ({apiCode}): {detail}";
            case PlatformErrorKind.Decoding:
                return $"Failed to parse usage data: {detail}";
            default:
                return detail;
        }
    }

    public static PlatformException EmptyToken() =>
        new(PlatformErrorKind.EmptyToken, BuildMessage(PlatformErrorKind.EmptyToken, ""));

    public static PlatformException Network(string detail) =>
        new(PlatformErrorKind.Network, BuildMessage(PlatformErrorKind.Network, detail));

    public static PlatformException Http(int code) =>
        new(PlatformErrorKind.Http, BuildMessage(PlatformErrorKind.Http, "", httpCode: code), httpCode: code);

    public static PlatformException Api(int code, string msg) =>
        new(PlatformErrorKind.Api, BuildMessage(PlatformErrorKind.Api, msg, apiCode: code), apiCode: code);

    public static PlatformException Decoding(string detail) =>
        new(PlatformErrorKind.Decoding, BuildMessage(PlatformErrorKind.Decoding, detail));
}

/// <summary>
/// 平台私有接口客户端（platform.deepseek.com，用浏览器登录态 userToken 鉴权）。
/// 对齐 macOS 版 PlatformService.swift，接口契约一致：
///   /auth-api/v0/users/current、/api/v0/users/get_user_summary、
///   /api/v0/usage/by_api_key/amount、/api/v0/usage/by_api_key/cost（实时）
/// </summary>
public sealed class PlatformService
{
    private const string BaseUrl = "https://platform.deepseek.com";
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly HttpClient Http = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    /// <summary>
    /// 浏览器会话占位 Token：平台的登录态已经放到 Cookie，localStorage 里
    /// 可能根本没有可用 token。该值只用于让「已登录」状态成立，
    /// 永不作为 Authorization 头发送。
    /// </summary>
    public const string BrowserSessionToken = "browser-session";

    private readonly IPlatformTransport? _transport;

    /// <summary>传入 transport 时所有请求走浏览器内 fetch；否则退回裸 HttpClient（自测用）。</summary>
    public PlatformService(IPlatformTransport? transport = null)
    {
        _transport = transport;
    }

    /// <summary>当前用户信息。</summary>
    public readonly record struct UserInfo(string Email, string Currency);

    // MARK: - 接口

    /// <summary>校验 Token 并返回用户信息（email、currency）；data / biz_data 为空视为校验失败。</summary>
    public async Task<UserInfo> FetchCurrentUserAsync(string token, CancellationToken ct = default)
    {
        var response = await GetAsync<CurrentUserResponse>("/auth-api/v0/users/current", token, ct);
        EnsureSuccess(response.Code, response.Msg);
        if (response.Data is null)
            throw PlatformException.Api(response.Code, "User info is empty");
        EnsureBizSuccess(response.Data.BizCode, response.Data.BizMsg);
        var user = response.Data.BizData;
        if (user is null || string.IsNullOrEmpty(user.Id))
            throw PlatformException.Api(response.Code, "User info is empty");
        return new UserInfo(user.Email ?? "", user.Currency ?? "CNY");
    }

    /// <summary>平台侧账户汇总（余额 / 累计消费）。</summary>
    public async Task<UserSummary> FetchSummaryAsync(string token, CancellationToken ct = default)
    {
        var response = await GetAsync<SummaryResponse>("/api/v0/users/get_user_summary", token, ct);
        EnsureSuccess(response.Code, response.Msg);
        if (response.Data is null)
            throw PlatformException.Api(response.Code, "summary is empty");
        EnsureBizSuccess(response.Data.BizCode, response.Data.BizMsg);
        return response.Data.BizData
            ?? throw PlatformException.Api(response.Code, "summary is empty");
    }

    /// <summary>按 API Key 的 token 用量（实时；start/end 为 Unix 秒，tz 为秒偏移，bucket=86400 按天分桶）。
    /// 与 usage/amount（当日数据延迟数小时～次日）不同，该接口当日数据实时。</summary>
    public async Task<ApiKeyAmountData> FetchApiKeyAmountAsync(string token, long start, long end, int tz, CancellationToken ct = default)
    {
        var response = await GetAsync<ApiKeyAmountResponse>($"/api/v0/usage/by_api_key/amount?start={start}&end={end}&tz={tz}&bucket=86400", token, ct);
        EnsureSuccess(response.Code, response.Msg);
        if (response.Data is null)
            throw PlatformException.Api(response.Code, "by_api_key amount is empty");
        EnsureBizSuccess(response.Data.BizCode, response.Data.BizMsg);
        return response.Data.BizData
            ?? throw PlatformException.Api(response.Code, "by_api_key amount is empty");
    }

    /// <summary>按 API Key 的费用（实时；start/end 为 Unix 秒，tz 为秒偏移，bucket=86400 按天分桶）。</summary>
    public async Task<ApiKeyCostData> FetchApiKeyCostAsync(string token, long start, long end, int tz, CancellationToken ct = default)
    {
        var response = await GetAsync<ApiKeyCostResponse>($"/api/v0/usage/by_api_key/cost?start={start}&end={end}&tz={tz}&bucket=86400", token, ct);
        EnsureSuccess(response.Code, response.Msg);
        if (response.Data is null)
            throw PlatformException.Api(response.Code, "by_api_key cost is empty");
        EnsureBizSuccess(response.Data.BizCode, response.Data.BizMsg);
        return response.Data.BizData
            ?? throw PlatformException.Api(response.Code, "by_api_key cost is empty");
    }

    // MARK: - 请求

    private async Task<T> GetAsync<T>(string path, string token, CancellationToken ct = default)
    {
        // 走浏览器时凭据在 Cookie 里，token 允许为空；走裸 HttpClient 时必须有 token
        if (_transport is null && string.IsNullOrWhiteSpace(token))
            throw PlatformException.EmptyToken();

        string json;

        if (_transport is not null)
        {
            // 占位 Token 不能当 Bearer 发出去，否则可能被平台判成非法凭据
            var bearer = token == BrowserSessionToken ? null : token;
            PlatformHttpResult result;
            try
            {
                result = await _transport.GetAsync(BaseUrl + path, bearer, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw PlatformException.Network("Request timed out");
            }
            catch (PlatformException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw PlatformException.Network(ex.Message);
            }

            if (result.StatusCode != 200)
                throw PlatformException.Http(result.StatusCode);
            json = result.Body;
        }
        else
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            request.Headers.TryAddWithoutValidation("Referer", "https://platform.deepseek.com/usage");
            request.Headers.TryAddWithoutValidation("Origin", "https://platform.deepseek.com");

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw PlatformException.Network("Request timed out");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw PlatformException.Network(ex.Message);
            }

            using (response)
            {
                if ((int)response.StatusCode != 200)
                    throw PlatformException.Http((int)response.StatusCode);
                json = await response.Content.ReadAsStringAsync(ct);
            }
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Json.Options)
                ?? throw PlatformException.Decoding("Response is empty");
        }
        catch (JsonException ex)
        {
            throw PlatformException.Decoding(ex.Message);
        }
    }

    private static void EnsureSuccess(int code, string msg)
    {
        if (code != 0)
            throw PlatformException.Api(code, msg);
    }

    /// <summary>
    /// 业务层成功判断：data.biz_code 非 0 时抛 Api 异常，
    /// 避免平台业务错误（biz_code != 0）被当成成功数据。
    /// internal 以便 selftest 直接覆盖 biz_code 非 0 场景。
    /// </summary>
    internal static void EnsureBizSuccess(int bizCode, string bizMsg)
    {
        if (bizCode != 0)
            throw PlatformException.Api(bizCode, bizMsg);
    }

    // MARK: - 响应 DTO（与 macOS 版保持相同结构）

    private sealed class CurrentUserData
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")] public string Id { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("email")] public string? Email { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("currency")] public string? Currency { get; set; }
    }

    private sealed class CurrentUserResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")] public int Code { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("data")] public BizWrapper<CurrentUserData>? Data { get; set; }
    }

    private sealed class SummaryResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")] public int Code { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("data")] public BizWrapper<UserSummary>? Data { get; set; }
    }

    private sealed class ApiKeyAmountResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")] public int Code { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("data")] public BizWrapper<ApiKeyAmountData>? Data { get; set; }
    }

    private sealed class ApiKeyCostResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("code")] public int Code { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("msg")] public string Msg { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("data")] public BizWrapper<ApiKeyCostData>? Data { get; set; }
    }
}
