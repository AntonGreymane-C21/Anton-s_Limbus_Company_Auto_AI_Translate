using System.Net.Http.Json;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// 术语解释器。
///
/// 调用 DeepSeek 批量解释候选术语：
///   输入一批英文术语/名字
///   输出每个术语的：中文推荐译名 + 上下文含义 + 是否有由来（典故/神话/文学等）
/// 用于「增量术语库」功能。
/// </summary>
public sealed class TermExplainer : IDisposable
{
    /// <summary>单次术语解释请求的最大术语数</summary>
    public const int MaxTermsPerRequest = 25;

    private readonly HttpClient _http;
    private readonly DeepSeekOptions _options;

    public TermExplainer(DeepSeekOptions options)
    {
        _options = options;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 120),
        };
        _http.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
    }

    /// <summary>术语解释系统提示词</summary>
    private const string ExplainPrompt =
        "你是《Limbus Company / 边狱巴士》的世界设定专家。用户会给你一批英文术语或专有名词。" +
        "请为每一个术语提供：\n" +
        "1. suggested_translation：最贴切的中文译名（优先采用边狱巴士中文社区通译；只能给出一个译名，禁止使用斜杠、顿号、或、括号列出备选）；\n" +
        "2. meaning：这个词在这个游戏中的含义（简明，一两句话）；\n" +
        "3. origin：这个词是否有出处/由来（如圣经、神话、歌剧、文学作品、历史典故等），没有则写\"无特别由来\"。\n" +
        "输出必须是合法的 json 对象，固定为 {\"terms\":[{\"original\":\"...\",\"suggested_translation\":\"...\",\"meaning\":\"...\",\"origin\":\"...\"}]}，不得输出额外内容。";

    /// <summary>术语解释系统提示词，供诊断与单元测试验证 JSON 输出约束</summary>
    public static string ExplainSystemPrompt => ExplainPrompt;

    /// <summary>
    /// 批量解释术语。
    /// </summary>
    /// <param name="terms">要解释的术语列表</param>
    /// <returns>术语 → 解释结果</returns>
    public async Task<IReadOnlyDictionary<string, TermExplanation>> ExplainAsync(
        IReadOnlyList<string> terms,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("[错误] 未配置 DeepSeek API Key，请在 config/appsettings.json 中填写。");
        }
        if (terms.Count == 0)
        {
            return new Dictionary<string, TermExplanation>();
        }

        var uniqueTerms = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (uniqueTerms.Count == 0)
        {
            return new Dictionary<string, TermExplanation>();
        }

        var results = new Dictionary<string, TermExplanation>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        foreach (var batch in uniqueTerms.Chunk(MaxTermsPerRequest))
        {
            var batchTerms = batch.ToList();
            var batchResults = await ExplainBatchAsync(batchTerms, cancellationToken);
            foreach (var item in batchResults)
            {
                results[item.Key] = item.Value;
            }

            completed += batchTerms.Count;
            progress?.Invoke(completed, uniqueTerms.Count);
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<string, TermExplanation>> ExplainBatchAsync(
        IReadOnlyList<string> terms,
        CancellationToken cancellationToken)
    {
        var payload = new { terms };
        var requestBody = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = ExplainPrompt },
                new { role = "user", content = JsonSerializer.Serialize(payload) },
            },
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            response_format = new { type = "json_object" },
        };

        var retryDelay = 2;
        for (var attempt = 0; attempt <= _options.MaxRetry; attempt++)
        {
            try
            {
                using var response = await _http.PostAsJsonAsync(_options.ApiUrl, requestBody, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"[错误] API 返回 {(int)response.StatusCode}: {Truncate(body, 200)}");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseExplainResponse(content, terms);
            }
            catch (Exception ex) when (attempt < _options.MaxRetry && IsRetryable(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), cancellationToken);
                retryDelay *= 2;
            }
        }

        throw new InvalidOperationException("[错误] 重试次数已用尽，术语解释失败。");
    }


    /// <summary>
    /// 解析并校验术语解释响应。
    /// </summary>
    private static IReadOnlyDictionary<string, TermExplanation> ParseExplainResponse(
        string content,
        IReadOnlyList<string> requestTerms)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var msgContent))
        {
            throw new JsonException("[错误] 术语解释响应缺少 message.content");
        }

        var inner = msgContent.GetString() ?? string.Empty;
        using var innerDoc = JsonDocument.Parse(inner);

        if (!innerDoc.RootElement.TryGetProperty("terms", out var termItems)
            || termItems.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("[错误] 术语解释响应缺少 terms 数组");
        }

        var results = new Dictionary<string, TermExplanation>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in termItems.EnumerateArray())
        {
            var original = item.TryGetProperty("original", out var o) ? o.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }
            var suggested = item.TryGetProperty("suggested_translation", out var s)
                ? s.GetString() ?? string.Empty
                : string.Empty;
            var meaning = item.TryGetProperty("meaning", out var m)
                ? m.GetString() ?? string.Empty
                : string.Empty;
            var origin = item.TryGetProperty("origin", out var o2)
                ? o2.GetString() ?? string.Empty
                : string.Empty;

            results[original] = new TermExplanation
            {
                SuggestedTranslation = NormalizeSuggestedTranslation(suggested),
                IsSuggestedTranslationValid = IsSingleSuggestedTranslation(suggested),
                Meaning = meaning,
                Origin = origin,
            };
        }

        return results;
    }

    private static bool IsRetryable(Exception ex)
        => ex is HttpRequestException or JsonException or TaskCanceledException;

    private static string NormalizeSuggestedTranslation(string value)
    {
        var firstLine = value
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        return firstLine.Trim().Trim('"', '“', '”', '「', '」');
    }

    private static bool IsSingleSuggestedTranslation(string value)
    {
        var normalized = NormalizeSuggestedTranslation(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return !normalized.Contains('/')
               && !normalized.Contains('／')
               && !normalized.Contains('、')
               && !normalized.Contains(';')
               && !normalized.Contains('；')
               && !normalized.Contains(" 或 ", StringComparison.Ordinal)
               && !value.Contains('\n');
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// 术语解释结果。
/// </summary>
public sealed class TermExplanation
{
    /// <summary>推荐译名</summary>
    public string SuggestedTranslation { get; init; } = string.Empty;

    /// <summary>推荐译名是否符合“仅一个译名”的约束</summary>
    public bool IsSuggestedTranslationValid { get; init; } = true;

    /// <summary>含义</summary>
    public string Meaning { get; init; } = string.Empty;

    /// <summary>由来（是否有典故）</summary>
    public string Origin { get; init; } = string.Empty;

    /// <summary>组合解释文本（含义 + 由来）</summary>
    public string FullExplanation =>
        string.IsNullOrEmpty(Origin) || Origin.Contains("无特别", StringComparison.Ordinal)
            ? Meaning
            : $"{Meaning}\n【由来】{Origin}";
}
