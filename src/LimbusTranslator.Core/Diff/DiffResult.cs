using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Diff;

/// <summary>
/// Diff 结果汇总。
/// </summary>
public sealed class DiffResult
{
    public required IReadOnlyList<DiffEntry> Entries { get; init; }

    /// <summary>
    /// 第5轮：完整新版英文解析单元（含未变化文本，供 Context 索引使用）。
    /// 复用 DiffWorkflowService 已有的解析结果，避免为 Context 重新扫描磁盘。
    /// </summary>
    public IReadOnlyList<TranslationUnit> NewUnits { get; init; } = Array.Empty<TranslationUnit>();

    /// <summary>
    /// 第9.0B-P5轮：本次分析中**已经解析过**的旧中文单元（<c>UnitKey</c> → 旧中文文本存于 <c>SourceText</c>）。
    ///
    /// 用途：三个 KR 模式以韩文为权威结构，某些 Key 只存在于韩文树（英文 Diff 里没有），
    /// 此时必须按 <c>UnitKey</c> 直接从旧中文取 OldTranslation（而不是依赖“该 Key 是否恰好出现在英文 DiffEntry 里”）。
    ///
    /// 数据来源：<see cref="DiffEngine"/> 把 <c>Analyze</c> / <c>AnalyzeWithPreviousUnits</c> 已解析的旧中文单元原样带出，
    /// **不做第二次解析**（复用现有结果）。
    /// </summary>
    public IReadOnlyList<TranslationUnit> OldChineseUnits { get; init; } = Array.Empty<TranslationUnit>();

    /// <summary>新增条数</summary>
    public int AddedCount { get; init; }

    /// <summary>修改条数</summary>
    public int ModifiedCount { get; init; }

    /// <summary>未变化条数</summary>
    public int UnchangedCount { get; init; }

    /// <summary>删除条数</summary>
    public int DeletedCount { get; init; }

    /// <summary>缺失旧译条数（英文没变但旧中文缺失）</summary>
    public int MissingTranslationCount { get; init; }
}
