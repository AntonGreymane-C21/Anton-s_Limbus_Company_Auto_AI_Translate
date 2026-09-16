using System.Text.Json;
using System.Text.Json.Serialization;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 最近一次输出任务的已核验文件清单。
/// 清单使用非 .json 扩展名，避免被部署服务误当成游戏本地化文件复制。
/// </summary>
public sealed class OutputRunManifest
{
    public int FormatVersion { get; init; } = OutputManifestVersions.Current;

    /// <summary>本次清单唯一 Id（每次保存都会重新生成 → 旧确认自动失效）</summary>
    public string ManifestId { get; init; } = string.Empty;
    public DateTime CreatedAtUtc { get; init; }
    public bool IsComplete { get; init; }
    public int RequestedFileCount { get; init; }
    public int RequestedEntryCount { get; init; }
    public int WrittenFileCount { get; init; }
    public int VerifiedEntryCount { get; init; }
    public int IssueCount { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();

    // ---------- 第3轮：ReleaseGate ----------

    /// <summary>发布门禁结论</summary>
    public ReleaseGateStatus ReleaseGateStatus { get; init; } = ReleaseGateStatus.Passed;

    /// <summary>QA Error 数</summary>
    public int ErrorCount { get; init; }

    /// <summary>QA Warning 数</summary>
    public int WarningCount { get; init; }

    /// <summary>待审核条目数</summary>
    public int NeedsReviewCount { get; init; }

    /// <summary>导致 Blocked 的 Error 数</summary>
    public int BlockingErrorCount { get; init; }

    /// <summary>历史继承旧中文的结构安全差异条目数</summary>
    public int HistoricalInheritedErrorCount { get; init; }

    /// <summary>阻断原因（人类可读，可解释“为什么不能部署”）</summary>
    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();

    /// <summary>需要人工确认的原因摘要</summary>
    public string? RequiresConfirmationReason { get; init; }

    /// <summary>结构化门禁原因（含定位样本）</summary>
    public IReadOnlyList<OutputManifestGateReason> GateReasons { get; init; } = Array.Empty<OutputManifestGateReason>();

    /// <summary>人工确认记录（未确认为 null；绑定 ManifestId）</summary>
    public OutputManifestConfirmation? Confirmation { get; init; }

    /// <summary>是否为缺少门禁信息的旧版清单</summary>
    public bool IsLegacy => FormatVersion < OutputManifestVersions.Current;

    /// <summary>确认是否绑定在当前清单上（重新输出后自动失效）</summary>
    public bool HasValidConfirmation =>
        Confirmation is not null
        && !string.IsNullOrEmpty(ManifestId)
        && string.Equals(Confirmation.ManifestId, ManifestId, StringComparison.Ordinal);
}

/// <summary>
/// 输出任务清单读写服务。
/// </summary>
public static class OutputManifestService
{
    public const string ManifestFileName = ".limbus-output.manifest";

    public static string GetManifestPath(string outputRoot)
        => Path.Combine(outputRoot, ManifestFileName);

    /// <summary>
    /// 保存最近一次任务中已写入且写后校验通过的文件清单 + 发布门禁结果。
    /// 即使任务不完整也会覆盖旧清单，避免部署时误用上一轮遗留输出。
    ///
    /// 每次保存都会生成新的 <see cref="OutputRunManifest.ManifestId"/>，
    /// 因此上一轮的人工确认会自动失效（禁止“一次确认永久放行”）。
    /// </summary>
    public static OutputRunManifest Save(string outputRoot, OutputMergeResult result, ReleaseGateResult gate)
    {
        var manifest = new OutputRunManifest
        {
            FormatVersion = OutputManifestVersions.Current,
            ManifestId = Guid.NewGuid().ToString("N"),
            CreatedAtUtc = DateTime.UtcNow,
            IsComplete = result.IsComplete,
            RequestedFileCount = result.RequestedFileCount,
            RequestedEntryCount = result.RequestedEntryCount,
            WrittenFileCount = result.WrittenFileCount,
            VerifiedEntryCount = result.VerifiedEntryCount,
            IssueCount = result.Issues.Count,
            Files = result.Files
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            ReleaseGateStatus = gate.Status,
            ErrorCount = gate.ErrorCount,
            WarningCount = gate.WarningCount,
            NeedsReviewCount = gate.NeedsReviewCount,
            BlockingErrorCount = gate.BlockingErrorCount,
            HistoricalInheritedErrorCount = gate.HistoricalInheritedErrorCount,
            BlockingReasons = gate.BlockingReasons,
            RequiresConfirmationReason = gate.RequiresConfirmationReason,
            GateReasons = gate.Reasons.Select(ToRecord).ToArray(),
            Confirmation = null,
        };

        Write(outputRoot, manifest);
        return manifest;
    }

