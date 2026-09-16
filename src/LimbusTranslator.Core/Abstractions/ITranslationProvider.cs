using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// 翻译提供者。负责调用翻译服务（第一阶段为 DeepSeekClient）。
/// 所有 Agent 必须通过统一入口调用，禁止重复创建 HttpClient。
/// </summary>
public interface ITranslationProvider
{
    /// <summary>
    /// 批量翻译任务。
    /// </summary>
    /// <param name="entries">待翻译条目</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="stageId">Stage（文件）标识，用于 Trace 关联（可空）</param>
    /// <param name="contexts">第5轮：UnitKey → 翻译上下文（邻句；可空表示无上下文）</param>
    /// <returns>key 到译文的映射</returns>
    Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
        IReadOnlyList<DiffEntry> entries,
        CancellationToken cancellationToken = default,
        string? stageId = null,
        IReadOnlyDictionary<string, TranslationContext>? contexts = null);
}
