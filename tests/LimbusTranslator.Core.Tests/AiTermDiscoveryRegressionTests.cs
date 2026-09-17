using System.Net;
using System.Text;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.11轮：**真实故障回归**（用户实测：AI 找生词跑了 4 分 43 秒后失败，报"找生词响应为空"）。
///
/// 根因：找生词请求**没有关闭思考**，`thinking=always_on + reasoningEffort=high + maxTokens=8192`
/// 会把 max_tokens 吃满 ⇒ `content` 为空；而空响应又被当成"可重试"⇒ 白重试 6 次（≈4 分 43 秒）。
///
/// 修复断言：
///   ① 找生词的请求体必须带 `"thinking":{"type":"disabled"}`（且不发 reasoning_effort）；
///   ② 空响应属于**确定性失败** ⇒ 立即抛 <see cref="DeepSeekEmptyResponseException"/>，**只请求 1 次**。
/// </summary>
public sealed class AiTermDiscoveryRegressionTests
{
    private static DeepSeekOptions Options() => new()
    {
        ApiKey = "sk-test",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "m",
        MaxRetry = 5,                  // 故意设大：验证"空响应不会重试"
        TimeoutSeconds = 30,
        Thinking = true,
        ThinkingMode = TranslationThinkingMode.AlwaysOn,
        ReasoningEffort = "high",
    };

    [Fact]
    public async Task 找生词请求必须关闭思考()
    {
        var handler = new RecordingHandler("""{"terms":[]}""");

        using var explainer = new TermExplainer(Options(), handler);
        await explainer.DiscoverUnknownTermsAsync(new[] { "The Nursefathers arrived." });

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", handler.LastRequestBody);
        Assert.DoesNotContain("reasoning_effort", handler.LastRequestBody);
    }

    [Fact]
    public async Task 空响应必须立即失败且不重试()
    {
        var handler = new RecordingHandler(string.Empty);

        using var explainer = new TermExplainer(Options(), handler);

        await Assert.ThrowsAsync<DeepSeekEmptyResponseException>(() =>
            explainer.DiscoverUnknownTermsAsync(new[] { "Hello." }));

        Assert.Equal(1, handler.RequestCount);   // MaxRetry=5 也不重试（空响应是确定性失败）
    }

    /// <summary>记录请求体与次数的假处理器（绝不访问网络）。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly string _innerContent;

        public RecordingHandler(string innerContent) => _innerContent = innerContent;

        public int RequestCount { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = _innerContent } } },
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            };
        }
    }
}
