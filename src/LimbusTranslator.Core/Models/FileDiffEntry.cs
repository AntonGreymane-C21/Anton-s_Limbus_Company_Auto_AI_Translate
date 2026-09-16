namespace LimbusTranslator.Core.Models;

/// <summary>
/// 文件级 Diff 条目。
/// </summary>
public sealed class FileDiffEntry
{
    /// <summary>逻辑相对路径（中文名，无 EN_ 前缀），如 StoryData/1D101A.json</summary>
    public required string LogicalPath { get; init; }

    /// <summary>英文物理相对路径（带 EN_ 前缀），如 StoryData/EN_1D101A.json</summary>
    public required string EnglishPath { get; init; }

    /// <summary>文件变化类型</summary>
    public required FileDiffKind Kind { get; init; }

    /// <summary>该文件的翻译单元数量（可翻译字段条数）</summary>
    public int UnitCount { get; set; }
}
