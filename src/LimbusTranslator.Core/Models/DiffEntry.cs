namespace LimbusTranslator.Core.Models;

/// <summary>
/// Diff 结果条目。
/// DiffKind 描述英文变化；TranslationAction 描述实际需要执行的动作。
/// </summary>
public sealed class DiffEntry
{
    /// <summary>复合主键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>新版英文原文</summary>
    public string? NewSourceText { get; set; }

    /// <summary>旧版英文原文</summary>
    public string? OldSourceText { get; init; }

    /// <summary>旧版中文译文（可能缺失）</summary>
    public string? OldTranslation { get; init; }

    /// <summary>英文变化类型</summary>
    public required DiffKind DiffKind { get; set; }

    /// <summary>需要执行的处理动作</summary>
    public required TranslationAction Action { get; set; }

    /// <summary>说话人</summary>
    public string? Speaker { get; init; }

    /// <summary>排序</summary>
    public int Order { get; init; }

    /// <summary>最终译文（处理完成后填充）</summary>
    public string? Translation { get; set; }

    /// <summary>是否需要人工审核</summary>
    public bool NeedsReview { get; set; }

    /// <summary>
    /// 最终译文来源（Provenance）。
    /// null 表示尚未确定来源；Inherit 条目为 Inherited，TM 命中为原记录来源，
    /// Provider 结果为 Provider 给出的来源，人工审核确认后为 HumanReviewed。
    /// </summary>
    public TranslationSource? Provenance { get; set; }

    /// <summary>需要人工审核的原因（AI 自报 reason 或 Placeholder 校验异常说明）</summary>
    public string? ReviewReason { get; set; }

    /// <summary>Translation Memory 命中级别（未命中为 None）</summary>
    public TranslationMemoryMatchType TmMatchType { get; set; } = TranslationMemoryMatchType.None;

    /// <summary>
    /// 第2轮：结构化校验问题列表（ValidatorPipeline 产出）。
    /// ReviewReason 只是它的可读汇总，结构化数据不会被丢弃。
    /// </summary>
    public IReadOnlyList<ValidationIssue> ValidationIssues { get; set; } = Array.Empty<ValidationIssue>();

    /// <summary>
    /// 韩文原文（Canonical，第9.0B.2轮）：KR 模式由管线按 UnitKey 注入；EN_ONLY 保持 null（不参与请求）。
    /// </summary>
    public string? CanonicalKoreanText { get; set; }

    /// <summary>旧韩文原文（Modified 时由管线注入）</summary>
    public string? OldCanonicalKoreanText { get; set; }

    /// <summary>
    /// TM SourceHash 盐（第9.0B.2轮）：由管线按模式注入（`TranslationModePolicy.BuildModeSalt`）。
    /// EN_ONLY 必须为 null（与旧 EN TM 兼容）；其它模式非空 → 四模式 TM 互相隔离。
    /// </summary>
    public string? SourceHashSalt { get; set; }

    /// <summary>
    /// 第9.0B-P1轮：本次 Run 的翻译模式（由 <c>ApplyToEntries</c> 注入）。
    /// **null ⇒ 旧英文语义**（EN_ONLY 不注入任何 Canonical 相关元数据）。
    /// 供 Validator（语言感知）与 Thinking（模式感知）作为唯一事实来源。
    /// </summary>
    public TranslationMode? RunTranslationMode { get; set; }

    /// <summary>
    /// 第9.0B-P1轮：本条目**实际生效**的源语言（由 <c>ApplyToEntries</c> 注入；
    /// 参考译本缺失回退韩文时为 <see cref="SourceLanguage.Korean"/>）。
    /// null ⇒ 英文（旧语义）。
    /// </summary>
    public SourceLanguage? EffectiveSourceLanguage { get; set; }

    /// <summary>
    /// 第9.0B-P1轮：本条目命中的术语（**只计算一次**，由生产计划按模式多源匹配后注入）。
    /// Prompt / TerminologyValidator / Trace 必须复用这一份，禁止各自重新匹配。
    /// null ⇒ 尚未注入（例如未经过生产计划的旧路径）。
    /// </summary>
    public IReadOnlyList<TerminologyRequirement>? MatchedTerms { get; set; }

    /// <summary>
    /// 第9.0B-P1轮：**英文参考译本**相对上一份快照是否变化（仅诊断 / Trace）。
    /// 不参与动作与缓存语义；null ⇒ N/A（EN_ONLY 或未注入）。
    /// </summary>
    public bool? EnglishReferenceChanged { get; set; }

    /// <summary>
    /// 第9.0B-P1轮：**日文参考译本**相对上一份快照是否变化（仅诊断 / Trace）。
    /// 不参与动作与缓存语义；null ⇒ N/A（EN_ONLY 或未注入）。
    /// </summary>
    public bool? JapaneseReferenceChanged { get; set; }

    // ───────── 第9.0C.2轮：锁定术语自动修正（Locked 术语执行闭环） ─────────

    /// <summary>本条目执行的锁定术语自动修正次数（0 = 未触发；本轮上限 1）</summary>
    public int TerminologyRepairAttempts { get; set; }

    /// <summary>自动修正后是否已不再违反锁定术语</summary>
    public bool TerminologyRepairSucceeded { get; set; }

    /// <summary>自动修正的可读结论（进入 Review 技术详情；null = 未发生）</summary>
    public string? TerminologyRepairNote { get; set; }
}
