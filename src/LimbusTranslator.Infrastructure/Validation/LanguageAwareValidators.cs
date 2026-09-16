using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 日文残留校验（第9.0B-P1轮，启发式）。
///
/// 全部四种模式的**目标语言都是简体中文**，因此译文里出现平假名 / 片假名即属残留
/// （例如「你好です」）——典型场景是模型把日文参考文本整段带出。
///
/// 边界：
///   - 只按**假名** Unicode 判定，**不用汉字判断**（汉字是中日共用字符，用汉字会大量误报）；
///   - 检查 **Translation**（不是源文）；
///   - 只对可见文本（去掉保护标记与富文本标签）判定，避免标签属性里的假名误报；
///   - Warning 级：结构未损坏，进入人工审核而非硬阻断。
/// </summary>
public sealed class JapaneseResidueValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(JapaneseResidueValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var visible = ValidationTextTools.ToVisibleText(context.Translation);
        if (visible.Length == 0 || !SourceLanguageDetector.ContainsKana(visible))
        {
            return Array.Empty<ValidationIssue>();
        }

        var kanaCount = SourceLanguageDetector.CountKana(visible);
        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.JapaneseResidue,
                ValidationSeverity.Warning,
                ValidationCategory.Language,
                Name,
                $"译文出现日文假名残留（{kanaCount} 个）：目标语言应为简体中文，请人工确认"),
        };
    }
}

/// <summary>
/// Canonical 韩文原文缺失校验（第9.0B-P1轮）。
///
/// 规则（与产品语义一致）：
///   - 只对 KR_EN / KR_JP / KR_ONLY 产生（<c>RunTranslationMode</c> 为 KR 模式且
///     <c>CanonicalKoreanPresent == false</c>）；
///   - **EN_ONLY 永远不产生本 Issue**（其 canonical 语义为 N/A ⇒ <c>CanonicalKoreanPresent == null</c>）；
///   - Warning 级 + 由 NeedsReview 策略升级为「待人工审核」（不进入硬阻断）。
///
/// 典型场景：KR_ONLY 缺韩文原文 ⇒ 条目按 Inherit/SkipDeleted 处理（不调用 Provider），
/// 本 Issue 由 ReleaseGate 的继承条目补齐校验路径产出，保证人工可见。
/// </summary>
public sealed class CanonicalKoreanSourceMissingValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(CanonicalKoreanSourceMissingValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null)
        {
            return Array.Empty<ValidationIssue>();
        }

        // null ⇒ 不适用（EN_ONLY / 旧英文路径）
        if (context.CanonicalKoreanPresent is not false)
        {
            return Array.Empty<ValidationIssue>();
        }

        var mode = context.RunTranslationMode;
        if (mode is null || !TranslationModePolicy.UsesCanonicalKoreanDiff(mode.Value))
        {
            return Array.Empty<ValidationIssue>();
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.CanonicalKoreanSourceMissing,
                ValidationSeverity.Warning,
                ValidationCategory.Language,
                Name,
                $"韩文原文（Canonical）缺失：模式 {TranslationModeCodes.ToCode(mode.Value)} 无法按原文校验，请人工确认"),
        };
    }
}