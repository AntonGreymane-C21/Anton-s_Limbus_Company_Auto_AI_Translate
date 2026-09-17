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
    private readonly Action<string> _log;

    public TermExplainer(DeepSeekOptions options, HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? (_ => { });
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : 120);
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
    /// <summary>
    /// 「找出你不认识/不确定的词」系统提示词（第9.0C.10轮）。
    ///
    /// 与本地扫描的差别：本地是“大写即候选 + 停用词名单”（名单外的常用词会漏进来），
    /// 这里由模型自己判断“哪些词需要人工确认译法”，并显式禁止把普通常用词列进来。
    /// </summary>
    private const string DiscoverPrompt =
        "你是《Limbus Company / 边狱巴士》的世界设定专家，同时精通中英韩日四种语言。"
        + "用户会给你一批游戏内文本（英文/韩文/日文）。请只挑出你不认识或不确定其准确译法的词或短语，例如："
        + "游戏自造词、角色名、组织名、地名、技能/状态名、专有术语、文化典故、音译名。"
        + "不要列出普通常用词、寒暄用语、语法词（例如 alright / thanks / maybe / please / suddenly / everyone 这类）。"
        + "对每个词给出：original（原文，必须与文本中出现的写法一致）、"
        + "suggested_translation（最贴切的中文译名，只给一个，禁止用斜杠/顿号/括号列备选）、"
        + "reason（为什么不确定：如自造词/多义/需要社区通译，一两句话）。"
        + "输出必须是合法 json 对象，固定为 {\"terms\":[{\"original\":\"...\",\"suggested_translation\":\"...\",\"reason\":\"...\"}]}；"
        + "没有可选词时返回 {\"terms\":[]}；不得输出额外内容。";

    /// <summary>「找生词」提示词（诊断 / 单元测试用）。</summary>
    public static string DiscoverSystemPrompt => DiscoverPrompt;

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
                    var status = (int)response.StatusCode;
                    var message = $"[错误] API 返回 {status}: {Truncate(body, 200)}";
                    if (message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
                    {
                        message += "（提示：config 的 deepSeek.maxTokens 超出模型允许范围，建议 8192 ~ 16384）";
                    }

                    // 第9.0C.16轮：4xx（除 408 / 429）是**确定性拒绝**（例如 max_tokens 超范围、鉴权失败、模型名错误），
                    // 重试只会重复烧钱并拖长时间（真实故障：400 被重试 5 次、退避 2/4/8/16 秒）。
                    if (status is >= 400 and < 500 && status != 408 && status != 429)
                    {
                        throw new InvalidOperationException(message);
                    }

                    throw new HttpRequestException(message);
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
        => ex is HttpRequestException or JsonException or TaskCanceledException
           // 第9.0C.11轮：空响应是**确定性失败**，重试只是重复烧钱（真实故障里白重试了 6 次）
           && ex is not DeepSeekEmptyResponseException;

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

    /// <summary>
    /// 让 AI 自己找出“不认识 / 不确定”的词（第9.0C.10轮）。
    ///
    /// 行为：把文本按字符预算切块，逐块请求模型，合并去重；
    /// 取消（OperationCanceledException）原样向外传播 —— 由界面决定如何提示。
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredTerm>> DiscoverUnknownTermsAsync(
        IReadOnlyList<string> texts,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException("[错误] 未配置 DeepSeek API Key，请在 config/appsettings.json 中填写。");
        }

        var chunks = ChunkTexts(texts, MaxCharsPerDiscoveryRequest);
        if (chunks.Count == 0)
        {
            return Array.Empty<DiscoveredTerm>();
        }

        var found = new Dictionary<string, DiscoveredTerm>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var batchIndex = 0;
        foreach (var chunk in chunks)
        {
            batchIndex++;
            // 第9.0C.11轮：每批发送前后都写日志（真实故障里“第一批返回前界面一直 0%”，看起来像卡死）
            _log($"[调试] AI 找生词：第 {batchIndex}/{chunks.Count} 批发送中（{chunk.Count} 条 / {chunk.Sum(text => text.Length)} 字符）");
            var batch = await DiscoverBatchAsync(chunk, cancellationToken);
            foreach (var term in batch)
            {
                found.TryAdd(term.Original, term);
            }

            completed += chunk.Count;
            _log($"[调试] AI 找生词：第 {batchIndex}/{chunks.Count} 批完成（累计 {completed}/{texts.Count} 条文本，已找到 {found.Count} 个词）");
            progress?.Invoke(completed, texts.Count);
        }

        return found.Values.ToList();
    }

    /// <summary>把文本按字符预算切成若干请求批次（单条超长文本独占一批）。</summary>
    public static List<List<string>> ChunkTexts(IReadOnlyList<string> texts, int maxCharsPerRequest)
    {
        var chunks = new List<List<string>>();
        var current = new List<string>();
        var currentChars = 0;

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (current.Count > 0 && currentChars + text.Length > maxCharsPerRequest)
            {
                chunks.Add(current);
                current = new List<string>();
                currentChars = 0;
            }

            current.Add(text);
            currentChars += text.Length;
        }

        if (current.Count > 0)
        {
            chunks.Add(current);
        }

        return chunks;
    }

    /// <summary>单次“找生词”请求的文本字符上限（第9.0C.10轮）。</summary>
    public const int MaxCharsPerDiscoveryRequest = 8000;

    private async Task<IReadOnlyList<DiscoveredTerm>> DiscoverBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        var payload = new { texts };
        var requestBody = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = DiscoverPrompt },
                new { role = "user", content = JsonSerializer.Serialize(payload) },
            },
            temperature = _options.Temperature,
            max_tokens = _options.MaxTokens,
            response_format = new { type = "json_object" },
            // 第9.0C.11轮：找生词是“挑词/分类”任务，不需要长推理。
            // 真实故障：thinking=always_on + high 会把 max_tokens 吃满 ⇒ content 为空 ⇒ 一直失败。
            thinking = new { type = "disabled" },
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
                    var status = (int)response.StatusCode;
                    var message = $"[错误] API 返回 {status}: {Truncate(body, 200)}";
                    if (message.Contains("max_tokens", StringComparison.OrdinalIgnoreCase))
                    {
                        message += "（提示：config 的 deepSeek.maxTokens 超出模型允许范围，建议 8192 ~ 16384）";
                    }

                    // 第9.0C.16轮：4xx（除 408 / 429）是**确定性拒绝**（max_tokens 超范围 / 鉴权失败 / 模型名错误），
                    // 重试只会重复烧钱并拖长时间（真实故障：400 被重试 5 次、白等 62 秒）。
                    if (status is >= 400 and < 500 && status != 408 && status != 429)
                    {
                        throw new InvalidOperationException(message);
                    }

                    throw new HttpRequestException(message);
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseDiscoverResponse(content);
            }
            catch (Exception ex) when (attempt < _options.MaxRetry && IsRetryable(ex))
            {
                // 第9.0C.11轮：重试必须写在日志里（否则用户只看到进度不动，误以为卡死）
                _log($"[调试] AI 找生词：第 {attempt + 1} 次失败（{ex.GetType().Name}），{retryDelay} 秒后重试；原因：{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(retryDelay), cancellationToken);
                retryDelay *= 2;
            }
        }

        throw new InvalidOperationException("[错误] 重试次数已用尽，找生词失败。");
    }

    private static IReadOnlyList<DiscoveredTerm> ParseDiscoverResponse(string content)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var msgContent))
        {
            throw new JsonException("[错误] 找生词响应缺少 message.content");
        }

        var inner = msgContent.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inner))
        {
            throw new DeepSeekEmptyResponseException("[错误] 找生词响应为空（思考可能占满 max_tokens；本请求已关闭思考，仍为空请检查模型/配额）");
        }

        using var innerDoc = JsonDocument.Parse(inner);
        if (!innerDoc.RootElement.TryGetProperty("terms", out var termItems)
            || termItems.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("[错误] 找生词响应缺少 terms 数组");
        }

        var results = new List<DiscoveredTerm>();
        foreach (var item in termItems.EnumerateArray())
        {
            var original = item.TryGetProperty("original", out var o) ? o.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(original))
            {
                continue;
            }

            var suggested = item.TryGetProperty("suggested_translation", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            var reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            results.Add(new DiscoveredTerm(original.Trim(), NormalizeSuggestedTranslation(suggested), reason));
        }

        return results;
    }

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

/// <summary>AI 找出的“不认识 / 不确定”的词（第9.0C.10轮）。</summary>
public sealed record DiscoveredTerm(string Original, string SuggestedTranslation, string Reason);
