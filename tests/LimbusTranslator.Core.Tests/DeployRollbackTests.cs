using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第3.5轮：部署事务与回滚测试。
/// 使用临时目录 + 可控文件系统测试缝（<see cref="FakeDeployFileOperations"/>）稳定制造“第 N 个文件失败”。
/// 全部在临时目录中进行，绝不访问真实游戏目录。
/// </summary>
public class DeployRollbackTests
{
    /// <summary>
    /// 可注入失败的文件系统实现：生产行为委托给系统实现，仅在匹配到注入点时抛异常。
    /// </summary>
    private sealed class FakeDeployFileOperations : IDeployFileOperations
    {
        private readonly IDeployFileOperations _inner = SystemDeployFileOperations.Instance;

        public int CopyFileCallCount { get; private set; }

        public int MoveFileCallCount { get; private set; }

        /// <summary>返回非 null 时抛出该异常（参数：源路径、目标路径、是否覆盖）</summary>
        public Func<string, string, bool, Exception?>? CopyFileHook { get; init; }

        /// <summary>返回非 null 时抛出该异常（参数：源路径、目标路径、是否覆盖）</summary>
        public Func<string, string, bool, Exception?>? MoveFileHook { get; init; }

        public bool FileExists(string path) => _inner.FileExists(path);

        public long GetFileLength(string path) => _inner.GetFileLength(path);

