using System.Text.RegularExpressions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Context;

/// <summary>
/// 上下文索引中的节点（一条可参与邻句的对话行）。
/// </summary>
public sealed record TranslationScopeNode
{
    /// <summary>UnitKey 字符串（沿用现有语义）</summary>
    public required string UnitKey { get; init; }

    public required string RelativeFilePath { get; init; }
    public required string RecordId { get; init; }
    public required string FieldPath { get; init; }

    /// <summary>英文原文（构建时已排除空/空白）</summary>
    public required string SourceText { get; init; }

    /// <summary>说话人（teller 优先、其次 model；可为空）</summary>
    public string? Speaker { get; init; }

    /// <summary>Parser 的记录序号（同 Order 时需要 tie-break）</summary>
    public required int Order { get; init; }

    /// <summary>在其 ContextScope 内的稳定序号（0 基；排序后分配）</summary>
    public required int SequenceIndex { get; init; }
}

/// <summary>
/// StoryData 邻句上下文索引（第5轮）。
///
/// 数据来源：**完整新版英文解析结果**（不是 DiffEntry 待翻译条目），
/// 因此未变化文本也能成为邻句；已删除的旧文本不在其中。
///
/// 构建：每次翻译运行构建**一次**，构建后只读（不再修改），保证并发确定性。
/// 复杂度：构建 O(N log N)（按作用域排序），查询 O(1)（UnitKey → 作用域 + 序号）。
/// </summary>
public sealed class TranslationContextIndex
{
    private static readonly Regex TrailingIndexRegex = new(@"\[\d+\]$", RegexOptions.Compiled);

    private readonly Dictionary<string, IReadOnlyList<TranslationScopeNode>> _scopes;
    private readonly Dictionary<string, (string ScopeKey, int Index)> _lookup;

    private TranslationContextIndex(
        Dictionary<string, IReadOnlyList<TranslationScopeNode>> scopes,
        Dictionary<string, (string ScopeKey, int Index)> lookup)
    {
        _scopes = scopes;
        _lookup = lookup;
    }

    /// <summary>空索引（无任何上下文，功能关闭时使用）</summary>
    public static TranslationContextIndex Empty { get; } = new(
        new Dictionary<string, IReadOnlyList<TranslationScopeNode>>(StringComparer.Ordinal),
        new Dictionary<string, (string, int)>(StringComparer.Ordinal));

    /// <summary>作用域数量</summary>
    public int ScopeCount => _scopes.Count;

    /// <summary>索引节点数量</summary>
    public int NodeCount => _lookup.Count;

    /// <summary>构建时被跳过的单元数量（空文本 / 非 StoryData content）</summary>
    public int SkippedUnitCount { get; private init; }

    /// <summary>该 UnitKey 是否在索引中（仅 StoryData content 且源文非空）。</summary>
    public bool Contains(string? unitKey)
        => !string.IsNullOrEmpty(unitKey) && _lookup.ContainsKey(unitKey);

    /// <summary>取该 UnitKey 所属作用域键。</summary>
    public bool TryGetScopeKey(string? unitKey, out string scopeKey)
    {
        scopeKey = string.Empty;
        if (string.IsNullOrEmpty(unitKey) || !_lookup.TryGetValue(unitKey, out var location))
        {
            return false;
        }

        scopeKey = location.ScopeKey;
        return true;
    }

