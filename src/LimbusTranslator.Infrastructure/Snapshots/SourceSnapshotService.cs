using System.Text.Json;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Snapshots;

/// <summary>快照中的单个源文单元（最小字段集）。</summary>
public sealed class SourceSnapshotUnit
{
    /// <summary>相对文件路径（逻辑路径，与 UnitKey 一致）</summary>
    public required string RelativeFilePath { get; init; }

    /// <summary>记录 ID</summary>
    public required string RecordId { get; init; }

    /// <summary>字段路径</summary>
    public required string FieldPath { get; init; }

    /// <summary>源文</summary>
    public required string SourceText { get; init; }

    /// <summary>说话人（可空）</summary>
    public string? Speaker { get; init; }

    /// <summary>顺序（可空）</summary>
    public int? Order { get; init; }
}

/// <summary>源语言快照（第8.89轮）：用于下一次分析得到**真实 OldSource**。</summary>
public sealed class SourceSnapshot
{
    /// <summary>结构版本</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>语言（当前阶段仅 en）</summary>
    public string Language { get; init; } = "en";

    /// <summary>创建时间（UTC）</summary>
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>源根目录（诊断）</summary>
    public string? SourceRoot { get; init; }

    /// <summary>单元</summary>
    public IReadOnlyList<SourceSnapshotUnit> Units { get; init; } = Array.Empty<SourceSnapshotUnit>();

    /// <summary>当前结构版本</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// 源快照服务（第8.89轮）。
///
/// 目的：磁盘上通常只有**一份**当前英文源，无法得到真实的 <c>TranslateModified</c>；
/// 本服务在每次**成功**分析后保存基线，下一次分析即可用「上一份快照 vs 当前源」构成真实 Diff。
///
/// 存储：<c>{projectRoot}/data/cache/source_snapshots/{ko|en|ja}/{current|previous}.json</c>（每种语言独立轮换）。
/// 写入：临时文件 → 回读校验 → 原子替换；**分析失败不得覆盖旧快照**（调用方只在成功时调用本服务）。
/// 读取：文件内声明的 <c>language</c> 必须与调用方请求一致，否则视为无效（不得静默错用）。
/// </summary>
public static class SourceSnapshotService
{
    /// <summary>快照目录（相对项目根）</summary>
    public const string RelativeDirectory = "data/cache/source_snapshots";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>快照文件名（集中定义，避免散落魔法文件名）。</summary>
    public const string CurrentFileName = "current.json";

    /// <summary>上一版快照文件名。</summary>
    public const string PreviousFileName = "previous.json";

    /// <summary>快照根目录绝对路径。</summary>
    public static string GetDirectory(string projectRoot) => Path.Combine(projectRoot, RelativeDirectory);

    /// <summary>某语言的快照目录：<c>{root}/data/cache/source_snapshots/{ko|en|ja}/</c>。</summary>
    public static string GetLanguageDirectory(string projectRoot, SourceLanguage language)
        => Path.Combine(GetDirectory(projectRoot), SourceLanguageHelper.ToCode(language));

    /// <summary>某语言当前快照路径。</summary>
    public static string GetCurrentPath(string projectRoot, SourceLanguage language = SourceLanguage.English)
        => Path.Combine(GetLanguageDirectory(projectRoot, language), CurrentFileName);

    /// <summary>某语言上一版快照路径。</summary>
    public static string GetPreviousPath(string projectRoot, SourceLanguage language = SourceLanguage.English)
        => Path.Combine(GetLanguageDirectory(projectRoot, language), PreviousFileName);

    /// <summary>读取当前快照（文件不存在/损坏/语言不匹配 → null）。</summary>
    public static SourceSnapshot? TryLoadCurrent(string projectRoot, SourceLanguage language = SourceLanguage.English)
        => TryLoad(GetCurrentPath(projectRoot, language), language);

    /// <summary>读取上一版快照。</summary>
    public static SourceSnapshot? TryLoadPrevious(string projectRoot, SourceLanguage language = SourceLanguage.English)
        => TryLoad(GetPreviousPath(projectRoot, language), language);

