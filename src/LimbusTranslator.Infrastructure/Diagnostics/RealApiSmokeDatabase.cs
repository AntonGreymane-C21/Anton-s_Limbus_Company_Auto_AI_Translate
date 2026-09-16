using System.Security.Cryptography;
using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// 第8轮真实数据库受控冒烟所需的只读指纹、离线备份与迁移核验。
///
/// 该类刻意不读取或输出任何 SourceText / Translation / RequestCache 响应内容；
/// 只处理 schema、行数、哈希和聚合统计。
/// </summary>
public static class RealApiSmokeDatabase
{
    /// <summary>读取数据库指纹，不会执行 migration 或写入数据库。</summary>
    public static RealApiSmokeDatabaseSnapshot Capture(string databasePath)
    {
        var fullPath = RequireExistingDatabase(databasePath);
        var file = new FileInfo(fullPath);

        using var connection = OpenReadOnly(fullPath);
        var translationsExists = TableExists(connection, "translations");
        var cacheExists = TableExists(connection, "request_cache");

        return new RealApiSmokeDatabaseSnapshot
        {
            DatabasePath = fullPath,
            DatabaseSizeBytes = file.Length,
            DatabaseSha256 = ComputeSha256(fullPath),
            WalExists = File.Exists(fullPath + "-wal"),
            WalSizeBytes = GetFileSize(fullPath + "-wal"),
            ShmExists = File.Exists(fullPath + "-shm"),
            ShmSizeBytes = GetFileSize(fullPath + "-shm"),
            UserVersion = ReadUserVersion(connection),
            TranslationsRowCount = translationsExists ? ReadCount(connection, "translations") : 0,
            RequestCacheRowCount = cacheExists ? ReadCount(connection, "request_cache") : 0,
            TranslationsColumns = translationsExists ? ReadColumns(connection, "translations") : Array.Empty<string>(),
            RequestCacheColumns = cacheExists ? ReadColumns(connection, "request_cache") : Array.Empty<string>(),
            TranslationSourceDistribution = translationsExists
                ? ReadTranslationSourceDistribution(connection)
                : new Dictionary<string, int>(),
            NeedsReviewCount = translationsExists ? ReadNeedsReviewCount(connection) : 0,
        };
    }

    /// <summary>
    /// 创建离线一致性备份。
    /// 当前策略只允许 WAL 为空时复制 .db/.db-wal/.db-shm；否则拒绝，避免产生不一致备份。
    /// </summary>
    public static RealApiSmokeDatabaseSnapshot CreateOfflineBackup(string databasePath, string destinationDirectory)
    {
        var before = Capture(databasePath);
        if (before.WalExists && before.WalSizeBytes > 0)
        {
            throw new InvalidOperationException(
                "[错误] 当前 translation_memory.db-wal 非空，已拒绝离线复制。请先停止占用进程并完成 checkpoint，或改用 SQLite 在线 Backup API。");
        }

        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new InvalidOperationException($"[错误] 备份目录已存在内容，拒绝覆盖: {destination}");
        }

        Directory.CreateDirectory(destination);
        var targetDatabase = Path.Combine(destination, Path.GetFileName(before.DatabasePath));
        File.Copy(before.DatabasePath, targetDatabase, overwrite: false);
        CopySidecarIfPresent(before.DatabasePath + "-wal", targetDatabase + "-wal");
        CopySidecarIfPresent(before.DatabasePath + "-shm", targetDatabase + "-shm");

        var copied = Capture(targetDatabase);
        if (!string.Equals(before.DatabaseSha256, copied.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("[错误] 备份后 SHA256 与源数据库不一致，已停止后续操作。");
        }

        SaveSnapshot(Path.Combine(destination, "database_snapshot.json"), copied);
        return copied;
    }

    /// <summary>
    /// 仅初始化现有真实数据库，触发项目既有的增量 schema migration，不会翻译或访问网络。
    /// </summary>
    public static RealApiSmokeMigrationResult MigrateExistingDatabase(string databasePath, Action<string>? log = null)
    {
        var before = Capture(databasePath);
        using (var memory = new SqliteTranslationMemory(
                   new TranslationMemoryOptions { DatabasePath = before.DatabasePath }, log))
        {
            // 构造函数内的 Initialize 是当前项目唯一 schema migration 入口。
        }

        var after = Capture(before.DatabasePath);
        ValidateMigration(before, after);
        return new RealApiSmokeMigrationResult { Before = before, After = after };
    }

