using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.20轮（P1）：**翻译记忆批量载入**（<c>FindExactUnits</c>）语义回归。
///
/// 用途：「从 output 载入进度」补上 output 里没有的条目（output 只覆盖"曾写出过"的文件）。
/// 批量版必须与单条 <c>FindExactUnit</c> **同语义**：
///   - UnitKey 与 SourceHash 同时一致才算命中（模式盐不同 ⇒ 不命中）；
///   - 源文纯空白不参与命中；
///   - 返回的来源 / NeedsReview / ReviewReason 与单条一致。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class TranslationMemoryBulkLoadTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "LT_TMBulk_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // 清理失败可忽略
        }
    }

    private SqliteTranslationMemory Open()
        => new(new TranslationMemoryOptions { DatabasePath = _dbPath });

    private static UnitKey Key(string file, string id)
        => new() { RelativeFilePath = file, RecordId = id, FieldPath = "dataList[0].content" };

    private static TranslationUnit Unit(UnitKey key, string source, string? salt)
        => TranslationUnitFactory.FromDiffEntry(new DiffEntry
        {
            Key = key,
            NewSourceText = source,
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateNew,
            SourceHashSalt = salt,
        });

    [Fact]
    public void 批量命中_只返回UnitKey与哈希都一致的条目()
    {
        using var tm = Open();
        var a = Key("StoryData/A.json", "1");
        var b = Key("StoryData/B.json", "2");

        tm.Save(Unit(a, "안녕", "salt-kr-en"),
            new TranslationResult { Key = a, Translation = "你好", Source = TranslationSource.AI });
        tm.Save(Unit(b, "안녕", "salt-kr-jp"),
            new TranslationResult { Key = b, Translation = "日文模式的译文", Source = TranslationSource.HumanReviewed });

        var wanted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [a.ToString()] = SqliteTranslationMemory.ComputeSourceHash("안녕", "salt-kr-en"),   // 命中
            [b.ToString()] = SqliteTranslationMemory.ComputeSourceHash("안녕", "salt-kr-en"),   // 盐不同 ⇒ 不命中
            ["StoryData/C.json|3|dataList[0].content"] = SqliteTranslationMemory.ComputeSourceHash("没有这条", null),
        };

        var hits = tm.FindExactUnits(wanted);

        Assert.Single(hits);
        Assert.Equal("你好", hits[a.ToString()].Translation);
        Assert.Equal(TranslationSource.AI, hits[a.ToString()].Source);
    }

    [Fact]
    public void 批量命中_与单条查询结果一致()
    {
        using var tm = Open();
        var key = Key("StoryData/D.json", "7");
        var hash = SqliteTranslationMemory.ComputeSourceHash("반갑습니다", null);

        tm.Save(Unit(key, "반갑습니다", null), new TranslationResult
        {
            Key = key,
            Translation = "很高兴见到您",
            Source = TranslationSource.AI,
            NeedsReview = true,
            ReviewReason = "术语待确认",
        });

        var single = tm.FindExactUnit(key, hash);
        var bulk = tm.FindExactUnits(new Dictionary<string, string>(StringComparer.Ordinal) { [key.ToString()] = hash });

        Assert.NotNull(single);
        var hit = Assert.Single(bulk).Value;
        Assert.Equal(single!.Translation, hit.Translation);
        Assert.Equal(single.Source, hit.Source);
        Assert.Equal(single.NeedsReview, hit.NeedsReview);
        Assert.Equal(single.ReviewReason, hit.ReviewReason);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, hit.TmMatchType);
    }

    [Fact]
    public void 批量命中_分块查询仍然正确()
    {
        using var tm = Open();

        var keys = new List<(UnitKey Key, string Hash)>();
        for (var index = 0; index < 5; index++)
        {
            var key = Key($"StoryData/Chunk{index}.json", index.ToString());
            var source = "文本" + index;
            tm.Save(Unit(key, source, null),
                new TranslationResult { Key = key, Translation = "译文" + index, Source = TranslationSource.AI });
            keys.Add((key, SqliteTranslationMemory.ComputeSourceHash(source, null)));
        }

        var wanted = keys.ToDictionary(pair => pair.Key.ToString(), pair => pair.Hash, StringComparer.Ordinal);
        var hits = tm.FindExactUnits(wanted, batchSize: 1);   // 每批 1 个 ⇒ 强制分块

        Assert.Equal(5, hits.Count);
        Assert.Equal("译文3", hits[keys[3].Key.ToString()].Translation);
    }

    [Fact]
    public void 批量命中_空集合与空哈希都不报错且不命中()
    {
        using var tm = Open();
        var key = Key("StoryData/E.json", "1");
        tm.Save(Unit(key, "텍스트", null),
            new TranslationResult { Key = key, Translation = "文本", Source = TranslationSource.AI });

        Assert.Empty(tm.FindExactUnits(new Dictionary<string, string>()));

        var emptyHashWanted = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key.ToString()] = SqliteTranslationMemory.ComputeSourceHash(string.Empty, null),   // 空哈希不可用
        };
        Assert.Empty(tm.FindExactUnits(emptyHashWanted));
    }
}
