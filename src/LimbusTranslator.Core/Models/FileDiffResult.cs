namespace LimbusTranslator.Core.Models;

/// <summary>
/// 文件级 Diff 结果。
/// </summary>
public sealed class FileDiffResult
{
    public required IReadOnlyList<FileDiffEntry> Entries { get; init; }

    /// <summary>新增文件数</summary>
    public int NewCount { get; init; }

    /// <summary>缺失汉化文件数</summary>
    public int MissingChineseCount { get; init; }

    /// <summary>修改文件数</summary>
    public int ModifiedCount { get; init; }

    /// <summary>未变化文件数</summary>
    public int UnchangedCount { get; init; }

    /// <summary>删除文件数</summary>
    public int DeletedCount { get; init; }
}
