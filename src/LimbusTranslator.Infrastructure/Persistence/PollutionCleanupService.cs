using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Infrastructure.Persistence;

/// <summary>
/// 生产库污染清理（第8.875轮）。
///
/// 场景：术语链损坏期间（第8.87轮修复前）真实运行过翻译，向生产 TM / request_cache 写入了错误术语条件下的结果。
///
/// 安全约束（硬编码，不可放宽）：
///   - 只删除 **AI** 来源（TranslationSource = 3）的 translations；
///   - 只删除落在指定 UTC 日期窗口 [day 00:00, 次日 00:00) 内的记录；
///   - HumanReviewed(1) / Official(0) / Imported(2) 及其它来源**永不删除**；
///   - 单事务 + 事务内计数校验，不符则 ROLLBACK 并抛错。
/// </summary>
public static class PollutionCleanupService
{
    /// <summary>AI 来源枚举整数值（与 TranslationSource.AI 一致）。</summary>
    public const int AiSourceValue = 3;

    /// <summary>清理候选数量（只读，不修改任何数据）。</summary>
    public static (int Translations, int CacheEntries) CountCandidates(SqliteConnection connection, DateOnly utcDay)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var tm = CountRows(connection, null, TranslatingCountSql, utcDay, true);
        var cache = CountRows(connection, null, CacheCountSql, utcDay, false);
        return (tm, cache);
    }

    /// <summary>人工 / 官方 / 导入来源合计（清理前后必须一致）。</summary>
    public static int CountPreservedSources(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM translations WHERE TranslationSource <> @src";
        cmd.Parameters.AddWithValue("@src", AiSourceValue);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>执行清理（单事务；dryRun=true 时只统计）。校验失败会 ROLLBACK 并抛异常。</summary>
    public static PollutionCleanupOutcome Execute(SqliteConnection connection, DateOnly utcDay, bool dryRun = false)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var preservedBefore = CountPreservedSources(connection);
        var (expectedTm, expectedCache) = CountCandidates(connection, utcDay);

        if (dryRun)
        {
            return new PollutionCleanupOutcome
            {
                DryRun = true,
                CandidateTranslations = expectedTm,
                CandidateCacheEntries = expectedCache,
                PreservedBefore = preservedBefore,
                PreservedAfter = preservedBefore,
            };
        }

        using var transaction = connection.BeginTransaction();

        var deletedTm = DeleteRows(connection, transaction, TranslatingDeleteSql, utcDay, true);
        var deletedCache = DeleteRows(connection, transaction, CacheDeleteSql, utcDay, false);
        var remaining = CountRows(connection, transaction, TranslatingCountSql, utcDay, true);
        var preservedAfter = CountPreservedSources(connection, transaction);

        var ok = deletedTm == expectedTm && deletedCache == expectedCache
                 && remaining == 0 && preservedAfter == preservedBefore;

        if (!ok)
        {
            transaction.Rollback();
            throw new InvalidOperationException(
                $"[错误] 污染清理事务校验失败，已回滚：TM={deletedTm}/{expectedTm}，Cache={deletedCache}/{expectedCache}，" +
                $"剩余={remaining}，人工来源 前={preservedBefore}/后={preservedAfter}");
        }

        transaction.Commit();

        return new PollutionCleanupOutcome
        {
            DeletedTranslations = deletedTm,
            DeletedCacheEntries = deletedCache,
            CandidateTranslations = expectedTm,
            CandidateCacheEntries = expectedCache,
            PreservedBefore = preservedBefore,
            PreservedAfter = preservedAfter,
            Committed = true,
        };
    }

    private const string TranslatingCountSql =
        "SELECT COUNT(*) FROM translations WHERE TranslationSource = @src AND CreatedAt >= @from AND CreatedAt < @to";

    private const string TranslatingDeleteSql =
        "DELETE FROM translations WHERE TranslationSource = @src AND CreatedAt >= @from AND CreatedAt < @to";

    private const string CacheCountSql =
        "SELECT COUNT(*) FROM request_cache WHERE CreatedAt >= @from AND CreatedAt < @to";

    private const string CacheDeleteSql =
        "DELETE FROM request_cache WHERE CreatedAt >= @from AND CreatedAt < @to";

    private static int CountRows(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        DateOnly utcDay,
        bool withSourceFilter)
        => Convert.ToInt32(Prepare(connection, transaction, sql, utcDay, withSourceFilter).ExecuteScalar());

    private static int DeleteRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        DateOnly utcDay,
        bool withSourceFilter)
    {
        using var cmd = Prepare(connection, transaction, sql, utcDay, withSourceFilter);
        return cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Prepare(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        DateOnly utcDay,
        bool withSourceFilter)
    {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        if (withSourceFilter)
        {
            cmd.Parameters.AddWithValue("@src", AiSourceValue);
        }

        cmd.Parameters.AddWithValue("@from", utcDay.ToString("yyyy-MM-dd") + "T00:00:00");
        cmd.Parameters.AddWithValue("@to", utcDay.AddDays(1).ToString("yyyy-MM-dd") + "T00:00:00");
        return cmd;
    }
}

/// <summary>污染清理结果（报告与验证用）。</summary>
public sealed class PollutionCleanupOutcome
{
    /// <summary>是否为预演（未删除）</summary>
    public bool DryRun { get; init; }

    /// <summary>实际删除的 translations 数量</summary>
    public int DeletedTranslations { get; init; }

    /// <summary>实际删除的 request_cache 数量</summary>
    public int DeletedCacheEntries { get; init; }

    /// <summary>候选 translations 数量</summary>
    public int CandidateTranslations { get; init; }

    /// <summary>候选 request_cache 数量</summary>
    public int CandidateCacheEntries { get; init; }

    /// <summary>清理前非 AI 来源计数</summary>
    public int PreservedBefore { get; init; }

    /// <summary>清理后非 AI 来源计数（必须与清理前一致）</summary>
    public int PreservedAfter { get; init; }

    /// <summary>是否已提交</summary>
    public bool Committed { get; init; }
}