    /// <summary>
    /// 从完整新版英文单元构建索引。
    /// </summary>
    /// <param name="newEnglishUnits">完整新版英文解析结果（含未变化文本）</param>
    /// <param name="log">调试日志（单个异常单元不会中断构建）</param>
    public static TranslationContextIndex Build(
        IEnumerable<TranslationUnit>? newEnglishUnits,
        Action<string>? log = null)
    {
        if (newEnglishUnits is null)
        {
            return Empty;
        }

        var grouped = new Dictionary<string, List<(TranslationScopeNode Node, int Traversal)>>(StringComparer.Ordinal);
        var skipped = 0;
        var traversal = 0;

        foreach (var unit in newEnglishUnits)
        {
            try
            {
                if (unit?.Key is null)
                {
                    skipped++;
                    continue;
                }

                // 仅索引 StoryData 的 content 对话行
                if (!IsDialogueContentUnit(unit.Key))
                {
                    skipped++;
                    continue;
                }

                // 空 / 空白文本不参与（不得成为 Neighbor）
                if (!SourceTextGuard.IsReusable(unit.SourceText))
                {
                    skipped++;
                    continue;
                }

                var scopeKey = BuildScopeKey(unit.Key);
                if (!grouped.TryGetValue(scopeKey, out var list))
                {
                    list = new List<(TranslationScopeNode, int)>();
                    grouped[scopeKey] = list;
                }

                list.Add((
                    new TranslationScopeNode
                    {
                        UnitKey = unit.Key.ToString(),
                        RelativeFilePath = unit.Key.RelativeFilePath,
                        RecordId = unit.Key.RecordId,
                        FieldPath = unit.Key.FieldPath,
                        SourceText = unit.SourceText,
                        Speaker = unit.Speaker,
                        Order = unit.Order,
                        SequenceIndex = -1,
                    },
                    traversal));

                traversal++;
            }
            catch (Exception ex)
            {
                // 单个异常单元不允许中断整个索引构建
                skipped++;
                log?.Invoke($"[调试] Context 索引跳过异常单元: {ex.Message}");
            }
        }

        var scopes = new Dictionary<string, IReadOnlyList<TranslationScopeNode>>(StringComparer.Ordinal);
        var lookup = new Dictionary<string, (string ScopeKey, int Index)>(StringComparer.Ordinal);

        foreach (var (scopeKey, items) in grouped)
        {
            // 稳定排序：Order → FieldPath（同 Order tie-break）→ 解析遍历顺序
            var ordered = items
                .OrderBy(x => x.Node.Order)
                .ThenBy(x => x.Node.FieldPath, StringComparer.Ordinal)
                .ThenBy(x => x.Traversal)
                .Select((x, index) => x.Node with { SequenceIndex = index })
                .ToList();

            scopes[scopeKey] = ordered;
            for (var i = 0; i < ordered.Count; i++)
            {
                lookup[ordered[i].UnitKey] = (scopeKey, i);
            }
        }

        return new TranslationContextIndex(scopes, lookup) { SkippedUnitCount = skipped };
    }

    /// <summary>
    /// 取同作用域内的上一条 / 下一条有效对话（越界返回 null，绝不跨作用域）。
    /// </summary>
    public TranslationScopeNode? FindNeighbor(string? unitKey, NeighborRole role)
    {
        if (string.IsNullOrEmpty(unitKey)
            || !_lookup.TryGetValue(unitKey, out var location)
            || !_scopes.TryGetValue(location.ScopeKey, out var nodes))
        {
            return null;
        }

        var index = role == NeighborRole.Previous ? location.Index - 1 : location.Index + 1;
        return index >= 0 && index < nodes.Count ? nodes[index] : null;
    }

    /// <summary>作用域键：文件 + 对话块父路径（去掉承载该数组的最后一个 [n]）。</summary>
    internal static string BuildScopeKey(UnitKey key)
    {
        var fieldPath = key.FieldPath ?? string.Empty;
        var lastDot = fieldPath.LastIndexOf('.');
        var parent = lastDot > 0 ? fieldPath[..lastDot] : string.Empty;
        var blockPath = TrailingIndexRegex.Replace(parent, string.Empty);
        return $"{key.RelativeFilePath}#{blockPath}";
    }

    /// <summary>是否为 StoryData 的 content 对话行（使用项目既有分类器，不使用脆弱文件名匹配）。</summary>
    internal static bool IsDialogueContentUnit(UnitKey key)
    {
        if (TextCategoryHelper.FromRelativePath(key.RelativeFilePath) != TextCategory.StoryData)
        {
            return false;
        }

        return IsContentField(key.FieldPath);
    }

    /// <summary>叶子字段是否为 content（去掉数组下标）。</summary>
    internal static bool IsContentField(string? fieldPath)
    {
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return false;
        }

        var lastDot = fieldPath.LastIndexOf('.');
        var leaf = lastDot >= 0 ? fieldPath[(lastDot + 1)..] : fieldPath;
        leaf = TrailingIndexRegex.Replace(leaf, string.Empty);
        return string.Equals(leaf, "content", StringComparison.OrdinalIgnoreCase);
    }
}
