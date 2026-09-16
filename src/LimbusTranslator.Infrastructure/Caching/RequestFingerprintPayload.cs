using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace LimbusTranslator.Infrastructure.Caching;

/// <summary>
/// 请求指纹的 canonical payload（第4轮）。
///
/// 代表“模型真正看到的请求”：由 <see cref="DeepSeekRequestComposer"/> 实际产出的
/// System Prompt / User Content 加上真正影响输出的采样参数与逐条 item 组成。
///
/// 【顺序规则】
///   - Items 顺序 **参与** 指纹（模型看到的数组顺序变化即视为不同请求）；
///   - 语义等价的字典/子集（术语、角色风格）由调用方先做稳定排序再传入。
/// </summary>
public sealed class RequestFingerprintPayload
{
    /// <summary>指纹算法版本（算法变化时整体失效旧缓存，而不是错误解释）</summary>
    public int SchemaVersion { get; init; } = RequestFingerprintBuilder.SchemaVersion;

    /// <summary>Provider 标识（如 deepseek）</summary>
    public required string Provider { get; init; }

    /// <summary>Provider 身份（脱敏：scheme://host，不含密钥与查询串）</summary>
    public required string ProviderIdentity { get; init; }

    /// <summary>模型名</summary>
    public required string Model { get; init; }

    /// <summary>温度</summary>
    public double? Temperature { get; init; }

    /// <summary>max_tokens</summary>
    public int? MaxTokens { get; init; }

    /// <summary>响应格式（如 json_object）</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>
    /// 思考模式开关（第8.5轮）：对应真实请求的 <c>thinking.type</c>。
    /// 会真实改变模型行为，因此必须进入指纹。
    /// </summary>
    public bool? Thinking { get; init; }

    /// <summary>思考强度（第8.5轮）：对应真实请求的 <c>reasoning_effort</c>；thinking=false 时为 null。</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>实际发送的 System Prompt 内容</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>实际发送的 User Content 内容</summary>
    public string? UserContent { get; init; }

    /// <summary>实际注入的术语表段落（已排序）</summary>
    public string? GlossaryPrompt { get; init; }

    /// <summary>实际注入的角色风格段落（已排序）</summary>
    public string? CharacterStylePrompt { get; init; }

    /// <summary>逐条请求项（顺序敏感）</summary>
    /// <summary>
    /// 第9.0B-P1轮（Fingerprint v3）：翻译模式码（en_only / kr_en / kr_jp / kr_only）。
    /// 与 <see cref="RequestFingerprintItem.TranslationMode"/>（那是 TranslationAction）**语义不同**。
    /// </summary>
    public string? SourceModeCode { get; init; }

    /// <summary>v3：实际生效的源语言（en / ko / ja）</summary>
    public string? EffectiveSourceLanguage { get; init; }

    /// <summary>v3：本次翻译依据文本</summary>
    public string? SelectedSourceText { get; init; }

    /// <summary>v3：旧的翻译依据文本</summary>
    public string? OldSelectedSourceText { get; init; }

    /// <summary>v3：是否因选择语言缺失而回退韩文</summary>
    public bool? UsedKoreanFallback { get; init; }

    /// <summary>v3：韩文原文（**EN_ONLY 必须为 null** ⇒ 不参与指纹）</summary>
    public string? CanonicalKoreanText { get; init; }

    /// <summary>v3：旧韩文原文（**EN_ONLY 必须为 null**）</summary>
    public string? OldCanonicalKoreanText { get; init; }

    /// <summary>v3：韩文原文是否变化（**EN_ONLY 必须为 null** ⇒ N/A，不参与指纹）</summary>
    public bool? CanonicalChanged { get; init; }

    /// <summary>v3：英文 / 日文参考是否变化（仅诊断，不影响 EN_ONLY 基线）</summary>
    public bool? EnglishChanged { get; init; }

    /// <summary>v3：日文参考是否变化</summary>
    public bool? JapaneseChanged { get; init; }

    /// <summary>v3：英文参考是否变化</summary>
    public required IReadOnlyList<RequestFingerprintItem> Items { get; init; }
}

/// <summary>请求指纹中的单条 item（与模型实际看到的字段一一对应）。</summary>
public sealed class RequestFingerprintItem
{
    /// <summary>请求使用的 id（Base64 编码后的 UnitKey，与模型看到的完全一致）</summary>
    public required string Id { get; init; }

    /// <summary>UnitKey 原文（可读，用于 Trace）</summary>
    public required string UnitKey { get; init; }

    /// <summary>译文模式（TranslateNew / TranslateModified / TranslateMissing）</summary>
    public required string TranslationMode { get; init; }

    /// <summary>Placeholder 保护后的源文（模型实际看到的内容）</summary>
    public string? Source { get; init; }

    /// <summary>Placeholder 保护后的旧英文（模型实际看到的内容）</summary>
    public string? OldSource { get; init; }

    /// <summary>Placeholder 保护后的旧中文（模型实际看到的内容）</summary>
    public string? OldTranslation { get; init; }

    /// <summary>说话人</summary>
    public string? Speaker { get; init; }

    /// <summary>
    /// 上下文（第5轮 Neighbor Context 预留）。
    /// 本轮恒为 null；一旦 Neighbor 进入真实请求，此处与 UserContent 会同时变化 → 自然 Cache Miss。
    /// </summary>
    public string? Context { get; init; }
}
