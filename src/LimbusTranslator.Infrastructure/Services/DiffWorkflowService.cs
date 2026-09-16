using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Diff;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// Diff 分析工作流服务。UI / CLI 的一站式入口。
///
/// 流程：扫描目录 → 解析文件 → Diff 计算 → 输出统计。
/// </summary>
public sealed class DiffWorkflowService
{
    private readonly IFileParser _parser;
    private readonly DiffEngine _diffEngine = new();
    private readonly FileDiffEngine _fileDiffEngine = new();

    public DiffWorkflowService(string? configDir = null)
    {
        _parser = new JsonGameFileParser(configDir);
    }

    /// <summary>
    /// 执行一次完整的文件级对比。
    /// 判断哪些文件是新增的、缺失汉化的、被修改的。
    /// </summary>
    public FileDiffResult AnalyzeFiles(
        string oldEnglishDir,
        string oldChineseDir,
        string newEnglishDir)
    {
        var oldEnFiles = GameFileLocator.ScanJsonFiles(oldEnglishDir);
        var newEnFiles = GameFileLocator.ScanJsonFiles(newEnglishDir);
        var oldZhFiles = GameFileLocator.ScanJsonFiles(oldChineseDir);

        return _fileDiffEngine.Compute(
            oldEnFiles, oldZhFiles, newEnFiles,
            oldEnglishRoot: Directory.Exists(oldEnglishDir) ? oldEnglishDir : null,
            newEnglishRoot: Directory.Exists(newEnglishDir) ? newEnglishDir : null);
    }

    /// <summary>
    /// 执行一次完整的 Diff 分析。
    /// </summary>
    /// <param name="oldEnglishDir">旧版英文目录（文件带 EN_ 前缀）</param>
    /// <param name="oldChineseDir">旧版中文目录（文件不带前缀）</param>
    /// <param name="newEnglishDir">新版英文目录（文件带 EN_ 前缀）</param>
    /// <param name="progress">进度回调（当前文件数/总数/文件名）</param>
    public DiffResult Analyze(
        string oldEnglishDir,
        string oldChineseDir,
        string newEnglishDir,
        Action<int, int, string>? progress = null)
    {
        var oldEnFiles = GameFileLocator.ScanJsonFiles(oldEnglishDir);
        var newEnFiles = GameFileLocator.ScanJsonFiles(newEnglishDir);
        var oldZhFiles = GameFileLocator.ScanJsonFiles(oldChineseDir);

        // 中文目录按相对路径索引
        var oldZhMap = oldZhFiles.ToDictionary(
            p => p.Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);

        var totalFiles = newEnFiles.Count;
        var processed = 0;

        var oldEnglishUnits = new List<TranslationUnit>();
        var oldChineseUnits = new List<TranslationUnit>();
        var newEnglishUnits = new List<TranslationUnit>();

        // 1) 解析旧英文
        foreach (var file in oldEnFiles)
        {
            processed++;
            progress?.Invoke(processed, totalFiles, file);
            var absPath = Path.Combine(oldEnglishDir, file);
            var physical = file.Replace('\\', '/');
            // UnitKey 使用逻辑路径（中文名），保证新旧英文与中文主键一致
            var logical = GameFileLocator.ToChineseRelativePath(physical);
            oldEnglishUnits.AddRange(_parser.Parse(absPath, logical));
        }

        // 2) 解析旧中文（仅解析新版英文中仍存在的文件，减少无谓 IO）
        foreach (var file in newEnFiles)
        {
            var zhRelative = GameFileLocator.ToChineseRelativePath(file.Replace('\\', '/'));
            if (!oldZhMap.ContainsKey(zhRelative))
            {
                continue;
            }
            var absPath = Path.Combine(oldChineseDir, zhRelative);
            if (File.Exists(absPath))
            {
                oldChineseUnits.AddRange(_parser.Parse(absPath, zhRelative));
            }
        }

        // 3) 解析新版英文
        foreach (var file in newEnFiles)
        {
            var absPath = Path.Combine(newEnglishDir, file);
            var physical = file.Replace('\\', '/');
            // UnitKey 使用逻辑路径（中文名），保证新旧英文与中文主键一致
            var logical = GameFileLocator.ToChineseRelativePath(physical);
            newEnglishUnits.AddRange(_parser.Parse(absPath, logical));
        }

        // 4) Diff 计算
        return _diffEngine.Compute(oldEnglishUnits, oldChineseUnits, newEnglishUnits);
    }

    /// <summary>
    /// 第8.89轮：使用**上一份源快照**作为旧英文执行分析（磁盘上只有一个英文版本时，提供真实 OldSource）。
    /// 与 <see cref="Analyze"/> 共用同一套解析/Diff 逻辑，不新增第二套 Diff。
    /// </summary>
    /// <param name="previousEnglishUnits">上一份快照还原出的旧英文单元</param>
    /// <param name="oldChineseDir">旧版中文目录</param>
    /// <param name="newEnglishDir">当前英文目录</param>
    /// <param name="progress">进度回调</param>
    public DiffResult AnalyzeWithPreviousUnits(
        IReadOnlyList<TranslationUnit> previousEnglishUnits,
        string oldChineseDir,
        string newEnglishDir,
        Action<int, int, string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(previousEnglishUnits);
        var newEnFiles = GameFileLocator.ScanJsonFiles(newEnglishDir);
        var oldZhFiles = GameFileLocator.ScanJsonFiles(oldChineseDir);

        var oldZhMap = oldZhFiles.ToDictionary(
            p => p.Replace('\\', '/'),
            StringComparer.OrdinalIgnoreCase);

        var totalFiles = newEnFiles.Count;
        var processed = 0;

        var oldChineseUnits = new List<TranslationUnit>();
        var newEnglishUnits = new List<TranslationUnit>();

        foreach (var file in newEnFiles)
        {
            processed++;
            progress?.Invoke(processed, totalFiles, file);

            var zhRelative = GameFileLocator.ToChineseRelativePath(file.Replace('\\', '/'));
            if (oldZhMap.ContainsKey(zhRelative))
            {
                var zhPath = Path.Combine(oldChineseDir, zhRelative);
                if (File.Exists(zhPath))
                {
                    oldChineseUnits.AddRange(_parser.Parse(zhPath, zhRelative));
                }
            }

            var enPath = Path.Combine(newEnglishDir, file);
            var logical = GameFileLocator.ToChineseRelativePath(file.Replace('\\', '/'));
            newEnglishUnits.AddRange(_parser.Parse(enPath, logical));
        }

        return _diffEngine.Compute(previousEnglishUnits.ToList(), oldChineseUnits, newEnglishUnits);
    }
}
