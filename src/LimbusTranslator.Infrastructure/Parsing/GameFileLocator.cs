namespace LimbusTranslator.Infrastructure.Parsing;

/// <summary>
/// 游戏文件定位器。
///
/// 已勘察确认的命名规则：
///   - 英文文件名带 EN_ 前缀（如 EN_Enemies-a1c5p2.json）
///   - 中文文件名不带前缀（如 Enemies-a1c5p2.json）
///   - 对应关系：英文文件名 = "EN_" + 中文文件名
///
/// 功能：在旧英文 / 旧中文 / 新英文 三个目录之间建立文件映射。
/// </summary>
public static class GameFileLocator
{
    /// <summary>英文文件名前缀</summary>
    public const string EnglishPrefix = "EN_";

    /// <summary>
    /// 扫描目录下所有 JSON 文件，返回相对路径（从目录根开始，含子目录）。
    /// </summary>
    public static IReadOnlyList<string> ScanJsonFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        return Directory
            .EnumerateFiles(rootPath, "*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(rootPath, f))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 将英文相对路径转换为中文相对路径（去掉 EN_ 前缀）。
    /// 例：StoryData/EN_1D101A.json -> StoryData/1D101A.json
    /// </summary>
    public static string ToChineseRelativePath(string englishRelativePath)
    {
        var fileName = Path.GetFileName(englishRelativePath);
        var dir = Path.GetDirectoryName(englishRelativePath) ?? string.Empty;

        var chineseName = fileName.StartsWith(EnglishPrefix, StringComparison.Ordinal)
            ? fileName[EnglishPrefix.Length..]
            : fileName;

        return string.IsNullOrEmpty(dir)
            ? chineseName
            : Path.Combine(dir, chineseName).Replace('\\', '/');
    }

    /// <summary>
    /// 将中文相对路径转换为英文相对路径（加上 EN_ 前缀）。
    /// 例：StoryData/1D101A.json -> StoryData/EN_1D101A.json
    /// </summary>
    public static string ToEnglishRelativePath(string chineseRelativePath)
    {
        var fileName = Path.GetFileName(chineseRelativePath);
        var dir = Path.GetDirectoryName(chineseRelativePath) ?? string.Empty;

        var englishName = fileName.StartsWith(EnglishPrefix, StringComparison.Ordinal)
            ? fileName
            : EnglishPrefix + fileName;

        return string.IsNullOrEmpty(dir)
            ? englishName
            : Path.Combine(dir, englishName).Replace('\\', '/');
    }
}
