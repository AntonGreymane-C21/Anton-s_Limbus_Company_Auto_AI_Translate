namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 部署事务状态 → 面向用户的中文文案（第8.85轮）。
///
/// 第三种（回滚不完整）必须显著提示：此时游戏目录可能处于新旧混合状态。
/// </summary>
public static class DeployStatusText
{
    /// <summary>状态对应的用户文案。</summary>
    public static string Describe(DeploymentStatus status) => status switch
    {
        DeploymentStatus.Succeeded => "部署成功",
        DeploymentStatus.FailedRolledBack => "部署失败，已完整回滚（游戏目录已恢复部署前状态）",
        DeploymentStatus.FailedRollbackIncomplete => "部署失败，回滚不完整，请立即检查备份目录并手动恢复！",
        _ => "未知状态",
    };

    /// <summary>是否为需要显著高亮的高危状态（回滚未完成）。</summary>
    public static bool IsCritical(DeploymentStatus status)
        => status == DeploymentStatus.FailedRollbackIncomplete;
}
