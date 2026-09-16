using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Caching;

/// <summary>
/// 请求指纹（含 Trace 需要的子 Hash）。
/// </summary>
public sealed class RequestFingerprint
{
    /// <summary>完整指纹：<c>v1:&lt;sha256&gt;</c>（SchemaVersion 作为逻辑前缀，便于整体失效旧缓存）</summary>
    public required string Value { get; init; }

    /// <summary>指纹算法版本</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>实际 System Prompt 内容 Hash</summary>
    public required string PromptHash { get; init; }

    /// <summary>上下文 Hash（OldSource / OldTranslation / Speaker / Context / 角色风格 / 术语子集）</summary>
    public required string ContextHash { get; init; }

    /// <summary>术语子集 Hash</summary>
    public required string GlossarySubsetHash { get; init; }

    /// <summary>角色风格 Hash</summary>
    public required string CharacterStyleHash { get; init; }

    /// <summary>canonical JSON（仅供测试与诊断，不进入日志）</summary>
    public required string CanonicalJson { get; init; }

    /// <summary>日志用的短指纹（前 12 位）</summary>
    public string ShortValue => Value.Length <= 12 ? Value : Value[..12];
}

/// <summary>
/// 请求指纹构造器（第4轮）。
///
/// 规则：
///   1. **只**基于模型真正看到的语义内容（实际 Prompt / User Content / 采样参数 / 逐条 item）；
///   2. 不包含 RunId / RequestId / 时间 / 日志路径 / 重试次数等运行时信息（否则每次运行都会 Miss）；
///   3. canonical 序列化：固定字段顺序、显式 null、UTF-8、SHA256；
///   4. 算法版本变化 → 指纹前缀变化 → 旧缓存自然失效（不需要数据库迁移）。
/// </summary>
public static class RequestFingerprintBuilder
{
    /// <summary>当前指纹算法版本</summary>
    /// <summary>
    /// 指纹算法版本。
    /// 第8.5轮从 1 升到 2：canonical payload 新增 <c>thinking</c> / <c>reasoningEffort</c>，
    /// 旧缓存（v1 指纹）不会被错误解释，只会自然 Cache Miss。
    /// </summary>
    public const int SchemaVersion = 3;

    /// <summary>
    /// 指纹前缀（由 <see cref="SchemaVersion"/> 派生，便于整体失效旧缓存）。
    /// 第8.5轮：SchemaVersion 1 → 2（新增 thinking / reasoningEffort），前缀随之变为 <c>v2</c>。
    /// </summary>
    public static string FingerprintPrefix => $"v{SchemaVersion}";

    /// <summary>
    /// 构造指纹。
    /// </summary>
    public static RequestFingerprint Build(RequestFingerprintPayload payload)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var canonical = SerializeCanonical(payload);

