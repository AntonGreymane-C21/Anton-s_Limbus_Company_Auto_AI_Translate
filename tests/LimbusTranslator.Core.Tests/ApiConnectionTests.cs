using System.Net;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Security;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.87轮：API「测试连接」诊断（错误映射 / 脱敏 / 极小探针）。
/// 全部使用 Fake HttpMessageHandler，禁止真实网络。
/// </summary>
public sealed class ApiConnectionTests
{
    private static DeepSeekOptions MakeOptions() => new()
    {
        ApiUrl = "https://api.deepseek.com/chat/completions",
        ApiKey = "sk-secret-key-1234567890abcd",
        Model = "deepseek-v4-flash",
        TimeoutSeconds = 20,
    };

    private static ApiConnectionTester MakeTester(HttpMessageHandler handler, DeepSeekOptions? options = null)
        => new(options ?? MakeOptions(), handler);

    private static string SuccessBody => JsonSerializer.Serialize(new
    {
        id = "resp-1",
        model = "deepseek-v4-flash",
        choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" } } },
        usage = new { prompt_tokens = 12, completion_tokens = 3, total_tokens = 15 },
    });

    [Fact]
    public async Task 成功_应返回模型与耗时与Token()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        var result = await MakeTester(handler).TestAsync();

        Assert.True(result.Success);
        Assert.Equal(ApiConnectionTestStatus.Success, result.Status);
        Assert.Equal("deepseek-v4-flash", result.ResponseModel);
        Assert.Equal(12, result.InputTokens);
        Assert.Equal(3, result.OutputTokens);
        Assert.Contains("连接成功", ApiConnectionStatusText.Format(result));
    }

    [Fact]
    public async Task 探针请求_必须极小且不携带游戏文本()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        await MakeTester(handler).TestAsync();

        Assert.NotNull(handler.LastBody);
        Assert.Contains(ApiConnectionTester.ProbePrompt, handler.LastBody!);
        Assert.Contains("\"max_tokens\":8", handler.LastBody!.Replace(" ", string.Empty));
        Assert.Contains("\"disabled\"", handler.LastBody!);
        // 探针不得请求翻译协议（不需要 JSON 输出协议）
        Assert.DoesNotContain("response_format", handler.LastBody!);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ApiConnectionTestStatus.InvalidKey)]
    [InlineData(HttpStatusCode.Forbidden, ApiConnectionTestStatus.InvalidKey)]
    [InlineData(HttpStatusCode.NotFound, ApiConnectionTestStatus.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiConnectionTestStatus.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, ApiConnectionTestStatus.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, ApiConnectionTestStatus.HttpError)]
    public async Task HTTP失败_应映射到稳定状态(HttpStatusCode status, ApiConnectionTestStatus expected)
    {
        var handler = new RecordingHandler(status, "{\"error\":{\"message\":\"bad\"}}");
        var result = await MakeTester(handler).TestAsync();

        Assert.False(result.Success);
        Assert.Equal(expected, result.Status);
        Assert.Equal((int)status, result.HttpStatusCode);
        Assert.Contains("（HTTP " + (int)status + "）", result.Message);
    }

    [Fact]
    public async Task 超时_应映射为Timeout()
    {
        var options = MakeOptions();
        options.TimeoutSeconds = 1;
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody, delayMs: 10000);

        var result = await MakeTester(handler, options).TestAsync();

        Assert.False(result.Success);
        Assert.Equal(ApiConnectionTestStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task 网络异常_应映射为NetworkError()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody, throwNetwork: true);
        var result = await MakeTester(handler).TestAsync();

        Assert.False(result.Success);
        Assert.Equal(ApiConnectionTestStatus.NetworkError, result.Status);
    }

    [Theory]
    [InlineData("", "key", "model")]
    [InlineData("not-a-url", "key", "model")]
    [InlineData("https://api.deepseek.com/chat/completions", "", "model")]
    [InlineData("https://api.deepseek.com/chat/completions", "key", "")]
    public async Task 配置错误_应不发请求直接返回ConfigError(string url, string key, string model)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, SuccessBody);
        var options = new DeepSeekOptions { ApiUrl = url, ApiKey = key, Model = model };

        var result = await MakeTester(handler, options).TestAsync();

        Assert.False(result.Success);
        Assert.Equal(ApiConnectionTestStatus.ConfigError, result.Status);
        Assert.Null(handler.LastBody);
    }

    [Fact]
    public async Task 响应不含choices_应视为不可解析()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"foo\":1}");
        var result = await MakeTester(handler).TestAsync();

        Assert.False(result.Success);
        Assert.Equal(ApiConnectionTestStatus.HttpError, result.Status);
        Assert.Contains("无法解析", result.Message);
    }

    // ── 脱敏 ─────────────────────────────────────────────────────────────

    [Fact]
    public void 密钥掩码_只保留尾部四位()
    {
        Assert.Equal("sk-****abcd", SecretRedactor.MaskKey("sk-secret-key-abcd"));
        Assert.Equal("(未配置)", SecretRedactor.MaskKey(null));
        Assert.Equal("****", SecretRedactor.MaskKey("abc"));
    }

    [Fact]
    public void 脱敏_应清除密钥与Bearer令牌()
    {
        var key = "sk-secret-key-1234567890abcd";
        var raw = $"Authorization: Bearer {key} failed for {key}";

        var redacted = SecretRedactor.Redact(raw, key);

        Assert.DoesNotContain(key, redacted);
        Assert.Contains("sk-****abcd", redacted);

        // 未显式传入的 Bearer 令牌也必须被清理（避免错误消息里出现别人的密钥）
        var other = SecretRedactor.Redact("Authorization: Bearer sk-other-token-9876543210xyz");
        Assert.DoesNotContain("sk-other-token-9876543210xyz", other);
        Assert.Contains("Bearer ****", other);
    }

    [Fact]
    public async Task 失败消息_不得泄露API密钥()
    {
        var key = "sk-secret-key-1234567890abcd";
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, $"{{\"error\":\"{key}\"}}");
        var options = MakeOptions();
        options.ApiKey = key;

        var result = await MakeTester(handler, options).TestAsync();

        Assert.False(result.Success);
        Assert.DoesNotContain(key, result.Message);
        Assert.DoesNotContain(key, ApiConnectionStatusText.Format(result));
    }

    /// <summary>可配置的假 HTTP handler（记录请求体，可注入状态码 / 延迟 / 网络异常）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly int _delayMs;
        private readonly bool _throwNetwork;

        public RecordingHandler(HttpStatusCode status, string body, int delayMs = 0, bool throwNetwork = false)
        {
            _status = status;
            _body = body;
            _delayMs = delayMs;
            _throwNetwork = throwNetwork;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            if (_throwNetwork)
            {
                throw new HttpRequestException("connection refused");
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}