        public void EnsureDirectory(string directoryPath) => _inner.EnsureDirectory(directoryPath);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        {
            CopyFileCallCount++;
            var error = CopyFileHook?.Invoke(sourcePath, destinationPath, overwrite);
            if (error is not null)
            {
                throw error;
            }

            _inner.CopyFile(sourcePath, destinationPath, overwrite);
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            MoveFileCallCount++;
            var error = MoveFileHook?.Invoke(sourcePath, destinationPath, overwrite);
            if (error is not null)
            {
                throw error;
            }

            _inner.MoveFile(sourcePath, destinationPath, overwrite);
        }

        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_ROLLBACK_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // 清理失败可忽略（临时目录）
            }
        }
    }

    /// <summary>写入一份 Passed 门禁清单（含指定文件列表）。</summary>
    private static OutputRunManifest WriteManifest(string output, params string[] files)
    {
        var gate = new ReleaseGateResult
        {
            Status = ReleaseGateStatus.Passed,
            EvaluatedEntryCount = files.Length,
            ErrorCount = 0,
            WarningCount = 0,
            NeedsReviewCount = 0,
            BlockingErrorCount = 0,
            HistoricalInheritedErrorCount = 0,
            Reasons = Array.Empty<ReleaseGateReason>(),
        };

        return OutputManifestService.Save(
            output,
            new OutputMergeResult
            {
                RequestedFileCount = files.Length,
                RequestedEntryCount = files.Length,
                WrittenEntryCount = files.Length,
                VerifiedEntryCount = files.Length,
                Files = files,
                Issues = Array.Empty<OutputMergeIssue>(),
            },
            gate);
    }

    private static readonly string[] ThreeFiles = { "A.json", "B.json", "C.json" };

    /// <summary>
    /// 准备三个文件：source 内容 new-X，target 内容 old-X（模拟游戏目录已有旧汉化）。
    /// </summary>
    private static (string Output, string Target, string Backup) PrepareThree(params string[] files)
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();

        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            File.WriteAllText(Path.Combine(output, file), $"new-{name}");
            File.WriteAllText(Path.Combine(target, file), $"old-{name}");
        }

        WriteManifest(output, files);
        return (output, target, backup);
    }

    private static string ReadTarget(string target, string file) => File.ReadAllText(Path.Combine(target, file));

    private static void AssertNoTempFiles(string target)
        => Assert.Empty(Directory.GetFiles(target, "*" + DeployService.TempSuffix, SearchOption.AllDirectories));

    // ---------- 1. Preflight：Source 不存在 ----------

    [Fact]
    public void Preflight失败_Source不存在_0个目标被修改()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            // 删除第 2 个源文件，模拟输出目录被破坏
            File.Delete(Path.Combine(output, "B.json"));
            var ops = new FakeDeployFileOperations();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, ops));

            Assert.Contains("输出清单中的文件不存在", ex.Message);
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
            Assert.Equal("old-B", ReadTarget(target, "B.json"));
            Assert.Equal("old-C", ReadTarget(target, "C.json"));
            Assert.Equal(0, ops.MoveFileCallCount);           // 0 个目标被写入
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void Preflight失败_清单文件数与预期不一致_0个目标被修改()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "A.json"), "new-A");
            File.WriteAllText(Path.Combine(target, "A.json"), "old-A");

            var gate = new ReleaseGateResult
            {
                Status = ReleaseGateStatus.Passed,
                EvaluatedEntryCount = 2,
                ErrorCount = 0,
                WarningCount = 0,
                NeedsReviewCount = 0,
                BlockingErrorCount = 0,
                HistoricalInheritedErrorCount = 0,
                Reasons = Array.Empty<ReleaseGateReason>(),
            };
            OutputManifestService.Save(
                output,
                new OutputMergeResult
                {
                    RequestedFileCount = 2,          // 预期 2 个文件，实际只有 1 个
                    RequestedEntryCount = 2,
                    WrittenEntryCount = 1,
                    VerifiedEntryCount = 1,
                    Files = new[] { "A.json" },
                    Issues = Array.Empty<OutputMergeIssue>(),
                },
                gate);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, new FakeDeployFileOperations()));

            Assert.Contains("文件数与预期不一致", ex.Message);
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void Preflight失败_路径穿越_0个目标被修改()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "ok.json"), "new-ok");
            File.WriteAllText(Path.Combine(target, "ok.json"), "old-ok");
            // 清单中混入穿越路径（同时把一个正常文件也不部署）
            File.WriteAllText(Path.Combine(output, ".._evil.json"), "x");
            WriteManifest(output, "ok.json", "../evil.json");

            Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, new FakeDeployFileOperations()));

            Assert.Equal("old-ok", ReadTarget(target, "ok.json"));
            Assert.False(File.Exists(Path.Combine(target, "..", "evil.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 2. Backup 阶段失败 ----------

    [Fact]
    public void 备份阶段失败_0个目标被修改()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            // 第 2 个备份（B.json 的备份写入）抛异常
            var ops = new FakeDeployFileOperations
            {
                CopyFileHook = (_, destination, _) =>
                    destination.StartsWith(backup, StringComparison.OrdinalIgnoreCase)
                    && destination.EndsWith("B.json", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟备份失败")
                        : null,
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, ops));

            Assert.Contains("备份失败", ex.Message);
            Assert.Contains("未修改任何目标文件", ex.Message);
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
            Assert.Equal("old-B", ReadTarget(target, "B.json"));
            Assert.Equal("old-C", ReadTarget(target, "C.json"));
            Assert.Equal(0, ops.MoveFileCallCount);
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 3. 部署第一个文件失败 ----------

    [Fact]
    public void 部署第一个文件失败_游戏目录保持原状()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            var ops = new FakeDeployFileOperations
            {
                MoveFileHook = (_, destination, _) =>
                    destination.EndsWith("A.json", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟替换失败")
                        : null,
            };

            var result = DeployService.Deploy(output, target, backup, ops);

            Assert.Equal(DeploymentStatus.FailedRolledBack, result.Status);
            Assert.Equal("A.json", result.FailedRelativePath);
            Assert.Equal(0, result.RolledBackCount);
            Assert.True(result.RollbackSucceeded);
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
            Assert.Equal("old-B", ReadTarget(target, "B.json"));
            Assert.Equal("old-C", ReadTarget(target, "C.json"));
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 4. 部署中间文件失败 ----------

    [Fact]
    public void 部署中间文件失败_已覆盖文件全部恢复()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            var ops = new FakeDeployFileOperations
            {
                MoveFileHook = (_, destination, _) =>
                    destination.EndsWith("C.json", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟第 3 个文件替换失败")
                        : null,
            };

            var result = DeployService.Deploy(output, target, backup, ops);

            Assert.Equal(DeploymentStatus.FailedRolledBack, result.Status);
            Assert.Equal("C.json", result.FailedRelativePath);
            Assert.Equal(2, result.DeployedCount);        // 曾经替换过 A、B
            Assert.Equal(2, result.RolledBackCount);      // 已全部恢复
            Assert.True(result.RollbackSucceeded);
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
            Assert.Equal("old-B", ReadTarget(target, "B.json"));
            Assert.Equal("old-C", ReadTarget(target, "C.json"));
            AssertNoTempFiles(target);

            // 备份保留（用户以后可手工恢复）
            Assert.True(Directory.Exists(result.BackupDir));
            Assert.Equal("old-A", File.ReadAllText(Path.Combine(result.BackupDir, "A.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 5. 最后一个文件失败 ----------

    [Fact]
    public void 部署最后一个文件失败_前序文件全部恢复()
    {
        var files = new[] { "A.json", "B.json", "C.json", "D.json" };
        var (output, target, backup) = PrepareThree(files);
        try
        {
            var ops = new FakeDeployFileOperations
            {
                MoveFileHook = (_, destination, _) =>
                    destination.EndsWith("D.json", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟最后一个文件替换失败")
                        : null,
            };

            var result = DeployService.Deploy(output, target, backup, ops);

            Assert.Equal(DeploymentStatus.FailedRolledBack, result.Status);
            Assert.Equal("D.json", result.FailedRelativePath);
            Assert.Equal(3, result.RolledBackCount);
            foreach (var file in files)
            {
                Assert.Equal($"old-{Path.GetFileNameWithoutExtension(file)}", ReadTarget(target, file));
            }
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 6. 目标原本不存在 → 回滚后应重新不存在 ----------

    [Fact]
    public void 目标原本不存在_回滚后应重新不存在()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            foreach (var file in ThreeFiles)
            {
                File.WriteAllText(Path.Combine(output, file), $"new-{Path.GetFileNameWithoutExtension(file)}");
            }

            // 目标目录只有 A、C（B 是本次新文件）
            File.WriteAllText(Path.Combine(target, "A.json"), "old-A");
            File.WriteAllText(Path.Combine(target, "C.json"), "old-C");
            WriteManifest(output, ThreeFiles);

            var ops = new FakeDeployFileOperations
            {
                MoveFileHook = (_, destination, _) =>
                    destination.EndsWith("C.json", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟替换失败")
                        : null,
            };

            var result = DeployService.Deploy(output, target, backup, ops);

            Assert.Equal(DeploymentStatus.FailedRolledBack, result.Status);
            Assert.False(File.Exists(Path.Combine(target, "B.json")));   // 新建的文件被删除
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
            Assert.Equal("old-C", ReadTarget(target, "C.json"));
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 7. 回滚自身失败 ----------

    [Fact]
    public void 回滚自身失败_报告为RollbackPartiallyFailed并列出未恢复文件()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            var targetB = Path.Combine(target, "B.json");
            var ops = new FakeDeployFileOperations
            {
                MoveFileHook = (_, destination, _) =>
                    destination.EndsWith("C.json", StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟替换失败")
                        : null,

                // 回滚 B.json 时（把备份复制回目标）抛异常
                CopyFileHook = (_, destination, _) =>
                    string.Equals(destination, targetB, StringComparison.OrdinalIgnoreCase)
                        ? new IOException("模拟恢复失败")
                        : null,
            };

            var result = DeployService.Deploy(output, target, backup, ops);

            Assert.Equal(DeploymentStatus.FailedRollbackIncomplete, result.Status);
            Assert.False(result.RollbackSucceeded);
            Assert.Contains("B.json", result.UnrecoveredRelativePaths);
            Assert.NotEmpty(result.RollbackErrors);
            Assert.Contains(result.RollbackErrors, e => e.Contains("B.json"));
            // A 已恢复，B 未恢复（保持被覆盖后的内容），C 未被改动
            Assert.Equal("old-A", ReadTarget(target, "A.json"));
            Assert.Equal("new-B", ReadTarget(target, "B.json"));
            Assert.Equal("old-C", ReadTarget(target, "C.json"));
            // 备份目录可用于人工恢复
            Assert.True(Directory.Exists(result.BackupDir));
            Assert.Equal("old-B", File.ReadAllText(Path.Combine(result.BackupDir, "B.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    // ---------- 8. 全部成功 ----------

    [Fact]
    public void 全部成功_DeployedCount正确且无回滚()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            var ops = new FakeDeployFileOperations();

            var result = DeployService.Deploy(output, target, backup, ops);

            Assert.Equal(DeploymentStatus.Succeeded, result.Status);
            Assert.Equal(3, result.DeployedCount);
            Assert.Equal(3, result.BackedUpCount);
            Assert.Equal(0, result.RolledBackCount);
            Assert.True(result.RollbackSucceeded);
            Assert.Null(result.FailedRelativePath);
            Assert.Empty(result.Errors);
            Assert.Equal(new[] { "A.json", "B.json", "C.json" }, result.Files);
            foreach (var file in ThreeFiles)
            {
                Assert.Equal($"new-{Path.GetFileNameWithoutExtension(file)}", ReadTarget(target, file));
            }
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }


    // ---------- 9 / 10. 门禁前置检查必须在文件写阶段之前生效 ----------

    /// <summary>写入指定门禁结论的清单。</summary>
    private static OutputRunManifest WriteManifestWithStatus(
        string output,
        ReleaseGateStatus status,
        params string[] files)
    {
        var reasons = status switch
        {
            ReleaseGateStatus.Blocked => new[]
            {
                new ReleaseGateReason
                {
                    Kind = ReleaseGateReasonKinds.NewTranslationHardError,
                    Code = ValidationIssueCodes.PlaceholderMismatch,
                    Severity = ValidationSeverity.Error,
                    Provenance = TranslationSource.AI,
                    Count = 1,
                    Escalation = ReleaseGateStatus.Blocked,
                    Message = "AI 译文存在 1 条结构安全错误（PLACEHOLDER_MISMATCH），禁止直接部署。",
                },
            },
            ReleaseGateStatus.RequiresConfirmation => new[]
            {
                new ReleaseGateReason
                {
                    Kind = ReleaseGateReasonKinds.NeedsReview,
                    Code = "*",
                    Severity = ValidationSeverity.Warning,
                    Count = 1,
                    Escalation = ReleaseGateStatus.RequiresConfirmation,
                    Message = "存在 1 条未审核结果（待人工确认）。",
                },
            },
            _ => Array.Empty<ReleaseGateReason>(),
        };

        var gate = new ReleaseGateResult
        {
            Status = status,
            EvaluatedEntryCount = files.Length,
            ErrorCount = status == ReleaseGateStatus.Blocked ? 1 : 0,
            WarningCount = 0,
            NeedsReviewCount = status == ReleaseGateStatus.RequiresConfirmation ? 1 : 0,
            BlockingErrorCount = status == ReleaseGateStatus.Blocked ? 1 : 0,
            HistoricalInheritedErrorCount = 0,
            Reasons = reasons,
        };

        return OutputManifestService.Save(
            output,
            new OutputMergeResult
            {
                RequestedFileCount = files.Length,
                RequestedEntryCount = files.Length,
                WrittenEntryCount = files.Length,
                VerifiedEntryCount = files.Length,
                Files = files,
                Issues = Array.Empty<OutputMergeIssue>(),
            },
            gate);
    }

    [Fact]
    public void GateBlocked_在文件写阶段之前被拒绝()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            WriteManifestWithStatus(output, ReleaseGateStatus.Blocked, ThreeFiles);
            var ops = new FakeDeployFileOperations();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, ops));

            Assert.Contains("发布门禁阻断", ex.Message);
            Assert.Equal(0, ops.CopyFileCallCount);      // 连备份阶段都未进入
            Assert.Equal(0, ops.MoveFileCallCount);
            foreach (var file in ThreeFiles)
            {
                Assert.Equal($"old-{Path.GetFileNameWithoutExtension(file)}", ReadTarget(target, file));
            }
            AssertNoTempFiles(target);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void RequiresConfirmation未确认_0个目标被修改()
    {
        var (output, target, backup) = PrepareThree(ThreeFiles);
        try
        {
            WriteManifestWithStatus(output, ReleaseGateStatus.RequiresConfirmation, ThreeFiles);
            var ops = new FakeDeployFileOperations();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, ops));

            Assert.Contains("需要人工确认", ex.Message);
            Assert.Equal(0, ops.CopyFileCallCount);
            Assert.Equal(0, ops.MoveFileCallCount);
            foreach (var file in ThreeFiles)
            {
                Assert.Equal($"old-{Path.GetFileNameWithoutExtension(file)}", ReadTarget(target, file));
            }
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }
}

