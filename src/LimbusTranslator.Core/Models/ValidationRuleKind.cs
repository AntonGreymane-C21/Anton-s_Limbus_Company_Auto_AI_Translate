namespace LimbusTranslator.Core.Models;

/// <summary>
/// 校验规则性质。
///
/// HardSafety：结构性硬安全规则（空译文、占位符、标签）。违反 → Error → 必须人工确认。
/// Heuristic ：启发式质量规则（数字、换行、英文/韩文残留、长度、术语、同源文）。
///             违反 → Warning；不得仅凭一条 Warning 抹掉“已人工确认”的语义。
/// </summary>
public enum ValidationRuleKind
{
    /// <summary>硬安全规则</summary>
    HardSafety,

    /// <summary>启发式质量规则</summary>
    Heuristic,
}
