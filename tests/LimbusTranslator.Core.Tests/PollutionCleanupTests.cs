using LimbusTranslator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.875轮：污染清理谓词（只删 AI + 指定日期窗口；人工/官方/导入永不删除）。全部使用临时 SQLite。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class PollutionCleanupTests : IDisposable
{
    private readonly string _dbPath;

    public PollutionCleanupTests()
        => _dbPath = Path.Combine(Path.GetTempPath(), "limbus_cleanup_" + Guid.NewGuid().ToString("N")[..8] + ".db");

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private SqliteConnection OpenWithSchema()
    {
        var connection = new SqliteConnection("Data Source=" + _dbPath);
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE translations (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, UnitKey TEXT NOT NULL, SourceHash TEXT NOT NULL,
    SourceText TEXT NOT NULL, Translation TEXT NOT NULL, ContextKey TEXT NULL,
    TranslationSource INTEGER NOT NULL, NeedsReview INTEGER NOT NULL DEFAULT 0,
    CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, ReviewReason TEXT NULL);
CREATE TABLE request_cache (
    Id INTEGER PRIMARY KEY AUTOINCREMENT, Fingerprint TEXT NOT NULL UNIQUE,
    ResponseJson TEXT NOT NULL, CreatedAt TEXT NOT NULL);";
        cmd.ExecuteNonQuery();
        return connection;
    }

    private static void Insert(SqliteConnection connection, string unitKey, int source, string createdAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"INSERT INTO translations
            (UnitKey, SourceHash, SourceText, Translation, TranslationSource, NeedsReview, CreatedAt, UpdatedAt)
            VALUES (@k, @h, 'Source', '译', @src, 0, @c, @c)";
        cmd.Parameters.AddWithValue("@k", unitKey);
        cmd.Parameters.AddWithValue("@h", "HASH" + unitKey);
        cmd.Parameters.AddWithValue("@src", source);
        cmd.Parameters.AddWithValue("@c", createdAt);
        cmd.ExecuteNonQuery();
    }

    private static void InsertCache(SqliteConnection connection, string fingerprint, string createdAt)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO request_cache (Fingerprint, ResponseJson, CreatedAt) VALUES (@f, '{}', @c)";
        cmd.Parameters.AddWithValue("@f", fingerprint);
        cmd.Parameters.AddWithValue("@c", createdAt);
        cmd.ExecuteNonQuery();
    }

    private static int Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void 清理_只删AI且只删指定日期窗口_人工来源必须保留()
    {
        using var connection = OpenWithSchema();
        const int ai = PollutionCleanupService.AiSourceValue;

        Insert(connection, "A|1|f", ai, "2026-09-15T02:01:04.4000000Z");
        Insert(connection, "A|2|f", ai, "2026-09-15T02:01:04.5000000Z");
        Insert(connection, "A|3|f", ai, "2026-09-14T07:05:07.0000000Z");
        Insert(connection, "A|4|f", 1, "2026-09-15T03:00:00.0000000Z");
        Insert(connection, "A|5|f", 0, "2026-09-15T03:00:00.0000000Z");
        Insert(connection, "A|6|f", 2, "2026-09-15T03:00:00.0000000Z");
        InsertCache(connection, "v2:aaa", "2026-09-15T01:55:14.0000000Z");
        InsertCache(connection, "v1:bbb", "2026-09-14T07:05:07.0000000Z");

        var day = new DateOnly(2026, 9, 15);
        var (tmCandidates, cacheCandidates) = PollutionCleanupService.CountCandidates(connection, day);
        Assert.Equal(2, tmCandidates);
        Assert.Equal(1, cacheCandidates);

        var outcome = PollutionCleanupService.Execute(connection, day);

        Assert.True(outcome.Committed);
        Assert.Equal(2, outcome.DeletedTranslations);
        Assert.Equal(1, outcome.DeletedCacheEntries);
        Assert.Equal(outcome.PreservedBefore, outcome.PreservedAfter);

        Assert.Equal(3, Scalar(connection, "SELECT COUNT(*) FROM translations WHERE TranslationSource <> 3"));
        Assert.Equal(4, Scalar(connection, "SELECT COUNT(*) FROM translations"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM request_cache"));
    }

    [Fact]
    public void 清理_dryRun不得修改任何数据()
    {
        using var connection = OpenWithSchema();
        Insert(connection, "B|1|f", PollutionCleanupService.AiSourceValue, "2026-09-15T02:01:04.4000000Z");
        InsertCache(connection, "v2:ccc", "2026-09-15T02:01:04.5000000Z");

        var outcome = PollutionCleanupService.Execute(connection, new DateOnly(2026, 9, 15), dryRun: true);

        Assert.True(outcome.DryRun);
        Assert.Equal(1, outcome.CandidateTranslations);
        Assert.Equal(1, outcome.CandidateCacheEntries);
        Assert.Equal(0, outcome.DeletedTranslations);
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM translations"));
        Assert.Equal(1, Scalar(connection, "SELECT COUNT(*) FROM request_cache"));
    }

    [Fact]
    public void 清理_重复执行应为幂等()
    {
        using var connection = OpenWithSchema();
        Insert(connection, "C|1|f", PollutionCleanupService.AiSourceValue, "2026-09-15T02:01:04.4000000Z");

        var day = new DateOnly(2026, 9, 15);
        Assert.Equal(1, PollutionCleanupService.Execute(connection, day).DeletedTranslations);
        Assert.Equal(0, PollutionCleanupService.Execute(connection, day).DeletedTranslations);
    }
}