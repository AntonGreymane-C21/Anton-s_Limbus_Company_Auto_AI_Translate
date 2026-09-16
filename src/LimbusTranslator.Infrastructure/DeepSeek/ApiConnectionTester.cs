using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Security;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// API 连接测试结果状态（第8.87轮）：必须能区分 Key / Endpoint / 额度 / 超时等问题。
/// </summary>
public enum ApiConnectionTestStatus
{
    /// <summary>连接与鉴权成功，且返回可解析响应</summary>
    Success,

    /// <summary>配置缺失或非法（未发请求）</summary>
    ConfigError,

    /// <summary>401 / 403：API Key 无效或无权限</summary>
    InvalidKey,

    /// <summary>404：Endpoint 或模型名错误</summary>
    NotFound,

    /// <summary>429：请求频率或额度限制</summary>
    RateLimited,

    /// <summary>请求超时</summary>
    Timeout,

    /// <summary>网络错误（DNS / 连接失败等）</summary>
    NetworkError,

    /// <summary>服务端错误（5xx）</summary>
    ServerError,

    /// <summary>其它 HTTP 错误</summary>
    HttpError,
}

/// <summary>一次「测试连接」的结果（消息一律脱敏，绝不含完整 API Key）。</summary>
public sealed record ApiConnectionTestResult
{
    /// <summary>是否成功</summary>
    public required bool Success { get; init; }

    /// <summary>状态</summary>
    public required ApiConnectionTestStatus Status { get; init; }

    /// <summary>HTTP 状态码（未发请求或网络异常时为 null）</summary>
    public int? HttpStatusCode { get; init; }

    /// <summary>响应中实际返回的模型名（缺失时回落到配置值）</summary>
    public string? ResponseModel { get; init; }

    /// <summary>耗时（毫秒）</summary>
    public long DurationMs { get; init; }

    /// <summary>输入 token（服务端返回时才有）</summary>
    public int? InputTokens { get; init; }

    /// <summary>输出 token（服务端返回时才有）</summary>
    public int? OutputTokens { get; init; }

    /// <summary>用户可读消息（已脱敏）</summary>
    public required string Message { get; init; }
}

/// <summary>
/// DeepSeek API「测试连接」诊断（第8.87轮）。
///
/// 边界（重要）：
///   - 只有用户**主动点击**才发起真实网络请求；
///   - 只发一条极小探针请求（要求模型只回复 OK，max_tokens 极小）；
///   - <b>不写</b> TranslationMemory / request_cache / Trace / Manifest，也不修改任何配置；
///   - 所有错误消息与密钥显示都经过 <see cref="SecretRedactor"/> 脱敏。
/// </summary>
public sealed class ApiConnectionTester
{
    /// <summary>探针请求的固定提示词（极小输出，不涉及任何游戏文本）。</summary>
    public const string ProbePrompt = "Reply with exactly: OK";

    private readonly DeepSeekOptions _options;
    private readonly HttpMessageHandler? _handler;

    /// <param name="options">当前 DeepSeek 配置（ApiUrl / ApiKey / Model / TimeoutSeconds）</param>
    /// <param name="handler">测试注入用 handler；生产为 null（使用真实 HttpClient）</param>
    public ApiConnectionTester(DeepSeekOptions options, HttpMessageHandler? handler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler;
    }

    /// <summary>执行测试连接。任何异常都转换为结果对象，不向调用方抛出。</summary>
    public async Task<ApiConnectionTestResult> TestAsync(CancellationToken cancellationToken = default)
    {
        // 1) 配置预检：不发请求即可确定的错误
        if (string.IsNullOrWhiteSpace(_options.ApiUrl))
        {
            return ConfigError("未配置 API 地址（deepSeek.apiUrl）。");
        }

        if (!Uri.TryCreate(_options.ApiUrl, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
        {
            return ConfigError("API 地址非法（必须是 http/https 绝对地址）。");
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return ConfigError("未配置 API Key（deepSeek.apiKey）。");
        }

        if (string.IsNullOrWhiteSpace(_options.Model))
        {
            return ConfigError("未配置模型名（deepSeek.model）。");
        }

        var timeoutSeconds = _options.TimeoutSeconds > 0 ? Math.Min(_options.TimeoutSeconds, 60) : 30;
        var body = BuildProbeRequestBody(_options.Model);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = _handler is null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) }
                : new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_options.ApiKey}");

