namespace LimbusTranslator.Core.Models;

/// <summary>
/// 翻译任务。分配给 TranslationAgent 的最小工作单元。
/// </summary>
public sealed class TranslationTask
{
    /// <summary>任务 ID</summary>
    public required string TaskId { get; init; }

    /// <summary>需要处理的 Diff 条目</summary>
    public required IReadOnlyList<DiffEntry> Entries { get; init; }
}
