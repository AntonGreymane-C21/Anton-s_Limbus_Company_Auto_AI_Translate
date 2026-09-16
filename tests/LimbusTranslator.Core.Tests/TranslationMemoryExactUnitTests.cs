using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第1轮：Translation Memory 精确命中（ExactUnit）、来源优先级与空源文 fail-safe 测试。
/// </summary>
[Collection(SqliteCollection.Name)]
public class TranslationMemoryExactUnitTests
{
    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_TM_EXACT_" + Guid.NewGuid().ToString("N"));
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

    private static TranslationUnit MakeUnit(string fieldPath, string sourceText, string recordId = "1")
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = recordId, FieldPath = fieldPath };
        return new TranslationUnit
        {
            Key = key,
            FilePath = key.RelativeFilePath,
            RecordId = key.RecordId,
            FieldPath = key.FieldPath,
            SourceText = sourceText,
        };
    }

    [Fact]
    public void ExactUnit_UnitKey与SourceHash都相同_命中()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var unit = MakeUnit("dataList[0].content", "The same sentence.");
            tm.Save(unit, new TranslationResult
            {
                Key = unit.Key,
                Translation = "同一句译文。",
                Source = TranslationSource.AI,
            });

            var hit = tm.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash(unit.SourceText));

            Assert.NotNull(hit);
            Assert.Equal("同一句译文。", hit.Translation);
            Assert.Equal(TranslationMemoryMatchType.ExactUnit, hit.TmMatchType);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 相同SourceHash不同UnitKey_不自动复用()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var saved = MakeUnit("dataList[0].content", "Shared English text.");
            tm.Save(saved, new TranslationResult
            {
                Key = saved.Key,
                Translation = "已存在的译文。",
                Source = TranslationSource.AI,
            });

            // 另一个位置出现完全相同的英文
            var other = MakeUnit("dataList[9].content", "Shared English text.");
            var hash = SqliteTranslationMemory.ComputeSourceHash(other.SourceText);

            // ExactUnit：UnitKey 不同 → 不命中，禁止自动复用
            Assert.Null(tm.FindExactUnit(other.Key, hash));

            // CrossUnitSource：只能作为参考候选，且明确标记命中级别
            var candidate = tm.FindCrossUnitSource(hash);
            Assert.NotNull(candidate);
            Assert.Equal(TranslationMemoryMatchType.CrossUnitSource, candidate.TmMatchType);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 空SourceText_不写入TM且不发生跨Unit命中()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);

            var emptyA = MakeUnit("dataList[0].desc", string.Empty);
            var emptyB = MakeUnit("dataList[1].flavor", string.Empty);
            tm.Save(emptyA, new TranslationResult { Key = emptyA.Key, Translation = "不应写入", Source = TranslationSource.AI });
            tm.Save(emptyB, new TranslationResult { Key = emptyB.Key, Translation = "不应写入", Source = TranslationSource.AI });

            var emptyHash = SqliteTranslationMemory.ComputeSourceHash(string.Empty);
            Assert.Equal(SqliteTranslationMemory.EmptySourceHash, emptyHash);

            // 空源文既不能命中自己的 UnitKey，也不能跨 Unit 命中
            Assert.Null(tm.FindExactUnit(emptyA.Key, emptyHash));
            Assert.Null(tm.FindExactUnit(emptyB.Key, emptyHash));
            Assert.Null(tm.FindCrossUnitSource(emptyHash));

            // 有效源文的其他 Unit 不受影响
            var valid = MakeUnit("dataList[2].desc", "Real text.");
            tm.Save(valid, new TranslationResult { Key = valid.Key, Translation = "真实译文", Source = TranslationSource.AI });
            Assert.NotNull(tm.FindExactUnit(valid.Key, SqliteTranslationMemory.ComputeSourceHash("Real text.")));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 纯空白SourceText_不写入TM且不发生跨Unit命中()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var blank = MakeUnit("dataList[0].title", "   ");
            tm.Save(blank, new TranslationResult { Key = blank.Key, Translation = "不应写入", Source = TranslationSource.AI });

            Assert.Null(tm.FindExactUnit(blank.Key, SqliteTranslationMemory.ComputeSourceHash("   ")));
            Assert.Null(tm.FindCrossUnitSource(SqliteTranslationMemory.ComputeSourceHash("   ")));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 来源优先级_Official优先于AI()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var unit = MakeUnit("dataList[0].name", "Priority test.");

            // 先写官方，再写更新的 AI：新写入不能覆盖更高优先级来源
            tm.Save(unit, new TranslationResult { Key = unit.Key, Translation = "官方译文", Source = TranslationSource.Official });
            tm.Save(unit, new TranslationResult { Key = unit.Key, Translation = "AI 译文", Source = TranslationSource.AI });

            var hit = tm.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash(unit.SourceText));
            Assert.NotNull(hit);
            Assert.Equal("官方译文", hit.Translation);
            Assert.Equal(TranslationSource.Official, hit.Source);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 来源优先级_HumanReviewed优先于AI()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var unit = MakeUnit("dataList[0].name", "Priority test 2.");

            tm.Save(unit, new TranslationResult { Key = unit.Key, Translation = "AI 译文", Source = TranslationSource.AI });
            Assert.True(tm.SaveHumanReviewed(unit, "人工审核译文"));

            var hit = tm.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash(unit.SourceText));
            Assert.NotNull(hit);
            Assert.Equal("人工审核译文", hit.Translation);
            Assert.Equal(TranslationSource.HumanReviewed, hit.Source);
            Assert.False(hit.NeedsReview);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 同优先级_取最新写入记录()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var unit = MakeUnit("dataList[0].add", "Same priority.");
            tm.Save(unit, new TranslationResult { Key = unit.Key, Translation = "旧 AI 译文", Source = TranslationSource.AI });
            tm.Save(unit, new TranslationResult { Key = unit.Key, Translation = "新 AI 译文", Source = TranslationSource.AI });

            var hit = tm.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash(unit.SourceText));
            Assert.NotNull(hit);
            Assert.Equal("新 AI 译文", hit.Translation);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 命中_应恢复NeedsReview与ReviewReason()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var unit = MakeUnit("dataList[0].content", "Needs review text.");
            tm.Save(unit, new TranslationResult
            {
                Key = unit.Key,
                Translation = "待审译文",
                Source = TranslationSource.AI,
                NeedsReview = true,
                ReviewReason = "存在歧义",
            });

            var hit = tm.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash(unit.SourceText));

            Assert.NotNull(hit);
            Assert.True(hit.NeedsReview);
            Assert.Equal("存在歧义", hit.ReviewReason);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void CrossUnitSource查询_无记录时返回null()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            Assert.Null(tm.FindCrossUnitSource(SqliteTranslationMemory.ComputeSourceHash("不存在的文本")));
        }
        finally
        {
            Cleanup(options);
        }
    }


    [Fact]
    public void 旧版Schema数据库_应增量迁移且不丢数据()
    {
        var options = MakeOptions();
        try
        {
            // 1) 手工构造第0轮之前的旧 schema（无 ReviewReason 列），并写入一条历史记录
            using (var conn = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE translations (
                        Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                        UnitKey       TEXT NOT NULL,
                        SourceHash    TEXT NOT NULL,
                        SourceText    TEXT NOT NULL,
                        Translation   TEXT NOT NULL,
                        ContextKey    TEXT NULL,
                        TranslationSource INTEGER NOT NULL,
                        NeedsReview   INTEGER NOT NULL DEFAULT 0,
                        CreatedAt     TEXT NOT NULL,
                        UpdatedAt     TEXT NOT NULL
                    );
                    INSERT INTO translations
                        (UnitKey, SourceHash, SourceText, Translation, ContextKey, TranslationSource, NeedsReview, CreatedAt, UpdatedAt)
                    VALUES
                        ('Test.json|1|dataList[0].content', 'HASH_OLD', 'Legacy text.', '历史译文', NULL, 3, 1, '2026-08-31T00:00:00Z', '2026-08-31T00:00:00Z');
                    """;
                cmd.ExecuteNonQuery();
            }

            // 2) 用新版本打开：应自动 ADD COLUMN，且老数据仍可查询
            using (var tm = new SqliteTranslationMemory(options))
            {
                var hit = tm.FindExactUnit(
                    new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
                    "HASH_OLD");

                Assert.NotNull(hit);
                Assert.Equal("历史译文", hit.Translation);
                Assert.True(hit.NeedsReview);
                Assert.Null(hit.ReviewReason);
            }

            // 3) 迁移后应可写入带 ReviewReason 的新记录
            using (var tm = new SqliteTranslationMemory(options))
            {
                var unit = MakeUnit("dataList[1].content", "After migration.");
                tm.Save(unit, new TranslationResult
                {
                    Key = unit.Key,
                    Translation = "迁移后译文",
                    Source = TranslationSource.AI,
                    NeedsReview = true,
                    ReviewReason = "迁移后原因",
                });

                var hit = tm.FindExactUnit(unit.Key, SqliteTranslationMemory.ComputeSourceHash(unit.SourceText));
                Assert.NotNull(hit);
                Assert.Equal("迁移后原因", hit.ReviewReason);
            }

            // 4) schema 版本应被记录为 1
            using (var conn = new SqliteConnection($"Data Source={options.DatabasePath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA user_version;";
                Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));
            }
        }
        finally
        {
            Cleanup(options);
        }
    }
}
