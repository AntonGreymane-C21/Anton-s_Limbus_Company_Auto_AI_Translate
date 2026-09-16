namespace LimbusTranslator.Core.Models;

/// <summary>
/// 校验策略：按译文来源决定跑哪些规则、以及哪些规则有权改变 NeedsReview。
///
/// 目的：第2轮不允许把所有历史旧译文（尤其 Inherited / HumanReviewed）突然变成大规模 NeedsReview。
/// </summary>
public enum ValidationPolicy
{
    /// <summary>完整 QA：AI / Mock / AI 来源的 TM 命中 / 未知来源。</summary>
    Full,

    /// <summary>
    /// 硬安全 + 启发式（仅记录）：人工审核过 / 官方译文。
    /// 只有 Error 能重新标记 NeedsReview；启发式 Warning 不得抹掉“已人工确认”的语义。
    /// </summary>
    HardSafetyOnly,

    /// <summary>
    /// 仅硬结构安全检查：继承的旧中文。
    /// 启发式规则本轮默认不跑（避免 14 万条历史条目产生海量误报）。
    /// </summary>
    InheritedStructureOnly,

    /// <summary>
    /// 仅硬结构安全检查，但 Error 必须暴露为 NeedsReview（第8.5轮，Passthrough 等确定性来源）。
    /// 与 <see cref="InheritedStructureOnly"/> 的区别：不吞掉 HardSafety Error 的审核标记。
    /// </summary>
    PassthroughStructureOnly,
}

/// <summary>
/// 校验策略解析：从译文来源得到策略。
/// </summary>
public static class ValidationPolicyResolver
{
    /// <summary>
    /// 解析策略。
    /// </summary>
    public static ValidationPolicy Resolve(TranslationSource? provenance) => provenance switch
    {
        TranslationSource.HumanReviewed => ValidationPolicy.HardSafetyOnly,
        TranslationSource.Official => ValidationPolicy.HardSafetyOnly,
        TranslationSource.Inherited => ValidationPolicy.InheritedStructureOnly,
        // 导入的历史译文与继承旧中文同类：本轮不做大规模启发式重标
        TranslationSource.Imported => ValidationPolicy.HardSafetyOnly,
        // 系统原样保留（纯符号）：只跑硬结构安全检查，但结构 Error 必须暴露（不能成为后门）
        TranslationSource.Passthrough => ValidationPolicy.PassthroughStructureOnly,
        // AI / Mock / TranslationMemory / 未知来源 → 完整 QA
        _ => ValidationPolicy.Full,
    };
}

/// <summary>
/// NeedsReview 决策层（与 ValidationIssue 分离）。
/// </summary>
public static class ValidationNeedsReviewPolicy
{
    /// <summary>
    /// 根据校验报告与当前状态，计算新的 NeedsReview 值。
    /// </summary>
    /// <param name="report">校验报告</param>
    /// <param name="currentNeedsReview">当前已经存在的 NeedsReview（如 AI 自报、Provider 宽容恢复）</param>
    public static bool Resolve(ValidationReport report, bool currentNeedsReview) => report.Policy switch
    {
        // 完整 QA：Error / Warning 均可促成 NeedsReview；已有的 true 不会被清除
        ValidationPolicy.Full => currentNeedsReview || report.HasError || report.HasWarning,

        // 继承的旧中文：本轮只产出结构化 Issue，不自动把历史条目变成“待审核”。
        // 实测真实数据中约 3~4% 的历史译文存在占位符 / 标签结构差异，
        // 若在此自动标记会让大量历史条目涌入审核列表；这些 Issue 由第3轮 ReleaseGate 按需消费。
        ValidationPolicy.InheritedStructureOnly => currentNeedsReview,

        // 系统原样保留（纯符号）：启发式不跑，但硬结构 Error 必须暴露为待审核
        ValidationPolicy.PassthroughStructureOnly => report.HasError || currentNeedsReview,

        // 人工审核 / 官方：硬安全问题（Error）必须暴露，启发式 Warning 不改变“已人工确认”语义
        _ => report.HasError || currentNeedsReview,
    };
}
