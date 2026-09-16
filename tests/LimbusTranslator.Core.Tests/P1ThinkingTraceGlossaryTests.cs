using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Translation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P1轮（主线纠偏）：**Adaptive Thinking 真正进入最终请求** + **Trace 四模式字段** +
/// **Glossary 多源匹配 / MatchedTerms 单次共享** 的 Agent 级验收。
///
/// 全部走真实链（生产计划 → ApplyToEntries → Agent → 真实 DeepSeekTranslationProvider +
/// 注入的 Fake Batch Client → Trace），Fake Provider / Fake Client，绝不访问网络。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class P1ThinkingTraceGlossaryTests : IDisposable
{
    private const string KoreanA = "안녕하세요";
    private const string KoreanB = "반갑습니다";
    private const string EnglishC = "Hello";
    private const string EnglishChanged = "Hello changed";
    private const string JapaneseC = "こんにちは";

    private readonly FourModeAgentE2EHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static Func<TranslationCacheServices, ITranslationProvider> DeepSeekFactory(
        FakeE2EBatchClient client,
        TranslationMode mode)
        => services => new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-e2e-fake",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "e2e-test-model",
                MaxRetry = 0,
            },
            configDir: null,
            batchOptions: null,
            cacheServices: services,
            client: client,
            translationMode: mode);

    private static ActiveGlossarySnapshot Glossary(params (string Term, string Translation)[] terms)
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, translation) in terms)
        {
            entries[term] = new GlossaryEntry { Translation = translation, Locked = true };
        }

        return ActiveGlossarySnapshot.FromEntries(entries, sourcePath: null);
    }

    private static IReadOnlyList<JsonElement> TraceOf(AgentE2ERun run) => run.Trace;

    private static JsonElement SingleTrace(AgentE2ERun run) => Assert.Single(TraceOf(run));

    private static bool? TraceBool(JsonElement trace, string property)
        => trace.TryGetProperty(property, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetBoolean()
            : null;

    private static string? TraceString(JsonElement trace, string property)
        => trace.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // ───────── Adaptive Thinking：最终请求证据 ─────────

    [Fact]
    public async Task Thinking最终请求_KRONLY必须开启思考()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null),
            new E2ESources(new[] { KoreanB }, new[] { EnglishC }, null));

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanOnly,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanOnly));

        Assert.Equal(1, client.BatchCallCount);
        Assert.Equal(TranslationMode.KoreanOnly, Assert.Single(client.Modes));
        var thinking = Assert.Single(client.Thinkings);
        Assert.NotNull(thinking);
        Assert.True(thinking!.Enabled);

        // Trace 与最终请求一致
        var trace = SingleTrace(run);
        Assert.Equal(ThinkingPolicyReasons.KoreanCanonical, TraceString(trace, "thinkingPolicyReason"));

        // 第9.0B 最终轮：KR_ONLY 的韩文就是原文，不存在「参考译本缺失 → 回退韩文」
        Assert.False(TraceBool(trace, "usedKoreanFallback"));
        Assert.Equal("ko", TraceString(trace, "effectiveSourceLanguage"));
    }

    [Fact]
    public async Task Trace字段_KREN参考译本缺失回退韩文时为true()
    {
        // EN 缺失 ⇒ KR_EN 回退韩文：此时 usedKoreanFallback 必须为 true（与 KR_ONLY 区分）
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, null, null),
            new E2ESources(new[] { KoreanB }, null, null));

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanEnglish));

        var trace = SingleTrace(run);
        Assert.True(TraceBool(trace, "usedKoreanFallback"));
        Assert.Equal("ko", TraceString(trace, "effectiveSourceLanguage"));
        Assert.Equal(ThinkingPolicyReasons.KoreanCanonical, TraceString(trace, "thinkingPolicyReason"));
    }

    [Fact]
    public async Task Thinking最终请求_KR回退韩文必须开启思考()
    {
        // JP 缺失 ⇒ KR_JP 回退韩文（Effective = Korean）
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null),
            new E2ESources(new[] { KoreanB }, new[] { EnglishC }, null));

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanJapanese,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanJapanese));

        var thinking = Assert.Single(client.Thinkings);
        Assert.NotNull(thinking);
        Assert.True(thinking!.Enabled);

        var trace = SingleTrace(run);
        Assert.Equal("ko", TraceString(trace, "effectiveSourceLanguage"));
        Assert.True(TraceBool(trace, "usedKoreanFallback"));
        Assert.Equal(TranslationMode.KoreanJapanese, Assert.Single(client.Modes));   // 模式不得被改写
    }

    [Fact]
    public async Task Thinking最终请求_ENONLY普通英文保持既有策略()
    {
        _harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null));
        var capture = _harness.Capture();

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.EnglishOnly),
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { EnglishC });

        Assert.Equal(1, client.BatchCallCount);
        var thinking = Assert.Single(client.Thinkings);
        Assert.NotNull(thinking);
        Assert.False(thinking!.Enabled);                                        // 普通英文 → 既有策略（OFF）
        Assert.Equal(ThinkingPolicyReasons.DefaultOff, TraceString(SingleTrace(run), "thinkingPolicyReason"));
    }

    // ───────── Trace 四模式字段 ─────────

    [Fact]
    public async Task Trace字段_KREN写入模式与生效语言()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { EnglishC }, new[] { JapaneseC }),
            new E2ESources(new[] { KoreanB }, new[] { EnglishC }, new[] { JapaneseC }));

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanEnglish));

        var trace = SingleTrace(run);
        Assert.Equal("kr_en", TraceString(trace, "translationMode"));
        Assert.Equal("en", TraceString(trace, "effectiveSourceLanguage"));
        Assert.False(TraceBool(trace, "usedKoreanFallback"));
        Assert.True(TraceBool(trace, "canonicalKoreanPresent"));
        Assert.True(TraceBool(trace, "canonicalChanged"));      // KR A → B
        Assert.False(TraceBool(trace, "englishChanged"));       // EN 未变
        Assert.False(TraceBool(trace, "japaneseChanged"));      // JP 未变
    }

    [Fact]
    public async Task Trace字段_ENONLY的Canonical字段必须为N_A()
    {
        _harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null));
        var capture = _harness.Capture();

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.EnglishOnly),
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { EnglishC });

        var trace = SingleTrace(run);
        Assert.Equal("en_only", TraceString(trace, "translationMode"));
        Assert.Equal("en", TraceString(trace, "effectiveSourceLanguage"));
        // EN_ONLY 根本不使用 Canonical ⇒ 必须是 null（N/A），不得写 false
        Assert.Null(TraceBool(trace, "canonicalKoreanPresent"));
        Assert.Null(TraceBool(trace, "canonicalChanged"));
        Assert.Null(TraceBool(trace, "englishChanged"));
        Assert.Null(TraceBool(trace, "japaneseChanged"));
    }

    [Fact]
    public async Task Trace字段_参考译本变化只影响诊断字段()
    {
        // KR 未变 + EN 变 ⇒ 动作仍由 Canonical 决定；Trace 必须能看出 englishChanged=true / japaneseChanged=false。
        _harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishC }, new[] { JapaneseC }));
        var baseline = _harness.Capture();
        _harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishChanged }, new[] { JapaneseC }));
        var changed = _harness.Capture();
        Assert.NotNull(baseline);

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            changed,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanEnglish));

        Assert.Equal(1, client.BatchCallCount);
        var trace = SingleTrace(run);
        Assert.True(TraceBool(trace, "englishChanged"));
        Assert.False(TraceBool(trace, "japaneseChanged"));
    }

    // ───────── Glossary 多源匹配 + MatchedTerms 单次共享 ─────────

    [Fact]
    public async Task Glossary_KREN按EN与KR双源匹配且Prompt与Trace共享()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null),
            new E2ESources(new[] { KoreanB }, new[] { EnglishC }, null));

        var glossary = Glossary(("Hello", "你好"), ("반갑습니다", "再见"));
        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: glossary);

        // ① 计划按 KR_EN 的**多源**（EN 参考 + KR 原文）匹配到两条术语
        var entry = Assert.Single(run.Plan.OutputEntries);
        Assert.Equal(1, run.Plan.MatchedTermsInjectedCount);
        Assert.NotNull(entry.MatchedTerms);
        var sources = entry.MatchedTerms!.Select(term => term.Source).OrderBy(name => name, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "Hello", "반갑습니다" }, sources);

        // ② Prompt 使用同一份（不重新匹配）
        //    第9.0C.2轮：Fake 批量客户端返回的译文违反锁定术语 ⇒ 会追加一次**修正请求**
        //    （修正请求不注入术语表段落），因此这里只断言「翻译请求」那一条。
        var prompt = Assert.Single(client.GlossaryPrompts, p => !string.IsNullOrEmpty(p));
        Assert.Contains("Hello", prompt);
        Assert.Contains("반갑습니다", prompt);

        // ③ Trace 记录同一份（取携带术语摘要的那条 Trace）
        var traceTerms = TraceOf(run)
            .Select(trace => TraceString(trace, "glossaryTerms"))
            .FirstOrDefault(value => value is not null);
        Assert.NotNull(traceTerms);
        Assert.Contains("Hello", traceTerms);
        Assert.Contains("반갑습니다", traceTerms);
    }

    [Fact]
    public async Task Glossary_ENONLY只按英文源匹配()
    {
        _harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null));
        var capture = _harness.Capture();

        var glossary = Glossary(("Hello", "你好"), ("반갑습니다", "再见"));
        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.EnglishOnly),
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { EnglishC },
            glossarySnapshot: glossary);

        var entry = Assert.Single(run.Plan.OutputEntries);
        Assert.Null(entry.CanonicalKoreanText);                       // EN_ONLY 不注入 Canonical
        Assert.Equal(new[] { "Hello" }, entry.MatchedTerms!.Select(term => term.Source).ToArray());
        // 第9.0C.2轮：违反锁定术语会追加一次修正请求（不注入术语表段落）⇒ 只看翻译请求那一条
        var prompt = Assert.Single(client.GlossaryPrompts, p => !string.IsNullOrEmpty(p));
        Assert.Contains("Hello", prompt);
        Assert.DoesNotContain("반갑습니다", prompt);
    }

    [Fact]
    public async Task MatchedTerms单次共享_Validator没有自己的术语表也能判定术语()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null),
            new E2ESources(new[] { KoreanB }, new[] { EnglishC }, null));

        var glossary = Glossary(("Hello", "你好"));
        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            DeepSeekFactory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: glossary);

        var entry = Assert.Single(run.Plan.OutputEntries);
        // Fake Client 返回「测试译文」（不含规定译法）⇒ 必须由 TerminologyValidator 依据**共享的** MatchedTerms 判定
        Assert.Equal("测试译文", entry.Translation);
        Assert.Contains(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
        Assert.True(entry.NeedsReview);
    }
}
