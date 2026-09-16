using LimbusTranslator.Core.Release;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 部署结果（含部署事务 / 回滚信息）。
/// </summary>
public sealed class DeployResult
{
    /// <summary>成功替换过的目标文件数（失败回滚后这些修改已不再生效）</summary>
    public int DeployedCount { get; init; }

    /// <summary>备份的文件数</summary>
    public int BackedUpCount { get; init; }

    /// <summary>备份目录</summary>
    public required string BackupDir { get; init; }

    /// <summary>目标目录</summary>
    public required string TargetDir { get; init; }

    /// <summary>本次尝试替换过的文件相对路径列表</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>是否使用本轮已核验输出清单部署（第3轮起恒为 true）</summary>
    public bool UsedOutputManifest { get; init; }

    /// <summary>本轮输出是否完整；为 false 时 Files 仍均经过逐文件校验</summary>
    public bool IsOutputComplete { get; init; }

    /// <summary>本次部署时的发布门禁状态</summary>
    public ReleaseGateStatus ReleaseGateStatus { get; init; } = ReleaseGateStatus.Passed;

    /// <summary>本次部署使用的清单 Id</summary>
    public string ManifestId { get; init; } = string.Empty;

    // ---------- 第3.5轮：部署事务 ----------

    /// <summary>部署事务状态</summary>
    public DeploymentStatus Status { get; init; } = DeploymentStatus.Succeeded;

    /// <summary>已恢复为部署前状态的文件数</summary>
    public int RolledBackCount { get; init; }

    /// <summary>回滚是否全部成功</summary>
    public bool RollbackSucceeded => Status != DeploymentStatus.FailedRollbackIncomplete;

    /// <summary>导致部署失败的相对路径（成功时为 null）</summary>
    public string? FailedRelativePath { get; init; }

    /// <summary>部署阶段的错误信息</summary>
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    /// <summary>回滚阶段的错误信息</summary>
    public IReadOnlyList<string> RollbackErrors { get; init; } = Array.Empty<string>();

    /// <summary>回滚后仍未恢复的相对路径（高危状态时才非空）</summary>
    public IReadOnlyList<string> UnrecoveredRelativePaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 一键部署服务（含“预检查 + 完整备份 + 受控写入 + 异常自动回滚”）。
///
/// 顺序（第3.5轮）：
///   1. 前置检查：目录、清单、完整性、ReleaseGate、人工确认（不通过直接抛错，0 个目标被修改）
///   2. Preflight：构造 DeploymentPlan（源文件存在、路径合法、无重复目标、数量一致）
///   3. 备份：在修改任何目标文件之前，完成全部已存在目标的备份并校验
///   4. 写入：逐个 源文件 → 同目录 .deploytmp → 校验长度 → 原子替换目标
///   5. 异常：逆序回滚已修改文件（恢复备份 / 删除本次新建），并显式报告回滚是否完整
///
/// 能力边界（重要）：
///   - 这里的“事务”是**可恢复的普通异常**（IOException / 业务失败）级别，不是数据库式 ACID；
///   - 无法防御突然断电、系统崩溃、进程被强制终止、磁盘物理损坏：
///     这些情况下仍可能留下部分部署状态（需要未来的 Deployment Journal 才能解决）。
///
/// 已知未修问题（第0轮记录，另行处理）：
///   - 本实现已解决“中途失败不回滚”，但不提供跨进程崩溃恢复。
/// </summary>
public static class DeployService
{
    /// <summary>部署使用的临时文件后缀（与目标文件同目录，保证同卷替换）</summary>
    public const string TempSuffix = ".deploytmp";

