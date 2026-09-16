using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Release;

/// <summary>
/// 门禁原因的可定位样本（供 WPF / 未来 CLI 展示：文件 + UnitKey + Code + Severity + 简短说明）。
/// </summary>
public sealed class ReleaseGateTarget
{
    /// <summary>复合主键字符串（RelativeFilePath|RecordId|FieldPath）</summary>
    public required string UnitKey { get; init; }

    /// <summary>相对文件路径</summary>
    public required string RelativeFilePath { get; init; }

    /// <summary>问题机器码</summary>
    public required string Code { get; init; }

    /// <summary>严重级别</summary>
    public required ValidationSeverity Severity { get; init; }

    /// <summary>简短说明</summary>
    public required string Message { get; init; }

    public override string ToString() => $"{RelativeFilePath} | {UnitKey} | {Code} | {Severity} | {Message}";
}

/// <summary>
/// 门禁原因（结构化）：说明“为什么不能直接部署”。
/// </summary>
public sealed class ReleaseGateReason
{
    /// <summary>原因分类（见 <see cref="ReleaseGateReasonKinds"/>）</summary>
    public required string Kind { get; init; }

    /// <summary>问题机器码（汇总类原因可为 "*"）</summary>
    public required string Code { get; init; }

    /// <summary>严重级别</summary>
    public required ValidationSeverity Severity { get; init; }

    /// <summary>涉及的译文来源</summary>
    public TranslationSource? Provenance { get; init; }

    /// <summary>条目数</summary>
    public required int Count { get; init; }

    /// <summary>该原因导致的门禁结论（Passed 表示仅记录不升级）</summary>
    public required ReleaseGateStatus Escalation { get; init; }

    /// <summary>面向用户的中文摘要</summary>
    public required string Message { get; init; }

    /// <summary>可定位样本（最多若干条）</summary>
    public IReadOnlyList<ReleaseGateTarget> Samples { get; init; } = Array.Empty<ReleaseGateTarget>();

    public override string ToString() => $"[{Kind}/{Code}] {Message}";
}
