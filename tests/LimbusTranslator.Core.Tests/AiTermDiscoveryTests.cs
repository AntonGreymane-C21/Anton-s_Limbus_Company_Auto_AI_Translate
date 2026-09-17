using System.Net;
using System.Text;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.10轮：**AI 找生词** 回归。
///
/// 用户诉求：原来的本地扫描是"大写即候选 + 停用词名单"，名单外的常用词会漏进来；
/// 改为把选中文件的文本交给 AI，由 AI 判断"哪些词它不认识 / 不确定"，并给出建议译名与原因。
/// 本文件只测**不花钱**的部分（切块 / 解析 / 提示词约束 / 取消），真实网络调用用假 HTTP 处理器拦截。
/// </summary>
public sealed class AiTermDiscoveryTests
{
    // ───────── 切块 ─────────

    [Fact]
    public void 切块_按字符预算分组且跳过空文本()
    {
        var texts = new[] { new string('a', 5), "   ", new string('b', 5), new string('c', 5) };

        var chunks = TermExplainer.ChunkTexts(texts, maxCharsPerRequest: 10);

        Assert.Equal(2, chunks.Count);                       // 5 + 5 ⇒ 一组；再 5 ⇒ 另一组
        Assert.Equal(2, chunks[0].Count);
        Assert.Single(chunks[1]);
    }

    [Fact]
    public void 切块_单条超长文本独占一批()
    {
        var texts = new[] { new string('x', 50), new string('y', 3) };

        var chunks = TermExplainer.ChunkTexts(texts, maxCharsPerRequest: 10);

        Assert.Equal(2, chunks.Count);
        Assert.Single(chunks[0]);
        Assert.Single(chunks[1]);
    }

    // ───────── 解析（用假 HTTP 处理器，不发真实请求）─────────

    [Fact]
    public async Task 找生词_解析模型的词表与建议译名()
    {
        const string inner = """{"terms":[{"original":"Nursefather","suggested_translation":"护父","reason":"自造词"},{"original":"Pinky","suggested_translation":"\"小指\"","reason":"组织名"}]}""";
        var handler = new FakeHandler(inner);

        using var explainer = new TermExplainer(Options(), handler);
        var discovered = await explainer.DiscoverUnknownTermsAsync(new[] { "The Nursefathers arrived." });

        Assert.Equal(2, discovered.Count);
        Assert.Equal("Nursefather", discovered[0].Original);
        Assert.Equal("护父", discovered[0].SuggestedTranslation);
        Assert.Equal("自造词", discovered[0].Reason);
        Assert.Equal("小指", discovered[1].SuggestedTranslation);   // 引号被清理
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task 找生词_空词表返回空且不报错()
    {
        var handler = new FakeHandler("""{"terms":[]}""");

        using var explainer = new TermExplainer(Options(), handler);
        var discovered = await explainer.DiscoverUnknownTermsAsync(new[] { "Hello there." });

        Assert.Empty(discovered);
    }

    [Fact]
    public async Task 找生词_多块文本会分成多次请求()
    {
        var handler = new FakeHandler("""{"terms":[]}""");
        var big = new string('a', TermExplainer.MaxCharsPerDiscoveryRequest + 1);

        using var explainer = new TermExplainer(Options(), handler);
        await explainer.DiscoverUnknownTermsAsync(new[] { big, "short text" });

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task 找生词_响应不是JSON时必须报错()
    {
        var handler = new FakeHandler("这不是 JSON");

        using var explainer = new TermExplainer(Options(), handler);

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            explainer.DiscoverUnknownTermsAsync(new[] { "Hello." }));
    }

    [Fact]
    public async Task 找生词_取消必须原样向外传播()
    {
        var handler = new FakeHandler("""{"terms":[]}""");
        using var explainer = new TermExplainer(Options(), handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            explainer.DiscoverUnknownTermsAsync(new[] { "Hello." }, cancellationToken: cts.Token));
    }

    // ───────── 提示词约束（这是"常用词不再进来"的关键）─────────

    [Fact]
    public void 找生词提示词必须显式排除普通常用词()
    {
        var prompt = TermExplainer.DiscoverSystemPrompt;

        Assert.Contains("不认识", prompt);
        Assert.Contains("不要列出普通常用词", prompt);
        Assert.Contains("original", prompt);
        Assert.Contains("suggested_translation", prompt);
        Assert.Contains("json", prompt);
    }

    private static DeepSeekOptions Options() => new()
    {
        ApiKey = "sk-test",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "m",
        MaxRetry = 0,
        TimeoutSeconds = 30,
    };

    /// <summary>假 HTTP 处理器：记录请求次数并返回脚本化响应（绝不访问网络）。</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly string _innerContent;

        public FakeHandler(string innerContent) => _innerContent = innerContent;

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;

            var envelope = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = _innerContent } } },
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(envelope, Encoding.UTF8, "application/json"),
            });
        }
    }
}