    /// <summary>某语言是否已有 baseline。</summary>
    public static bool HasBaseline(string projectRoot, SourceLanguage language = SourceLanguage.English)
        => TryLoadCurrent(projectRoot, language) is not null;

    private static SourceSnapshot? TryLoad(string path, SourceLanguage requested)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<SourceSnapshot>(File.ReadAllText(path), JsonOptions);
            if (snapshot is null || snapshot.SchemaVersion != SourceSnapshot.CurrentSchemaVersion)
            {
                return null;
            }

            // 语言必须与调用方请求一致：不一致视为无效，**不得静默错用**
            var fileLanguage = SourceLanguageHelper.TryParseCode(snapshot.Language);
            if (fileLanguage is null || fileLanguage != requested)
            {
                return null;
            }

            return snapshot;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 保存为新基线：先把现有 current 轮换为 previous，再原子写入新的 current。
    /// **仅应在分析成功后调用**（失败路径不得调用本方法）。第9.0A轮：按 <see cref="SourceSnapshot.Language"/> 独立轮换。
    /// </summary>
    public static void SaveBaseline(string projectRoot, SourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var language = ParseLanguageOrThrow(snapshot.Language);
        Directory.CreateDirectory(GetLanguageDirectory(projectRoot, language));

        var currentPath = GetCurrentPath(projectRoot, language);
        var previousPath = GetPreviousPath(projectRoot, language);

        if (File.Exists(currentPath))
        {
            File.Copy(currentPath, previousPath, overwrite: true);
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);

        // 回读校验：避免写入损坏快照
        _ = JsonSerializer.Deserialize<SourceSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("[错误] 源快照序列化结果无法回读，已放弃写入。");

        var temp = currentPath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(currentPath))
        {
            File.Replace(temp, currentPath, null);
        }
        else
        {
            File.Move(temp, currentPath);
        }
    }

    /// <summary>
    /// 第9.0C.1轮：**事务式批量保存 baseline**（三语一起提交）。
    ///
    /// 语义（解决「只写了 KR，EN/JP 没写」导致 Canonical 基线损坏的问题）：
    ///   1. 准备阶段：把每种语言的快照写入 <c>&lt;current&gt;.pending</c>（写入前回读校验）；
    ///      任一语言准备失败 ⇒ 删除全部 pending 文件，**不修改任何正式快照**；
    ///   2. 提交阶段（**不可取消**，只做文件替换）：逐语言 复制 current→previous，再用 pending 原子替换 current；
    ///   3. 回滚阶段：若提交中途失败，已提交的语言用 previous 还原，并清理剩余 pending。
    ///
    /// 调用方必须在准备阶段之前检查取消（取消 ⇒ 不进入本方法，baseline 保持原状）。
    /// </summary>
    /// <returns>成功提交的语言列表（顺序与传入一致）</returns>
    public static IReadOnlyList<SourceLanguage> SaveBaselineBatch(
        string projectRoot,
        IReadOnlyList<(SourceLanguage Language, IReadOnlyList<TranslationUnit> Units, string? SourceRoot)> items,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(items);

        var prepared = new List<(SourceLanguage Language, string Current, string Pending)>();
        var skipped = new List<SourceLanguage>();

        try
        {
            foreach (var (language, units, sourceRoot) in items)
            {
                if (units is null || units.Count == 0)
                {
                    skipped.Add(language);
                    continue;
                }

                var snapshot = FromTranslationUnits(units, sourceRoot, language);
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                _ = JsonSerializer.Deserialize<SourceSnapshot>(json, JsonOptions)
                    ?? throw new InvalidOperationException("[错误] 源快照序列化结果无法回读，已放弃写入。");

                Directory.CreateDirectory(GetLanguageDirectory(projectRoot, language));
                var currentPath = GetCurrentPath(projectRoot, language);
                var pendingPath = currentPath + ".pending";
                File.WriteAllText(pendingPath, json);
                prepared.Add((language, currentPath, pendingPath));
            }
        }
        catch (Exception ex)
        {
            foreach (var item in prepared)
            {
                TryDelete(item.Pending);
            }

            log?.Invoke($"[错误] 三语快照准备阶段失败，未修改任何正式快照：{ex.Message}");
            throw;
        }

        foreach (var language in skipped)
        {
            log?.Invoke($"[调试] 未发现{SourceLanguageHelper.GetDisplayName(language)}源目录，本次不更新{SourceLanguageHelper.GetDisplayName(language)} Snapshot。");
        }

        // 提交阶段：不再检查取消（文件替换是毫秒级，保证三语一起生效或一起回滚）
        var committed = new List<(SourceLanguage Language, string Current, string Previous)>();
        try
        {
            foreach (var item in prepared)
            {
                var previousPath = GetPreviousPath(projectRoot, item.Language);
                if (File.Exists(item.Current))
                {
                    File.Copy(item.Current, previousPath, overwrite: true);
                }

                if (File.Exists(item.Current))
                {
                    File.Replace(item.Pending, item.Current, null);
                }
                else
                {
                    File.Move(item.Pending, item.Current);
                }

                committed.Add((item.Language, item.Current, previousPath));
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[错误] 三语快照提交失败，正在回滚：{ex.Message}");
            foreach (var item in committed)
            {
                try
                {
                    if (File.Exists(item.Previous))
                    {
                        File.Copy(item.Previous, item.Current, overwrite: true);
                    }
                }
                catch (Exception rollbackEx)
                {
                    log?.Invoke($"[错误] 回滚{SourceLanguageHelper.GetDisplayName(item.Language)}快照失败：{rollbackEx.Message}");
                }
            }

            foreach (var item in prepared)
            {
                TryDelete(item.Pending);
            }

            throw;
        }

        return prepared.Select(item => item.Language).ToList();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程（下次写入会覆盖）
        }
    }

