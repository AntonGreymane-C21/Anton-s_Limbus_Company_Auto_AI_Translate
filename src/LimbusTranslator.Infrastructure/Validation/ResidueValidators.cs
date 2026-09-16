using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 英文残留校验（启发式，非常保守）。
///
/// 本项目是 英文 → 简中，但游戏存在大量合法英文内容，因此：
///   允许 HP / SP / E.G.O / ID / UI 等缩写、Glossary 明确保留英文的项、
///   Placeholder / Tag / 纯数字单位、用户配置允许词；
///   只对“连续多个英文词构成的词组 / 完整句段”告警（宁可漏报，不要误报）。
/// 禁止使用 “存在 A-Z 即告警” 这类粗暴规则。
/// </summary>
public sealed class EnglishResidueValidator : ITranslationValidator
{
    private readonly ValidationOptions _options;

    public EnglishResidueValidator(ValidationOptions? options = null)
    {
        _options = options ?? ValidationOptions.Default;
    }

    /// <inheritdoc />
    public string Name => nameof(EnglishResidueValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var allowed = BuildAllowedSet(context);
        var runs = ValidationTextTools.FindLatinRuns(
            context.Translation,
            _options.EnglishResidueMinWords,
            _options.EnglishResidueMinChars,
            allowed);

        if (runs.Count == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        var sample = runs[0].Length <= 40 ? runs[0] : runs[0][..40] + "...";
        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.EnglishResidue,
                ValidationSeverity.Warning,
                ValidationCategory.Language,
                Name,
                $"译文存在连续英文残留（{runs.Count} 处，例如 \"{sample}\"）"),
        };
    }

    private HashSet<string> BuildAllowedSet(ValidationContext context)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in _options.AllowedEnglishTerms)
        {
            allowed.Add(term);
        }
        foreach (var term in _options.ExtraAllowedEnglishTerms)
        {
            if (!string.IsNullOrWhiteSpace(term))
            {
                allowed.Add(term.Trim());
            }
        }
        foreach (var term in context.AllowedEnglishTerms)
        {
            if (!string.IsNullOrWhiteSpace(term))
            {
                allowed.Add(term.Trim());
            }
        }

        // Glossary 明确要求保留英文的术语（如 E.G.O）
        foreach (var term in context.Terminology)
        {
            if (term.PreservedAsEnglish || !term.Locked)
            {
                allowed.Add(term.Source);
            }
        }

        return allowed;
    }
}

/// <summary>
/// 韩文残留校验（启发式）。
/// 只检测 Hangul Unicode；Parser 已通过字段黑名单排除 model 等内部韩文 ID，
/// 本 Validator 不承担字段过滤职责（两者不混在一起）。
/// </summary>
public sealed class KoreanResidueValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(KoreanResidueValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        if (!ValidationTextTools.ContainsHangul(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.KoreanResidue,
                ValidationSeverity.Warning,
                ValidationCategory.Language,
                Name,
                "译文中检测到韩文字符残留"),
        };
    }
}

/// <summary>
/// 源语言异常校验（第8.5轮，启发式）。
///
/// 【为什么需要】第8轮真实样本证明 `EN_*.json` 不等于纯英文：
/// 例如 `필립 싱클레어가 탈출장치로 후퇴` 出现在英文源目录中，模型只能靠推理猜测中文译名。
/// 这类源文必须显式暴露给人工审核，而不是静静翻译。
///
/// 【边界】
///   - 检查 **SourceText**（源文），不是 Translation；
///   - 只作用于可翻译的 TranslationUnit，**不扫描整个 JSON record**，
///     因此 model 等内部韩文 ID（Parser 已列为不可翻译字段）不会进入本校验；
///   - Warning 级：源语言异常 ≠ 译文结构损坏，不进入 HardSafety Block；
///   - 译文里的韩文残留继续由 <see cref="KoreanResidueValidator"/> 负责。
/// </summary>
public sealed class SourceLanguageAnomalyValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(SourceLanguageAnomalyValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.SourceText))
        {
            return Array.Empty<ValidationIssue>();
        }

        // 第9.0B-P1轮：**语言感知** —— 生效源语言不是英文时（KR_ONLY 的韩文原文、
        // KR_EN/KR_JP 回退韩文的场景），韩文是完全正常的源文，绝不复报。
        var effective = context.EffectiveSourceLanguage ?? SourceLanguage.English;
        if (effective != SourceLanguage.English)
        {
            return Array.Empty<ValidationIssue>();
        }

        var source = context.SourceText!;
        if (!ValidationTextTools.ContainsHangul(source))
        {
            return Array.Empty<ValidationIssue>();
        }

        var hangulCount = SourceLanguageDetector.CountHangul(source);
        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.SourceLanguageAnomaly,
                ValidationSeverity.Warning,
                ValidationCategory.Language,
                Name,
                $"源文出现韩文字符（{hangulCount} 个）：英文源文件中混入韩文，请人工确认译名与语义"),
        };
    }
}
