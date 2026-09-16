using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Diagnostics;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8轮：真实库受控冒烟辅助的离线测试。
/// 所有文件均在临时目录，不访问用户真实数据库。
/// </summary>
[Collection(SqliteCollection.Name)]
public class RealApiSmokeDatabaseTests
{
    [Fact]
    public void 旧Schema_备份与仅Migration_保留聚合统计()
    {
        var root = Path.Combine(Path.GetTempPath(), "LT_SmokeDb_" + Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(root, "translation_memory.db");
        var backupDirectory = Path.Combine(root, "backup");
        Directory.CreateDirectory(root);

        try
        {
            CreateLegacyDatabase(databasePath);
            var before = RealApiSmokeDatabase.Capture(databasePath);
            var backup = RealApiSmokeDatabase.CreateOfflineBackup(databasePath, backupDirectory);
            var result = RealApiSmokeDatabase.MigrateExistingDatabase(databasePath);

            Assert.Equal(0, before.UserVersion);
            Assert.Equal(1, before.TranslationsRowCount);
            Assert.Equal(before.DatabaseSha256, backup.DatabaseSha256);
            Assert.Equal(1, result.After.UserVersion);
            Assert.Contains("ReviewReason", result.After.TranslationsColumns);
            Assert.Equal(before.TranslationsRowCount, result.After.TranslationsRowCount);
            Assert.Equal(before.RequestCacheRowCount, result.After.RequestCacheRowCount);
            Assert.Equal(before.NeedsReviewCount, result.After.NeedsReviewCount);
            Assert.Equal(before.TranslationSourceDistribution, result.After.TranslationSourceDistribution);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响测试结论。
            }
        }
    }

    private static void CreateLegacyDatabase(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE translations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UnitKey TEXT NOT NULL,
                SourceHash TEXT NOT NULL,
                SourceText TEXT NOT NULL,
                Translation TEXT NOT NULL,
                ContextKey TEXT NULL,
                TranslationSource INTEGER NOT NULL,
                NeedsReview INTEGER NOT NULL DEFAULT 0,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE request_cache (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Fingerprint TEXT NOT NULL,
                ResponseJson TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                UNIQUE(Fingerprint)
            );
            INSERT INTO translations
                (UnitKey, SourceHash, SourceText, Translation, ContextKey, TranslationSource, NeedsReview, CreatedAt, UpdatedAt)
            VALUES
                ('Smoke.json|1|dataList[0].name', 'HASH', 'source', 'translation', NULL, {(int)TranslationSource.AI}, 1, '2026-09-14T00:00:00.0000000Z', '2026-09-14T00:00:00.0000000Z');
            INSERT INTO request_cache (Fingerprint, ResponseJson, CreatedAt)
            VALUES ('v1:test', 'empty', '2026-09-14T00:00:00.0000000Z');
            PRAGMA user_version = 0;
            """;
        command.ExecuteNonQuery();
    }
}
