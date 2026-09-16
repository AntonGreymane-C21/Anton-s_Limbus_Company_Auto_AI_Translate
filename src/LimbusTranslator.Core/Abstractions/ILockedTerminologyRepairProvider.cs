using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// 锁定术语修正能力（第9.0C.2轮）。
///
/// 与 <see cref="ITranslationProvider"/> 的关系：
///   - 常规翻译由 <see cref="ITranslationProvider"/> 负责；
///   - 本接口只负责「把已经翻译好的译文按锁定术语要求修订一次」，**不做自由翻译**。
///
/// 由 Provider 实现（复用其 HTTP 客户端 / 请求缓存 / Trace / 提示词合成）；
/// 是否调用由 <c>LockedTerminologyRepairService</c> 依据校验结果决定。
/// </summary>
public interface ILockedTerminologyRepairProvider
{
    /// <summary>
    /// 针对单条条目执行一次锁定术语修正请求。
    /// </summary>
    /// <param name="entry">目标条目（携带四模式语义所需的 Selected Source / Canonical 字段）</param>
    /// <param name="lockedTerms">本次必须遵守的锁定术语（**来自 MatchedTerms，不重新匹配术语表**）</param>
    /// <param name="currentTranslation">当前译文（待修订）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task<LockedTerminologyRepairResult> RepairLockedTerminologyAsync(
        DiffEntry entry,
        IReadOnlyList<TerminologyRequirement> lockedTerms,
        string currentTranslation,
        CancellationToken cancellationToken = default);
}
