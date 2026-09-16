using System.Text.Json;
using LimbusTranslator.Infrastructure.Translation;

namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// Trace JSONL 统计（第8.85轮）。
///
/// 只读聚合：请求数（网络 / 缓存）、Token、耗时、重试、指纹，
/// 以及**实际** Thinking 决策分布（Thinking ON by reason / OFF）——
/// 这是 GUI 展示"真实 Provider 请求"的权威来源，优于按 Policy 重算的理论值。
/// </summary>
public sealed class TranslationTraceStats
{
    public int NetworkCalled { get; private set; }

    public int CacheHit { get; private set; }

    public int? InputTokens { get; private set; }

    public int? OutputTokens { get; private set; }

    public int? ReasoningTokens { get; private set; }

    public int? VisibleOutputTokens { get; private set; }

    public int? TotalTokens { get; private set; }

    public long? DurationMs { get; private set; }

    public int? RetryCount { get; private set; }

    public string? FirstFingerprint { get; private set; }

    /// <summary>实际 Thinking ON（SourceLanguageAnomaly）请求数。</summary>
    public int ThinkingOnSourceLanguageAnomaly { get; private set; }

    /// <summary>实际 Thinking ON（StoryData）请求数。</summary>
    public int ThinkingOnStoryData { get; private set; }

    /// <summary>实际 Thinking ON（其它原因）请求数。</summary>
    public int ThinkingOnOther { get; private set; }

    /// <summary>实际 Thinking OFF 请求数。</summary>
    public int ThinkingOff { get; private set; }

    /// <summary>总请求数（网络 + 缓存命中）。</summary>
    public int RequestCount => NetworkCalled + CacheHit;

    /// <summary>
    /// 读取并聚合 Trace 文件（不存在 / 解析失败时返回空统计，**绝不抛异常**）。
    /// </summary>
    public static TranslationTraceStats Read(string? traceFilePath)
    {
        var stats = new TranslationTraceStats();
        try
        {
            if (string.IsNullOrWhiteSpace(traceFilePath) || !File.Exists(traceFilePath))
            {
                return stats;
            }

            foreach (var line in File.ReadLines(traceFilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;   // 坏行按跳过处理，不影响整体统计
                }

                using (doc)
                {
                    stats.AddLine(doc.RootElement);
                }
            }
        }
        catch
        {
            // Trace 仅用于展示
        }

        return stats;
    }

    private void AddLine(JsonElement root)
    {
        if (root.TryGetProperty("networkCalled", out var nc) && nc.ValueKind == JsonValueKind.True)
        {
            NetworkCalled++;
        }

        if (root.TryGetProperty("cacheHit", out var ch) && ch.ValueKind == JsonValueKind.True)
        {
            CacheHit++;
        }

        InputTokens = Add(InputTokens, GetInt(root, "inputTokens"));
        OutputTokens = Add(OutputTokens, GetInt(root, "outputTokens"));
        ReasoningTokens = Add(ReasoningTokens, GetInt(root, "reasoningTokens"));
        VisibleOutputTokens = Add(VisibleOutputTokens, GetInt(root, "visibleOutputTokens"));
        TotalTokens = Add(TotalTokens, GetInt(root, "totalTokens"));
        DurationMs = AddLong(DurationMs, GetLong(root, "durationMs"));
        RetryCount = Add(RetryCount, GetInt(root, "retryCount"));

        if (FirstFingerprint is null
            && root.TryGetProperty("fingerprint", out var fp)
            && fp.ValueKind == JsonValueKind.String)
        {
            FirstFingerprint = fp.GetString();
        }

        // 实际 Thinking 决策（按行统计，与请求一一对应）
        var enabled = root.TryGetProperty("thinkingEnabled", out var te) && te.ValueKind == JsonValueKind.True;
        var reason = root.TryGetProperty("thinkingPolicyReason", out var tr) && tr.ValueKind == JsonValueKind.String
            ? tr.GetString()
            : null;

        if (!enabled)
        {
            ThinkingOff++;
        }
        else if (reason == ThinkingPolicyReasons.SourceLanguageAnomaly)
        {
            ThinkingOnSourceLanguageAnomaly++;
        }
        else if (reason == ThinkingPolicyReasons.StoryData)
        {
            ThinkingOnStoryData++;
        }
        else
        {
            ThinkingOnOther++;
        }
    }

    private static int? GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static long? GetLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static int? Add(int? current, int? delta) => delta.HasValue ? (current ?? 0) + delta.Value : current;

    private static long? AddLong(long? current, long? delta) => delta.HasValue ? (current ?? 0) + delta.Value : current;
}
