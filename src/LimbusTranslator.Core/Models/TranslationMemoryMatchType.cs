namespace LimbusTranslator.Core.Models;

/// <summary>
/// Translation Memory 命中级别。
///
/// 本轮（第1轮）只允许 ExactUnit 直接作为最终译文复用；
/// CrossUnitSource 仅作为来源标记保留给未来 Similar TM / ContextBuilder 参考，
/// 禁止自动覆盖当前译文。
/// </summary>
public enum TranslationMemoryMatchType
{
    /// <summary>未命中 Translation Memory。</summary>
    None,

    /// <summary>UnitKey + SourceHash 同时一致：精确缓存，可直接作为最终译文。</summary>
    ExactUnit,

    /// <summary>仅 SourceHash 相同但 UnitKey 不同：不得自动复用，只能作为参考候选。</summary>
    CrossUnitSource,
}