    /// <summary>
    /// 第9.0A轮：按语言保存 baseline（内部构造快照）。
    /// 语言目录不存在 / units 为空时**不伪造空快照**，返回 false（由调用方记录日志）。
    /// </summary>
    public static bool SaveBaseline(
        string projectRoot,
        SourceLanguage language,
        IReadOnlyList<TranslationUnit> units,
        string? sourceRoot = null)
    {
        if (units is null || units.Count == 0)
        {
            return false;
        }

        SaveBaseline(projectRoot, FromTranslationUnits(units, sourceRoot, language));
        return true;
    }

    /// <summary>由解析结果构造快照（保持 unit 原顺序）。</summary>
    public static SourceSnapshot FromTranslationUnits(
        IEnumerable<TranslationUnit> units,
        string? sourceRoot = null,
        SourceLanguage language = SourceLanguage.English)
    {
        // 第9.0A轮：**保留全部解析到的单元**（含空源文）。
        // 若在此处过滤空源文，快照会与解析结果不一致，下一次比较会把「被过滤掉的单元」误判为 Added。
        var list = units
            .Select(u => new SourceSnapshotUnit
            {
                RelativeFilePath = u.Key.RelativeFilePath,
                RecordId = u.Key.RecordId,
                FieldPath = u.Key.FieldPath,
                SourceText = u.SourceText,
                Speaker = u.Speaker,
                Order = u.Order,
            })
            .ToList();

        return new SourceSnapshot
        {
            Language = SourceLanguageHelper.ToCode(language),
            CreatedAtUtc = DateTime.UtcNow,
            SourceRoot = sourceRoot,
            Units = list,
        };
    }

    private static SourceLanguage ParseLanguageOrThrow(string? code)
        => SourceLanguageHelper.TryParseCode(code)
           ?? throw new ArgumentException($"[错误] 快照语言无效：{code ?? "(null)"}（仅支持 ko/en/ja）。", nameof(code));

    /// <summary>快照 → 供 DiffEngine 使用的旧源单元。</summary>
    public static List<TranslationUnit> ToTranslationUnits(SourceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Units
            .Select(u => new TranslationUnit
            {
                Key = new UnitKey
                {
                    RelativeFilePath = u.RelativeFilePath,
                    RecordId = u.RecordId,
                    FieldPath = u.FieldPath,
                },
                SourceText = u.SourceText,
                RecordId = u.RecordId,
                FieldPath = u.FieldPath,
                FilePath = u.RelativeFilePath,
                Speaker = u.Speaker,
                Order = u.Order ?? 0,
            })
            .ToList();
    }
}