    /// <summary>
    /// 部署汉化文件到游戏目录。
    /// </summary>
    /// <param name="outputRoot">data/output/ 目录（源）</param>
    /// <param name="gameChineseDir">游戏中文目录（目标，如 Lang/LLC_zh-CN）</param>
    /// <param name="backupRoot">备份根目录（如 data/backup）</param>
    /// <param name="fileOperations">文件系统操作（测试缝；null 使用系统实现）</param>
    public static DeployResult Deploy(
        string outputRoot,
        string gameChineseDir,
        string backupRoot,
        IDeployFileOperations? fileOperations = null)
    {
        var ops = fileOperations ?? SystemDeployFileOperations.Instance;

        if (!Directory.Exists(outputRoot))
        {
            throw new DirectoryNotFoundException($"[错误] 输出目录不存在: {outputRoot}");
        }
        if (!Directory.Exists(gameChineseDir))
        {
            throw new DirectoryNotFoundException($"[错误] 游戏汉化目录不存在: {gameChineseDir}");
        }

        var manifest = OutputManifestService.Load(outputRoot);
        if (manifest is null)
        {
            // 没有清单就无法验证发布门禁状态：拒绝部署，避免“删掉清单即可绕过门禁”
            throw new InvalidOperationException(
                "[错误] 未找到输出清单（.limbus-output.manifest），无法确认发布门禁状态，已拒绝部署。"
                + "请先执行「开始汉化」或「审核后重新输出」。");
        }

        if (!manifest.IsComplete)
        {
            throw new InvalidOperationException(
                $"[错误] 最近一次输出任务未完成核验（已核验 {manifest.VerifiedEntryCount}/{manifest.RequestedEntryCount} 条，问题 {manifest.IssueCount} 条），已拒绝部署。请先完成汉化、审核后重新输出或从缓存恢复 output。");
        }

        // ReleaseGate：Blocked 永远拒绝；RequiresConfirmation 必须绑定当前清单的人工确认
        if (manifest.ReleaseGateStatus == ReleaseGateStatus.Blocked)
        {
            var reasons = manifest.BlockingReasons.Count > 0
                ? string.Join("；", manifest.BlockingReasons)
                : "存在阻断性 QA 错误";
            throw new InvalidOperationException($"[错误] 发布门禁阻断，已拒绝部署：{reasons}");
        }

        if (manifest.ReleaseGateStatus == ReleaseGateStatus.RequiresConfirmation && !manifest.HasValidConfirmation)
        {
            var reason = string.IsNullOrWhiteSpace(manifest.RequiresConfirmationReason)
                ? "存在需要人工确认的 QA 问题"
                : manifest.RequiresConfirmationReason;
            throw new InvalidOperationException(
                $"[错误] 本轮输出需要人工确认后才能部署：{reason}。"
                + "请在界面勾选「我已知晓这些警告，继续部署」后重试。");
        }

        // Preflight：先构造完整计划，任何一项不通过都不会修改目标文件
        var plan = BuildPlan(outputRoot, gameChineseDir, backupRoot, manifest, ops);

        // 备份阶段：必须在第一次目标写入之前全部完成
        var backedUpCount = BackupAll(plan, ops);

        // 写入阶段：临时文件 + 原子替换，异常时逆序回滚
        return ApplyPlan(plan, manifest, backedUpCount, ops);
    }

    /// <summary>
    /// 构造部署计划（Preflight）。任何一项检查失败都会抛异常，且不会修改任何目标文件。
    /// </summary>
    public static DeploymentPlan BuildPlan(
        string outputRoot,
        string gameChineseDir,
        string backupRoot,
        OutputRunManifest manifest,
        IDeployFileOperations? fileOperations = null)
    {
        var ops = fileOperations ?? SystemDeployFileOperations.Instance;

        var relativePaths = manifest.Files
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 文件数量一致性：清单声明的预期文件数与实际文件列表必须一致
        if (manifest.RequestedFileCount > 0 && relativePaths.Count != manifest.RequestedFileCount)
        {
            throw new InvalidOperationException(
                $"[错误] 输出清单文件数与预期不一致（清单 {relativePaths.Count} / 预期 {manifest.RequestedFileCount}），已拒绝部署以保持一致性。");
        }

        if (relativePaths.Count == 0)
        {
            throw new InvalidOperationException(
                "[错误] 最近一次输出任务没有通过核验的文件，无法部署。请先完成汉化、重新输出或从缓存恢复输出。");
        }

        var manifestId = string.IsNullOrWhiteSpace(manifest.ManifestId) ? "nomanifestid" : manifest.ManifestId;
        var backupDirName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{manifestId[..Math.Min(8, manifestId.Length)]}";
        var backupDir = Path.Combine(backupRoot, backupDirName);

        var entries = new List<DeploymentPlanEntry>();
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relative in relativePaths)
        {
            var sourcePath = ResolvePathUnderRoot(outputRoot, relative);
            var destinationPath = ResolvePathUnderRoot(gameChineseDir, relative);

            if (!ops.FileExists(sourcePath))
            {
                throw new InvalidOperationException($"[错误] 输出清单中的文件不存在: {relative}");
            }

            if (!destinations.Add(destinationPath))
            {
                throw new InvalidOperationException($"[错误] 输出清单存在重复目标路径: {relative}");
            }

            var exists = ops.FileExists(destinationPath);
            entries.Add(new DeploymentPlanEntry
            {
                RelativePath = relative,
                SourcePath = sourcePath,
                DestinationPath = destinationPath,
                DestinationOriginallyExists = exists,
                // 目标原本不存在时不伪造空备份
                BackupPath = exists ? ResolvePathUnderRoot(backupDir, relative) : null,
                TempPath = destinationPath + TempSuffix,
            });
        }

