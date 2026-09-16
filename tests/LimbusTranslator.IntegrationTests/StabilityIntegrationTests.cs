using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.IntegrationTests.Support;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// 第7轮（T-4 / T-5）集成测试：配置 fail-closed 与空源文不入队。
///
/// 全部使用临时目录与临时 SQLite；不访问真实 API / 真实数据库 / 真实游戏目录。
/// </summary>
[Collection(SqliteCollection.Name)]
public class StabilityIntegrationTests
{
    private const string GeneralCaseAKey = "General.json|3|dataList[2].name";
    private const string GeneralCaseBKey = "General.json|2|dataList[1].name";
    private const string GeneralNormalKey = "General.json|4|dataList[3].name";

    [Fact]
    public void 损坏DeepSeek配置_翻译任务拒绝启动_且零写入()
    {
        using var fixture = IntegrationFixture.Create();
        File.WriteAllText(
            Path.Combine(fixture.ConfigDir, "appsettings.json"),
            "{ \"deepSeek\": { \"apiKey\": ");

        var settings = AppSettingsLoader.LoadProviderSettings(fixture.ConfigDir);
        var logs = new List<string>();
        var bootstrap = TranslationRunBootstrap.CreateProvider(
            settings, fixture.ConfigDir, cacheServices: null, log: logs.Add);

        Assert.False(settings.Success);
        Assert.False(bootstrap.CanStart);
        Assert.Null(bootstrap.Provider);                                        // 没有 Mock 兜底
        Assert.Contains(logs, l => l.Contains("[错误]"));
        Assert.DoesNotContain(logs, l => l.Contains("Mock（测试模式）"));

        // 拒绝启动 ⇒ 0 个 Provider 调用、0 条 TM、0 条 request_cache（连数据库文件都没有创建）
        Assert.False(File.Exists(fixture.TmDbPath));
        Assert.False(Directory.EnumerateFiles(fixture.OutputDir).Any());
    }

    [Fact]
    public void 显式Mock配置_允许启动且日志明显()
    {
        using var fixture = IntegrationFixture.Create();
        File.WriteAllText(
            Path.Combine(fixture.ConfigDir, "appsettings.json"),
            """{ "provider": "mock" }""");

        var logs = new List<string>();
        var bootstrap = TranslationRunBootstrap.CreateProvider(
            AppSettingsLoader.LoadProviderSettings(fixture.ConfigDir),
            fixture.ConfigDir,
            cacheServices: null,
            log: logs.Add);

        Assert.True(bootstrap.CanStart);
        Assert.True(bootstrap.IsMock);
        Assert.Contains(logs, l => l.Contains("当前翻译Provider：Mock（测试模式）"));
    }

    [Fact]
    public async Task 空源文不调用Provider_且Merge结构保留()
    {
        using var fixture = IntegrationFixture.Create();
        fixture.WriteEmptySourceFiles();
        using var harness = PipelineHarness.Create(fixture);

        var run = await harness.RunAsync();

        // 1) 空源文绝不进入 Provider 请求（含纯空白）
        Assert.DoesNotContain(harness.AllRequestItems, i => string.IsNullOrWhiteSpace(i.Source));
        var generalItems = harness.AllRequestItems
            .Where(i => i.Id.StartsWith("General.json|", StringComparison.Ordinal))
            .ToList();
        Assert.Single(generalItems);
        Assert.Equal(GeneralNormalKey, generalItems[0].Id);

        // 2) 情况 A：旧中文被保留；情况 B：译文维持空串
        var caseA = run.AllEntries.Single(e => e.Key.ToString() == GeneralCaseAKey);
        Assert.Equal("空字段应保留的旧中文", caseA.Translation);
        Assert.Equal(TranslationSource.Inherited, caseA.Provenance);
        Assert.False(caseA.NeedsReview);

        var caseB = run.AllEntries.Single(e => e.Key.ToString() == GeneralCaseBKey);
        Assert.Equal(string.Empty, caseB.Translation);
        Assert.Equal(TranslationMemoryMatchType.None, caseB.TmMatchType);

        // 3) 空源文不写 TM（空哈希同样不可复用）
        var emptyHash = SqliteTranslationMemory.ComputeSourceHash(string.Empty);
        Assert.Null(harness.Memory.FindExactUnit(caseB.Key, emptyHash));
        Assert.Null(harness.Memory.FindCrossUnitSource(emptyHash));

        // 4) Merge 结构保留：空字段仍为空、情况 A 为旧中文、正常条目为 Fake 译文
        using var output = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(fixture.OutputDir, "General.json")));
        var list = output.RootElement.GetProperty("dataList");
        Assert.Equal(string.Empty, list[1].GetProperty("name").GetString());
        Assert.Equal("空字段应保留的旧中文", list[2].GetProperty("name").GetString());
        Assert.Equal("[译]Real new sentence", list[3].GetProperty("name").GetString());
        Assert.True(run.Output.IsComplete);
    }
}
