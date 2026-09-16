using System.Text.Json;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// DeepSeek（OpenAI 兼容）响应解析与 ID 集合校验。
///
/// 职责边界（第2轮）：只处理协议层 —— JSON 结构、id 集合（Missing / Extra / Duplicate）。
/// 禁止在此处加入 Glossary QA、术语检查、StoryData、Neighbor、Lore 等业务逻辑。
///
/// 【第2轮修复】Duplicate ID 必须在写入字典之前显式检测：
///   旧实现用 Dictionary 覆盖后再比 Count，会出现 “A,A,B” 这种重复 ID 恰好补齐数量的漏检。
/// </summary>
public static class DeepSeekResponseParser
{
    /// <summary>
    /// 解析响应内容并校验 id 集合。
    /// </summary>
    /// <exception cref="JsonException">结构异常或 id 集合 Missing / Extra / Duplicate 时抛出（可重试）</exception>
    public static IReadOnlyDictionary<string, DeepSeekTranslateItem> Parse(
        string content,
        IReadOnlyList<DeepSeekTranslateRequestItem> requestItems)
        => ParseWithMetadata(content, requestItems).Items;

    /// <summary>
    /// 解析响应内容、校验 id 集合（Missing / Extra / Duplicate）并提取响应元数据。
    /// 元数据（响应 id / 模型 / token 用量）缺失时返回 null，不影响解析结果。
    /// </summary>
    public static (IReadOnlyDictionary<string, DeepSeekTranslateItem> Items, DeepSeekResponseMetadata Metadata)
        ParseWithMetadata(
            string content,
            IReadOnlyList<DeepSeekTranslateRequestItem> requestItems)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;
        var metadata = ReadMetadata(root);

        if (!root.TryGetProperty("choices", out var choices)
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var messageContent)
            || messageContent.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("[错误] API 响应缺少 choices[0].message.content");
        }

        var inner = messageContent.GetString() ?? string.Empty;
        using var innerDoc = JsonDocument.Parse(inner);

        if (!innerDoc.RootElement.TryGetProperty("items", out var resultItems)
            || resultItems.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("[错误] 响应 JSON 缺少 items 数组");
        }

        var requestIds = requestItems.Select(i => i.Id).ToHashSet();
        var results = new Dictionary<string, DeepSeekTranslateItem>();

        foreach (var item in resultItems.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                throw new JsonException("[错误] 响应 items 中存在缺少 id 的条目");
            }

            // 响应 id 是 Base64 编码，解码回逻辑 key
            var encodedId = idEl.GetString() ?? string.Empty;
            var id = DecodeId(encodedId);

            // Duplicate：必须在写入字典之前明确检测，禁止依赖字典覆盖后偶然发现
            if (results.ContainsKey(id))
            {
                throw new JsonException($"[错误] 响应包含重复 id: {id}");
            }

            if (!item.TryGetProperty("translation", out var transEl) || transEl.ValueKind != JsonValueKind.String)
            {
                throw new JsonException($"[错误] items[{id}] 缺少 translation");
            }

            var needsReview = false;
            if (item.TryGetProperty("needs_review", out var reviewEl) && reviewEl.ValueKind == JsonValueKind.True)
            {
                needsReview = true;
            }

            var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                ? reasonEl.GetString()
                : string.Empty;

            results[id] = new DeepSeekTranslateItem(id, transEl.GetString() ?? string.Empty, needsReview, reason ?? string.Empty);
        }

        if (results.Count != requestItems.Count)
        {
            throw new JsonException($"[错误] 响应条目数({results.Count})与请求条目数({requestItems.Count})不一致");
        }

        foreach (var rid in requestIds)
        {
            if (!results.ContainsKey(rid))
            {
                throw new JsonException($"[错误] 响应缺少请求中的 id: {rid}");
            }
        }

        foreach (var rid in results.Keys)
        {
            if (!requestIds.Contains(rid))
            {
                throw new JsonException($"[错误] 响应包含额外 id: {rid}");
            }
        }

        return (results, metadata);
    }

    /// <summary>
    /// 读取响应元数据（id / model / usage.*）。
    /// 缺失一律为 null：**不得因为 usage 缺失导致翻译失败**。
    /// </summary>
    private static DeepSeekResponseMetadata ReadMetadata(JsonElement root)
    {
        string? responseId = null;
        string? responseModel = null;
        int? promptTokens = null;
        int? completionTokens = null;
        int? totalTokens = null;
        int? reasoningTokens = null;

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
        {
            responseId = idEl.GetString();
        }

        if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            responseModel = modelEl.GetString();
        }

        if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            promptTokens = ReadInt(usageEl, "prompt_tokens");
            completionTokens = ReadInt(usageEl, "completion_tokens");
            totalTokens = ReadInt(usageEl, "total_tokens");

            // 第8.5轮：隐藏推理 token（thinking 模式）。缺失一律 null，绝不因此失败。
            if (usageEl.TryGetProperty("completion_tokens_details", out var detailsEl)
                && detailsEl.ValueKind == JsonValueKind.Object)
            {
                reasoningTokens = ReadInt(detailsEl, "reasoning_tokens");
            }
        }

        return new DeepSeekResponseMetadata(
            responseId, responseModel, promptTokens, completionTokens, totalTokens, reasoningTokens);
    }

    private static int? ReadInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    /// <summary>
    /// 将 Base64 解码回逻辑 key（解码失败时原样返回，兼容旧数据）。
    /// </summary>
    public static string DecodeId(string encoded)
    {
        try
        {
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch
        {
            return encoded;
        }
    }

    /// <summary>
    /// 将逻辑 key 编码为 Base64（无特殊字符，AI 安全）。
    /// </summary>
    public static string EncodeId(string key)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(key));
}
