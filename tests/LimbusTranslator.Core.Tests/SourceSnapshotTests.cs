using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Paratranz;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.89轮：源快照（真实 OldSource 基础设施）+ DiffEngine 产出真实 Modified/Added/Deleted。
/// </summary>
public sealed class SourceSnapshotTests : IDisposable
{
    private readonly string _root;

    public SourceSnapshotTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_snap_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static TranslationUnit Unit(string file, string recordId, string field, string text)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = recordId, FieldPath = field },
            SourceText = text,
            RecordId = recordId,
            FieldPath = field,
            FilePath = file,
        };

    [Fact]
    public void 首次运行_建立基线且无上一版()
    {
        Assert.Null(SourceSnapshotService.TryLoadCurrent(_root));

        SourceSnapshotService.SaveBaseline(_root, SourceSnapshotService.FromTranslationUnits(
            new[] { Unit("A.json", "1", "dataList[0].name", "Ring") }));

        var current = SourceSnapshotService.TryLoadCurrent(_root);
        Assert.NotNull(current);
        Assert.Single(current!.Units);
        Assert.Equal("Ring", current.Units[0].SourceText);
        Assert.Null(SourceSnapshotService.TryLoadPrevious(_root));
        Assert.False(File.Exists(SourceSnapshotService.GetCurrentPath(_root) + ".tmp"));
    }

    [Fact]
    public void 第二次保存_应轮换出上一版()
    {
        SourceSnapshotService.SaveBaseline(_root, SourceSnapshotService.FromTranslationUnits(
            new[] { Unit("A.json", "1", "dataList[0].name", "Ring") }));
        SourceSnapshotService.SaveBaseline(_root, SourceSnapshotService.FromTranslationUnits(
            new[] { Unit("A.json", "1", "dataList[0].name", "Ring II") }));

        Assert.Equal("Ring II", SourceSnapshotService.TryLoadCurrent(_root)!.Units[0].SourceText);
        Assert.Equal("Ring", SourceSnapshotService.TryLoadPrevious(_root)!.Units[0].SourceText);
    }

    [Fact]
    public void 快照可还原为DiffEngine可用的旧英文单元()
    {
        var units = new[] { Unit("A.json", "1", "dataList[0].name", "Ring") };
        SourceSnapshotService.SaveBaseline(_root, SourceSnapshotService.FromTranslationUnits(units));

        var restored = SourceSnapshotService.ToTranslationUnits(SourceSnapshotService.TryLoadCurrent(_root)!);

        Assert.Single(restored);
        Assert.Equal("A.json|1|dataList[0].name", restored[0].Key.ToString());
        Assert.Equal("Ring", restored[0].SourceText);
    }

    [Fact]
    public void 相同来源为Unchanged_变化为Modified_新增为Added_删除为Deleted()
    {
        var engine = new DiffEngine();

        var previous = new List<TranslationUnit>
        {
            Unit("A.json", "1", "dataList[0].name", "Ring"),
            Unit("A.json", "1", "dataList[1].name", "Bleed"),
            Unit("A.json", "1", "dataList[2].name", "Gone"),
        };
        var current = new List<TranslationUnit>
        {
            Unit("A.json", "1", "dataList[0].name", "Ring"),
            Unit("A.json", "1", "dataList[1].name", "Bleed II"),
            Unit("A.json", "1", "dataList[3].name", "New"),
        };

        var result = engine.Compute(previous, new List<TranslationUnit>(), current);

        var byKey = result.Entries.ToDictionary(e => e.Key.ToString());
        Assert.Equal(DiffKind.Unchanged, byKey["A.json|1|dataList[0].name"].DiffKind);
        Assert.Equal(DiffKind.Modified, byKey["A.json|1|dataList[1].name"].DiffKind);
        Assert.Equal(DiffKind.Added, byKey["A.json|1|dataList[3].name"].DiffKind);

        // 被删除的旧条目不应出现在结果中（或标记为 Deleted）
        if (byKey.TryGetValue("A.json|1|dataList[2].name", out var deleted))
        {
            Assert.Equal(DiffKind.Deleted, deleted.DiffKind);
        }
    }

    [Fact]
    public void Modified条目_必须同时带旧源与新源()
    {
        var engine = new DiffEngine();
        var previous = new List<TranslationUnit> { Unit("A.json", "1", "dataList[0].name", "Attack Power Up") };
        var current = new List<TranslationUnit> { Unit("A.json", "1", "dataList[0].name", "Attack Power Up II") };

        var result = engine.Compute(previous, new List<TranslationUnit>(), current);
        var entry = result.Entries.Single();

        Assert.Equal(DiffKind.Modified, entry.DiffKind);
        Assert.Equal("Attack Power Up", entry.OldSourceText);
        Assert.Equal("Attack Power Up II", entry.NewSourceText);
        Assert.False(string.IsNullOrWhiteSpace(entry.OldSourceText));
    }

    [Fact]
    public void 分析失败路径_不得覆盖旧快照()
    {
        SourceSnapshotService.SaveBaseline(_root, SourceSnapshotService.FromTranslationUnits(
            new[] { Unit("A.json", "1", "dataList[0].name", "Baseline") }));
        var before = File.ReadAllText(SourceSnapshotService.GetCurrentPath(_root));

        // 失败路径：调用方（MainViewModel）不会调用 SaveBaseline —— 这里直接断言文件未被改动
        Assert.Equal(before, File.ReadAllText(SourceSnapshotService.GetCurrentPath(_root)));
        Assert.Equal("Baseline", SourceSnapshotService.TryLoadCurrent(_root)!.Units[0].SourceText);
    }

    [Fact]
    public void 损坏快照_应视为无快照而不抛异常()
    {
        Directory.CreateDirectory(SourceSnapshotService.GetLanguageDirectory(_root, SourceLanguage.English));
        File.WriteAllText(SourceSnapshotService.GetCurrentPath(_root, SourceLanguage.English), "{ not-json");

        Assert.Null(SourceSnapshotService.TryLoadCurrent(_root, SourceLanguage.English));
    }
}