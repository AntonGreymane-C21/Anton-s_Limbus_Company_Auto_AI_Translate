using System.Security.Cryptography;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Infrastructure.Diff;

/// <summary>
/// 文件级对比引擎。
///
/// 对比三个目录的 JSON 文件集合，判断每个文件的整体状态：
///   New            新版英文有、旧版英文没有 → 新文件（需要汉化）
///   MissingChinese 英文存在但中文缺失       → 缺汉化（需要翻译）
///   Modified       新旧英文内容不同         → 文件被修改（需要重新翻译）
///   Unchanged      新旧英文一致且有中文     → 无需处理
///   Deleted        旧版有、新版没有         → 已删除
/// </summary>
public sealed class FileDiffEngine
{
    /// <summary>
    /// 执行文件级对比。
    /// </summary>
    /// <param name="oldEnglishFiles">旧版英文相对路径列表</param>
    /// <param name="oldChineseFiles">旧版中文相对路径列表</param>
    /// <param name="newEnglishFiles">新版英文相对路径列表</param>
    /// <param name="oldEnglishRoot">旧版英文根目录（用于计算 hash，可为空则跳过修改检测）</param>
    /// <param name="newEnglishRoot">新版英文根目录（用于计算 hash，可为空则跳过修改检测）</param>
    public FileDiffResult Compute(
        IReadOnlyList<string> oldEnglishFiles,
        IReadOnlyList<string> oldChineseFiles,
        IReadOnlyList<string> newEnglishFiles,
        string? oldEnglishRoot = null,
        string? newEnglishRoot = null)
    {
        var oldEnSet = oldEnglishFiles
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var oldZhSet = oldChineseFiles
            .Select(p => p.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var entries = new List<FileDiffEntry>();
        var newEnSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in newEnglishFiles)
        {
            var physical = file.Replace('\\', '/');
            var logical = GameFileLocator.ToChineseRelativePath(physical);
            newEnSet.Add(physical);

            FileDiffKind kind;

            // 1) 旧英文不存在 → 新文件
            if (!oldEnSet.Contains(physical))
            {
                kind = FileDiffKind.New;
            }
            // 2) 旧英文存在但中文缺失 → 缺失汉化
            else if (!oldZhSet.Contains(logical))
            {
                kind = FileDiffKind.MissingChinese;
            }
            // 3) 新旧英文内容不同 → 修改
            else if (oldEnglishRoot is not null && newEnglishRoot is not null
                     && !HashEqual(Path.Combine(oldEnglishRoot, physical),
                                    Path.Combine(newEnglishRoot, physical)))
            {
                kind = FileDiffKind.Modified;
            }
            // 4) 其余 → 未变化
            else
            {
                kind = FileDiffKind.Unchanged;
            }

            entries.Add(new FileDiffEntry
            {
                LogicalPath = logical,
                EnglishPath = physical,
                Kind = kind,
            });
        }

        // 5) 旧版有、新版没有 → 已删除
        foreach (var file in oldEnglishFiles)
        {
            var physical = file.Replace('\\', '/');
            if (!newEnSet.Contains(physical))
            {
                entries.Add(new FileDiffEntry
                {
                    LogicalPath = GameFileLocator.ToChineseRelativePath(physical),
                    EnglishPath = physical,
                    Kind = FileDiffKind.Deleted,
                });
            }
        }

        return new FileDiffResult
        {
            Entries = entries,
            NewCount = entries.Count(e => e.Kind == FileDiffKind.New),
            MissingChineseCount = entries.Count(e => e.Kind == FileDiffKind.MissingChinese),
            ModifiedCount = entries.Count(e => e.Kind == FileDiffKind.Modified),
            UnchangedCount = entries.Count(e => e.Kind == FileDiffKind.Unchanged),
            DeletedCount = entries.Count(e => e.Kind == FileDiffKind.Deleted),
        };
    }

    /// <summary>
    /// 比较两个文件内容是否一致（SHA256）。
    /// </summary>
    private static bool HashEqual(string pathA, string pathB)
    {
        try
        {
            if (!File.Exists(pathA) || !File.Exists(pathB))
            {
                return false;
            }
            using var sha = SHA256.Create();
            var hashA = sha.ComputeHash(File.ReadAllBytes(pathA));
            var hashB = sha.ComputeHash(File.ReadAllBytes(pathB));
            return Convert.ToBase64String(hashA) == Convert.ToBase64String(hashB);
        }
        catch
        {
            return false;
        }
    }
}