        return new RequestFingerprint
        {
            Value = $"{FingerprintPrefix}:{Sha256Hex(canonical)}",
            SchemaVersion = payload.SchemaVersion,
            PromptHash = Sha256Hex(payload.SystemPrompt ?? string.Empty),
            ContextHash = ComputeContextHash(payload),
            GlossarySubsetHash = Sha256Hex(payload.GlossaryPrompt ?? string.Empty),
            CharacterStyleHash = Sha256Hex(payload.CharacterStylePrompt ?? string.Empty),
            CanonicalJson = canonical,
        };
    }

    /// <summary>SHA256 → 小写 hex。</summary>
    public static string Sha256Hex(string? value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// Provider 身份脱敏：只保留 scheme://host。
    /// **禁止**包含 API Key、路径或查询串。
    /// </summary>
    public static string SanitizeProviderIdentity(string? apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            return "unknown";
        }

        return Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}"
            : "custom";
    }

    /// <summary>canonical 序列化：字段顺序固定、null 显式写出、数组保持原顺序。</summary>
    private static string SerializeCanonical(RequestFingerprintPayload payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = false,
                   Encoder = JavaScriptEncoder.Default,
               }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", payload.SchemaVersion);
            writer.WriteString("provider", payload.Provider);
            writer.WriteString("providerIdentity", payload.ProviderIdentity);
            writer.WriteString("model", payload.Model);

            if (payload.Temperature.HasValue)
            {
                writer.WriteNumber("temperature", payload.Temperature.Value);
            }
            else
            {
                writer.WriteNull("temperature");
            }

            if (payload.MaxTokens.HasValue)
            {
                writer.WriteNumber("maxTokens", payload.MaxTokens.Value);
            }
            else
            {
                writer.WriteNull("maxTokens");
            }

            WriteNullableString(writer, "responseFormat", payload.ResponseFormat);

            if (payload.Thinking.HasValue)
            {
                writer.WriteBoolean("thinking", payload.Thinking.Value);
            }
            else
            {
                writer.WriteNull("thinking");
            }

            WriteNullableString(writer, "reasoningEffort", payload.ReasoningEffort);
            WriteNullableString(writer, "systemPrompt", payload.SystemPrompt);
            WriteNullableString(writer, "userContent", payload.UserContent);
            WriteNullableString(writer, "glossaryPrompt", payload.GlossaryPrompt);
            WriteNullableString(writer, "characterStylePrompt", payload.CharacterStylePrompt);

            // 第9.0B-P1轮（v3）：四模式语义（EN_ONLY 的 canonical 字段为 null ⇒ 不参与指纹，KR 更新不会破坏 EN_ONLY 缓存）
            WriteNullableString(writer, "sourceModeCode", payload.SourceModeCode);
            WriteNullableString(writer, "effectiveSourceLanguage", payload.EffectiveSourceLanguage);
            WriteNullableString(writer, "selectedSourceText", payload.SelectedSourceText);
            WriteNullableString(writer, "oldSelectedSourceText", payload.OldSelectedSourceText);
            if (payload.UsedKoreanFallback is not null)
            {
                writer.WriteBoolean("usedKoreanFallback", payload.UsedKoreanFallback.Value);
            }

            WriteNullableString(writer, "canonicalKoreanText", payload.CanonicalKoreanText);
            WriteNullableString(writer, "oldCanonicalKoreanText", payload.OldCanonicalKoreanText);
            if (payload.CanonicalChanged is not null)
            {
                writer.WriteBoolean("canonicalChanged", payload.CanonicalChanged.Value);
            }

            if (payload.EnglishChanged is not null)
            {
                writer.WriteBoolean("englishChanged", payload.EnglishChanged.Value);
            }

            if (payload.JapaneseChanged is not null)
            {
                writer.WriteBoolean("japaneseChanged", payload.JapaneseChanged.Value);
            }

            writer.WriteStartArray("items");
            foreach (var item in payload.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("id", item.Id);
                writer.WriteString("unitKey", item.UnitKey);
                writer.WriteString("translationMode", item.TranslationMode);
                WriteNullableString(writer, "source", item.Source);
                WriteNullableString(writer, "oldSource", item.OldSource);
                WriteNullableString(writer, "oldTranslation", item.OldTranslation);
                WriteNullableString(writer, "speaker", item.Speaker);
                WriteNullableString(writer, "context", item.Context);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    /// <summary>上下文 Hash：只覆盖“上下文类”信息，避免与整请求指纹重复。</summary>
    private static string ComputeContextHash(RequestFingerprintPayload payload)
    {
        var builder = new StringBuilder();
        foreach (var item in payload.Items)
        {
            builder.Append(item.UnitKey).Append('\u001f')
                .Append(item.OldSource ?? "\u0000").Append('\u001f')
                .Append(item.OldTranslation ?? "\u0000").Append('\u001f')
                .Append(item.Speaker ?? "\u0000").Append('\u001f')
                .Append(item.Context ?? "\u0000").Append('\u001e');
        }

        builder.Append(payload.CharacterStylePrompt ?? "\u0000").Append('\u001f');
        builder.Append(payload.GlossaryPrompt ?? "\u0000");

        return Sha256Hex(builder.ToString());
    }
}
