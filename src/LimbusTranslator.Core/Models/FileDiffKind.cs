namespace LimbusTranslator.Core.Models;

/// <summary>
/// 文件级变化类型。
/// 与条目级 DiffKind 不同，这里描述的是"整个 JSON 文件"的变化。
/// </summary>
public enum FileDiffKind
{
    /// <summary>新增文件（新版英文有，旧版英文没有）</summary>
    New,

    /// <summary>缺失汉化（旧英文已有，但中文文件不存在）</summary>
    MissingChinese,

    /// <summary>文件内容发生变化（新旧英文内容不同）</summary>
    Modified,

    /// <summary>未变化</summary>
    Unchanged,

    /// <summary>已删除（旧版有，新版没有）</summary>
    Deleted,
}
