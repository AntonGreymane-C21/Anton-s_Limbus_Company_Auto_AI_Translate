using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P7轮：KR 权威模式下**旧中文解析范围**（修复 P2-η）的 E2E 验收。
///
/// 场景：Current KR 有 `KrOnly.json`，Current EN 没有该文件，旧中文目录里存在同名文件。
/// 老行为：旧中文只按「当前英文文件集合」解析 ⇒ KR-only 文件里的旧中文被漏掉 ⇒ 退化成 TranslateMissing。
/// 新行为：KR 三模式按 Current EN ∪ **Current KR 文件集** 解析 ⇒ 该文件按 UnitKey 正确 Inherit。
///
/// 全部走真实链路（DiffWorkflowService.Analyze → TryCapture → OldChineseScopeLoader →
/// ProductionTranslationPlanBuilder → Coordinator/TranslationAgent → Merge → ReleaseGate），
/// Fake Provider，TEMP 落盘。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class KrOnlyFileOldChineseScopeTests : IDisposable
{
    private const string KoreanA = "안녕하세요";
    private const string KoreanStable = "감사합니다";
    private const string EnglishC = "Hello";
    private const string CommonFile = "Items.json";
    private const string KrOnlyFile = "KrOnly.json";
    private const string FakeTranslation = "测试译文";

    private readonly string _root;

    public KrOnlyFileOldChineseScopeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_p7_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // 临时目录清理失败可忽略
        }
    }

    private sealed record Run(
        ProductionTranslationPlan Plan,
        OldChineseScopeResult Scope,
        FakeE2EProvider Provider,
        CoordinatorResult Coordinator,
        OutputMergeResult Merge,
        ReleaseGateResult Gate);

    private static string Items(params (int Id, string Name)[] records)
        => JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["dataList"] = records
                    .Select(record => new Dictionary<string, object?> { ["id"] = record.Id, ["name"] = record.Name })
                    .ToList(),
            },
            new JsonSerializerOptions { WriteIndented = true });

    private void WriteLanguage(SourceLanguage language, params (string File, string Json)[] files)
    {
        var directory = Path.Combine(_root, "Localize", SourceLanguageHelper.GetLocalizeDirectoryName(language));
        Directory.CreateDirectory(directory);
        foreach (var (file, json) in files)
        {
            File.WriteAllText(Path.Combine(directory, SourceLanguageHelper.GetFilePrefix(language) + file), json);
        }
    }

    private void WriteOldEnglish(params (string File, string Json)[] files)
        => WritePlain("old_en", "EN_", files);

    private void WriteOldChinese(params (string File, string Json)[] files)
        => WritePlain("old_zh", string.Empty, files);

    private void WritePlain(string subDirectory, string prefix, params (string File, string Json)[] files)
    {
        var directory = Path.Combine(_root, subDirectory);
        Directory.CreateDirectory(directory);
        foreach (var (file, json) in files)
        {
            File.WriteAllText(Path.Combine(directory, prefix + file), json);
        }
    }

    /// <summary>从某语言树里删除一个文件（模拟「整个文件被删除」）。</summary>
    private void DeleteLanguageFile(SourceLanguage language, string file)
    {
        var path = Path.Combine(
            _root,
            "Localize",
            SourceLanguageHelper.GetLocalizeDirectoryName(language),
            SourceLanguageHelper.GetFilePrefix(language) + file);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ConfigDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, "field_rules.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 config/field_rules.json");
    }

    private MultilingualCaptureResult CaptureBaseline()
        => ProductionTranslationPlanBuilder.TryCapture(
            _root,
            Path.Combine(_root, "Localize", "en"),
            ConfigDirectory(),
            preParsedEnglish: null,
            log: null)!;

    /// <summary>真实链路运行一次（KR 基线已由测试准备好）。</summary>
    private async Task<Run> RunAsync(TranslationMode mode, MultilingualCaptureResult? capture = null)
    {
        var configDir = ConfigDirectory();
        var newEnDir = Path.Combine(_root, "Localize", "en");
        var oldEnDir = Path.Combine(_root, "old_en");
        var oldZhDir = Path.Combine(_root, "old_zh");

        var diffResult = new DiffWorkflowService(configDir).Analyze(oldEnDir, oldZhDir, newEnDir, null);
        capture ??= CaptureBaseline();
        var scope = OldChineseScopeLoader.Load(mode, diffResult.OldChineseUnits, capture, oldZhDir, configDir);
        var plan = ProductionTranslationPlanBuilder.Build(mode, diffResult.Entries, capture, newEnDir, scope.Units);

        var provider = new FakeE2EProvider { Mode = mode, TranslationFactory = _ => FakeTranslation };
        using var memory = new SqliteTranslationMemory(
            new TranslationMemoryOptions { DatabasePath = Path.Combine(_root, "data", "cache", "tm.db") }, null);
        var coordinator = new Coordinator(provider, memory, maxConcurrentAgents: 1, maxConcurrentApiRequests: 1);
        var coordinatorResult = await coordinator.ExecuteAsync(plan.NeedTranslate);

        var merge = new MergeOutputService().MergeAllWithReport(
            plan.AuthoritativeDirectory,
            Coordinator.CollectTranslations(plan.OutputEntries),
            Path.Combine(_root, "output"),
            plan.ExpectedOutputKeys,
            plan.AuthoritativeLanguage);
        var gate = ReleaseGateService.Evaluate(
            plan.OutputEntries,
            null,
            null,
            new ReleaseGateKeySet
            {
                ExpectedKeys = plan.ExpectedOutputKeys,
                OutputKeys = merge.WrittenKeys,
                AuthoritativeSourceCode = SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage),
            });

        return new Run(plan, scope, provider, coordinatorResult, merge, gate);
    }

    /// <summary>
    /// 准备场景 A–D：先在「基线 KR（含 KR-only 文件）」上捕获，再写入当前 KR（内容相同 ⇒ KR 未变化）
    /// 并再次捕获（这样 Canonical Diff 才是 Unchanged，而不是首次基线迁移）。
    /// </summary>
    private MultilingualCaptureResult PrepareBaselineWithKrOnlyFile(string? krOnlyOldChinese)
    {
        WriteLanguage(SourceLanguage.Korean,
            (CommonFile, Items((1, KoreanA))),
            (KrOnlyFile, Items((1, KoreanA))));
        WriteLanguage(SourceLanguage.English, (CommonFile, Items((1, EnglishC))));
        WriteOldEnglish((CommonFile, Items((1, EnglishC))));
        WriteOldChinese(
            (CommonFile, Items((1, "旧中文公共"))),
            (KrOnlyFile, Items((1, krOnlyOldChinese ?? "旧中文专属"))));

        CaptureBaseline();   // 建立 Previous KR 基线

        // 当前 KR 与基线完全一致（KR 未变化）
        WriteLanguage(SourceLanguage.Korean,
            (CommonFile, Items((1, KoreanA))),
            (KrOnlyFile, Items((1, KoreanA))));
        return CaptureBaseline();
    }

    private static string KrOnlyUnitKey => $"{KrOnlyFile}|1|dataList[0].name";

    private static string CommonUnitKey => $"{CommonFile}|1|dataList[0].name";

    // ───────── A/B/C：KR 三模式，KR-only 文件 + 旧中文存在 ⇒ Inherit，ProviderCalls = 0 ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task ABC_KROnly文件旧中文存在时必须继承且不调用Provider(TranslationMode mode)
    {
        var capture = PrepareBaselineWithKrOnlyFile("旧中文专属");
        var run = await RunAsync(mode, capture);

        // 旧中文解析范围：KR-only 文件被额外解析（老行为不会）
        Assert.Contains(KrOnlyFile, run.Scope.AdditionalLogicalFiles);
        Assert.Equal(SourceLanguage.Korean, run.Scope.ScopeLanguage);

        Assert.Equal(2, run.Plan.InheritCount);
        Assert.Equal(0, run.Plan.TranslateMissingCount);
        Assert.Empty(run.Plan.NeedTranslate);
        Assert.Equal(0, run.Provider.CallCount);

        var krOnly = run.Plan.OutputEntries.Single(entry => entry.Key.ToString() == KrOnlyUnitKey);
        Assert.Equal(TranslationAction.Inherit, krOnly.Action);
        Assert.Equal("旧中文专属", krOnly.OldTranslation);
        Assert.Equal("旧中文专属", krOnly.Translation);
        Assert.Equal(TranslationSource.Inherited, krOnly.Provenance);

        // 继承结果真的进入最终 output
        Assert.Equal(2, run.Merge.WrittenEntryCount);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
        var output = Path.Combine(_root, "output", KrOnlyFile);
        Assert.True(File.Exists(output));
        Assert.Contains("旧中文专属", File.ReadAllText(output));
    }

    // ───────── D：KR-only 文件没有旧中文 ⇒ TranslateMissing 并进入 Agent ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task D_KROnly文件无旧中文时仍要缺译并进入Agent(TranslationMode mode)
    {
        // 旧中文里没有 KrOnly.json（只有公共文件）
        WriteLanguage(SourceLanguage.Korean,
            (CommonFile, Items((1, KoreanA))),
            (KrOnlyFile, Items((1, KoreanA))));
        WriteLanguage(SourceLanguage.English, (CommonFile, Items((1, EnglishC))));
        WriteOldEnglish((CommonFile, Items((1, EnglishC))));
        WriteOldChinese((CommonFile, Items((1, "旧中文公共"))));
        CaptureBaseline();                                  // 建立 Previous KR 基线
        WriteLanguage(SourceLanguage.Korean,
            (CommonFile, Items((1, KoreanA))),
            (KrOnlyFile, Items((1, KoreanA))));
        var capture = CaptureBaseline();                    // KR 未变化

        var run = await RunAsync(mode, capture);

        Assert.Empty(run.Scope.AdditionalLogicalFiles);
        Assert.Equal(1, run.Plan.InheritCount);              // 公共文件继承
        Assert.Equal(1, run.Plan.TranslateMissingCount);     // KR-only 文件缺译
        Assert.Equal(1, run.Provider.CallCount);
        Assert.Equal(KrOnlyUnitKey, Assert.Single(run.Provider.ReceivedEntries).UnitKey);

        var krOnly = run.Plan.OutputEntries.Single(entry => entry.Key.ToString() == KrOnlyUnitKey);
        Assert.Null(krOnly.OldTranslation);
        Assert.Equal(FakeTranslation, krOnly.Translation);
    }

    // ───────── E：EN_ONLY 忽略 KR-only 文件（即使旧中文有同名文件） ─────────

    [Fact]
    public async Task E_ENONLY不得因为KR多出文件而扩大范围()
    {
        var capture = PrepareBaselineWithKrOnlyFile("旧中文专属");
        var run = await RunAsync(TranslationMode.EnglishOnly, capture);

        Assert.Equal(SourceLanguage.English, run.Scope.ScopeLanguage);   // 旧中文范围仍以 EN 为准
        Assert.Empty(run.Scope.AdditionalLogicalFiles);                  // 不为韩文多出的文件扩范围
        Assert.DoesNotContain(run.Scope.Units, unit => unit.Key.ToString() == KrOnlyUnitKey);

        Assert.DoesNotContain(run.Plan.Candidates, entry => entry.Key.ToString() == KrOnlyUnitKey);
        Assert.DoesNotContain(KrOnlyUnitKey, run.Plan.ExpectedOutputKeys);
        Assert.DoesNotContain(KrOnlyUnitKey, run.Merge.WrittenKeys);
        Assert.DoesNotContain(KrOnlyFile, run.Merge.Files);
        Assert.False(File.Exists(Path.Combine(_root, "output", KrOnlyFile)));

        // 公共文件：EN 未变 + 旧中文存在 ⇒ 继承，不调用 Provider
        Assert.Equal(1, run.Plan.InheritCount);
        Assert.Equal(CommonUnitKey, Assert.Single(run.Plan.OutputEntries).Key.ToString());
        Assert.Equal(0, run.Provider.CallCount);
    }

    // ───────── F：KR 删除整个文件 ⇒ 不继承 / 不翻译 / 不输出 ─────────

    [Fact]
    public async Task F_KR删除整个文件时不得继承或输出()
    {
        const string deletedFile = "OldFile.json";
        const string deletedKey = deletedFile + "|1|dataList[0].name";

        // 基线：KR 同时有公共文件与被删除文件
        WriteLanguage(SourceLanguage.Korean,
            (CommonFile, Items((1, KoreanA))),
            (deletedFile, Items((1, KoreanA))));
        WriteLanguage(SourceLanguage.English,
            (CommonFile, Items((1, EnglishC))),
            (deletedFile, Items((1, EnglishC))));
        WriteOldEnglish(
            (CommonFile, Items((1, EnglishC))),
            (deletedFile, Items((1, EnglishC))));
        WriteOldChinese(
            (CommonFile, Items((1, "旧中文公共"))),
            (deletedFile, Items((1, "旧中文被删文件"))));
        CaptureBaseline();                                  // 建立 Previous KR 基线

        // 当前 KR：整个文件被删除；EN 与旧中文仍然存在
        DeleteLanguageFile(SourceLanguage.Korean, deletedFile);
        var capture = CaptureBaseline();                    // Canonical Diff 看到 OldFile 被删除

        var run = await RunAsync(TranslationMode.KoreanEnglish, capture);

        Assert.DoesNotContain(deletedFile, run.Scope.AdditionalLogicalFiles);
        Assert.DoesNotContain(deletedKey, run.Plan.ExpectedOutputKeys);
        Assert.DoesNotContain(run.Plan.OutputEntries, entry => entry.Key.ToString() == deletedKey);
        Assert.Equal(0, run.Plan.TranslateMissingCount);
        Assert.Empty(run.Plan.NeedTranslate);
        Assert.Equal(0, run.Provider.CallCount);

        Assert.DoesNotContain(deletedKey, run.Merge.WrittenKeys);
        Assert.False(File.Exists(Path.Combine(_root, "output", deletedFile)));
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
    }

    // ───────── 旧中文解析范围断言（P2-η 语义） ─────────

    [Fact]
    public async Task 旧中文解析范围_KR模式为EN与KR文件集合并()
    {
        var capture = PrepareBaselineWithKrOnlyFile("旧中文专属");
        var run = await RunAsync(TranslationMode.KoreanEnglish, capture);

        Assert.Equal(SourceLanguage.Korean, run.Scope.ScopeLanguage);
        Assert.Equal(new[] { KrOnlyFile }, run.Scope.AdditionalLogicalFiles);
        Assert.Equal(2, run.Scope.ScannedAuthoritativeFileCount);        // 当前 KR 共 2 个文件
        Assert.Equal(2, run.Scope.Units.Count);                          // 两个文件的旧中文都在范围内
        Assert.Contains(run.Scope.Units, unit => unit.Key.ToString() == CommonUnitKey);
        Assert.Contains(run.Scope.Units, unit => unit.Key.ToString() == KrOnlyUnitKey);
    }

    [Fact]
    public async Task 旧中文解析范围_ENONLY仍只跟EN()
    {
        var capture = PrepareBaselineWithKrOnlyFile("旧中文专属");
        var run = await RunAsync(TranslationMode.EnglishOnly, capture);

        Assert.Equal(SourceLanguage.English, run.Scope.ScopeLanguage);
        Assert.Equal(0, run.Scope.ScannedAuthoritativeFileCount);
        Assert.Empty(run.Scope.AdditionalLogicalFiles);
        Assert.Single(run.Scope.Units);                                  // 只包含工作流已解析的 EN 范围结果
        Assert.Contains(run.Scope.Units, unit => unit.Key.ToString() == CommonUnitKey);
    }
}

