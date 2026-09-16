using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0A.1轮：Analyze 管线级集成测试——三语快照 + 韩文 Canonical Diff 完整生命周期。
///
/// 使用临时目录构造真实 JSON 格式的 <c>Localize/{kr,en,jp}/</c> 树；
/// 被测入口为 <see cref="MultilingualSnapshotCapture"/>（Analyze 管线中承担「解析三语 → 对齐 → Canonical Diff → 保存快照」的模块）。
/// </summary>
public sealed class MultilingualPipelineIntegrationTests : IDisposable
{
    private readonly string _root;

    public MultilingualPipelineIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_pipe_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    /// <summary>找到项目 config 目录（field_rules.json 所在）。</summary>
    private static string ResolveConfigDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (File.Exists(Path.Combine(candidate, "field_rules.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到项目 config 目录（field_rules.json）。");
    }

    private string LocalizeRoot => Path.Combine(_root, "Localize");

    /// <summary>写入某语言的真实格式 JSON（dataList[{id,name,desc}]）。</summary>
    private void WriteLanguageFile(SourceLanguage language, string logicalName, params string[] names)
    {
        var dir = Path.Combine(LocalizeRoot, SourceLanguageHelper.GetLocalizeDirectoryName(language));
        Directory.CreateDirectory(dir);

        var rows = names
            .Select((name, index) =>
                $"{{\n      \"id\": {index + 1},\n      \"name\": \"{name}\",\n      \"desc\": \"{name} desc\"\n    }}")
            .ToArray();

        var json = "{\n  \"dataList\": [\n    " + string.Join(",\n    ", rows) + "\n  ]\n}\n";
        File.WriteAllText(
            Path.Combine(dir, SourceLanguageHelper.GetFilePrefix(language) + logicalName),
            json);
    }

    private Dictionary<SourceLanguage, string?> Directories()
    {
        var map = new Dictionary<SourceLanguage, string?>();
        foreach (var language in SourceLanguageHelper.All)
        {
            var dir = Path.Combine(LocalizeRoot, SourceLanguageHelper.GetLocalizeDirectoryName(language));
            map[language] = Directory.Exists(dir) ? dir : null;
        }

        return map;
    }

    private MultilingualCaptureResult RunCapture(Action<string>? log = null)
        => MultilingualSnapshotCapture.Capture(_root, Directories(), ResolveConfigDir(), log);

    private void CreateAllThree(string krName = "한국어 이름", string enName = "English Name", string jpName = "日本語名")
    {
        WriteLanguageFile(SourceLanguage.Korean, "Items.json", krName);
        WriteLanguageFile(SourceLanguage.English, "Items.json", enName);
        WriteLanguageFile(SourceLanguage.Japanese, "Items.json", jpName);
    }

    // ── 1) First Analyze ────────────────────────────────────────────────
    [Fact]
    public void FirstAnalyze_写入三语current_且无previous()
    {
        CreateAllThree();

        var result = RunCapture();

        Assert.True(result.Success);
        Assert.True(result.IsFirstCanonicalBaseline);
        foreach (var language in SourceLanguageHelper.All)
        {
            Assert.True(File.Exists(SourceSnapshotService.GetCurrentPath(_root, language)), $"{language} current 应存在");
            Assert.False(File.Exists(SourceSnapshotService.GetPreviousPath(_root, language)), $"{language} previous 不应存在");
        }

        Assert.Equal(3, result.SavedLanguages.Count);
    }

    // ── 2) Second Same Analyze ──────────────────────────────────────────
    [Fact]
    public void SecondSameAnalyze_无变化且previous就位()
    {
        CreateAllThree();
        RunCapture();

        var second = RunCapture();

        Assert.True(second.Success);
        Assert.False(second.IsFirstCanonicalBaseline);
        Assert.Equal(0, second.CanonicalDiff!.Modified);
        Assert.Equal(0, second.CanonicalDiff.Added);
        Assert.Equal(0, second.CanonicalDiff.Deleted);
        Assert.Equal(2, second.CanonicalDiff.Unchanged); // name + desc
        foreach (var language in SourceLanguageHelper.All)
        {
            Assert.NotNull(SourceSnapshotService.TryLoadPrevious(_root, language));
        }
    }

    // ── 3) EN-only change ───────────────────────────────────────────────
    [Fact]
    public void 只改英文_CanonicalModified为0且EnglishChanged为1()
    {
        CreateAllThree();
        RunCapture();

        WriteLanguageFile(SourceLanguage.English, "Items.json", "English Name Changed");

        var result = RunCapture();

        Assert.Equal(0, result.CanonicalDiff!.Modified);
        Assert.Equal(0, result.CanonicalDiff.Added);
        // 夹具中 desc = "{name} desc"，因此 name 变化会同时影响 name 与 desc 两条单元 → 2 条英文参考变化
        Assert.Equal(2, result.CanonicalDiff.EnglishChangedCount);
        Assert.Equal(0, result.CanonicalDiff.JapaneseChangedCount);
    }

    // ── 4) JP-only change ───────────────────────────────────────────────
    [Fact]
    public void 只改日文_CanonicalModified为0且JapaneseChanged为1()
    {
        CreateAllThree();
        RunCapture();

        WriteLanguageFile(SourceLanguage.Japanese, "Items.json", "変更された日本語");

        var result = RunCapture();

        Assert.Equal(0, result.CanonicalDiff!.Modified);
        Assert.Equal(0, result.CanonicalDiff.EnglishChangedCount);
        Assert.Equal(2, result.CanonicalDiff.JapaneseChangedCount); // name + desc 均含名称
    }

    // ── 5) KR-only change ───────────────────────────────────────────────
    [Fact]
    public void 只改韩文_产生CanonicalModified且带新旧韩文()
    {
        CreateAllThree();
        RunCapture();

        WriteLanguageFile(SourceLanguage.Korean, "Items.json", "변경된 한국어 원문");

        var result = RunCapture();

        Assert.True(result.CanonicalDiff!.Modified >= 1);
        Assert.Equal(0, result.CanonicalDiff.EnglishChangedCount); // EN 未变
        Assert.Equal(0, result.CanonicalDiff.JapaneseChangedCount); // JP 未变

        var nameEntry = result.CanonicalDiff.Entries.First(e => e.Key.FieldPath.EndsWith(".name", StringComparison.Ordinal));
        Assert.Equal("한국어 이름", nameEntry.OldSourceText);
        Assert.Equal("변경된 한국어 원문", nameEntry.NewSourceText);
        Assert.Equal(DiffKind.Modified, nameEntry.DiffKind);
    }

    // ── 6) KR Added ─────────────────────────────────────────────────────
    [Fact]
    public void 韩文新增_产生CanonicalAdded_即使英文日文没有该单元()
    {
        CreateAllThree();
        RunCapture();

        // KR 增加一行；EN / JP 保持不变
        WriteLanguageFile(SourceLanguage.Korean, "Items.json", "한국어 이름", "새로 추가된 한국어");

        var result = RunCapture();

        Assert.True(result.CanonicalDiff!.Added >= 1);
        Assert.Equal(0, result.CanonicalDiff.Modified);
        Assert.Contains(result.CanonicalDiff.Entries, e => e.DiffKind == DiffKind.Added);
    }

    // ── 7) KR Deleted ───────────────────────────────────────────────────
    [Fact]
    public void 韩文删除_产生CanonicalDeleted()
    {
        WriteLanguageFile(SourceLanguage.Korean, "Items.json", "한국어 이름", "삭제될 한국어");
        WriteLanguageFile(SourceLanguage.English, "Items.json", "English Name", "Will be removed");
        WriteLanguageFile(SourceLanguage.Japanese, "Items.json", "日本語名", "削除される");
        RunCapture();

        // KR 删掉第二行；EN / JP 保持不变
        WriteLanguageFile(SourceLanguage.Korean, "Items.json", "한국어 이름");

        var result = RunCapture();

        Assert.True(result.CanonicalDiff!.Deleted >= 1);
        Assert.Contains(result.CanonicalDiff.Entries, e => e.DiffKind == DiffKind.Deleted);
    }

    // ── 8) JP 目录缺失 ──────────────────────────────────────────────────
    [Fact]
    public void 缺日文目录_不得写空日文快照()
    {
        WriteLanguageFile(SourceLanguage.Korean, "Items.json", "한국어 이름");
        WriteLanguageFile(SourceLanguage.English, "Items.json", "English Name");

        var result = RunCapture();

        Assert.True(result.Success);
        Assert.True(File.Exists(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.Korean)));
        Assert.True(File.Exists(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.English)));
        Assert.False(File.Exists(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.Japanese)));
        Assert.Contains(SourceLanguage.Japanese, result.MissingDirectories);
        Assert.DoesNotContain(SourceLanguage.Japanese, result.SavedLanguages);
    }

    // ── 9) KR 目录缺失 ──────────────────────────────────────────────────
    [Fact]
    public void 缺韩文目录_Canonical被阻止且不得退回英文()
    {
        CreateAllThree();
        Directory.Delete(Path.Combine(LocalizeRoot, "kr"), true);

        var result = RunCapture();

        Assert.False(result.Success);
        Assert.Equal(MultilingualSnapshotCapture.MissingKoreanReason, result.FailureReason);
        // 不得创建 KO 快照，也不得退回 EN 作为 Canonical
        Assert.False(SourceSnapshotService.HasBaseline(_root, SourceLanguage.Korean));
        Assert.False(File.Exists(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.Korean)));
        Assert.Null(result.CanonicalDiff);
    }

    // ── 10) 失败不覆盖已有快照 ──────────────────────────────────────────
    [Fact]
    public void 分析失败_三语快照内容必须保持原样()
    {
        CreateAllThree();
        Assert.True(RunCapture().Success);

        var before = new Dictionary<SourceLanguage, string>();
        foreach (var language in SourceLanguageHelper.All)
        {
            before[language] = File.ReadAllText(SourceSnapshotService.GetCurrentPath(_root, language));
        }

        // 制造失败：KR 目录整体删除
        Directory.Delete(Path.Combine(LocalizeRoot, "kr"), true);
        var failed = RunCapture();
        Assert.False(failed.Success);

        foreach (var language in SourceLanguageHelper.All)
        {
            Assert.Equal(before[language], File.ReadAllText(SourceSnapshotService.GetCurrentPath(_root, language)));
        }
    }
}