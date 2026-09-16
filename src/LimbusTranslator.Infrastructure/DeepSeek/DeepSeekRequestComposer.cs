using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// DeepSeek 请求构造器（第4轮抽取）。
///
/// 【唯一事实来源】实际发送给模型的 System Prompt / User Content 只能由这里生成：
///   - <see cref="DeepSeekClient"/> 用它构造真实 HTTP 请求；
///   - <see cref="RequestFingerprintBuilder"/> 用同样的输出计算请求指纹。
/// 这样 Fingerprint 天然代表“模型真正看到的请求”，不会出现影子字段清单漂移。
/// </summary>
public static class DeepSeekRequestComposer
{
    /// <summary>响应格式（与实际请求中的 response_format.type 一致）</summary>
    public const string ResponseFormatType = "json_object";

    /// <summary>可读的提示词版本号（仅用于 Trace/日志，禁止作为缓存正确性依据）</summary>
    public const string PromptVersion = "1.0";

    /// <summary>
    /// 第5轮：邻句上下文规则（只在请求确实携带 context 时追加到系统提示词）。
    /// 明确告诉模型 previous / next 只是参考，不能为它们生成翻译项。
    /// </summary>
    public const string ContextRule =
        "上下文规则（重要）：items[].context 中的 previous / next 只是参考上下文，"
        + "禁止为它们生成任何翻译项；输出必须且只能包含当前请求 items 中每个 id 的 translation。";

