using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Paratranz;

/// <summary>
/// Paratranz 术语缓存读写（第8.88轮）。
///
/// 设计要点：
///   - 缓存独立于 config/glossary.json（绝不把远程数据直接写进用户术语库）；
///   - 原子写：临时文件 → 校验 → 替换；失败时不破坏旧缓存；
///   - 远程文本视为不可信输入：长度限制、空值过滤、只做 JSON 解析（不执行任何 HTML/脚本）。
/// </summary>
public static class ParatranzGlossaryCacheStore
{
    /// <summary>单条术语最大长度（防止恶意/异常超长文本进入 Prompt）</summary>
    public const int MaxTermLength = 200;

    /// <summary>单条译名最大长度</summary>
    public const int MaxTranslationLength = 200;

    /// <summary>缓存序列化选项（camelCase，与项目其他 JSON 一致）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>读取缓存；文件不存在/损坏时返回 null（调用方应保留上一次状态）。</summary>
    public static ParatranzGlossaryCache? TryLoad(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            var cache = JsonSerializer.Deserialize<ParatranzGlossaryCache>(File.ReadAllText(cachePath), JsonOptions);
            if (cache is null || cache.SchemaVersion != ParatranzGlossaryCache.CurrentSchemaVersion)
            {
                return null;
            }

            return cache;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写入缓存（temp → validate → replace）。</summary>
    public static void SaveAtomic(string cachePath, ParatranzGlossaryCache cache)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(cache, JsonOptions);

        // validate：必须能被重新解析
        _ = JsonSerializer.Deserialize<ParatranzGlossaryCache>(json, JsonOptions)
            ?? throw new InvalidOperationException("[错误] Paratranz 缓存序列化结果无法回读，已放弃写入。");

        var temp = cachePath + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(cachePath))
        {
            File.Replace(temp, cachePath, null);
        }
        else
        {
            File.Move(temp, cachePath);
        }
    }

    /// <summary>
    /// 规范化 + 审计：过滤空值/重复/韩文残留/未翻译项，并统计冲突。
    /// 不因“来自远程”而默认数据正确。
    /// </summary>
    public static (IReadOnlyList<ParatranzTermEntry> Entries, ParatranzAuditSummary Audit) Normalize(
        IEnumerable<ParatranzTermEntry> raw)
    {
        var rawList = raw?.ToList() ?? new List<ParatranzTermEntry>();
        var result = new List<ParatranzTermEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var bySource = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var remoteIds = new HashSet<int>();
        var duplicateRemoteIds = 0;

        int emptySource = 0, emptyTarget = 0, duplicate = 0, hangulTarget = 0, sameAsSource = 0;

        foreach (var entry in rawList)
        {
            var term = entry.Term?.Trim() ?? string.Empty;
            var target = entry.Translation?.Trim() ?? string.Empty;

            if (term.Length == 0)
            {
                emptySource++;
                continue;
            }

            if (target.Length == 0)
            {
                emptyTarget++;
                continue;
            }

            if (term.Length > MaxTermLength || target.Length > MaxTranslationLength)
            {
                emptySource++; // 视为不可信超长文本
                continue;
            }

            if (string.Equals(term, target, StringComparison.OrdinalIgnoreCase))
            {
                sameAsSource++;
                continue;
            }

            if (ContainsHangul(target))
            {
                hangulTarget++;
                continue;
            }

            var key = term + "\u0000" + target;
            if (!seen.Add(key))
            {
                duplicate++;
                continue;
            }

            if (!bySource.TryGetValue(term, out var targets))
            {
                targets = new HashSet<string>(StringComparer.Ordinal);
                bySource[term] = targets;
            }

            targets.Add(target);

            if (entry.RemoteId is int id && !remoteIds.Add(id))
            {
                duplicateRemoteIds++;
            }

            result.Add(new ParatranzTermEntry
            {
                Term = term,
                Translation = target,
                RemoteId = entry.RemoteId,
                UpdatedAt = entry.UpdatedAt,
            });
        }

        var multiTarget = bySource.Count(kv => kv.Value.Count > 1);

        return (result, new ParatranzAuditSummary
        {
            RawCount = rawList.Count,
            DroppedEmptySource = emptySource,
            DroppedEmptyTarget = emptyTarget,
            DroppedDuplicate = duplicate,
            MultiTargetConflicts = multiTarget,
            DroppedHangulTarget = hangulTarget,
            DroppedSameAsSource = sameAsSource,
            DuplicateRemoteIds = duplicateRemoteIds,
            EffectiveCount = result.Count,
        });
    }

    private static bool ContainsHangul(string text)
    {
        foreach (var ch in text)
        {
            if ((ch >= '\uAC00' && ch <= '\uD7A3') || (ch >= '\u1100' && ch <= '\u11FF') || (ch >= '\u3130' && ch <= '\u318F'))
            {
                return true;
            }
        }

        return false;
    }
}