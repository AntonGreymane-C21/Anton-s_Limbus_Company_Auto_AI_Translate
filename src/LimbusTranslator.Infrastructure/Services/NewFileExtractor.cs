using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 新文件提取结果。
/// </summary>
public sealed class ExtractResult
{
    /// <summary>成功复制的文件数</summary>
    public int CopiedCount { get; init; }

    /// <summary>目标目录</summary>
    public required string TargetDir { get; init; }

    /// <summary>提取出的文件相对路径列表</summary>
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>
/// 新文件提取器。
///
/// 将"需要汉化的文件"（新增 / 缺失汉化 / 修改）从新版英文目录
/// 单独复制到待翻译工作目录 data/work/pending/，便于后续集中处理。
/// 复制时保留相对目录结构，且不修改任何原文件。
/// </summary>
public static class NewFileExtractor
{
    /// <summary>
    /// 提取需要汉化的文件。
    /// </summary>
    /// <param name="newEnglishRoot">新版英文根目录（源）</param>
    /// <param name="fileEntries">文件级对比结果</param>
    /// <param name="targetRoot">目标目录（如 data/work/pending）</param>
    /// <param name="kinds">要提取的文件类型（默认：新增+缺失汉化+修改）</param>
    public static ExtractResult Extract(
        string newEnglishRoot,
        IEnumerable<FileDiffEntry> fileEntries,
        string targetRoot,
        IEnumerable<FileDiffKind>? kinds = null)
    {
        var includeKinds = (kinds ?? new[]
        {
            FileDiffKind.New,
            FileDiffKind.MissingChinese,
            FileDiffKind.Modified,
        }).ToHashSet();

        Directory.CreateDirectory(targetRoot);

        var copied = new List<string>();
        foreach (var entry in fileEntries.Where(e => includeKinds.Contains(e.Kind)))
        {
            var source = Path.Combine(newEnglishRoot, entry.EnglishPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
            {
                continue;
            }

            // 在目标目录保留相对结构
            var dest = Path.Combine(targetRoot, entry.EnglishPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest, overwrite: true);
            copied.Add(entry.EnglishPath);
        }

        return new ExtractResult
        {
            CopiedCount = copied.Count,
            TargetDir = targetRoot,
            Files = copied,
        };
    }
}
