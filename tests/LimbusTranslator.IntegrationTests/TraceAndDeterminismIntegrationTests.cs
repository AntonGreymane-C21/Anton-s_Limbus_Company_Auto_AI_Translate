using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.IntegrationTests.Support;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// 第6轮：Trace 与确定性（Scenario H 与“重复运行结果一致”）。
/// </summary>
[Collection(SqliteCollection.Name)]
public class TraceAndDeterminismIntegrationTests
{
    [Fact]
    public async Task ScenarioH_Trace完整且脱敏()
    {
        using var fixture = IntegrationFixture.Create();
        using var harness = PipelineHarness.Create(fixture);

        await harness.RunAsync();

        var tracePath = harness.TraceWriter.FilePath!;
        Assert.True(File.Exists(tracePath));

        var lines = File.ReadAllLines(tracePath).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        Assert.Equal(2, lines.Count);   // General 与 StoryData 各一次请求

        var entries = lines
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();

        foreach (var entry in entries)
        {
            Assert.Equal(harness.RunId, entry.GetProperty("runId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("requestId").GetString()));
            Assert.StartsWith(RequestFingerprintBuilder.FingerprintPrefix + ":", entry.GetProperty("fingerprint").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("promptHash").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("contextHash").GetString()));
            Assert.True(entry.GetProperty("success").GetBoolean());
            Assert.False(entry.GetProperty("cacheHit").GetBoolean());      // 第一次运行全部未命中
            Assert.True(entry.GetProperty("networkCalled").GetBoolean());
            Assert.Equal(10, entry.GetProperty("inputTokens").GetInt32());
            Assert.Equal(20, entry.GetProperty("outputTokens").GetInt32());
            Assert.Equal(30, entry.GetProperty("totalTokens").GetInt32());
            Assert.NotEmpty(entry.GetProperty("unitKeys").EnumerateArray().ToList());
        }

        // 有上下文的请求与无上下文的请求 ContextHash 必须不同
        var storyEntry = entries.Single(e => e.GetProperty("stageId").GetString() == "StoryData/1D101A.json");
        var generalEntry = entries.Single(e => e.GetProperty("stageId").GetString() == "General.json");
        Assert.NotEqual(
            storyEntry.GetProperty("contextHash").GetString(),
            generalEntry.GetProperty("contextHash").GetString());

        // 脱敏：不出现 API Key / Authorization / 完整源文
        var raw = File.ReadAllText(tracePath);
        Assert.DoesNotContain("fixture-key-value", raw);
        Assert.DoesNotContain("Authorization", raw);
        Assert.DoesNotContain("Line one needs translation now.", raw);
    }

    [Fact]
    public async Task 确定性_两次独立运行的关键结果一致()
    {
        var first = await RunSignatureAsync();
        var second = await RunSignatureAsync();

        Assert.Equal(first.UnitSignatures, second.UnitSignatures);
        Assert.Equal(first.StoryContextHash, second.StoryContextHash);
        Assert.Equal(first.GateStatus, second.GateStatus);
        Assert.Equal(first.ManifestStats, second.ManifestStats);
        Assert.Equal(first.Fingerprints, second.Fingerprints);

        // 允许不同的东西：RunId / RequestId / ManifestId / 时间戳
        Assert.NotEqual(first.RunId, second.RunId);
    }

    private sealed record RunSignature(
        IReadOnlyList<string> UnitSignatures,
        string StoryContextHash,
        string GateStatus,
        string ManifestStats,
        IReadOnlyList<string> Fingerprints,
        string RunId);

    private static async Task<RunSignature> RunSignatureAsync()
    {
        using var fixture = IntegrationFixture.Create();
        using var harness = PipelineHarness.Create(fixture);

        var run = await harness.RunAsync();

        var unitSignatures = run.AllEntries
            .Select(e => $"{e.Key}|{e.Translation}|{e.Provenance}|{e.NeedsReview}|{e.TmMatchType}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        var storyCurrent = run.AllEntries.Single(e => e.Key.ToString() == "StoryData/1D101A.json|1|dataList[1].content");
        var storyContext = run.StoryContexts.FirstOrDefault(c => c.HasNeighbors);
        Assert.NotNull(storyContext);
        Assert.NotNull(storyCurrent);

        var storyContextHash = LimbusTranslator.Infrastructure.Caching.RequestFingerprintBuilder.Sha256Hex(
            LimbusTranslator.Infrastructure.DeepSeek.DeepSeekRequestComposer.BuildContextJson(storyContext));

        var manifestStats = string.Join(
            "|",
            run.Manifest.IsComplete,
            run.Manifest.RequestedFileCount,
            run.Manifest.WrittenFileCount,
            run.Manifest.VerifiedEntryCount,
            run.Manifest.ErrorCount,
            run.Manifest.WarningCount,
            run.Manifest.NeedsReviewCount);

        var fingerprints = File.ReadAllLines(harness.TraceWriter.FilePath!)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.GetProperty("fingerprint").GetString()!)
            .Where(f => f is not null)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        return new RunSignature(
            unitSignatures,
            storyContextHash,
            run.Manifest.ReleaseGateStatus.ToString(),
            manifestStats,
            fingerprints,
            harness.RunId);
    }
}
