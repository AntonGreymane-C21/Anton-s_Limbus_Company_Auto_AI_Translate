using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Parsing;

/// <summary>
/// 语言文件映射（第9.0轮）：集中处理 <c>EN_ / KR_ / JP_</c> 前缀与 Localize 目录差异。
///
/// 禁止在 MainViewModel 等处手工拼 <c>"EN_" + file</c>：
///   - <see cref="ToCanonicalRelativePath"/>：物理路径 → 逻辑路径（去掉任意语言前缀）；
///   - <see cref="ToPhysicalRelativePath"/>：逻辑路径 → 指定语言的物理路径（加对应前缀）；
///   - <see cref="ResolveLanguageDirectory"/>：游戏根 → <c>Localize/{en|kr|jp}</c>。
/// </summary>
public static class LanguageFileMapper
{
    /// <summary>Localize 目录相对路径（相对 <c>LimbusCompany_Data</c>）。</summary>
    public const string LocalizeRelativePath = "Assets/Resources_moved/Localize";

    /// <summary>所有已知语言前缀（用于归一化）。</summary>
    public static IReadOnlyList<string> KnownPrefixes { get; } = new[]
    {
        SourceLanguageHelper.KoreanPrefix,
        SourceLanguageHelper.EnglishPrefix,
        SourceLanguageHelper.JapanesePrefix,
    };

    /// <summary>去掉任意语言前缀，得到与 UnitKey 一致的逻辑相对路径。</summary>
    public static string ToCanonicalRelativePath(string physicalRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalRelativePath);

        var normalized = physicalRelativePath.Replace('\\', '/');
        var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(normalized);

        foreach (var prefix in KnownPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.Ordinal))
            {
                fileName = fileName[prefix.Length..];
                break;
            }
        }

        return string.IsNullOrEmpty(dir) ? fileName : dir + "/" + fileName;
    }

    /// <summary>逻辑相对路径 → 指定语言的物理相对路径（加前缀）。</summary>
    public static string ToPhysicalRelativePath(SourceLanguage language, string canonicalRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRelativePath);

        var normalized = canonicalRelativePath.Replace('\\', '/');
        var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(normalized);

        // 若已带语言前缀，先归一化再加目标前缀
        foreach (var prefix in KnownPrefixes)
        {
            if (fileName.StartsWith(prefix, StringComparison.Ordinal))
            {
                fileName = fileName[prefix.Length..];
                break;
            }
        }

        var physical = SourceLanguageHelper.GetFilePrefix(language) + fileName;
        return string.IsNullOrEmpty(dir) ? physical : dir + "/" + physical;
    }

    /// <summary>
    /// 由游戏数据根目录解析语言目录（<c>{gameDataRoot}/Assets/Resources_moved/Localize/{en|kr|jp}</c>）。
    /// 目录不存在 → null（调用方据此提示“未找到”）。
    /// </summary>
    public static string? ResolveLanguageDirectory(string gameDataRoot, SourceLanguage language)
    {
        if (string.IsNullOrWhiteSpace(gameDataRoot) || !Directory.Exists(gameDataRoot))
        {
            return null;
        }

        var candidate = Path.Combine(
            gameDataRoot,
            LocalizeRelativePath.Replace('/', Path.DirectorySeparatorChar),
            SourceLanguageHelper.GetLocalizeDirectoryName(language));

        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>扫描某语言目录下的全部 JSON 文件（物理相对路径）。</summary>
    public static IReadOnlyList<string> ScanPhysicalFiles(string languageDirectory)
        => string.IsNullOrWhiteSpace(languageDirectory) || !Directory.Exists(languageDirectory)
            ? Array.Empty<string>()
            : GameFileLocator.ScanJsonFiles(languageDirectory);

    /// <summary>该语言目录是否存在且含 JSON 文件。</summary>
    public static bool HasFiles(string? languageDirectory)
        => ScanPhysicalFiles(languageDirectory ?? string.Empty).Count > 0;

    /// <summary>某语言的物理文件路径（不存在 → null）。</summary>
    public static string? ResolvePhysicalFile(string languageDirectory, SourceLanguage language, string canonicalRelativePath)
    {
        if (string.IsNullOrWhiteSpace(languageDirectory))
        {
            return null;
        }

        var physical = ToPhysicalRelativePath(language, canonicalRelativePath);
        var full = Path.Combine(languageDirectory, physical.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? full : null;
    }
}