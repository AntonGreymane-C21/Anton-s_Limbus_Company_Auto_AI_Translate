namespace LimbusTranslator.Core.Models;

/// <summary>
/// Neighbor 角色：上一条 / 下一条有效对话。
/// </summary>
public enum NeighborRole
{
    /// <summary>上一条</summary>
    Previous,

    /// <summary>下一条</summary>
    Next,
}

/// <summary>
/// Neighbor 上下文条目（第5轮）。
///
/// 只包含“模型需要的少量语义上下文”，禁止携带内部 ID / 资源路径 / 状态字段；
/// 明确不包含任何在本次运行中新生成的 AI 译文（确定性要求）。
/// </summary>
public sealed class NeighborContextEntry
{
    /// <summary>角色：Previous / Next</summary>
    public required NeighborRole Role { get; init; }

    /// <summary>邻句 UnitKey（复用现有语义，不改变 UnitKey 定义）</summary>
    public required string UnitKey { get; init; }

    /// <summary>邻句英文原文（已跳过空/空白）</summary>
    public required string SourceText { get; init; }

    /// <summary>邻句说话人（teller 优先，其次 model 内部ID；可为空）</summary>
    public string? Speaker { get; init; }

    /// <summary>记录 ID</summary>
    public required string RecordId { get; init; }

    /// <summary>字段路径</summary>
    public required string FieldPath { get; init; }

    /// <summary>在其 ContextScope 内的稳定序号（0 基）</summary>
    public required int SequenceIndex { get; init; }

    public override string ToString() => $"{Role}: {SourceText}";
}

/// <summary>
/// 翻译上下文（第5轮首版）。
///
/// 数据来源被严格限制为“运行开始前就已确定的数据快照”：
///   新版英文原文 + 旧版英文（diff 用）+ Speaker + 稳定位置信息。
/// 因此任何 Agent/Batch 并发顺序都不会影响结果（确定性）。
/// </summary>
public sealed class TranslationContext
{
    /// <summary>空上下文（非 StoryData content，或没有可用邻句）</summary>
    public static TranslationContext Empty { get; } = new();

    /// <summary>上一条有效对话（可为 null）</summary>
    public NeighborContextEntry? Previous { get; init; }

    /// <summary>下一条有效对话（可为 null）</summary>
    public NeighborContextEntry? Next { get; init; }

    /// <summary>所属上下文作用域（文件 + 父路径），用于诊断；空上下文为 null</summary>
    public string? ContextScopeKey { get; init; }

    /// <summary>是否存在邻句</summary>
    public bool HasNeighbors => Previous is not null || Next is not null;

    /// <summary>邻句列表（Previous、Next 顺序固定）</summary>
    public IReadOnlyList<NeighborContextEntry> Neighbors
    {
        get
        {
            var list = new List<NeighborContextEntry>(2);
            if (Previous is not null)
            {
                list.Add(Previous);
            }
            if (Next is not null)
            {
                list.Add(Next);
            }
            return list;
        }
    }

    /// <summary>
    /// 【预留】Similar TM 示例（第5轮禁止实现，恒为空）。
    /// </summary>
    public IReadOnlyList<string> MemoryExamples { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 【预留】Lore 条目（第5轮禁止实现，恒为空）。
    /// </summary>
    public IReadOnlyList<string> LoreEntries { get; init; } = Array.Empty<string>();
}
