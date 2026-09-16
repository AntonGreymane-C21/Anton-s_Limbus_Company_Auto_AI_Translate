using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// DeepSeek API 客户端。
/// 职责：HTTP / Retry / OpenAI 兼容协议解析（负责 <see cref="IDeepSeekBatchClient"/>）。
/// 禁止在此处理 SQLite、ReleaseGate、Validator、游戏数据。
/// </summary>
public sealed class DeepSeekClient : IDisposable, IDeepSeekBatchClient
{
    private readonly HttpClient _http;
    private readonly DeepSeekOptions _options;
    private readonly PromptOptions _prompt;

    /// <summary>
    /// 请求体序列化选项（第8.5轮）：
    /// <c>reasoning_effort</c> 在 thinking=false 时为 null，必须**省略该字段**而不是发送 null。
    /// </summary>
    private static readonly JsonSerializerOptions RequestBodyJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public DeepSeekClient(DeepSeekOptions options, PromptOptions? prompt = null)
    {
        _options = options;
        _prompt = prompt ?? new PromptOptions();
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 120),
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
    }

    /// <summary>
    /// 组合完整系统提示词 —— 已移至 <see cref="DeepSeekRequestComposer.BuildSystemPrompt"/>，
    /// 使「实际请求」与「请求指纹」共用同一份构造逻辑。
    /// </summary>
    /// <summary>
    /// 批量翻译（仅返回条目，兼容旧调用）。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, DeepSeekTranslateItem>> TranslateBatchAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken = default,
        string glossaryPrompt = "",
        string characterStylePrompt = "")
    {
        var result = await TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt);
        return result.Items;
    }

    /// <summary>
    /// 批量翻译并返回响应元数据（响应 id / 模型 / token 用量 / 重试次数 / 耗时）。
    ///
    /// 职责边界保持不变：HTTP、Retry、协议解析；不涉及 SQLite / ReleaseGate / Validator。
    /// Thinking 使用全局配置（第8.75轮起推荐调用带 <see cref="DeepSeekRequestThinking"/> 的重载）。
    /// </summary>
    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken = default,
        string glossaryPrompt = "",
        string characterStylePrompt = "")
        => TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt,
            new DeepSeekRequestThinking(_options.Thinking, _options.ReasoningEffort));

    /// <summary>
    /// 带显式 Thinking 设置的批量翻译（第8.75轮）：自适应策略下每个 Provider Request Batch 各自决定 ON/OFF，
    /// 这里把该决策真正写进 HTTP 请求体（thinking.type / reasoning_effort）。
    /// </summary>
    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking)
        => TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt, thinking,
            includeModifiedRule: false, category: null);

    /// <summary>
    /// 第8.87轮：在 Thinking 决策之外，把「旧译文规则」与「文本分类指令」也真正写入 HTTP 请求体。
    ///
    /// 二者都会改变模型实际看到的 System Prompt，因此同样进入请求指纹（Fingerprint v2）——
    /// 提示词规则升级后旧缓存自动 Cache Miss，不需要手工清缓存。
    /// </summary>
    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking,
        bool includeModifiedRule,
        TextCategory? category)
        => TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt, thinking, includeModifiedRule,
            category, TranslationMode.EnglishOnly);

    /// <summary>第9.0B.3A轮：把 Run 的翻译模式真正写进 HTTP 请求体（system 权威规则）。</summary>
    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking,
        bool includeModifiedRule,
        TextCategory? category,
        TranslationMode translationMode)
        => TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt, thinking, includeModifiedRule, category,
            translationMode, _unusedOverloadMarker: true);

    public async Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking,
        bool includeModifiedRule,
        TextCategory? category,
        TranslationMode translationMode,
        bool _unusedOverloadMarker)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("[错误] 未配置 DeepSeek API Key，请在 config/appsettings.json 中填写。");
        }
        if (items.Count == 0)
        {
            return new DeepSeekBatchResult
            {
                Items = new Dictionary<string, DeepSeekTranslateItem>(),
                RetryCount = 0,
                DurationMs = 0,
            };
        }

        // 请求体（第8.5轮）：真实请求 JSON 由 DeepSeekRequestComposer 统一构造。
        // 依据 DeepSeek 官方 Thinking Mode 文档：
        //   - 开关：{"thinking": {"type": "enabled" | "disabled"}}
        //   - 强度：{"reasoning_effort": "low" | "high" | "max"}
        //   - 思考模式不支持 temperature / presence_penalty / frequency_penalty（传了不报错但不生效）
        // 这些字段会真实影响模型输出，因此同样进入请求指纹（见 RequestFingerprintPayload）。
        var requestBodyJson = DeepSeekRequestComposer.BuildRequestBodyJson(
            _options, _prompt, batchId, items, glossaryPrompt, characterStylePrompt, thinking, includeModifiedRule, category,
            translationMode);

        var startedAt = System.Diagnostics.Stopwatch.StartNew();
        var retryCount = 0;
        var retryDelay = 2;

        for (var attempt = 0; attempt <= _options.MaxRetry; attempt++)
        {
            try
            {
                using var requestContent = new StringContent(requestBodyJson, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(_options.ApiUrl, requestContent, cancellationToken);

                if ((int)response.StatusCode is >= 400 and < 500 && (int)response.StatusCode != 429)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new InvalidOperationException(
                        $"[错误] API 请求被拒绝 ({response.StatusCode}): {Truncate(body, 300)}");
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"[错误] API 返回 {(int)response.StatusCode}");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                var parsed = DeepSeekResponseParser.ParseWithMetadata(content, items);
                startedAt.Stop();

                return new DeepSeekBatchResult
                {
                    Items = parsed.Items,
                    ResponseId = parsed.Metadata.ResponseId,
                    ResponseModel = parsed.Metadata.ResponseModel,
                    PromptTokens = parsed.Metadata.PromptTokens,
                    CompletionTokens = parsed.Metadata.CompletionTokens,
                    TotalTokens = parsed.Metadata.TotalTokens,
                    ReasoningTokens = parsed.Metadata.ReasoningTokens,
                    RetryCount = retryCount,
                    DurationMs = startedAt.ElapsedMilliseconds,
                };
            }
            catch (Exception ex) when (attempt < _options.MaxRetry && IsRetryable(ex))
            {
                retryCount++;
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), cancellationToken);
                retryDelay *= 2;
            }
        }

        throw new InvalidOperationException("[错误] 重试次数已用尽，翻译失败。");
    }

    /// <summary>将逻辑 key 编码为 Base64（无特殊字符，AI 安全）。实现在 <see cref="DeepSeekResponseParser"/>。</summary>
    public static string EncodeId(string key) => DeepSeekResponseParser.EncodeId(key);

    /// <summary>将 Base64 解码回逻辑 key。实现在 <see cref="DeepSeekResponseParser"/>。</summary>
    public static string DecodeId(string encoded) => DeepSeekResponseParser.DecodeId(encoded);

    /// <summary>
    /// 响应解析与 id 集合校验（Missing / Extra / Duplicate）已移至
    /// <see cref="DeepSeekResponseParser"/>，便于单元测试覆盖协议层。
    /// </summary>
    private static bool IsRetryable(Exception ex)
        => ex is HttpRequestException or JsonException or TaskCanceledException;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}