    /// <summary>
    /// 构造**真实发送**给 DeepSeek 的请求体 JSON（第8.5轮）。
    ///
    /// 之所以把它从 <see cref="DeepSeekClient"/> 抽到本类：
    ///   1. 「真实请求」与「请求指纹」应尽量共用同一份事实来源，避免二者漂移；
    ///   2. 使 thinking / reasoning_effort 是否真正进入请求可以被单元测试直接断言，
    ///      而不需要访问真实网络。
    ///
    /// 字段依据 DeepSeek 官方 Thinking Mode 文档：
    ///   - <c>thinking.type</c> = enabled / disabled（服务端默认 enabled）
    ///   - <c>reasoning_effort</c> = low / high / max（仅 thinking=true 时发送）
    ///   - 思考模式不支持 temperature / presence_penalty / frequency_penalty（传了不生效）
    /// </summary>
    public static string BuildRequestBodyJson(
        DeepSeekOptions options,
        PromptOptions prompt,
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking? thinking = null,
        bool includeModifiedRule = false,
        TextCategory? category = null,
        TranslationMode mode = TranslationMode.EnglishOnly)
    {
        // 第8.75轮：Thinking 由**本批决策**决定（自适应策略下逐批不同）；未提供时回落到全局配置。
        var enabled = thinking?.Enabled ?? options.Thinking;
        var effort = thinking is null
            ? options.ReasoningEffort
            : thinking.ReasoningEffort;

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = options.Model,
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildSystemPrompt(
                        prompt, glossaryPrompt, characterStylePrompt, ContainsContext(items), includeModifiedRule, category, mode,
                        ContainsRepairItems(items)),
                },
                new
                {
                    role = "user",
                    content = BuildUserContent(batchId, items),
                },
            },
            ["temperature"] = options.Temperature,
            ["max_tokens"] = options.MaxTokens,
            ["response_format"] = new { type = ResponseFormatType },
            ["thinking"] = new { type = enabled ? "enabled" : "disabled" },
        };

        // reasoning_effort 只在 thinking=true 且显式配置了强度时出现；
        // 注意：JsonIgnoreCondition 不作用于 Dictionary 条目，因此这里必须显式判断，不能写成 null 占位。
        if (enabled && !string.IsNullOrWhiteSpace(effort))
        {
            body["reasoning_effort"] = effort.Trim();
        }

        return JsonSerializer.Serialize(body, RequestBodyJsonOptions);
    }

    /// <summary>请求体序列化选项：null 字段必须省略（thinking=false 时不发送 reasoning_effort）。</summary>
    private static readonly JsonSerializerOptions RequestBodyJsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// 第8.87轮：Modified 文本规则（只在请求确实携带旧英文/旧中文时追加）。
    /// 明确旧译文只是译风与未变化语义的参考，新英文才是事实依据。
    /// </summary>
    public const string ModifiedRule =
        "旧译文使用规则（重要）：OldSource / OldTranslation 仅供理解未变化语义与沿用既有汉化风格，"
        + "必须以新的 Source 作为唯一事实依据；旧译文已不再对应的部分不要机械照搬，也不要把旧译文当作可省略翻译的理由。";

    /// <summary>
    /// 第8.87轮：按文本分类追加指令（StoryData 与 UI / 技能 / 道具要求不同，
    /// 但不维护两套完全独立的 Prompt —— 统一为「Base Prompt + Category Instructions」）。
    /// </summary>
    public static string BuildCategoryInstruction(TextCategory category) => category switch
    {
        TextCategory.StoryData =>
            "本文本属于剧情内容：注意人物语气、前后文连贯与叙事节奏，使用自然的中文台词表达，"
            + "不遗漏任何语义，不添加原文没有的信息。",
        TextCategory.General =>
            "本文本属于界面 / 技能 / 道具等说明性内容：表达简洁准确，术语与数值格式必须保持一致，"
            + "不要文学化扩写，不要增加语气词。",
        TextCategory.PersonalityVoiceDlg or TextCategory.EGOVoiceDig or TextCategory.BattleAnnouncerDlg =>
            "本文本属于角色语音台词：保持该角色的说话风格与口吻，句子简短自然，符合语音长度。",
        TextCategory.BgmLyrics =>
            "本文本属于歌词：保持意象与节奏，允许适度意译，但不得改变原意。",
        _ => string.Empty,
    };

    /// <summary>
    /// 第9.0C.2轮：锁定术语修正规则（**只**在修正请求中追加）。
    /// 明确告知模型这是「修订已有译文」而不是重新翻译，并列出不得改动的部分。
    /// </summary>
    public const string RepairRule =
        "锁定术语修订任务（重要）：本次不是重新翻译，而是修订一条已经完成的中文译文。\n"
        + "items[].MustUseTerms 中列出的是【锁定术语】，属于强制规则：最终译文必须使用其中指定的中文译法，"
        + "不得使用同义词、近义词、意译或自行改写术语本身。\n"
        + "在尽量保持当前译文（items[].CurrentTranslation）其它内容不变的前提下，只修正术语违规。\n"
        + "禁止：删除原有信息、添加原文没有的信息、修改数字、修改占位符（如 {0}）、修改富文本标签、"
        + "修改角色名、改动不相关句子。\n"
        + "输出格式与普通翻译请求完全一致：仍然返回 items 数组，id 原样返回，translation 为修正后的完整中文译文。";

    /// <summary>请求中是否存在锁定术语修正项（决定是否追加修正规则）。</summary>
    public static bool ContainsRepairItems(IReadOnlyList<DeepSeekTranslateRequestItem>? items)
        => items is not null && items.Any(i => !string.IsNullOrEmpty(i.LockedTerms));

    /// <summary>
    /// 组合完整系统提示词（基础规则 + 输出格式 + 术语表 + 角色风格 [+ 上下文规则] [+ 旧译文规则] [+ 分类指令]）。
    ///
    /// includeContextRule / includeModifiedRule / category / includeRepairRule 均为可选：
    /// 未提供时输出与历史版本**逐字节一致**，避免无意义地让既有请求缓存全部失效。
    /// </summary>
    public static string BuildSystemPrompt(
        PromptOptions prompt,
        string glossaryPrompt,
        string characterStylePrompt,
        bool includeContextRule = false,
        bool includeModifiedRule = false,
        TextCategory? category = null,
        TranslationMode mode = TranslationMode.EnglishOnly,
        bool includeRepairRule = false)
    {
        var options = prompt ?? new PromptOptions();
        var basePrompt = options.SystemPrompt + "\n" + options.OutputFormat;

        if (!string.IsNullOrWhiteSpace(glossaryPrompt))
        {
            basePrompt += "\n\n" + glossaryPrompt;
        }
        if (!string.IsNullOrWhiteSpace(characterStylePrompt))
        {
            basePrompt += "\n\n" + characterStylePrompt;
        }

        // 第9.0B.2轮：仅 KR_EN / KR_JP 追加「韩文权威高于译本」规则（EN_ONLY 绝不追加）
        if (TranslationModePolicy.IncludesKoreanAuthorityRule(mode))
        {
            basePrompt += "\n\n" + TranslationModePromptBuilder.KoreanAuthorityRule;
        }
        if (includeContextRule)
        {
            basePrompt += "\n\n" + ContextRule;
        }
        if (includeModifiedRule)
        {
            basePrompt += "\n\n" + ModifiedRule;
        }
        if (category is not null)
        {
            var categoryInstruction = BuildCategoryInstruction(category.Value);
            if (!string.IsNullOrWhiteSpace(categoryInstruction))
            {
                basePrompt += "\n\n" + categoryInstruction;
            }
        }

        // 第9.0C.2轮：锁定术语修正规则（只在修正请求中追加；普通翻译请求不受影响）
        if (includeRepairRule)
        {
            basePrompt += "\n\n" + RepairRule;
        }

        return basePrompt;
    }

    /// <summary>
    /// 构造 User Content（与实际请求体一致：agent_id / batch_id / prompt_version / items）。
    ///
    /// 第5轮：item 只有在确实存在邻句时才追加 <c>context</c> 字段 ——
    /// 无上下文时输出与第4轮**逐字节一致**，避免无意义地让所有非 Story 请求缓存失效。
    /// </summary>
    public static string BuildUserContent(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items)
    {
        var payload = new
        {
            agent_id = "Coordinator",
            batch_id = batchId,
            prompt_version = PromptVersion,
            items = items.Select(BuildItemPayload),
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>请求中是否存在携带邻句上下文的 item（决定是否追加上下文规则）。</summary>
    public static bool ContainsContext(IReadOnlyList<DeepSeekTranslateRequestItem>? items)
        => items is not null && items.Any(i => i.Context?.HasNeighbors == true);

    /// <summary>
    /// 单条 item 的请求体（键顺序与命名保持与历史版本一致）。
    /// </summary>
    private static Dictionary<string, object?> BuildItemPayload(DeepSeekTranslateRequestItem item)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            // id 编码为 Base64，避免 AI 被 | [ ] 等字符干扰
            ["id"] = DeepSeekResponseParser.EncodeId(item.Id),
            ["Source"] = item.Source,
            ["OldSource"] = item.OldSource,
            ["OldTranslation"] = item.OldTranslation,
            ["Speaker"] = item.Speaker,
        };

        // 第9.0B.2轮：KR 模式的韩文原文（EN_ONLY 时为 null → 不写入请求，保证不发送韩文）
        if (!string.IsNullOrEmpty(item.CanonicalKorean))
        {
            payload["CanonicalKorean"] = item.CanonicalKorean;
        }

        if (!string.IsNullOrEmpty(item.OldCanonicalKorean))
        {
            payload["OldCanonicalKorean"] = item.OldCanonicalKorean;
        }

        var contextJson = BuildContextJson(item.Context);
        if (contextJson is not null)
        {
            payload["context"] = JsonNode.Parse(contextJson);
        }

        // 第9.0C.2轮：锁定术语修正项（普通翻译请求为 null ⇒ 请求体与历史逐字节一致）
        if (!string.IsNullOrEmpty(item.CurrentTranslation))
        {
            payload["CurrentTranslation"] = item.CurrentTranslation;
        }

        if (!string.IsNullOrEmpty(item.LockedTerms))
        {
            payload["MustUseTerms"] = item.LockedTerms;
        }

        return payload;
    }

    /// <summary>
    /// 邻句上下文的确定性 JSON 片段（请求与请求指纹共用同一份）：
    /// <c>{"previous":{"speaker":...,"source":...},"next":{...}}</c>
    /// 没有邻句时返回 null（调用方据此不写入 context 字段）。
    /// </summary>
    public static string? BuildContextJson(TranslationContext? context)
    {
        if (context is null || !context.HasNeighbors)
        {
            return null;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            writer.WriteStartObject();
            if (context.Previous is not null)
            {
                writer.WritePropertyName("previous");
                WriteNeighbor(writer, context.Previous);
            }
            if (context.Next is not null)
            {
                writer.WritePropertyName("next");
                WriteNeighbor(writer, context.Next);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNeighbor(Utf8JsonWriter writer, NeighborContextEntry entry)
    {
        writer.WriteStartObject();
        if (entry.Speaker is null)
        {
            writer.WriteNull("speaker");
        }
        else
        {
            writer.WriteString("speaker", entry.Speaker);
        }
        writer.WriteString("source", entry.SourceText);
        writer.WriteEndObject();
    }
}
