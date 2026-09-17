using System.Net;
using System.Text;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.16轮：**确定性 API 拒绝（4xx）不得重试** + 错误提示可执行。
///
/// 真实故障：配置里 <c>maxTokens = 1000000</c>（超出服务端范围 [1, 393216]）⇒ DeepSeek 返回 400
/// <c>invalid_request_error</c>，而 <c>TermExplainer</c> 把非 2xx 一律包成 <c>HttpRequestException</c>
/// ⇒ 被当成可重试 ⇒ 白重试 5 次、退避 2/4/8/16 秒。
/// </summary>
public sealed class TermExplainerRetryPolicyTests
{
    private static DeepSeekOptions Options(int maxRetry)
        => new()
        {
            ApiKey = "sk-test",
            ApiUrl = "https://api.deepseek.com/chat/completions",
            Model = "test-model",
            MaxRetry = maxRetry,
            MaxTokens = 8192,
        };

    [Fact]
    public async Task 找生词_遇到400必须立即失败且不重试()
    {
        const string body =
            """{"error":{"message":"Invalid max_tokens value, the valid range of max_tokens is [1, 393216]","type":"invalid_request_error"}}""";
        var handler = new FailingHandler(HttpStatusCode.BadRequest, body);

        using var explainer = new TermExplainer(Options(maxRetry: 5), handler);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => explainer.DiscoverUnknownTermsAsync(new[] { "The Nursefathers arrived." }));

        Assert.Equal(1, handler.RequestCount);              // 确定性拒绝 ⇒ 只请求一次
        Assert.Contains("400", ex.Message, StringComparison.Ordinal);
        Assert.Contains("max_tokens", ex.Message, StringComparison.Ordinal);
        Assert.Contains("建议 8192", ex.Message, StringComparison.Ordinal);   // 可执行提示
    }

    [Fact]
    public async Task 找生词_遇到500仍应重试()
    {
        var handler = new FailingHandler(HttpStatusCode.InternalServerError, "server exploded");

        using var explainer = new TermExplainer(Options(maxRetry: 1), handler);

        // 重试循环的形状：最后一次尝试的异常会**直接向外抛**（不会变成"重试次数已用尽"），
        // 因此这里断言"确实重试了"而不是异常类型。
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => explainer.DiscoverUnknownTermsAsync(new[] { "The Nursefathers arrived." }));

        Assert.Equal(2, handler.RequestCount);              // 1 次 + 1 次重试
        Assert.Contains("500", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FailingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
