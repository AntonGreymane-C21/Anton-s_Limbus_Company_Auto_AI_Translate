using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.Release;

/// <summary>
/// 发布门禁服务：先确保条目已通过 ValidatorPipeline，再按 <see cref="ReleaseGatePolicy"/> 计算门禁结论。
///
/// 与 ValidatorPipeline 的边界：
///   - 不修改任何 Validator 规则（第2轮规则保持不变）；
///   - 只补齐“尚未经过 Validator 的条目”（历史继承旧中文在正常翻译流程中不会进入翻译队列）；
///   - 不写 Translation Memory、不改变 NeedsReview 语义。
/// </summary>
public static class ReleaseGateService
{
    /// <summary>
    /// 计算门禁结果。
    /// </summary>
    /// <param name="entries">本次输出涉及的条目</param>
    /// <param name="validation">校验流水线；传入后会先补齐未校验条目（可为 null，表示条目已全部校验）</param>
    /// <param name="policy">门禁策略；null 使用默认策略</param>
    /// <param name="keySet">
    /// 输出结构 Key 集（第9.0B-P4轮）：必须与 Merge 使用**同一个**权威 Key 集
    ///（EN_ONLY → 当前英文；KR_EN / KR_JP / KR_ONLY → 当前韩文）。null = 不做 Key 集校验。
    /// </param>
    public static ReleaseGateResult Evaluate(
        IReadOnlyList<DiffEntry> entries,
        ValidationPipeline? validation = null,
        ReleaseGatePolicy? policy = null,
        ReleaseGateKeySet? keySet = null)
    {
        if (validation is not null)
        {
            EnsureValidation(entries, validation);
        }

        return ReleaseGate.Evaluate(entries, policy, keySet);
    }

    /// <summary>
    /// 为尚未经过 Validator 的条目补一次校验。
    ///
    /// 只处理“继承旧中文 / 来源未知且已有译文”的条目：
    ///   - 翻译队列与缓存恢复路径的条目在第2轮已校验过（ValidationIssues 非空或已判定无问题）；
    ///   - AI 条目即便无问题也不会被重复校验，避免大文件集重复计算。
    /// 校验结果只写入 DiffEntry.ValidationIssues；继承条目按第2轮策略不会改变 NeedsReview。
    /// </summary>
    private static void EnsureValidation(IReadOnlyList<DiffEntry> entries, ValidationPipeline validation)
    {
        foreach (var entry in entries)
        {
            if (entry is null || entry.Action == TranslationAction.SkipDeleted)
            {
                continue;
            }

            if (entry.ValidationIssues.Count > 0 || string.IsNullOrEmpty(entry.Translation))
            {
                continue;
            }

            var provenance = entry.Provenance;
            if (provenance is not null && provenance != TranslationSource.Inherited)
            {
                continue;
            }

            validation.ValidateAndApply(entry);
        }
    }
}
