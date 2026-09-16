namespace LimbusTranslator.Core.Models;

/// <summary>
/// Diff 结果：只说明英文原文发生了什么变化。
/// 注意：DiffKind 与 TranslationAction 必须分离。
/// </summary>
public enum DiffKind
{
    /// <summary>英文没变</summary>
    Unchanged,

    /// <summary>新增 ID</summary>
    Added,

    /// <summary>英文发生变化</summary>
    Modified,

    /// <summary>新版本已删除</summary>
    Deleted,
}
