using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// Thinking 决策来源（第8.75轮）。
///
/// 目的：让 Coordinator 在翻译开始时统计"哪些条目会开启思考"，
/// 而**不需要**把自适应策略复制到 Agent / Coordinator 里。
/// 只有真正实现自适应策略的 Provider（DeepSeek）才实现本接口；Mock 等不实现。
/// </summary>
public interface IThinkingDecisionSource
{
    /// <summary>该条目是否会开启 Thinking。</summary>
    bool IsThinkingEnabled(DiffEntry entry);

    /// <summary>
    /// 该条目的 Thinking 决策原因码
    /// （SourceLanguageAnomaly / StoryData / DefaultOff / AlwaysOn / AlwaysOff）。
    /// </summary>
    string ResolveThinkingReason(DiffEntry entry);
}
