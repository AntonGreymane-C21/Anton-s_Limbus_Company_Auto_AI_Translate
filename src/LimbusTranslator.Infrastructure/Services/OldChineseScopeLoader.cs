using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Parsing;
using LimbusTranslator.Infrastructure.Snapshots;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>旧中文解析范围结果（第9.0B-P7轮）。</summary>
public sealed class OldChineseScopeResult
{
    /// <summary>本次可用的旧中文单元（= 工作流已解析结果 ∪ 按 KR 文件范围额外解析的结果）。</summary>
    public required IReadOnlyList<TranslationUnit> Units { get; init; }

    /// <summary>本次因为「当前 KR 文件集」而**额外解析**的旧中文逻辑文件（按需扩展，通常很少）。</summary>
    public required IReadOnlyList<string> AdditionalLogicalFiles { get; init; }

    /// <summary>本次旧中文范围的权威来源（EN_ONLY → 英文；KR 三模式 → 韩文）。</summary>
    public required SourceLanguage ScopeLanguage { get; init; }

    /// <summary>本次权威范围内：扫描到的 KR 文件数（0 表示未扩范围）。</summary>
    public int ScannedAuthoritativeFileCount { get; init; }

    /// <summary>一行式摘要。</summary>
    public string Describe() =>
        $"旧中文解析范围：{SourceLanguageHelper.ToCode(ScopeLanguage)} 权威"
        + $"｜单元 {Units.Count} 条"
        + $"｜因 KR 文件集额外解析 {AdditionalLogicalFiles.Count} 个文件"
        + (ScannedAuthoritativeFileCount > 0 ? $"（当前 KR 文件 {ScannedAuthoritativeFileCount} 个）" : string.Empty);
}

/// <summary>
/// 旧中文解析范围（第9.0B-P7轮，修复 P2-η）。
///
/// 问题：<see cref="DiffWorkflowService.Analyze"/> 只对「当前英文文件集合」解析旧中文（IO 优化），
/// 因此「韩文有、英文没有」的整文件（KR-only File）即使旧中文目录里存在同名文件，也不会被解析，
/// 导致本可 Inherit 的条目退化成 TranslateMissing。
///
/// 规则（产品语义）：
///   EN_ONLY            → 保持 EN 权威范围（不因为韩文多出的文件扩大旧中文范围）；
///   KR_EN/KR_JP/KR_ONLY → Current EN 文件集 ∪ **Current KR 文件集**：
///                        凡当前 KR 权威输出结构可能需要的文件，只要旧中文目录存在同名文件，就必须解析。
///
/// 唯一数据流：本类是唯一实现；WPF / CLI / 测试都调用它，并把它产出的单元交给
/// <see cref="ProductionTranslationPlanBuilder.Build"/>（旧中文仍按 UnitKey 查找）。
///
/// 复用：KR 文件集合来自 <see cref="MultilingualCaptureResult.ScannedLogicalFiles"/>（捕获时已枚举），
/// 不第二次遍历整个韩文目录；只解析「尚未被工作流解析过」且「旧中文确实存在」的文件。
/// </summary>
public static class OldChineseScopeLoader
{
    /// <summary>按翻译模式与当前 KR 文件集扩展旧中文解析范围。</summary>
    /// <param name="mode">本次 Run 锁定的翻译模式</param>
    /// <param name="alreadyLoadedUnits">工作流已解析的旧中文单元（<c>DiffResult.OldChineseUnits</c>）</param>
    /// <param name="capture">三语捕获结果（提供 Current KR 文件集合）</param>
    /// <param name="oldChineseDirectory">旧中文目录（不存在 / 为空 → 不扩范围）</param>
    /// <param name="configDir">字段规则目录（与主解析保持一致；null → 内嵌默认规则）</param>
    public static OldChineseScopeResult Load(
        TranslationMode mode,
        IReadOnlyList<TranslationUnit>? alreadyLoadedUnits,
        MultilingualCaptureResult? capture,
        string? oldChineseDirectory,
        string? configDir = null)
    {
        var loaded = alreadyLoadedUnits ?? Array.Empty<TranslationUnit>();

        // EN_ONLY：保持 EN 权威语义，绝不因为韩文多出文件而扩大解析范围
        var scopeLanguage = TranslationModePolicy.GetOutputAuthoritativeLanguage(mode);
        if (scopeLanguage != SourceLanguage.Korean)
        {
            return new OldChineseScopeResult
            {
                Units = loaded,
                AdditionalLogicalFiles = Array.Empty<string>(),
                ScopeLanguage = SourceLanguage.English,
            };
        }

        IReadOnlyList<string>? koreanFiles = null;
        if (capture is null
            || string.IsNullOrWhiteSpace(oldChineseDirectory)
            || !Directory.Exists(oldChineseDirectory)
            || !capture.ScannedLogicalFiles.TryGetValue(SourceLanguage.Korean, out koreanFiles)
            || koreanFiles.Count == 0)
        {
            return new OldChineseScopeResult
            {
                Units = loaded,
                AdditionalLogicalFiles = Array.Empty<string>(),
                ScopeLanguage = SourceLanguage.Korean,
                ScannedAuthoritativeFileCount = koreanFiles?.Count ?? 0,
            };
        }

        // 已覆盖的文件（按逻辑相对路径去重；同一文件可能贡献多条单元）
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in loaded)
        {
            covered.Add(unit.Key.RelativeFilePath);
        }

        var parser = new JsonGameFileParser(configDir);
        var extraUnits = new List<TranslationUnit>();
        var parsedFiles = new List<string>();

        foreach (var logical in koreanFiles)
        {
            if (!covered.Add(logical))
            {
                continue; // 工作流已经解析过该文件（EN 权威范围内）
            }

            var fullPath = Path.Combine(oldChineseDirectory, logical.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                continue; // 旧中文没有同名文件：不伪造，后续按 TranslateMissing 处理
            }

            try
            {
                extraUnits.AddRange(parser.Parse(fullPath, logical));
                parsedFiles.Add(logical);
            }
            catch (Exception)
            {
                // 解析失败：忽略该文件（不猜测、不影响主链；该文件内的条目将按 TranslateMissing 处理）
            }
        }

        return new OldChineseScopeResult
        {
            Units = extraUnits.Count == 0 ? loaded : loaded.Concat(extraUnits).ToList(),
            AdditionalLogicalFiles = parsedFiles,
            ScopeLanguage = SourceLanguage.Korean,
            ScannedAuthoritativeFileCount = koreanFiles.Count,
        };
    }
}
