using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.4轮：**术语库保存后热更新**验收（不重新 Analyze、不重新 Diff）。
///
/// 机制事实：<c>TranslationRunBootstrap.CreateProvider</c> 在**每次「开始汉化」时**都会调用
/// <c>ActiveGlossarySnapshot.LoadWithParatranz(configDir, …)</c> 重新读盘，
/// 随后 <c>ProductionTranslationPlanBuilder.Build</c> 用该快照重新注入 <c>MatchedTerms</c>。
/// 因此「保存术语 → 直接开始汉化」本来就应当用新术语；本文件把这条承诺变成可执行证据。
/// </summary>
public sealed class GlossaryHotReloadTests : IDisposable
{
    private const string Source = "The Nursefathers who have died.";

    private readonly string _configDir;

    public GlossaryHotReloadTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "lt_hotreload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private void WriteGlossary(params (string Term, string Translation, bool Locked)[] terms)
    {
        var payload = terms.ToDictionary(
            term => term.Term,
            term => new { translation = term.Translation, locked = term.Locked });
        File.WriteAllText(
            Path.Combine(_configDir, "glossary.json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ActiveGlossarySnapshot Snapshot(params (string Term, string Translation, bool Locked)[] terms)
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, translation, locked) in terms)
        {
            entries[term] = new GlossaryEntry { Translation = translation, Locked = locked };
        }

        return ActiveGlossarySnapshot.FromEntries(entries, sourcePath: null);
    }

    // ───────── §二十一 / §二十二：快照生命周期 = 每次运行重新读盘 ─────────

    [Fact]
    public void 术语快照_每次加载都重新读盘()
    {
        WriteGlossary(("Nursefather", "护父", true));
        var first = ActiveGlossarySnapshot.Load(_configDir);
        Assert.Equal(1, first.Count);
        Assert.Equal("护父", first.Entries["Nursefather"].Translation);

        // 用户保存新术语（新增 + 修改 + 删除）—— 不重新 Analyze
        WriteGlossary(("Nursefather", "护工之父", false), ("Mirror Dungeon", "镜面迷宫", true));
        var second = ActiveGlossarySnapshot.Load(_configDir);

        Assert.Equal(2, second.Count);
        Assert.Equal("护工之父", second.Entries["Nursefather"].Translation);
        Assert.False(second.Entries["Nursefather"].Locked);
        Assert.True(second.Entries["Mirror Dungeon"].Locked);
    }

    [Fact]
    public void 每次运行都会重新读盘加载术语_接线守卫()
    {
        var root = FindRepositoryRoot();
        var bootstrap = File.ReadAllText(Path.Combine(
            root, "src", "LimbusTranslator.Infrastructure", "Services", "TranslationRunBootstrap.cs"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs"));

        // ① 每次 CreateProvider 都从磁盘重新加载术语快照
        Assert.Contains("ActiveGlossarySnapshot.LoadWithParatranz(", bootstrap);
        // ② 「开始汉化」在运行开始时才创建 Provider/快照（因此保存过的术语必然生效，无需重新分析）
        Assert.Contains("TranslationRunBootstrap.CreateProvider(", viewModel);
        Assert.Contains("_activeGlossarySnapshot = bootstrap.GlossarySnapshot;", viewModel);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（LimbusTranslator.slnx）");
    }
}

/// <summary>
/// 第9.0C.4轮：术语热更新在**真实生产链**上的行为（Fake 批量客户端，0 真实网络请求）。
///
/// 场景固定为「Analyze 之后用户才保存术语，且不重新 Analyze」：每次运行使用新的快照重新注入 MatchedTerms。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class GlossaryHotReloadChainTests : IDisposable
{
    private const string EnglishSource = "The Nursefathers who have died.";

    private readonly FourModeAgentE2EHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static ActiveGlossarySnapshot Snapshot(params (string Term, string Translation, bool Locked)[] terms)
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, translation, locked) in terms)
        {
            entries[term] = new GlossaryEntry { Translation = translation, Locked = locked };
        }

