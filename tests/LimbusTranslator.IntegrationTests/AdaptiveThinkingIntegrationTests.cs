using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.IntegrationTests.Support;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// 第8.75轮：自适应 Thinking 端到端集成测试。
///
/// 走完整主链：Parser → Diff → Context → Coordinator → Agent → 自适应 Thinking
/// → ProviderBatchBuilder → Fingerprint → FakeClient → Validator → TM。
/// 断言：FakeClient 实际收到 OFF 请求与 ON 请求，且**没有任何请求混合两种策略**。
/// </summary>
[Collection(SqliteCollection.Name)]
public class AdaptiveThinkingIntegrationTests
{
    private const string PlainKey = "General.json|2|dataList[1].name";
    private const string KoreanKey = "General.json|3|dataList[2].name";

    [Fact]
    public async Task 普通英文与韩文异常源_分别形成OFF与ON请求且不混合()
    {
        using var fixture = IntegrationFixture.Create();
        fixture.WriteAdaptiveThinkingFiles();
        using var harness = PipelineHarness.Create(fixture);

        var run = await harness.RunAsync();

        var calls = harness.Calls;
        Assert.True(calls.Count >= 2, "应至少产生 OFF 与 ON 两个请求");

        var offCalls = calls.Where(c => !c.Enabled).ToList();
        var onCalls = calls.Where(c => c.Enabled).ToList();
        Assert.NotEmpty(offCalls);
        Assert.NotEmpty(onCalls);

        // 普通英文只出现在 OFF 请求中
        Assert.Contains(offCalls, c => c.Ids.Contains(PlainKey));
        Assert.DoesNotContain(onCalls, c => c.Ids.Contains(PlainKey));

        // 韩文异常源只出现在 ON 请求中
        Assert.Contains(onCalls, c => c.Ids.Contains(KoreanKey));
        Assert.DoesNotContain(offCalls, c => c.Ids.Contains(KoreanKey));

        // StoryData（ON）不得出现在 OFF 请求中
        Assert.DoesNotContain(offCalls, c => c.Ids.Any(id => id.StartsWith("StoryData/")));

        // 没有请求同时包含"应 OFF"与"应 ON"的条目
        foreach (var call in calls)
        {
            var hasPlain = call.Ids.Contains(PlainKey);
            var hasOnWorthy = call.Ids.Contains(KoreanKey) || call.Ids.Any(id => id.StartsWith("StoryData/"));
            Assert.False(hasPlain && hasOnWorthy, "同一请求不得混合 ON 与 OFF 条目");
        }

        // 译文与 TM 仍然正常写入（FakeBatchClient 回显 "[译]" + 原文）
        Assert.Equal("[译]필립 싱클레어가 탈출장치로 후퇴", run.AllEntries.Single(e => e.Key.ToString() == KoreanKey).Translation);
        Assert.True(run.Output.IsComplete);
        Assert.True(harness.CountRows("translations") > 0);
    }

    [Fact]
    public async Task 自适应决策在重复运行中保持一致()
    {
        using var fixture = IntegrationFixture.Create();
        fixture.WriteAdaptiveThinkingFiles();

        using var first = PipelineHarness.Create(fixture);
        await first.RunAsync();
        var firstDecisions = ReadThinkingDecisions(first);

        first.ClearTranslations();

        using var second = PipelineHarness.Create(fixture);
        await second.RunAsync();
        var secondDecisions = ReadThinkingDecisions(second);

        // 第二次全部命中 request_cache（指纹一致）⇒ 不再调用 Provider，但 Thinking 决策必须一致
        Assert.Equal(0, second.ProviderCalls);
        Assert.Equal(firstDecisions, secondDecisions);
    }

    /// <summary>从 Trace 读取每个请求的 Thinking 决策（Cache Hit 也会记录）。</summary>
    private static string[] ReadThinkingDecisions(PipelineHarness harness)
    {
        var path = harness.TraceWriter.FilePath;
        Assert.NotNull(path);
        return System.IO.File.ReadLines(path!)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => System.Text.Json.JsonDocument.Parse(line).RootElement)
            .Select(e => (e.GetProperty("thinkingEnabled").GetBoolean(), e.GetProperty("itemCount").GetInt32()))
            .Select(x => $"{x.Item1}:{x.Item2}")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
    }
}
