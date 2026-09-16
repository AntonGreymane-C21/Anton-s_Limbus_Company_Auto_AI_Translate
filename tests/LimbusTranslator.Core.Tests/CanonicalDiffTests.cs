using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Multilingual;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0A轮：韩文 Canonical Diff 规则 + 多语言 Snapshot 隔离。</summary>
public sealed class CanonicalDiffTests : IDisposable
{
    private readonly string _root;

    public CanonicalDiffTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_canon_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static TranslationUnit Unit(string text, string field = "dataList[0].content")
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/1D101A.json", RecordId = "1", FieldPath = field },
            SourceText = text,
            RecordId = "1",
            FieldPath = field,
            FilePath = "StoryData/1D101A.json",
        };

    [Fact]
    public void KR相同_英文变化_不得产生CanonicalModified()
    {
        var result = CanonicalDiffService.Compute(
            new[] { Unit("안녕하세요") }, new[] { Unit("안녕하세요") },
            new[] { Unit("Hello") }, new[] { Unit("Hello there") });

        Assert.Single(result.Entries);
        Assert.Equal(DiffKind.Unchanged, result.Entries[0].DiffKind);
        Assert.Equal(0, result.Modified);
        var change = Assert.Single(result.EnglishChanges);
        Assert.Equal(ReferenceChangeKind.Changed, change.Kind);
        Assert.Equal(1, result.EnglishChangedCount);
    }

    [Fact]
    public void KR相同_日文变化_不得产生CanonicalModified()
    {
        var result = CanonicalDiffService.Compute(
            new[] { Unit("안녕하세요") }, new[] { Unit("안녕하세요") },
            null, null,
            new[] { Unit("こんにちは") }, new[] { Unit("こんばんは") });

        Assert.Equal(DiffKind.Unchanged, result.Entries[0].DiffKind);
        Assert.Equal(0, result.Modified);
        Assert.Single(result.JapaneseChanges);
        Assert.Equal(1, result.JapaneseChangedCount);
    }

    [Fact]
    public void KR变化_即使英文不变_仍为CanonicalModified()
    {
        var result = CanonicalDiffService.Compute(
            new[] { Unit("안녕하세요") }, new[] { Unit("반갑습니다") },
            new[] { Unit("Hello") }, new[] { Unit("Hello") });

        Assert.Equal(DiffKind.Modified, result.Entries[0].DiffKind);
        Assert.Equal(1, result.Modified);
        Assert.Empty(result.EnglishChanges);
        Assert.Equal("안녕하세요", result.Entries[0].OldSourceText);
        Assert.Equal("반갑습니다", result.Entries[0].NewSourceText);
    }

    [Fact]
    public void KR新增_为CanonicalAdded()
    {
        var result = CanonicalDiffService.Compute(
            Array.Empty<TranslationUnit>(), new[] { Unit("안녕하세요") },
            Array.Empty<TranslationUnit>(), new[] { Unit("Hello") });

        Assert.Equal(DiffKind.Added, result.Entries[0].DiffKind);
        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.EnglishChangedCount);
        Assert.Equal(ReferenceChangeKind.MissingBefore, result.EnglishChanges[0].Kind);
    }

    [Fact]
    public void KR删除_为CanonicalDeleted()
    {
        var result = CanonicalDiffService.Compute(
            new[] { Unit("안녕하세요") }, Array.Empty<TranslationUnit>(),
            new[] { Unit("Hello") }, Array.Empty<TranslationUnit>());

        Assert.Equal(DiffKind.Deleted, result.Entries[0].DiffKind);
        Assert.Equal(1, result.Deleted);
        Assert.Equal(ReferenceChangeKind.MissingNow, result.EnglishChanges[0].Kind);
    }

    [Fact]
    public void 第一次运行_无KO快照时全部按新增处理_不产生伪Modified()
    {
        var result = CanonicalDiffService.Compute(
            Array.Empty<TranslationUnit>(), new[] { Unit("안녕하세요"), Unit("반갑습니다", "dataList[1].content") });

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Modified);
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public void 第二次相同运行_不产生任何变化()
    {
        var current = new[] { Unit("안녕하세요"), Unit("반갑습니다", "dataList[1].content") };
        var result = CanonicalDiffService.Compute(current, current);

        Assert.Equal(2, result.Unchanged);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Modified);
        Assert.Equal(0, result.Deleted);
    }

    // ── Snapshot 语言隔离（§7/§28） ─────────────────────────────────────

    [Fact]
    public void 保存KO_不得轮换EN与JP()
    {
        SourceSnapshotService.SaveBaseline(_root, SourceLanguage.English, new[] { Unit("Hello") });
        SourceSnapshotService.SaveBaseline(_root, SourceLanguage.English, new[] { Unit("Hello II") });
        SourceSnapshotService.SaveBaseline(_root, SourceLanguage.Japanese, new[] { Unit("こんにちは") });

        SourceSnapshotService.SaveBaseline(_root, SourceLanguage.Korean, new[] { Unit("안녕하세요") });

        // KR 首次保存：只有 current，没有 previous
        Assert.Null(SourceSnapshotService.TryLoadPrevious(_root, SourceLanguage.Korean));
        Assert.NotNull(SourceSnapshotService.TryLoadCurrent(_root, SourceLanguage.Korean));

        // EN / JP 不受影响
        Assert.Equal("Hello II", SourceSnapshotService.TryLoadCurrent(_root, SourceLanguage.English)!.Units[0].SourceText);
        Assert.Equal("Hello", SourceSnapshotService.TryLoadPrevious(_root, SourceLanguage.English)!.Units[0].SourceText);
        Assert.Equal("こんにちは", SourceSnapshotService.TryLoadCurrent(_root, SourceLanguage.Japanese)!.Units[0].SourceText);
        Assert.Null(SourceSnapshotService.TryLoadPrevious(_root, SourceLanguage.Japanese));
    }

    [Fact]
    public void 每种语言独立维护current与previous()
    {
        foreach (var language in SourceLanguageHelper.All)
        {
            SourceSnapshotService.SaveBaseline(_root, language, new[] { Unit("v1") });
            SourceSnapshotService.SaveBaseline(_root, language, new[] { Unit("v2") });

            Assert.Equal("v2", SourceSnapshotService.TryLoadCurrent(_root, language)!.Units[0].SourceText);
            Assert.Equal("v1", SourceSnapshotService.TryLoadPrevious(_root, language)!.Units[0].SourceText);
        }
    }

    [Fact]
    public void 语言字段参与校验_KO文件不能按EN读取()
    {
        SourceSnapshotService.SaveBaseline(_root, SourceLanguage.Korean, new[] { Unit("안녕하세요") });

        // 把 KO 文件内容写到 EN 路径（模拟错置）
        var koJson = File.ReadAllText(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.Korean));
        Directory.CreateDirectory(SourceSnapshotService.GetLanguageDirectory(_root, SourceLanguage.English));
        File.WriteAllText(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.English), koJson);

        // EN 读取必须失败（language=ko ≠ en），不得静默错用
        Assert.Null(SourceSnapshotService.TryLoadCurrent(_root, SourceLanguage.English));
        Assert.NotNull(SourceSnapshotService.TryLoadCurrent(_root, SourceLanguage.Korean));
    }

    [Fact]
    public void 空单元_不伪造快照()
    {
        Assert.False(SourceSnapshotService.SaveBaseline(_root, SourceLanguage.Japanese, Array.Empty<TranslationUnit>()));
        Assert.False(SourceSnapshotService.HasBaseline(_root, SourceLanguage.Japanese));
        Assert.False(File.Exists(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.Japanese)));
    }

    [Fact]
    public void 快照路径_按语言分目录且文件名统一()
    {
        var ko = SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.Korean);
        Assert.EndsWith(Path.Combine("source_snapshots", "ko", "current.json"), ko);
        Assert.EndsWith(Path.Combine("source_snapshots", "ja", "previous.json"),
            SourceSnapshotService.GetPreviousPath(_root, SourceLanguage.Japanese));
    }
}
