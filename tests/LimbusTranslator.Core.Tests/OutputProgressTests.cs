using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Presentation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.8轮：**从 output 载入当前汉化进度** 回归。
///
/// 覆盖：字段路径解析（与 Merge 的写入语义一致）/ 目录读取（文件缺失与字段未解析计数）/
/// 与 TM 的组合链路（载入 → 单事务写 TM → ExactUnit 命中，含模式盐隔离）。
/// </summary>
public sealed class OutputProgressTests : IDisposable
{
    private readonly string _root;

    public OutputProgressTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lt_output_progress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 清理失败不影响测试结论
        }
    }

    private static DiffEntry Entry(string file, string recordId, string fieldPath, string source = "Source")
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = recordId, FieldPath = fieldPath },
            NewSourceText = source,
            DiffKind = DiffKind.Modified,
            Action = TranslationAction.TranslateModified,
        };

    // ───────── 字段路径解析（与 Merge 写入语义一致）─────────

    [Theory]
    [InlineData("name", "张三")]
    [InlineData("dataList[0].content", "甲")]
    [InlineData("dataList[1].content", "乙")]
    [InlineData("outer.inner.deep", "深")]
    public void 字段路径解析_命中字符串字段(string fieldPath, string expected)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"name":"张三","outer":{"inner":{"deep":"深"}},"dataList":[{"content":"甲"},{"content":"乙"}]}""");

        Assert.True(OutputProgressLoader.TryResolveFieldPath(document.RootElement, fieldPath, out var text));
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("dataList[9].content")]     // 数组越界
    [InlineData("dataList[0].missing")]     // 键不存在
    [InlineData("name.sub")]                // 字符串上继续取属性
    [InlineData("dataList[0]")]             // 目标不是字符串
    [InlineData("")]
    public void 字段路径解析_无效路径返回false(string fieldPath)
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"name":"张三","dataList":[{"content":"甲","id":7}]}""");

        Assert.False(OutputProgressLoader.TryResolveFieldPath(document.RootElement, fieldPath, out _));
    }

    // ───────── 目录读取 ─────────

    [Fact]
    public void 从output读取_命中已写出的译文并统计缺失()
    {
        var storyData = Path.Combine(_root, "StoryData");
        Directory.CreateDirectory(storyData);
        File.WriteAllText(Path.Combine(storyData, "A.json"), """{"dataList":[{"content":"甲"},{"content":"乙"}]}""");

        var entries = new[]
        {
            Entry("StoryData/A.json", "0", "dataList[0].content"),
            Entry("StoryData/A.json", "1", "dataList[1].content"),
            Entry("StoryData/A.json", "2", "dataList[2].content"),   // 越界 ⇒ 字段未解析
            Entry("StoryData/B.json", "0", "dataList[0].content"),   // 文件不存在
        };

        var result = OutputProgressLoader.Load(_root, entries);

        Assert.Equal(2, result.Translations.Count);
        Assert.Equal("甲", result.Translations["StoryData/A.json|0|dataList[0].content"]);
        Assert.Equal("乙", result.Translations["StoryData/A.json|1|dataList[1].content"]);
        Assert.Equal(1, result.FileCount);
        Assert.Equal(1, result.MissingFileEntryCount);
        Assert.Equal(1, result.UnresolvedFieldCount);
        Assert.Contains("命中条目 2 条", result.Describe());
    }

    [Fact]
    public void 空目录_不报错且不命中任何条目()
    {
        var result = OutputProgressLoader.Load(_root, new[] { Entry("StoryData/A.json", "0", "dataList[0].content") });

        Assert.Empty(result.Translations);
        Assert.Equal(0, result.FileCount);
        Assert.Equal(1, result.MissingFileEntryCount);
    }

    // ───────── 载入 → 写 TM → 命中（含模式盐隔离）─────────

    [Fact]
    public void 载入进度_写入TM后必须能被ExactUnit命中()
    {
        var storyData = Path.Combine(_root, "StoryData");
        Directory.CreateDirectory(storyData);
        File.WriteAllText(Path.Combine(storyData, "A.json"), """{"dataList":[{"content":"护父"}]}""");

        var entry = Entry("StoryData/A.json", "0", "dataList[0].content", "Nursefather");
        var loaded = OutputProgressLoader.Load(_root, new[] { entry });
        entry.Translation = loaded.Translations[entry.Key.ToString()];
        entry.Provenance = TranslationSource.Imported;

        var tmPath = Path.Combine(_root, "tm.db");
        using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions { DatabasePath = tmPath });
        var written = memory.SaveMany(new[]
        {
            (Unit: TranslationUnitFactory.FromDiffEntry(entry),
             Result: new TranslationResult
             {
                 Key = entry.Key,
                 Translation = entry.Translation!,
                 Source = TranslationSource.Imported,
             }),
        });
        Assert.Equal(1, written);

        var hash = SqliteTranslationMemory.ComputeSourceHash("Nursefather", entry.SourceHashSalt);
        var hit = memory.FindExactUnit(entry.Key, hash);

        Assert.NotNull(hit);
        Assert.Equal("护父", hit!.Translation);
        Assert.Equal(TranslationSource.Imported, hit.Source);
    }

    [Fact]
    public void 模式盐隔离_带盐写入不能被无盐查询命中()
    {
        var unit = new TranslationUnit
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/A.json", RecordId = "0", FieldPath = "dataList[0].content" },
            FilePath = "StoryData/A.json",
            RecordId = "0",
            FieldPath = "dataList[0].content",
            SourceText = "Nursefather",
            SourceHashSalt = "v3:kr_en:ko",
        };

        var tmPath = Path.Combine(_root, "tm2.db");
        using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions { DatabasePath = tmPath });
        Assert.Equal(1, memory.SaveMany(new[]
        {
            (Unit: unit, Result: new TranslationResult { Key = unit.Key, Translation = "护父", Source = TranslationSource.Imported }),
        }));

        Assert.NotNull(memory.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash("Nursefather", unit.SourceHashSalt)));
        Assert.Null(memory.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash("Nursefather", null)));
    }

    [Fact]
    public void 批量写入_空源文条目被跳过()
    {
        var tmPath = Path.Combine(_root, "tm3.db");
        using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions { DatabasePath = tmPath });

        var empty = new TranslationUnit
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/A.json", RecordId = "1", FieldPath = "dataList[1].content" },
            FilePath = "StoryData/A.json",
            RecordId = "1",
            FieldPath = "dataList[1].content",
            SourceText = "   ",
        };
        var normal = new TranslationUnit
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/A.json", RecordId = "2", FieldPath = "dataList[2].content" },
            FilePath = "StoryData/A.json",
            RecordId = "2",
            FieldPath = "dataList[2].content",
            SourceText = "Hello",
        };

        var written = memory.SaveMany(new[]
        {
            (Unit: empty, Result: new TranslationResult { Key = empty.Key, Translation = "不应写入", Source = TranslationSource.Imported }),
            (Unit: normal, Result: new TranslationResult { Key = normal.Key, Translation = "你好", Source = TranslationSource.Imported }),
        });

        Assert.Equal(1, written);
        Assert.Null(memory.FindExactUnit(empty.Key, SqliteTranslationMemory.ComputeSourceHash("   ", null)));
        Assert.NotNull(memory.FindExactUnit(normal.Key, SqliteTranslationMemory.ComputeSourceHash("Hello", null)));
    }
}
