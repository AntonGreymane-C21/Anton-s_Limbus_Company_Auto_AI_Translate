namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 单个部署条目（部署计划的最小单元）。
/// </summary>
public sealed class DeploymentPlanEntry
{
    /// <summary>相对路径（与 output / 清单一致）</summary>
    public required string RelativePath { get; init; }

    /// <summary>源文件绝对路径（data/output 下）</summary>
    public required string SourcePath { get; init; }

    /// <summary>目标文件绝对路径（游戏汉化目录下）</summary>
    public required string DestinationPath { get; init; }

    /// <summary>部署前目标文件是否已存在（决定回滚方式是“恢复”还是“删除”）</summary>
    public required bool DestinationOriginallyExists { get; init; }

    /// <summary>备份路径（目标原本不存在时为 null，不伪造空备份）</summary>
    public string? BackupPath { get; init; }

    /// <summary>本次部署使用的临时文件路径（同目录 .deploytmp）</summary>
    public required string TempPath { get; init; }
}

/// <summary>
/// 部署计划：在修改任何目标文件之前，先构造出的完整计划。
/// Preflight 全部通过后才会进入备份与写入阶段。
/// </summary>
public sealed class DeploymentPlan
{
    /// <summary>输出根目录</summary>
    public required string OutputRoot { get; init; }

    /// <summary>游戏汉化目录（目标根）</summary>
    public required string GameChineseDir { get; init; }

    /// <summary>备份根目录</summary>
    public required string BackupRoot { get; init; }

    /// <summary>本次部署的唯一备份目录</summary>
    public required string BackupDir { get; init; }

    /// <summary>本轮清单 Id（备份目录命名与审计用）</summary>
    public required string ManifestId { get; init; }

    /// <summary>部署条目（顺序即写入顺序）</summary>
    public required IReadOnlyList<DeploymentPlanEntry> Entries { get; init; }

    /// <summary>目标原本已存在的条目数（需要备份的数量）</summary>
    public int ExistingDestinationCount => Entries.Count(e => e.DestinationOriginallyExists);
}

/// <summary>
/// 部署事务状态（显式区分“部署失败但回滚完成”与“回滚也未全部完成”）。
/// </summary>
public enum DeploymentStatus
{
    /// <summary>全部成功</summary>
    Succeeded,

    /// <summary>部署失败，但已把已修改文件恢复为部署前状态</summary>
    FailedRolledBack,

    /// <summary>部署失败，且回滚未全部完成（高危：可能与部署前状态不一致）</summary>
    FailedRollbackIncomplete,
}