    /// <summary>
    /// 为当前清单写入人工确认（必须绑定 ManifestId）。
    /// 清单已变化 / 已被 Blocked 时拒绝写入。
    /// </summary>
    public static OutputRunManifest SaveConfirmation(
        string outputRoot,
        string manifestId,
        int acknowledgedWarningCount,
        int acknowledgedHistoricalInheritedErrorCount)
    {
        var current = Load(outputRoot)
            ?? throw new InvalidOperationException("[错误] 未找到输出清单，无法确认部署。请先执行汉化或重新输出。");

        if (!string.Equals(current.ManifestId, manifestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "[错误] 输出清单已重新生成，之前的人工确认已失效，请在界面上重新确认后再部署。");
        }

        if (current.ReleaseGateStatus == ReleaseGateStatus.Blocked)
        {
            throw new InvalidOperationException(
                "[错误] 当前输出被发布门禁阻断，不能通过人工确认绕过。请先修复问题并重新输出。");
        }

        var confirmed = new OutputRunManifest
        {
            FormatVersion = current.FormatVersion,
            ManifestId = current.ManifestId,
            CreatedAtUtc = current.CreatedAtUtc,
            IsComplete = current.IsComplete,
            RequestedFileCount = current.RequestedFileCount,
            RequestedEntryCount = current.RequestedEntryCount,
            WrittenFileCount = current.WrittenFileCount,
            VerifiedEntryCount = current.VerifiedEntryCount,
            IssueCount = current.IssueCount,
            Files = current.Files,
            ReleaseGateStatus = current.ReleaseGateStatus,
            ErrorCount = current.ErrorCount,
            WarningCount = current.WarningCount,
            NeedsReviewCount = current.NeedsReviewCount,
            BlockingErrorCount = current.BlockingErrorCount,
            HistoricalInheritedErrorCount = current.HistoricalInheritedErrorCount,
            BlockingReasons = current.BlockingReasons,
            RequiresConfirmationReason = current.RequiresConfirmationReason,
            GateReasons = current.GateReasons,
            Confirmation = new OutputManifestConfirmation
            {
                ManifestId = current.ManifestId,
                ConfirmedAtUtc = DateTime.UtcNow,
                AcknowledgedWarningCount = acknowledgedWarningCount,
                AcknowledgedHistoricalInheritedErrorCount = acknowledgedHistoricalInheritedErrorCount,
            },
        };

        Write(outputRoot, confirmed);
        return confirmed;
    }

    /// <summary>
    /// 读取最近一次输出任务清单；不存在时返回 null。
    ///
    /// 兼容策略：
    ///   - FormatVersion 1（第3轮之前的旧清单）没有门禁信息，无法验证 QA 结果，
    ///     因此归一化为 <see cref="ReleaseGateStatus.Blocked"/>（必须重新输出后再部署）；
    ///   - FormatVersion 2 = 当前版本。
    /// </summary>
    public static OutputRunManifest? Load(string outputRoot)
    {
        var path = GetManifestPath(outputRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<OutputRunManifest>(File.ReadAllText(path), SerializerOptions);
            if (manifest is null || manifest.Files is null)
            {
                throw new JsonException("输出清单格式无效。");
            }

            if (manifest.FormatVersion < OutputManifestVersions.Legacy
                || manifest.FormatVersion > OutputManifestVersions.Current)
            {
                throw new JsonException($"不支持的输出清单版本: {manifest.FormatVersion}");
            }

            return manifest.IsLegacy ? NormalizeLegacy(manifest) : manifest;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"[错误] 输出清单读取失败: {ex.Message}", ex);
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>原子写入清单（临时文件 + 覆盖替换）。</summary>
    private static void Write(string outputRoot, OutputRunManifest manifest)
    {
        Directory.CreateDirectory(outputRoot);
        var path = GetManifestPath(outputRoot);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, SerializerOptions);
            File.WriteAllText(tempPath, json, new System.Text.UTF8Encoding(false));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static OutputManifestGateReason ToRecord(ReleaseGateReason reason) => new()
    {
        Kind = reason.Kind,
        Code = reason.Code,
        Severity = reason.Severity,
        Provenance = reason.Provenance,
        Count = reason.Count,
        Escalation = reason.Escalation,
        Message = reason.Message,
        Samples = reason.Samples.Select(s => s.ToString()).ToArray(),
    };

    /// <summary>
    /// 旧版清单归一化：缺少门禁信息 → Blocked，并给出可解释原因。
    /// </summary>
    private static OutputRunManifest NormalizeLegacy(OutputRunManifest manifest)
    {
        const string message =
            "旧版输出清单（FormatVersion 1）不包含发布门禁信息，无法验证 QA 结果，已按 Blocked 处理。"
            + "请重新执行汉化或「审核后重新输出」以生成新版清单。";

        return new OutputRunManifest
        {
            FormatVersion = manifest.FormatVersion,
            ManifestId = manifest.ManifestId,
            CreatedAtUtc = manifest.CreatedAtUtc,
            IsComplete = manifest.IsComplete,
            RequestedFileCount = manifest.RequestedFileCount,
            RequestedEntryCount = manifest.RequestedEntryCount,
            WrittenFileCount = manifest.WrittenFileCount,
            VerifiedEntryCount = manifest.VerifiedEntryCount,
            IssueCount = manifest.IssueCount,
            Files = manifest.Files,
            ReleaseGateStatus = ReleaseGateStatus.Blocked,
            BlockingReasons = new[] { message },
            GateReasons = new[]
            {
                new OutputManifestGateReason
                {
                    Kind = ReleaseGateReasonKinds.LegacyManifest,
                    Code = "*",
                    Severity = ValidationSeverity.Error,
                    Count = 1,
                    Escalation = ReleaseGateStatus.Blocked,
                    Message = message,
                },
            },
        };
    }
}