        return ActiveGlossarySnapshot.FromEntries(entries, sourcePath: null);
    }

    private static Func<TranslationCacheServices, ITranslationProvider> Factory(FakeE2EBatchClient client)
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
            translationMode: TranslationMode.EnglishOnly);

    private static async Task<(string Prompt, DiffEntry Entry, FakeE2EBatchClient Client)> RunAsync(
        FourModeAgentE2EHarness harness,
        ActiveGlossarySnapshot? snapshot,
        string translation)
    {
        harness.WriteAll(new E2ESources(new[] { "핑키 너스파더" }, new[] { EnglishSource }, null));
        var capture = harness.Capture();
        var client = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => translation };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            Factory(client),
            oldEnglish: new[] { EnglishSource },
            newEnglish: new[] { EnglishSource },
            glossarySnapshot: snapshot);

        var prompt = client.GlossaryPrompts.FirstOrDefault(p => !string.IsNullOrEmpty(p)) ?? string.Empty;
        return (prompt, Assert.Single(run.Plan.OutputEntries), client);
    }

    // ───────── §三十二：Analyze 后新增术语 → Prompt 立即包含 ─────────

    [Fact]
    public async Task 术语热更新_未命中时Prompt不含术语_保存后立即包含()
    {
        // ① 分析时术语库还没有 Nursefather ⇒ Prompt 不含该术语
        using var before = new FourModeAgentE2EHarness();
        var (promptBefore, entryBefore, _) = await RunAsync(before, Snapshot(), "死去的护理之父们");
        Assert.DoesNotContain("Nursefather", promptBefore);
        // 快照为空 ⇒ 计划不会注入 MatchedTerms（保持 null）
        Assert.True(entryBefore.MatchedTerms is null || entryBefore.MatchedTerms.Count == 0);

        // ② 用户保存术语后直接再翻译（不重新 Analyze）⇒ Prompt 必须包含锁定术语
        using var after = new FourModeAgentE2EHarness();
        var (promptAfter, entryAfter, _) = await RunAsync(after, Snapshot(("Nursefather", "护父", true)), "死去的护理之父们");
        Assert.Contains("Nursefather", promptAfter);
        Assert.Contains("护父", promptAfter);

        var term = Assert.Single(entryAfter.MatchedTerms!);
        Assert.Equal("Nursefather", term.Source);
        Assert.Equal("护父", term.Target);
        Assert.True(term.Locked);
    }

    // ───────── §三十四 / §三十五：改 Target / 删术语 立即生效 ─────────

    [Fact]
    public async Task 术语热更新_修改Target立即生效()
    {
        using var harness = new FourModeAgentE2EHarness();
        var (prompt, entry, _) = await RunAsync(harness, Snapshot(("Nursefather", "护工之父", true)), "死去的护工之父们");

        Assert.Contains("护工之父", prompt);
        Assert.Equal("护工之父", Assert.Single(entry.MatchedTerms!).Target);
    }

    [Fact]
    public async Task 术语热更新_删除术语立即生效()
    {
        using var harness = new FourModeAgentE2EHarness();
        var (prompt, entry, _) = await RunAsync(harness, Snapshot(), "死去的护理之父们");

        Assert.DoesNotContain("Nursefather", prompt);
        Assert.True(entry.MatchedTerms is null || entry.MatchedTerms.Count == 0);   // 删除后不得残留
        Assert.DoesNotContain(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
    }

    // ───────── §三十一 / §三十三：TM 命中路径也用最新术语 ─────────

    [Fact]
    public async Task 术语热更新_TM命中路径仍按最新术语修正()
    {
        // ① 无术语时翻译，并把"护理之父们"写进 TM
        using var harness = new FourModeAgentE2EHarness();
        var (_, entryBefore, _) = await RunAsync(harness, Snapshot(), "死去的护理之父们");
        Assert.Equal("死去的护理之父们", entryBefore.Translation);

        // ② 用户保存 Nursefather → 护父（Locked），不重新 Analyze，直接再翻译
        harness.WriteAll(new E2ESources(new[] { "핑키 너스파더" }, new[] { EnglishSource }, null));
        var capture = harness.Capture();
        var repairClient = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => "死去的护父们" };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            Factory(repairClient),
            oldEnglish: new[] { EnglishSource },
            newEnglish: new[] { EnglishSource },
            glossarySnapshot: Snapshot(("Nursefather", "护父", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, entry.TmMatchType);   // 确实走的是 TM 命中
        Assert.Equal(1, entry.TerminologyRepairAttempts);                        // 命中后仍按最新术语修正
        Assert.Contains("护父", entry.Translation!);
        Assert.DoesNotContain(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
    }
}
