using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Context;

/// <summary>
/// 邻句上下文构造器（第5轮首版）。
///
/// 职责：`DiffEntry + TranslationContextIndex → TranslationContext`（纯查询 / 纯构造）。
///
/// 严格边界：
///   - 不调用 API、不写 SQLite / TM、不修改 DiffEntry、不扫描磁盘、不修改索引；
///   - 只使用“运行前快照”（新版英文 + Speaker + 稳定序号），
///     绝不以本次运行新生成的 AI 译文件作为邻句；
///   - 非 StoryData content、或索引中没有该条目、或无法确定顺序时，一律返回空上下文
///     （宁可没有上下文，也不生成错误 Neighbor）。
/// </summary>
public sealed class TranslationContextBuilder : ITranslationContextBuilder
{
    private readonly TranslationContextIndex? _index;

    /// <summary>构造器（索引为 null 或空索引时行为等同于“无上下文”）</summary>
    public TranslationContextBuilder(TranslationContextIndex? index)
    {
        _index = index;
    }

    /// <summary>关闭上下文的构造器</summary>
    public static TranslationContextBuilder Disabled { get; } = new(null);

    /// <inheritdoc />
    public TranslationContext Build(DiffEntry entry)
    {
        if (_index is null || _index.NodeCount == 0 || entry?.Key is null)
        {
            return TranslationContext.Empty;
        }

        try
        {
            // 首版只对 StoryData 的 content 生效
            if (!TranslationContextIndex.IsDialogueContentUnit(entry.Key))
            {
                return TranslationContext.Empty;
            }

            // 当前条目自身源文为空 → 不构造上下文
            var source = entry.NewSourceText ?? entry.OldSourceText;
            if (!SourceTextGuard.IsReusable(source))
            {
                return TranslationContext.Empty;
            }

            var unitKey = entry.Key.ToString();
            if (!_index.TryGetScopeKey(unitKey, out var scopeKey))
            {
                // 不在索引中（例如源文为空的对话行）：不猜测顺序，直接无上下文
                return TranslationContext.Empty;
            }

            var previous = _index.FindNeighbor(unitKey, NeighborRole.Previous);
            var next = _index.FindNeighbor(unitKey, NeighborRole.Next);
            if (previous is null && next is null)
            {
                return TranslationContext.Empty;
            }

            return new TranslationContext
            {
                Previous = previous is null ? null : ToEntry(previous, NeighborRole.Previous),
                Next = next is null ? null : ToEntry(next, NeighborRole.Next),
                ContextScopeKey = scopeKey,
            };
        }
        catch
        {
            // 任何异常都不允许影响翻译；但也不能悄悄生成错误 Neighbor → 回退为无上下文
            return TranslationContext.Empty;
        }
    }

    private static NeighborContextEntry ToEntry(TranslationScopeNode node, NeighborRole role) => new()
    {
        Role = role,
        UnitKey = node.UnitKey,
        SourceText = node.SourceText,
        Speaker = node.Speaker,
        RecordId = node.RecordId,
        FieldPath = node.FieldPath,
        SequenceIndex = node.SequenceIndex,
    };
}