            using var response = await client.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return MapFailure(response.StatusCode, responseText, stopwatch.ElapsedMilliseconds);
            }

            // 只做 TCP 连通性检查是不够的：必须能解析出可用响应结构
            var (parsed, model, inputTokens, outputTokens) = TryParseProbeResponse(responseText);
            if (!parsed)
            {
                return new ApiConnectionTestResult
                {
                    Success = false,
                    Status = ApiConnectionTestStatus.HttpError,
                    HttpStatusCode = (int)response.StatusCode,
                    ResponseModel = model,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Message = "连接成功但响应无法解析（可能不是 OpenAI 兼容接口）。"
                        + $" 摘要: {SecretRedactor.Summarize(responseText, 120, _options.ApiKey)}",
                };
            }

            return new ApiConnectionTestResult
            {
                Success = true,
                Status = ApiConnectionTestStatus.Success,
                HttpStatusCode = (int)response.StatusCode,
                ResponseModel = model ?? _options.Model,
                DurationMs = stopwatch.ElapsedMilliseconds,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                Message = "连接成功",
            };
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return Failure(ApiConnectionTestStatus.Timeout, stopwatch.ElapsedMilliseconds, null,
                $"请求超时（超过 {timeoutSeconds} 秒）");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Failure(ApiConnectionTestStatus.Timeout, stopwatch.ElapsedMilliseconds, null, "测试已取消");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return Failure(ApiConnectionTestStatus.NetworkError, stopwatch.ElapsedMilliseconds, null,
                $"网络错误: {SecretRedactor.Summarize(ex.Message, 160, _options.ApiKey)}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Failure(ApiConnectionTestStatus.HttpError, stopwatch.ElapsedMilliseconds, null,
                $"请求失败: {SecretRedactor.Summarize(ex.Message, 160, _options.ApiKey)}");
        }
    }

    private ApiConnectionTestResult ConfigError(string message) => new()
    {
        Success = false,
        Status = ApiConnectionTestStatus.ConfigError,
        Message = message,
    };

    private static ApiConnectionTestResult Failure(ApiConnectionTestStatus status, long ms, int? http, string message) => new()
    {
        Success = false,
        Status = status,
        HttpStatusCode = http,
        DurationMs = ms,
        Message = message,
    };

    /// <summary>HTTP 失败 → 稳定状态映射（消息脱敏，不 dump 完整响应体）。</summary>
    private ApiConnectionTestResult MapFailure(HttpStatusCode status, string responseText, long elapsedMs)
    {
        var (testStatus, hint) = (int)status switch
        {
            401 or 403 => (ApiConnectionTestStatus.InvalidKey, "API Key 无效或没有权限"),
            404 => (ApiConnectionTestStatus.NotFound, "Endpoint 或模型名不存在"),
            429 => (ApiConnectionTestStatus.RateLimited, "请求频率或额度受限，请稍后重试"),
            >= 500 => (ApiConnectionTestStatus.ServerError, "服务端错误，请稍后重试"),
            _ => (ApiConnectionTestStatus.HttpError, "请求被拒绝"),
        };

        return Failure(testStatus, elapsedMs, (int)status,
            $"{hint}（HTTP {(int)status}）: {SecretRedactor.Summarize(responseText, 160, _options.ApiKey)}");
    }

    /// <summary>构造极小探针请求体：只要一个 OK，thinking 显式关闭以最小化开销。</summary>
    private static string BuildProbeRequestBody(string model)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = new object[]
            {
                new { role = "user", content = ProbePrompt },
            },
            ["max_tokens"] = 8,
            ["stream"] = false,
            ["thinking"] = new { type = "disabled" },
        };
        return JsonSerializer.Serialize(body);
    }

    /// <summary>解析探针响应（OpenAI 兼容）：choices / usage / model。缺失字段允许为 null。</summary>
    private static (bool Parsed, string? Model, int? InputTokens, int? OutputTokens) TryParseProbeResponse(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;

            string? model = null;
            if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString();
            }

            var hasChoices = root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0;

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var ptValue))
                {
                    inputTokens = ptValue;
                }

                if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var ctValue))
                {
                    outputTokens = ctValue;
                }
            }

        return (hasChoices, model, inputTokens, outputTokens);
        }
        catch (JsonException)
        {
            return (false, null, null, null);
        }
    }
}

/// <summary>
/// 测试连接结果 → 用户可读文本（第8.87轮）。集中在此以便用单元测试锁定错误映射文案。
/// </summary>
public static class ApiConnectionStatusText
{
    /// <summary>状态的中文说明。</summary>
    public static string Describe(ApiConnectionTestStatus status) => status switch
    {
        ApiConnectionTestStatus.Success => "连接成功",
        ApiConnectionTestStatus.ConfigError => "配置错误",
        ApiConnectionTestStatus.InvalidKey => "API Key 无效或无权限",
        ApiConnectionTestStatus.NotFound => "Endpoint 或模型名错误",
        ApiConnectionTestStatus.RateLimited => "请求频率或额度限制",
        ApiConnectionTestStatus.Timeout => "请求超时",
        ApiConnectionTestStatus.NetworkError => "网络错误",
        ApiConnectionTestStatus.ServerError => "服务端错误",
        _ => "HTTP 错误",
    };

    /// <summary>一行式结果摘要（成功含模型 / 耗时 / token；失败含状态与脱敏消息）。</summary>
    public static string Format(ApiConnectionTestResult result)
    {
        if (result.Success)
        {
            var text = $"连接成功｜Model: {result.ResponseModel ?? "(未返回)"}｜耗时: {result.DurationMs} ms";
            if (result.InputTokens is not null || result.OutputTokens is not null)
            {
                text += $"｜Input: {result.InputTokens?.ToString() ?? "—"} / Output: {result.OutputTokens?.ToString() ?? "—"} Token";
            }

            return text;
        }

        return $"连接失败（{Describe(result.Status)}）: {result.Message}";
    }
}