    /// <summary>
    /// 仅在克隆库中删除指定 UnitKey + SourceHash 的当前记录，用于验证 TM Miss → RequestCache Hit。
    /// 返回实际删除行数。
    /// </summary>
    public static int DeleteExactTranslations(
        string clonedDatabasePath,
        IReadOnlyDictionary<string, string> unitKeyToSourceHash)
    {
        if (unitKeyToSourceHash.Count == 0)
        {
            return 0;
        }

        var fullPath = RequireExistingDatabase(clonedDatabasePath);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            var deleted = 0;
            foreach (var pair in unitKeyToSourceHash)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM translations WHERE UnitKey = @unitKey AND SourceHash = @sourceHash;";
                command.Parameters.AddWithValue("@unitKey", pair.Key);
                command.Parameters.AddWithValue("@sourceHash", pair.Value);
                deleted += command.ExecuteNonQuery();
            }

            transaction.Commit();
            return deleted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    /// <summary>统计指定 UnitKey + SourceHash 是否已写回 TM，不读取任何译文内容。</summary>
    public static RealApiSmokeSelectedMemoryStats CaptureSelectedMemory(
        string databasePath,
        IReadOnlyDictionary<string, string> unitKeyToSourceHash)
    {
        var fullPath = RequireExistingDatabase(databasePath);
        using var connection = OpenReadOnly(fullPath);
        var exactUnitHitCount = 0;
        var matchingRowCount = 0;
        var needsReviewRowCount = 0;
        var sourceDistribution = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var pair in unitKeyToSourceHash)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT TranslationSource, NeedsReview
                FROM translations
                WHERE UnitKey = @unitKey AND SourceHash = @sourceHash;
                """;
            command.Parameters.AddWithValue("@unitKey", pair.Key);
            command.Parameters.AddWithValue("@sourceHash", pair.Value);
            using var reader = command.ExecuteReader();
            var hit = false;
            while (reader.Read())
            {
                hit = true;
                matchingRowCount++;
                var source = reader.GetInt32(0);
                var sourceName = Enum.IsDefined(typeof(TranslationSource), source)
                    ? ((TranslationSource)source).ToString()
                    : $"Unknown({source})";
                sourceDistribution[sourceName] = sourceDistribution.TryGetValue(sourceName, out var count)
                    ? count + 1
                    : 1;
                if (reader.GetInt32(1) != 0)
                {
                    needsReviewRowCount++;
                }
            }

            if (hit)
            {
                exactUnitHitCount++;
            }
        }

        return new RealApiSmokeSelectedMemoryStats
        {
            PlannedUnitCount = unitKeyToSourceHash.Count,
            ExactUnitHitCount = exactUnitHitCount,
            MatchingRowCount = matchingRowCount,
            NeedsReviewRowCount = needsReviewRowCount,
            TranslationSourceDistribution = sourceDistribution,
        };
    }

    /// <summary>找出当前已存在 ExactUnit TM 的条目键，用于首轮真实 API 只选真正 TM Miss 的样本。</summary>
    public static IReadOnlySet<string> FindExistingExactUnitKeys(
        string databasePath,
        IEnumerable<DiffEntry> entries)
    {
        var fullPath = RequireExistingDatabase(databasePath);
        using var connection = OpenReadOnly(fullPath);
        var hits = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.NewSourceText))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1
                FROM translations
                WHERE UnitKey = @unitKey AND SourceHash = @sourceHash
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@unitKey", entry.Key.ToString());
            command.Parameters.AddWithValue("@sourceHash", SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText));
            if (command.ExecuteScalar() is not null)
            {
                hits.Add(entry.Key.ToString());
            }
        }

        return hits;
    }

    /// <summary>保存不含译文内容的数据库指纹 JSON。</summary>
    public static void SaveSnapshot(string path, RealApiSmokeDatabaseSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    /// <summary>保存不含译文内容的 migration 结果 JSON。</summary>
    public static void SaveMigrationResult(string path, RealApiSmokeMigrationResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(result, JsonOptions));
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void ValidateMigration(RealApiSmokeDatabaseSnapshot before, RealApiSmokeDatabaseSnapshot after)
    {
        if (after.UserVersion != 1)
        {
            throw new InvalidOperationException($"[错误] Migration 后 user_version 应为 1，实际为 {after.UserVersion}。");
        }
        if (!after.TranslationsColumns.Contains("ReviewReason", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("[错误] Migration 后 translations 缺少 ReviewReason 列。");
        }
        if (before.TranslationsRowCount != after.TranslationsRowCount)
        {
            throw new InvalidOperationException("[错误] Migration 后 translations 行数发生变化，已停止。");
        }
        if (before.RequestCacheRowCount != after.RequestCacheRowCount)
        {
            throw new InvalidOperationException("[错误] Migration 后 request_cache 行数发生变化，已停止。");
        }
        if (before.NeedsReviewCount != after.NeedsReviewCount)
        {
            throw new InvalidOperationException("[错误] Migration 后 NeedsReview 数量发生变化，已停止。");
        }
        if (!DictionaryEqual(before.TranslationSourceDistribution, after.TranslationSourceDistribution))
        {
            throw new InvalidOperationException("[错误] Migration 后 TranslationSource 分布发生变化，已停止。");
        }
    }

    private static bool DictionaryEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
        => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string RequireExistingDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        var fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("[错误] 真实 translation_memory.db 不存在，拒绝创建空库。", fullPath);
        }

        return fullPath;
    }

    private static long GetFileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static void CopySidecarIfPresent(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: false);
        }
    }

    private static string ComputeSha256(string path)
    {
        // SQLite 只读连接可能仍持有共享句柄；哈希读取不应因此误报“文件被占用”。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream));
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static int ReadUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int ReadCount(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IReadOnlyList<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static IReadOnlyDictionary<string, int> ReadTranslationSourceDistribution(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT TranslationSource, COUNT(*) FROM translations GROUP BY TranslationSource ORDER BY TranslationSource;";
        using var reader = command.ExecuteReader();
        var distribution = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var value = reader.GetInt32(0);
            var name = Enum.IsDefined(typeof(TranslationSource), value)
                ? ((TranslationSource)value).ToString()
                : $"Unknown({value})";
            distribution[name] = reader.GetInt32(1);
        }

        return distribution;
    }

    private static int ReadNeedsReviewCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM translations WHERE NeedsReview <> 0;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}

/// <summary>不含文本内容的真实数据库指纹。</summary>
public sealed class RealApiSmokeDatabaseSnapshot
{
    public required string DatabasePath { get; init; }
    public long DatabaseSizeBytes { get; init; }
    public required string DatabaseSha256 { get; init; }
    public bool WalExists { get; init; }
    public long WalSizeBytes { get; init; }
    public bool ShmExists { get; init; }
    public long ShmSizeBytes { get; init; }
    public int UserVersion { get; init; }
    public int TranslationsRowCount { get; init; }
    public int RequestCacheRowCount { get; init; }
    public required IReadOnlyList<string> TranslationsColumns { get; init; }
    public required IReadOnlyList<string> RequestCacheColumns { get; init; }
    public required IReadOnlyDictionary<string, int> TranslationSourceDistribution { get; init; }
    public int NeedsReviewCount { get; init; }
}

/// <summary>真实数据库 migration 前后对比。</summary>
public sealed class RealApiSmokeMigrationResult
{
    public required RealApiSmokeDatabaseSnapshot Before { get; init; }
    public required RealApiSmokeDatabaseSnapshot After { get; init; }
}

/// <summary>指定冒烟样本在 TM 中的脱敏命中统计。</summary>
public sealed class RealApiSmokeSelectedMemoryStats
{
    public int PlannedUnitCount { get; init; }
    public int ExactUnitHitCount { get; init; }
    public int MatchingRowCount { get; init; }
    public int NeedsReviewRowCount { get; init; }
    public required IReadOnlyDictionary<string, int> TranslationSourceDistribution { get; init; }
}
