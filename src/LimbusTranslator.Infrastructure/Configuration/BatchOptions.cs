using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// Provider 请求分批配置（第7.5轮）。
///
/// 唯一职责：决定「一个 Provider 请求最多包含多少条目 / 多少字符」。
/// 分批由 <see cref="DeepSeek.DeepSeekTranslationProvider"/> 独占负责
/// （<see cref="DeepSeek.ProviderBatchBuilder"/> 是唯一实现），
/// TranslationAgent 不再做任何 API 尺寸的分批。
///
/// 对应 config/appsettings.json 的 batch 段（沿用既有字段名，不另立第二套）：
/// <code>
/// "batch": {
///   "maxItemsPerBatch": 20,
///   "maxCharactersPerBatch": 30000,
///   "targetInputTokens": 20000
/// }
/// </code>
/// </summary>
public sealed class BatchOptions
{
    /// <summary>默认每批最大条目数（字段缺失时使用）。</summary>
    public const int DefaultMaxItemsPerBatch = 20;

    /// <summary>默认每批最大字符数（字段缺失时使用）。</summary>
    public const int DefaultMaxCharactersPerBatch = 30000;

    /// <summary>目标输入 token 的默认值（当前保留，不参与切分）。</summary>
    public const int DefaultTargetInputTokens = 20000;

    /// <summary>条目数上限（防呆：拒绝 999999999 之类的配置）。</summary>
    public const int MaxAllowedItemsPerBatch = 1000;

    /// <summary>字符数上限（防呆）。</summary>
    public const int MaxAllowedCharactersPerBatch = 2_000_000;

    /// <summary>目标输入 token 上限（防呆）。</summary>
    public const int MaxAllowedTargetInputTokens = 1_000_000;

    /// <summary>配置字段名（日志与错误信息统一引用，避免多处硬编码字符串）。</summary>
    public const string MaxItemsFieldName = "batch.maxItemsPerBatch";

    /// <inheritdoc cref="MaxItemsFieldName" />
    public const string MaxCharactersFieldName = "batch.maxCharactersPerBatch";

    /// <inheritdoc cref="MaxItemsFieldName" />
    public const string TargetInputTokensFieldName = "batch.targetInputTokens";

    /// <summary>每批最大条目数。</summary>
    public int MaxItemsPerBatch { get; set; } = DefaultMaxItemsPerBatch;

    /// <summary>
    /// 每批最大字符数。
    /// 字符预算 = 单条的 NewSourceText + OldSourceText + OldTranslation 长度之和
    /// （与分批前的历史行为完全一致，本轮不重新定义 token 估算）。
    /// </summary>
    public int MaxCharactersPerBatch { get; set; } = DefaultMaxCharactersPerBatch;

    /// <summary>
    /// 目标输入 token（保留位）。
    /// 当前分批仍以字符预算为准；该字段被读取与校验，便于后续切换到 token 预算。
    /// </summary>
    public int TargetInputTokens { get; set; } = DefaultTargetInputTokens;

    /// <summary>默认配置（20 条 / 30000 字符）。</summary>
    public static BatchOptions Default => new();

    /// <summary>
    /// 单条翻译项计入字符预算的长度（唯一定义处，Agent 与 Provider 共用同一语义）。
    /// </summary>
    public static int MeasureItemCharacters(DiffEntry entry)
        => (entry.NewSourceText?.Length ?? 0)
           + (entry.OldSourceText?.Length ?? 0)
           + (entry.OldTranslation?.Length ?? 0);
}