        return new DeploymentPlan
        {
            OutputRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputRoot)),
            GameChineseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameChineseDir)),
            BackupRoot = backupRoot,
            BackupDir = backupDir,
            ManifestId = manifest.ManifestId,
            Entries = entries,
        };
    }

    /// <summary>
    /// 备份阶段：为所有“部署前已存在”的目标创建备份并校验。
    /// 任何备份失败都会抛异常终止部署 —— 此时目标文件尚未被修改。
    /// </summary>
    private static int BackupAll(DeploymentPlan plan, IDeployFileOperations ops)
    {
        var backedUp = 0;
        foreach (var entry in plan.Entries)
        {
            if (!entry.DestinationOriginallyExists || entry.BackupPath is null)
            {
                continue;
            }

            try
            {
                ops.EnsureDirectory(Path.GetDirectoryName(entry.BackupPath)!);
                ops.CopyFile(entry.DestinationPath, entry.BackupPath, overwrite: true);

                // 备份成功校验：长度必须一致（避免“看起来有备份但其实不完整”）
                if (ops.GetFileLength(entry.BackupPath) != ops.GetFileLength(entry.DestinationPath))
                {
                    throw new IOException("备份文件大小与目标不一致");
                }

                backedUp++;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[错误] 备份失败（{entry.RelativePath}），已终止部署且未修改任何目标文件: {ex.Message}", ex);
            }
        }

        return backedUp;
    }


    /// <summary>
    /// 写入阶段：逐个“源 → 同目录临时文件 → 校验 → 原子替换”，异常时逆序回滚。
    /// </summary>
    private static DeployResult ApplyPlan(
        DeploymentPlan plan,
        OutputRunManifest manifest,
        int backedUpCount,
        IDeployFileOperations ops)
    {
        var modified = new List<DeploymentPlanEntry>();
        var replaced = new List<string>();
        DeploymentPlanEntry? current = null;

        try
        {
            foreach (var entry in plan.Entries)
            {
                current = entry;

                // 清理上一次异常可能残留的临时文件
                if (ops.FileExists(entry.TempPath))
                {
                    ops.DeleteFile(entry.TempPath);
                }

                ops.EnsureDirectory(Path.GetDirectoryName(entry.DestinationPath)!);
                ops.CopyFile(entry.SourcePath, entry.TempPath, overwrite: true);

                if (ops.GetFileLength(entry.TempPath) != ops.GetFileLength(entry.SourcePath))
                {
                    throw new IOException("临时文件大小与源文件不一致");
                }

                ops.MoveFile(entry.TempPath, entry.DestinationPath, overwrite: true);
                modified.Add(entry);
                replaced.Add(entry.RelativePath);
            }
        }
        catch (Exception ex)
        {
            return Rollback(plan, manifest, backedUpCount, ops, modified, replaced, current, ex);
        }

        CleanupTempFiles(plan, ops);
        return new DeployResult
        {
            DeployedCount = replaced.Count,
            BackedUpCount = backedUpCount,
            BackupDir = plan.BackupDir,
            TargetDir = plan.GameChineseDir,
            Files = replaced,
            UsedOutputManifest = true,
            IsOutputComplete = manifest.IsComplete,
            ReleaseGateStatus = manifest.ReleaseGateStatus,
            ManifestId = manifest.ManifestId,
            Status = DeploymentStatus.Succeeded,
            RolledBackCount = 0,
        };
    }

    /// <summary>
    /// 回滚：按修改顺序的**逆序**恢复已修改文件。
    /// 目标原本存在 → 用备份恢复；目标原本不存在 → 删除本次新建。
    /// 回滚本身失败会被如实记录（不假设回滚一定成功）。
    /// </summary>
    private static DeployResult Rollback(
        DeploymentPlan plan,
        OutputRunManifest manifest,
        int backedUpCount,
        IDeployFileOperations ops,
        List<DeploymentPlanEntry> modified,
        List<string> replaced,
        DeploymentPlanEntry? current,
        Exception failure)
    {
        var rollbackTargets = new List<DeploymentPlanEntry>(modified);

        // 防御：如果失败发生在“替换”这一步之后（临时文件已消失），该目标也可能已被改写。
        if (current is not null
            && !rollbackTargets.Contains(current)
            && !ops.FileExists(current.TempPath))
        {
            rollbackTargets.Add(current);
        }

        var rollbackErrors = new List<string>();
        var unrecovered = new List<string>();
        var rolledBack = 0;

        for (var i = rollbackTargets.Count - 1; i >= 0; i--)
        {
            var entry = rollbackTargets[i];
            try
            {
                if (entry.DestinationOriginallyExists && entry.BackupPath is not null)
                {
                    ops.CopyFile(entry.BackupPath, entry.DestinationPath, overwrite: true);
                }
                else if (ops.FileExists(entry.DestinationPath))
                {
                    ops.DeleteFile(entry.DestinationPath);
                }

                rolledBack++;
            }
            catch (Exception rollbackEx)
            {
                rollbackErrors.Add($"{entry.RelativePath}: {rollbackEx.Message}");
                unrecovered.Add(entry.RelativePath);
            }
        }

        CleanupTempFiles(plan, ops);

        var status = rollbackErrors.Count == 0
            ? DeploymentStatus.FailedRolledBack
            : DeploymentStatus.FailedRollbackIncomplete;

        return new DeployResult
        {
            DeployedCount = replaced.Count,
            BackedUpCount = backedUpCount,
            BackupDir = plan.BackupDir,
            TargetDir = plan.GameChineseDir,
            Files = replaced,
            UsedOutputManifest = true,
            IsOutputComplete = manifest.IsComplete,
            ReleaseGateStatus = manifest.ReleaseGateStatus,
            ManifestId = manifest.ManifestId,
            Status = status,
            RolledBackCount = rolledBack,
            FailedRelativePath = current?.RelativePath,
            Errors = new[] { failure.Message },
            RollbackErrors = rollbackErrors,
            UnrecoveredRelativePaths = unrecovered,
        };
    }

    /// <summary>尽力清理本次部署产生的临时文件（不留下 .deploytmp 垃圾）。</summary>
    private static void CleanupTempFiles(DeploymentPlan plan, IDeployFileOperations ops)
    {
        foreach (var entry in plan.Entries)
        {
            try
            {
                if (ops.FileExists(entry.TempPath))
                {
                    ops.DeleteFile(entry.TempPath);
                }
            }
            catch
            {
                // 清理失败不影响部署结论（临时文件不会参与部署）
            }
        }
    }

    /// <summary>把相对路径解析为受限于根目录的绝对路径（禁止路径穿越）。</summary>
    private static string ResolvePathUnderRoot(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException($"[错误] 非法相对路径: {relative}");
        }

        var rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(rootFullPath, relative));
        if (!candidate.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"[错误] 路径超出目标目录: {relative}");
        }

        return candidate;
    }
}
