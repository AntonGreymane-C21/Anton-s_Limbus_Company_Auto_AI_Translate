using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// 翻译上下文构造器（第5轮）。
///
/// 输入：DiffEntry（当前条目）+ 运行前构建好的上下文索引；
/// 输出：<see cref="TranslationContext"/>。
///
/// 规则（强制）：
///   - 纯查询 / 纯构造：禁止调用 API、写 SQLite / TM、修改 DiffEntry、扫描磁盘、修改索引；
///   - 结果必须只依赖“运行前快照”，不得依赖并发顺序或本轮新生成的 AI 译文；
///   - 非适用范围返回 <see cref="TranslationContext.Empty"/>（绝不为空文本伪造邻句）。
/// </summary>
public interface ITranslationContextBuilder
{
    /// <summary>当前条目适用的上下文（无邻句时返回空上下文）</summary>
    TranslationContext Build(DiffEntry entry);
}
