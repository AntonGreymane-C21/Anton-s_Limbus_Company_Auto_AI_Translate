using Microsoft.Data.Sqlite;
using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Persistence;

public sealed class SqliteTranslationMemory : ITranslationMemory, IRequestCache, IDisposable
{
    private readonly string _connectionString;
    private readonly TranslationMemoryOptions _options;
    private readonly Action<string>? _log;

    private static readonly JsonSerializerOptions CacheSerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public SqliteTranslationMemory(TranslationMemoryOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log;
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        Initialize();
    }

    private void Initialize()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();

        if (_options.EnableWal)
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = "PRAGMA busy_timeout=" + _options.BusyTimeoutMs + ";";
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS translations (
                Id            INTEGER PRIMARY KEY AUTOINCREMENT,
                UnitKey       TEXT NOT NULL,
                SourceHash    TEXT NOT NULL,
                SourceText    TEXT NOT NULL,
                Translation   TEXT NOT NULL,
                ContextKey    TEXT NULL,
                TranslationSource INTEGER NOT NULL,
                NeedsReview   INTEGER NOT NULL DEFAULT 0,
                ReviewReason  TEXT NULL,
                CreatedAt     TEXT NOT NULL,
                UpdatedAt     TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_translations_SourceHash ON translations(SourceHash);
            CREATE INDEX IF NOT EXISTS IX_translations_UnitKey ON translations(UnitKey);
            """;
        cmd.ExecuteNonQuery();

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS request_cache (
                Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                Fingerprint  TEXT NOT NULL,
                ResponseJson TEXT NOT NULL,
                CreatedAt    TEXT NOT NULL,
                UNIQUE(Fingerprint)
            );
            """;
        cmd.ExecuteNonQuery();

        // 第1轮：TM schema 版本 1 —— translations 新增可空 ReviewReason（增量迁移，不破坏既有数据）
        EnsureReviewReasonColumn(conn);
        using (var versionCmd = conn.CreateCommand())
        {
            versionCmd.CommandText = "PRAGMA user_version = 1;";
            versionCmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 幂等增量迁移：translations.ReviewReason。
    /// 目的：让 TM 命中时能恢复完整审核信息链（NeedsReview + ReviewReason）。
    /// </summary>
    private static void EnsureReviewReasonColumn(SqliteConnection conn)
    {
        var exists = false;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(translations);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "ReviewReason", StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "ALTER TABLE translations ADD COLUMN ReviewReason TEXT NULL;";
            cmd.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=" + _options.BusyTimeoutMs + ";";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// ExactUnit 精确命中：UnitKey 与 SourceHash 同时一致。
    /// fail-safe：空 / 纯空白 SourceText 与空哈希永远不参与命中。
    /// </summary>
    public TranslationResult? FindExactUnit(UnitKey key, string sourceHash)
    {
        if (key is null || !IsUsableSourceHash(sourceHash))
        {
            return null;
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT UnitKey, SourceText, Translation, TranslationSource, NeedsReview, ReviewReason
            FROM translations
            WHERE UnitKey = @unitKey
              AND SourceHash = @hash
              AND TRIM(SourceText) <> ''
            ORDER BY TranslationSource ASC, Id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@unitKey", key.ToString());
        cmd.Parameters.AddWithValue("@hash", sourceHash);
        return ReadSingle(cmd, TranslationMemoryMatchType.ExactUnit);
    }

    /// <summary>
    /// CrossUnitSource 查询：仅按 SourceHash 查找（UnitKey 可能不同）。
    /// 【禁止】作为最终译文复用；仅供未来 Similar TM / ContextBuilder 参考。
    /// </summary>
    public TranslationResult? FindCrossUnitSource(string sourceHash)
    {
        if (!IsUsableSourceHash(sourceHash))
        {
            return null;
        }

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT UnitKey, SourceText, Translation, TranslationSource, NeedsReview, ReviewReason
            FROM translations
            WHERE SourceHash = @hash
              AND TRIM(SourceText) <> ''
            ORDER BY TranslationSource ASC, Id DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@hash", sourceHash);
        return ReadSingle(cmd, TranslationMemoryMatchType.CrossUnitSource);
    }

    /// <summary>
    /// 空字符串的 SourceHash：所有空字段共用该哈希，必须显式排除。
    /// </summary>
    public static readonly string EmptySourceHash = ComputeSourceHash(string.Empty);

    /// <summary>
    /// 该哈希是否可用于 TM 命中（空哈希不可用）。
    /// </summary>
    private static bool IsUsableSourceHash(string? sourceHash)
        => !string.IsNullOrWhiteSpace(sourceHash)
           && !string.Equals(sourceHash, EmptySourceHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 读取单条记录（不返回任何译文以外的敏感信息）。
    /// </summary>
    private static TranslationResult? ReadSingle(SqliteCommand cmd, TranslationMemoryMatchType matchType)
    {
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new TranslationResult
        {
            Key = ParseKey(reader.GetString(0)),
            Translation = reader.GetString(2),
            Source = (TranslationSource)reader.GetInt32(3),
            NeedsReview = reader.GetInt32(4) != 0,
            ReviewReason = reader.IsDBNull(5) ? null : reader.GetString(5),
            TmMatchType = matchType,
        };
    }

    /// <summary>
    /// 保存译文（Batch 完成后立即保存）。
    /// fail-safe：空 / 纯空白 SourceText 不写入，避免空哈希记录污染 TM。
    /// </summary>
    public void Save(TranslationUnit unit, TranslationResult result)
    {
        if (!SourceTextGuard.IsReusable(unit.SourceText))
        {
            return;
        }

        Insert(unit, result.Translation, result.Source, result.NeedsReview, result.ReviewReason);
    }

    /// <summary>
    /// 人工审核确认后写回：TranslationSource.HumanReviewed 且 NeedsReview=false。
    /// 空源文或空译文不写入。
    /// </summary>
    /// <returns>是否成功写入</returns>
    public bool SaveHumanReviewed(TranslationUnit unit, string translation)
    {
        if (!SourceTextGuard.IsReusable(unit.SourceText))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        Insert(unit, translation, TranslationSource.HumanReviewed, needsReview: false, reviewReason: null);
        return true;
    }

    /// <summary>
    /// 单条短事务写入（Checkpoint）。ContextKey 仍固定为 NULL（Context 复用决策留给后续轮次）。
    /// </summary>
    private void Insert(
        TranslationUnit unit,
        string translation,
        TranslationSource source,
        bool needsReview,
        string? reviewReason)
    {
        var sourceHash = ComputeSourceHash(unit.SourceText, unit.SourceHashSalt);
        var unitKey = unit.Key.ToString();
        var now = DateTime.UtcNow.ToString("o");

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO translations
                    (UnitKey, SourceHash, SourceText, Translation,
                     ContextKey, TranslationSource, NeedsReview, ReviewReason, CreatedAt, UpdatedAt)
                VALUES
                    (@key, @hash, @source, @translation,
                     NULL, @sourceType, @needsReview, @reviewReason, @createdAt, @updatedAt)
                """;
            cmd.Parameters.AddWithValue("@key", unitKey);
            cmd.Parameters.AddWithValue("@hash", sourceHash);
            cmd.Parameters.AddWithValue("@source", unit.SourceText);
            cmd.Parameters.AddWithValue("@translation", translation);
            cmd.Parameters.AddWithValue("@sourceType", (int)source);
            cmd.Parameters.AddWithValue("@needsReview", needsReview ? 1 : 0);
            cmd.Parameters.AddWithValue("@reviewReason", (object?)reviewReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@createdAt", now);
            cmd.Parameters.AddWithValue("@updatedAt", now);
            cmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public string? FindRequestCache(string fingerprint)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ResponseJson FROM request_cache WHERE Fingerprint = @fp";
        cmd.Parameters.AddWithValue("@fp", fingerprint);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>
    /// 第4轮：按指纹读取可重放的 Provider 响应。
    /// 任何坏数据（JSON 损坏 / 版本不支持 / 结构不完整）都降级为 Cache Miss，绝不抛异常。
    /// </summary>
    public CachedProviderBatchResponse? TryGet(string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        try
        {
            var json = FindRequestCache(fingerprint);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var cached = JsonSerializer.Deserialize<CachedProviderBatchResponse>(json, CacheSerializerOptions);
            if (cached is null
                || cached.FormatVersion != CachedProviderBatchResponse.CurrentFormatVersion
                || cached.Items is null
                || cached.Items.Count == 0)
            {
                _log?.Invoke($"[调试] RequestCache 记录不可用（版本/结构），按 Cache Miss 处理: Fingerprint={Shorten(fingerprint)}");
                return null;
            }

            return cached;
        }
        catch (Exception ex)
        {
            // 一条坏缓存不允许导致整轮翻译失败
            _log?.Invoke($"[调试] RequestCache 读取失败，按 Cache Miss 处理: Fingerprint={Shorten(fingerprint)} - {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 第4轮：写入缓存响应（同指纹安全覆盖；并发重复写入不会失败）。
    /// </summary>
    public void Save(string fingerprint, CachedProviderBatchResponse response)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || response is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(response, CacheSerializerOptions);
        SaveRequestCache(fingerprint, json);
    }

    private static string Shorten(string fingerprint) => fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];

    public void SaveRequestCache(string fingerprint, string responseJson)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO request_cache (Fingerprint, ResponseJson, CreatedAt)
                VALUES (@fp, @json, @now)
                """;
            cmd.Parameters.AddWithValue("@fp", fingerprint);
            cmd.Parameters.AddWithValue("@json", responseJson);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
            cmd.ExecuteNonQuery();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static UnitKey ParseKey(string keyString)
    {
        var parts = keyString.Split('|');
        return new UnitKey
        {
            RelativeFilePath = parts.Length > 0 ? parts[0] : string.Empty,
            RecordId = parts.Length > 1 ? parts[1] : string.Empty,
            FieldPath = parts.Length > 2 ? parts[2] : string.Empty,
        };
    }

    public static string ComputeSourceHash(string sourceText)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(sourceText ?? string.Empty);
        return Convert.ToHexString(sha.ComputeHash(bytes));
    }

    /// <summary>
    /// 第9.0B轮：带盐的 SourceHash（源语言模式隔离）。
    /// <paramref name="salt"/> 为 null/空 → **完全等价于**历史 EN-only 哈希（旧 TM 仍可命中）；
    /// 非空 → SHA256(salt + NUL + sourceText)，使 JP / KO（含回退韩文）绝不命中旧 EN 记录。
    /// </summary>
    public static string ComputeSourceHash(string sourceText, string? salt)
        => string.IsNullOrEmpty(salt)
            ? ComputeSourceHash(sourceText)
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(salt + "\u0000" + (sourceText ?? string.Empty))));

    /// <summary>
    /// 由「请求语言 / 生效语言 / 韩文原文」构造 SourceHash 盐（集中一处，避免各处拼接漂移）。
    /// 纯 EN 模式（请求与生效都是 EN）返回 null → 保持与旧 TM 兼容；
    /// JP / KO / 回退韩文一律返回非空盐 → 绝不命中旧 EN 记录（即使最终文本相同）。
    /// </summary>
    public static string? BuildLanguageSalt(
        SourceLanguage requested,
        SourceLanguage effective,
        string? canonicalKoreanText)
    {
        if (requested == SourceLanguage.English && effective == SourceLanguage.English)
        {
            return null;
        }

        var koreanHash = string.IsNullOrWhiteSpace(canonicalKoreanText)
            ? "-"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalKoreanText!)))[..16];

        return $"v2|{SourceLanguageHelper.ToCode(requested)}|{SourceLanguageHelper.ToCode(effective)}|{koreanHash}";
    }

    public void Dispose()
    {
        // 执行 WAL checkpoint，确保数据落盘并释放文件句柄
        try
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 忽略释放错误
        }
    }
}