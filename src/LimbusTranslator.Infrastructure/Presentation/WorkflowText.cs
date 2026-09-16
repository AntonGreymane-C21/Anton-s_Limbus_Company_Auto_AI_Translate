using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>
/// 运行 / 结果相关的**用户可读文案**（第9.0C轮）。
/// 内部枚举与机器码保持不变，本类只负责展示层翻译（技术码放「高级详情」）。
/// </summary>
public static class WorkflowText
{
    /// <summary>处理动作（人话）。</summary>
    public static string Action(TranslationAction action) => action switch
    {
        TranslationAction.TranslateNew => "新增文本",
        TranslationAction.TranslateModified => "原文已修改",
        TranslationAction.TranslateMissing => "缺少翻译",
        TranslationAction.Inherit => "继承旧翻译",
        TranslationAction.SkipDeleted => "原文已删除",
        _ => action.ToString(),
    };

    /// <summary>变化类型（人话）。</summary>
    public static string DiffKind(DiffKind kind) => kind switch
    {
        Core.Models.DiffKind.Added => "新增",
        Core.Models.DiffKind.Modified => "已修改",
        Core.Models.DiffKind.Unchanged => "未变化",
        Core.Models.DiffKind.Deleted => "已删除",
        _ => kind.ToString(),
    };

    /// <summary>严重度文字（不依赖颜色作为唯一信息）。</summary>
    public static string Severity(ValidationSeverity severity) => severity switch
    {
        ValidationSeverity.Error => "错误",
        ValidationSeverity.Warning => "警告",
        _ => "提示",
    };

    /// <summary>发布门禁状态（人话）。</summary>
    public static string GateStatus(ReleaseGateStatus status) => status switch
    {
        ReleaseGateStatus.Passed => "检查通过",
        ReleaseGateStatus.RequiresConfirmation => "需要人工确认",
        ReleaseGateStatus.Blocked => "存在阻塞问题",
        _ => status.ToString(),
    };

    /// <summary>门禁状态的一句话说明（告诉普通用户下一步做什么）。</summary>
    public static string GateNextStep(ReleaseGateStatus status) => status switch
    {
        ReleaseGateStatus.Passed => "可以直接生成输出或部署到游戏。",
        ReleaseGateStatus.RequiresConfirmation => "请到「待审核」页确认列出的条目后再生成输出。",
        ReleaseGateStatus.Blocked => "存在必须修掉的问题，修好后重新生成输出。",
        _ => string.Empty,
    };

    /// <summary>语言名称（用户可读）。</summary>
    public static string Language(SourceLanguage language) => language switch
    {
        SourceLanguage.Korean => "韩文",
        SourceLanguage.English => "英文",
        SourceLanguage.Japanese => "日文",
        _ => language.ToString(),
    };

    /// <summary>思考模式（人话）。</summary>
    public static string ThinkingMode(Configuration.TranslationThinkingMode? mode) => mode switch
    {
        Configuration.TranslationThinkingMode.AlwaysOn => "始终开启",
        Configuration.TranslationThinkingMode.AlwaysOff => "始终关闭",
        Configuration.TranslationThinkingMode.Adaptive => "自动",
        _ => "自动",
    };

    /// <summary>自动思考模式的说明。</summary>
    public const string ThinkingAdaptiveHint = "自动模式会根据 StoryData、韩文直译等情况决定是否启用深度推理。";

    /// <summary>部署事务状态（人话）。</summary>
    public static string DeploymentStatus(Services.DeploymentStatus status) => status switch
    {
        Services.DeploymentStatus.Succeeded => "部署完成",
        Services.DeploymentStatus.FailedRolledBack => "部署失败（已回滚到部署前状态）",
        Services.DeploymentStatus.FailedRollbackIncomplete => "部署失败，且回滚未全部完成（需要人工检查）",
        _ => status.ToString(),
    };

    /// <summary>校验问题的中文名称 + 说明（代码 → 人话）。</summary>
    public static (string Name, string Explanation) Issue(string? code) => code switch
    {
        ValidationIssueCodes.EmptyTranslation => ("译文为空", "该条目有原文但没有译文。"),
        ValidationIssueCodes.SameAsSource => ("译文与原文相同", "译文看起来没有翻译，仍与原文一致。"),
        ValidationIssueCodes.PlaceholderMismatch => ("占位符不一致", "译文中的 {0} 等占位符与原文不匹配或缺失。"),
        ValidationIssueCodes.TagMismatch => ("富文本标签不一致", "译文里的 <color> 等标签结构与原文不同。"),
        ValidationIssueCodes.NumberMismatch => ("数字不一致", "译文中的数字与原文不一致（例如 +10% 变成了别的数字）。"),
        ValidationIssueCodes.LineBreakMismatch => ("换行不一致", "译文换行数量与原文不同。"),
        ValidationIssueCodes.EnglishResidue => ("仍有英文残留", "译文里还留着成段英文，可能没有翻译完整。"),
        ValidationIssueCodes.KoreanResidue => ("仍有韩文残留", "译文中仍出现韩文字符。"),
        ValidationIssueCodes.JapaneseResidue => ("仍有日文假名残留", "译文中出现平假名 / 片假名。"),
        ValidationIssueCodes.LengthAnomaly => ("长度异常", "译文长度与原文相差过大，可能漏译或多译。"),
        ValidationIssueCodes.TerminologyMismatch => ("术语可能未按指定译法", "译文可能没有使用术语表要求的译名。"),
        ValidationIssueCodes.SourceLanguageAnomaly => ("原文语言异常", "英文源文里混入了韩文，建议人工确认译名。"),
        ValidationIssueCodes.CanonicalKoreanSourceMissing => ("缺少韩文原文", "缺少韩文原文，建议人工检查该条目。"),
        ValidationIssueCodes.ValidatorFailure => ("校验器执行失败", "校验过程出现异常（一般不影响译文，可重试）。"),
        _ => (code ?? "未知问题", "没有对应的说明。"),
    };

    /// <summary>「技术详情」里展示的问题行文本：<c>CODE｜中文名</c>。</summary>
    public static string IssueTechnicalLine(string? code)
    {
        var (name, _) = Issue(code);
        return $"{code ?? "—"}｜{name}";
    }

    /// <summary>是否属于阻塞级（Error）。</summary>
    public static bool IsBlocking(ValidationSeverity severity) => severity == ValidationSeverity.Error;
}
