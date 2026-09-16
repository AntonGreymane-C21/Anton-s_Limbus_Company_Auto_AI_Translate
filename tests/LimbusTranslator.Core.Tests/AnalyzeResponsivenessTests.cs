using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.1轮：GUI 响应性 / 取消 / Snapshot 隔离的自动化验收。
///
/// 全部使用 TEMP 目录 + 确定性同步（TaskCompletionSource / 阶段钩子），不依赖真实 WPF 窗口，
/// 不使用 Thread.Sleep 制造脆弱时序。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class AnalyzeResponsivenessTests : IDisposable
{
    private readonly string _workspaceRoot = Path.Combine(
        Path.GetTempPath(), "limbus_responsive_" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _productionRoot = Path.Combine(
        Path.GetTempPath(), "limbus_prod_" + Guid.NewGuid().ToString("N")[..8]);

    public AnalyzeResponsivenessTests()
    {
        Directory.CreateDirectory(_workspaceRoot);
        Directory.CreateDirectory(_productionRoot);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _workspaceRoot, _productionRoot })
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // 清理失败不影响断言
            }
        }
    }

    // ---------- 状态机（Analyze 运行中 / 取消 / 取消后 / 异常后） ----------

    [Fact]
    public void 分析开始后立即进入Analyzing且可取消()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();

        Assert.Equal(GuiPhase.Analyzing, state.Phase);
        Assert.True(state.IsBusy);
        Assert.True(state.CanCancel);
        Assert.Equal("正在分析更新", state.PhaseText);

        Assert.False(state.CanAnalyze);
        Assert.False(state.CanTranslate);
        Assert.False(state.CanChangeTranslationMode);
        Assert.False(state.CanGenerateOutput);
        Assert.False(state.CanDeploy);
    }

    [Fact]
    public void 取消期间进入Cancelling且禁止一切新操作()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();
        state.BeginCancel();

        Assert.Equal(GuiPhase.Cancelling, state.Phase);
        Assert.True(state.IsCancelling);
        Assert.False(state.CanCancel);           // 不允许重复点击取消
        Assert.False(state.CanAnalyze);
        Assert.False(state.CanTranslate);
        Assert.False(state.CanChangeTranslationMode);
        Assert.False(state.CanGenerateOutput);
        Assert.False(state.CanDeploy);
        Assert.Equal("正在取消…", state.PhaseText);
    }

    [Fact]
    public void 取消完成后恢复Idle且不显示为错误()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();
        state.BeginCancel();
        state.CompleteCancel();

        Assert.Equal(GuiPhase.Idle, state.Phase);
        Assert.False(state.IsBusy);
        Assert.True(state.LastOperationCancelled);
        Assert.Null(state.LastErrorSummary);     // 取消不是错误
        Assert.True(state.CanAnalyze);
        Assert.True(state.CanChangeTranslationMode);
    }

    [Fact]
    public void 分析异常后恢复按钮并记录可读原因()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();
        state.Fail("分析失败：请检查目录。");

        Assert.Equal(GuiPhase.Idle, state.Phase);
        Assert.False(state.IsBusy);
        Assert.False(state.LastOperationCancelled);
        Assert.Equal("分析失败：请检查目录。", state.LastErrorSummary);
        Assert.True(state.CanAnalyze);
    }

    [Fact]
    public void 翻译取消同样不算错误()
    {
        var state = new GuiWorkflowState();
        state.BeginTranslate();
        state.BeginCancel();
        Assert.Equal(GuiPhase.Cancelling, state.Phase);

        state.CompleteCancel();
        Assert.Equal(GuiPhase.Idle, state.Phase);
        Assert.Null(state.LastErrorSummary);
    }

    // ---------- 后台线程边界 + 结果一致性 + 真实阶段进度 ----------

    [Fact]
    public async Task 分析在后台线程执行且结果与直接调用生产层一致()
    {
        var workspace = GuiDemoWorkspaceBuilder.Create(_workspaceRoot);
        var configDir = Path.Combine(FindRepositoryRoot(), "config");
        var callerThread = Environment.CurrentManagedThreadId;
        var stages = new List<string>();
        var progressReports = new List<AnalyzeStageReport>();

        var service = new ProductionAnalyzeService(configDir);
        service.StageEnteredHookForTests = stages.Add;

        var result = await service.RunAsync(
            new ProductionAnalyzeRequest(
                _workspaceRoot,
                workspace.OldEnglishDir,
                workspace.OldChineseDir,
                workspace.LocalizeEnglishDir,
                TranslationMode.EnglishOnly,
                configDir),
            new SynchronousProgress<AnalyzeStageReport>(progressReports.Add));

        // ① 每个阶段都不在调用线程（测试线程 ≈ UI 线程）上执行
        Assert.NotEmpty(result.Timings);
        Assert.All(result.Timings, timing => Assert.NotEqual(callerThread, timing.ManagedThreadId));

        // ② 真实阶段都被报告（不伪造阶段）
        Assert.Contains(AnalyzeStages.ScanFiles, stages);
        Assert.Contains(AnalyzeStages.CompareEntries, stages);
        Assert.Contains(AnalyzeStages.CaptureSources, stages);
        Assert.Contains(AnalyzeStages.ExpandOldChinese, stages);
        Assert.Contains(AnalyzeStages.BuildPlan, stages);
        Assert.NotEmpty(progressReports);

        // ③ 与「直接调用生产层」的结果完全一致（同输入同输出，没有偷偷少算）
        var directWorkflow = new DiffWorkflowService(configDir);
        var directFiles = directWorkflow.AnalyzeFiles(
            workspace.OldEnglishDir, workspace.OldChineseDir, workspace.LocalizeEnglishDir);
        var directDiff = directWorkflow.Analyze(
            workspace.OldEnglishDir, workspace.OldChineseDir, workspace.LocalizeEnglishDir, null);
        var directCapture = ProductionTranslationPlanBuilder.TryCapture(
            _workspaceRoot, workspace.LocalizeEnglishDir, configDir, directDiff.NewUnits, snapshotRoot: _workspaceRoot);
        var directScope = OldChineseScopeLoader.Load(
            TranslationMode.EnglishOnly, directDiff.OldChineseUnits, directCapture, workspace.OldChineseDir, configDir);
        var directPlan = ProductionTranslationPlanBuilder.Build(
            TranslationMode.EnglishOnly, directDiff.Entries, directCapture, workspace.LocalizeEnglishDir, directScope.Units);

        Assert.Equal(directFiles.NewCount, result.FileResult.NewCount);
        Assert.Equal(directDiff.AddedCount, result.DiffResult.AddedCount);
        Assert.Equal(directDiff.ModifiedCount, result.DiffResult.ModifiedCount);
        Assert.Equal(directDiff.MissingTranslationCount, result.DiffResult.MissingTranslationCount);
        Assert.Equal(directDiff.UnchangedCount, result.DiffResult.UnchangedCount);
        Assert.Equal(directDiff.Entries.Count, result.DiffResult.Entries.Count);
        Assert.Equal(directPlan.NeedTranslate.Count, result.Plan.NeedTranslate.Count);
        Assert.Equal(directScope.Units.Count, result.OldChineseScope.Units.Count);
    }

    /// <summary>同步进度接收器（测试中用；避免 Progress&lt;T&gt; 的异步投递导致断言时序不确定）。</summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    // ---------- Snapshot 隔离 / 取消不推进 baseline / 事务式提交 ----------

    [Fact]
    public async Task 取消分析不推进快照baseline()
    {
        var workspace = GuiDemoWorkspaceBuilder.Create(_workspaceRoot);
        var configDir = Path.Combine(FindRepositoryRoot(), "config");
        var snapshotRoot = Path.Combine(_workspaceRoot, "data-root");
        Directory.CreateDirectory(snapshotRoot);

        // 预置一份 baseline（模拟已有生产快照）
        Assert.True(SourceSnapshotService.SaveBaseline(
            snapshotRoot,
            SourceLanguage.Korean,
            new[] { Unit("999", "기존 한 문장") },
            workspace.LocalizeEnglishDir));
        var beforeHash = HashOf(SourceSnapshotService.GetCurrentPath(snapshotRoot, SourceLanguage.Korean));

        using var cts = new CancellationTokenSource();
        var service = new ProductionAnalyzeService(configDir);
        service.StageEnteredHookForTests = stage =>
        {
            if (stage == AnalyzeStages.ScanFiles)
            {
                cts.Cancel();   // 用户在分析刚开始时点击取消
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.RunAsync(
                new ProductionAnalyzeRequest(
                    _workspaceRoot,
                    workspace.OldEnglishDir,
                    workspace.OldChineseDir,
                    workspace.LocalizeEnglishDir,
                    TranslationMode.KoreanEnglish,
                    configDir,
                    snapshotRoot),
                null,
                null,
                cts.Token));

        // 取消后：快照内容逐字节不变，且没有残留 .pending 文件
        Assert.Equal(beforeHash, HashOf(SourceSnapshotService.GetCurrentPath(snapshotRoot, SourceLanguage.Korean)));
        Assert.Empty(Directory.GetFiles(snapshotRoot, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public void 三语快照批量提交是事务式的()
    {
        var snapshotRoot = Path.Combine(_workspaceRoot, "batch-root");
        var units = new[] { Unit("1", "문장") };

        var saved = SourceSnapshotService.SaveBaselineBatch(snapshotRoot, new[]
        {
            (SourceLanguage.Korean, (IReadOnlyList<TranslationUnit>)units, (string?)null),
            (SourceLanguage.English, (IReadOnlyList<TranslationUnit>)units, (string?)null),
            (SourceLanguage.Japanese, (IReadOnlyList<TranslationUnit>)units, (string?)null),
        });

        Assert.Equal(3, saved.Count);
        Assert.Empty(Directory.GetFiles(snapshotRoot, "*.pending", SearchOption.AllDirectories));
        foreach (var language in new[] { SourceLanguage.Korean, SourceLanguage.English, SourceLanguage.Japanese })
        {
            Assert.True(File.Exists(SourceSnapshotService.GetCurrentPath(snapshotRoot, language)));
        }

        // 第二次提交应轮换 previous（基线与轮换语义保持）
        SourceSnapshotService.SaveBaselineBatch(snapshotRoot, new[]
        {
            (SourceLanguage.Korean, (IReadOnlyList<TranslationUnit>)units, (string?)null),
        });
        Assert.True(File.Exists(SourceSnapshotService.GetPreviousPath(snapshotRoot, SourceLanguage.Korean)));
    }

    private static TranslationUnit Unit(string recordId, string text) => new()
    {
        Key = new UnitKey { RelativeFilePath = "Demo.json", RecordId = recordId, FieldPath = "dataList[0].name" },
        FilePath = "Demo.json",
        RecordId = recordId,
        FieldPath = "dataList[0].name",
        SourceText = text,
    };

    [Fact]
    public void 快照根解析_演示与TEMP隔离而普通路径仍用生产根()
    {
        // ① Demo 工作区（含 README_DEMO.txt）⇒ 隔离到工作区
        var demoWorkspace = GuiDemoWorkspaceBuilder.Create(Path.Combine(_workspaceRoot, "demo"));
        Assert.Equal(
            Normalize(Path.GetFullPath(demoWorkspace.Root)),
            Normalize(SnapshotRootResolver.Resolve(_productionRoot, demoWorkspace.LocalizeEnglishDir)));

        // ② TEMP 下的普通 Localize 布局（无 Demo 标记）同样隔离
        var tempWorkspace = Path.Combine(_workspaceRoot, "temp-ws");
        var tempEn = Path.Combine(tempWorkspace, "Localize", "en");
        Directory.CreateDirectory(tempEn);
        Assert.Equal(
            Normalize(Path.GetFullPath(tempWorkspace)),
            Normalize(SnapshotRootResolver.Resolve(_productionRoot, tempEn)));

        // ③ 非 Localize 布局 / 目录不存在 ⇒ 退回生产根（不会误隔离）
        var plainDir = Path.Combine(_workspaceRoot, "plain", "en");
        Directory.CreateDirectory(plainDir);
        Assert.Equal(_productionRoot, SnapshotRootResolver.Resolve(_productionRoot, plainDir));
        Assert.Equal(_productionRoot, SnapshotRootResolver.Resolve(_productionRoot, Path.Combine(_workspaceRoot, "not-exists", "en")));
        Assert.Equal(_productionRoot, SnapshotRootResolver.Resolve(_productionRoot, null));

        // ④ 内部环境变量可显式覆盖（测试 / 内部工具用；GUI **不**暴露该设置）
        var overrideRoot = Path.Combine(_workspaceRoot, "override-root");
        Environment.SetEnvironmentVariable(SnapshotRootResolver.OverrideEnvironmentVariable, overrideRoot);
        try
        {
            Assert.Equal(overrideRoot, SnapshotRootResolver.Resolve(_productionRoot, plainDir));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SnapshotRootResolver.OverrideEnvironmentVariable, null);
        }
    }

    [Fact]
    public async Task 分析服务使用隔离快照根时不写生产根()
    {
        var workspace = GuiDemoWorkspaceBuilder.Create(_workspaceRoot);
        var configDir = Path.Combine(FindRepositoryRoot(), "config");
        var snapshotRoot = Path.Combine(_workspaceRoot, "isolated-snapshots");
        Directory.CreateDirectory(snapshotRoot);

        var service = new ProductionAnalyzeService(configDir);
        var result = await service.RunAsync(new ProductionAnalyzeRequest(
            _workspaceRoot,
            workspace.OldEnglishDir,
            workspace.OldChineseDir,
            workspace.LocalizeEnglishDir,
            TranslationMode.EnglishOnly,
            configDir,
            snapshotRoot));

        Assert.Equal(snapshotRoot, result.SnapshotRoot);
        Assert.True(SnapshotRootResolver.IsIsolated(_productionRoot, result.SnapshotRoot));
        Assert.True(File.Exists(SourceSnapshotService.GetCurrentPath(snapshotRoot, SourceLanguage.Korean)));
        // projectRoot（= 演示工作区）下不应产生快照目录（说明没写到非隔离位置）
        Assert.False(Directory.Exists(Path.Combine(_workspaceRoot, "data", "cache", "source_snapshots")));
    }

    private static string Normalize(string path) => path.TrimEnd(Path.DirectorySeparatorChar);

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（LimbusTranslator.slnx）");
    }
}
