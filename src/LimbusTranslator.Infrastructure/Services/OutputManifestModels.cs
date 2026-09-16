using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 输出清单格式版本（等价于 ManifestVersion）。
///   1 = 第3轮之前（只有文件核验信息，没有发布门禁）
///   2 = 第3轮起（含 ReleaseGate 结果与人工确认绑定）
/// </summary>
public static class OutputManifestVersions
{
    /// <summary>旧版（无门禁信息）</summary>
    public const int Legacy = 1;

    /// <summary>当前版本</summary>
    public const int Current = 2;
}

/// <summary>
/// 人工确认记录。必须绑定 <see cref="ManifestId"/>：
/// 输出重新生成会产生新的清单 Id，因此旧确认自动失效（禁止“一次确认永久放行”）。
/// </summary>
public sealed class OutputManifestConfirmation
{
    /// <summary>被确认的清单 Id</summary>
    public required string ManifestId { get; init; }

    /// <summary>确认时间（UTC）</summary>
    public required DateTime ConfirmedAtUtc { get; init; }

    /// <summary>确认时已知晓的 Warning 数量</summary>
    public int AcknowledgedWarningCount { get; init; }

    /// <summary>确认时已知晓的历史继承结构差异数量</summary>
    public int AcknowledgedHistoricalInheritedErrorCount { get; init; }
}

/// <summary>
/// 清单中的门禁原因（<see cref="ReleaseGateReason"/> 的可序列化形态）。
/// </summary>
public sealed class OutputManifestGateReason
{
    /// <summary>原因分类</summary>
    public required string Kind { get; init; }

    /// <summary>问题机器码</summary>
    public required string Code { get; init; }

    /// <summary>严重级别</summary>
    public required ValidationSeverity Severity { get; init; }

    /// <summary>涉及的译文来源</summary>
    public TranslationSource? Provenance { get; init; }

    /// <summary>条目数</summary>
    public required int Count { get; init; }

    /// <summary>该原因导致的门禁结论</summary>
    public required ReleaseGateStatus Escalation { get; init; }

    /// <summary>中文摘要</summary>
    public required string Message { get; init; }

    /// <summary>可定位样本："文件 | UnitKey | Code | Severity | 说明"</summary>
    public IReadOnlyList<string> Samples { get; init; } = Array.Empty<string>();
}
