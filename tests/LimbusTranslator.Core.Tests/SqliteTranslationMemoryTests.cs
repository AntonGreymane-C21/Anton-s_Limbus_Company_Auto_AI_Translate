using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Services;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// SQLite TranslationMemory 测试。
/// </summary>
[Collection(SqliteCollection.Name)]
public class SqliteTranslationMemoryTests
{
    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_TM_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TranslationMemoryOptions
        {
            DatabasePath = Path.Combine(dir, "tm.db"),
        };
    }

    private static void Cleanup(TranslationMemoryOptions options)
    {
        SqliteConnection.ClearAllPools();
        var dir = Path.GetDirectoryName(options.DatabasePath)!;
        try
        {
            Directory.Delete(dir, true);
        }
        catch
        {
            // 清理失败可忽略（临时目录）
        }
    }

    [Fact]
    public void 保存后_可通过ExactUnit查找()
    {
        var options = MakeOptions();
        using var tm = new SqliteTranslationMemory(options);

        var unit = new TranslationUnit
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            FilePath = "Test.json",
            RecordId = "1",
            FieldPath = "dataList[0].content",
            SourceText = "Hello World",
        };
        var result = new TranslationResult
        {
            Key = unit.Key,
            Translation = "你好世界",
            Source = TranslationSource.AI,
        };

        tm.Save(unit, result);

        var hash = SqliteTranslationMemory.ComputeSourceHash("Hello World");
        var found = tm.FindExactUnit(unit.Key, hash);

        Assert.NotNull(found);
        Assert.Equal("你好世界", found.Translation);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, found.TmMatchType);

        Cleanup(options);
    }

    [Fact]
    public void 不同Source_不命中()
    {
        var options = MakeOptions();
        using var tm = new SqliteTranslationMemory(options);

        var unit = new TranslationUnit
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "content" },
            FilePath = "Test.json",
            RecordId = "1",
            FieldPath = "content",
            SourceText = "Hello",
        };
        tm.Save(unit, new TranslationResult { Key = unit.Key, Translation = "你好", Source = TranslationSource.AI });

        var hash = SqliteTranslationMemory.ComputeSourceHash("World");
        Assert.Null(tm.FindExactUnit(unit.Key, hash));

        Cleanup(options);
    }

    [Fact]
    public void RequestCache_保存并可查找()
    {
        var options = MakeOptions();
        using var tm = new SqliteTranslationMemory(options);

        tm.SaveRequestCache("fp123", "{\"ok\":true}");
        var found = tm.FindRequestCache("fp123");

        Assert.Equal("{\"ok\":true}", found);

        Cleanup(options);
    }

    [Fact]
    public void SourceHash_相同文本哈希一致()
    {
        var h1 = SqliteTranslationMemory.ComputeSourceHash("Gain 2 Haste");
        var h2 = SqliteTranslationMemory.ComputeSourceHash("Gain 2 Haste");
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length);
    }

    [Fact]
    public void 缓存恢复_当前英文哈希命中时_应收集译文()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var key = new UnitKey
            {
                RelativeFilePath = "StoryData/Test.json",
                RecordId = "1",
                FieldPath = "dataList[0].content",
            };
            var unit = new TranslationUnit
            {
                Key = key,
                FilePath = key.RelativeFilePath,
                RecordId = key.RecordId,
                FieldPath = key.FieldPath,
                SourceText = "The current English text.",
            };
            tm.Save(unit, new TranslationResult
            {
                Key = key,
                Translation = "当前英文文本的译文。",
                Source = TranslationSource.AI,
            });

            var entry = new DiffEntry
            {
                Key = key,
                NewSourceText = unit.SourceText,
                DiffKind = DiffKind.Added,
                Action = TranslationAction.TranslateNew,
            };

            var recovered = OutputRecoveryService.Collect(new[] { entry }, tm);

            Assert.Empty(recovered.MissingEntries);
            Assert.Equal(1, recovered.CacheHitCount);
            Assert.Equal("当前英文文本的译文。", recovered.Translations[key.ToString()]);
            Assert.Equal("当前英文文本的译文。", entry.Translation);
        }
        finally
        {
            Cleanup(options);
        }
    }
}