public sealed class DeepSeekTranslateRequestItem
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public string? OldSource { get; init; }
    public string? OldTranslation { get; init; }
    public string? Speaker { get; init; }

    /// <summary>
    /// 第5轮：邻句上下文（previous / next）。
    /// 为空或没有邻句时不写入请求（保持既有请求体字节兼容，不产生无意义的缓存失效）。
    /// </summary>
    public TranslationContext? Context { get; init; }

    /// <summary>
    /// 第9.0B.2轮：韩文原文（Canonical）。KR 模式由管线注入；EN_ONLY 必须为 null → **不写入请求**。
    /// </summary>
    public string? CanonicalKorean { get; init; }

    /// <summary>第9.0B.2轮：旧韩文原文（Modified；EN_ONLY 必须为 null）</summary>
    public string? OldCanonicalKorean { get; init; }

    // ───────── 第9.0C.2轮：锁定术语修正（普通翻译请求必须为 null） ─────────

    /// <summary>当前译文（仅修正请求携带；普通翻译请求为 null ⇒ 请求体与历史逐字节一致）</summary>
    public string? CurrentTranslation { get; init; }

    /// <summary>本次修正必须遵守的锁定术语（已排序的多行文本“Source → Target”；仅修正请求携带）</summary>
    public string? LockedTerms { get; init; }
}

public sealed record DeepSeekTranslateItem(string Id, string Translation, bool NeedsReview, string Reason);