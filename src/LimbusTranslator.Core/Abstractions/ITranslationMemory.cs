using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// 翻译记忆。负责保存真正有长期价值的翻译记忆。
/// 第一阶段使用 NullTranslationMemory，第二阶段替换为 SqliteTranslationMemory。
///
/// 【第1轮 fail-safe 规则】
///   1. 只有 UnitKey + SourceHash 同时一致（ExactUnit）才允许作为最终译文复用；
///   2. 仅 SourceHash 相同、UnitKey 不同（CrossUnitSource）不得自动复用，只能作为参考；
///   3. null / 空 / 纯空白 SourceText 一律不参与命中，也不写入。
/// </summary>
public interface ITranslationMemory
{
    /// <summary>
    /// ExactUnit 精确命中：UnitKey + SourceHash 同时一致。
    /// 命中结果可直接作为最终译文使用（仍需传播 NeedsReview 与来源）。
    /// </summary>
    TranslationResult? FindExactUnit(UnitKey key, string sourceHash);

    /// <summary>
    /// CrossUnitSource 查询：仅按 SourceHash 查找（UnitKey 可能不同）。
    /// 【禁止】直接作为最终译文；仅供未来 Similar TM / ContextBuilder 参考。
    /// </summary>
    TranslationResult? FindCrossUnitSource(string sourceHash);

    /// <summary>
    /// 保存译文（Batch 完成后必须立即保存）。
    /// 空 / 纯空白源文本不会被写入。
    /// </summary>
    void Save(TranslationUnit unit, TranslationResult result);

    /// <summary>
    /// 人工审核确认后写回：来源 HumanReviewed、NeedsReview=false。
    /// 空源文或空译文不写入，返回是否成功写入。
    /// </summary>
    bool SaveHumanReviewed(TranslationUnit unit, string translation);
}
