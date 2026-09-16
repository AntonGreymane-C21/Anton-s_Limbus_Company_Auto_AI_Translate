using System.Diagnostics;
using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>分析阶段（供 GUI 以真实阶段显示进度，禁止伪造）。</summary>
public static class AnalyzeStages
{
    public const string ScanFiles = "正在扫描游戏文本";
    public const string CompareEntries = "正在比较版本";
    public const string CaptureSources = "正在读取韩文/英文/日文";
    public const string ExpandOldChinese = "正在读取旧汉化";
    public const string BuildPlan = "正在生成分析结果";
    public const string ApplyToGui = "正在更新界面";
}

/// <summary>阶段进度报告（节流后回 GUI）。</summary>
public sealed record AnalyzeStageReport(string Stage, int Done, int Total, string? Detail = null)
{
    /// <summary>是否知道总数（决定 GUI 用真实百分比还是 Indeterminate）。</summary>
    public bool HasKnownTotal => Total > 0;
}

/// <summary>阶段耗时（第9.0C.1轮性能诊断；含线程 Id，用于证明不在 UI 线程）。</summary>
public sealed record AnalyzeStageTiming(string Stage, long ElapsedMs, int ItemCount, int ManagedThreadId)
{
    public string Describe()
        => $"[调试] 分析阶段：{Stage}｜{ItemCount} 项｜耗时 {ElapsedMs}ms｜ManagedThreadId={ManagedThreadId}";
}

/// <summary>分析请求。</summary>
public sealed record ProductionAnalyzeRequest(
    string ProjectRoot,
    string OldEnglishDirectory,
    string OldChineseDirectory,
    string NewEnglishDirectory,
    TranslationMode Mode,
    string? ConfigDir,
    string? SnapshotRoot = null);

/// <summary>分析结果（纯数据；GUI 只做绑定/展示，不重算）。</summary>
public sealed class ProductionAnalyzeResult
{
    public required FileDiffResult FileResult { get; init; }
    public required DiffResult DiffResult { get; init; }
    public MultilingualCaptureResult? Capture { get; init; }
    public required OldChineseScopeResult OldChineseScope { get; init; }
    public required ProductionTranslationPlan Plan { get; init; }
    public required IReadOnlyList<AnalyzeStageTiming> Timings { get; init; }
    public required string SnapshotRoot { get; init; }
    public required long TotalElapsedMs { get; init; }
}

/// <summary>
/// 生产分析服务（第9.0C.1轮）：把现有同步重任务（文件扫描 / 三语解析 / Canonical Diff /
/// 旧中文范围 / 生产计划）包装成**真正的后台执行边界**。
///
/// 约束：
///   - 只调用现有服务（<see cref="DiffWorkflowService"/> / <see cref="ProductionTranslationPlanBuilder"/> /
///     <see cref="OldChineseScopeLoader"/>），不重写任何算法；
///   - 所有重任务在 <see cref="Task.Run(Func{TResult}, CancellationToken)"/> 内执行 ⇒ **不在 UI 线程**；
///   - 阶段进度通过 <see cref="IProgress{T}"/> 上报（调用方自行节流，避免淹没消息泵）；
///   - 每个阶段前检查取消；<see cref="DiffWorkflowService.Analyze"/> 的文件回调也是取消点；
///   - 取消 / 异常都不写三语快照（快照提交在取消检查之后，且为事务式批量提交）。
/// </summary>
public sealed class ProductionAnalyzeService
{
    private readonly string? _configDir;

    public ProductionAnalyzeService(string? configDir) => _configDir = configDir;

    /// <summary>
    /// 测试专用阶段钩子（默认 null，不改变生产行为）：
    /// 每个阶段开始时调用，测试可在此阻塞以验证「调用方未被阻塞 / 取消生效」。
    /// </summary>
    public Action<string>? StageEnteredHookForTests { get; set; }

    /// <summary>在后台线程执行完整分析。</summary>
    public Task<ProductionAnalyzeResult> RunAsync(
        ProductionAnalyzeRequest request,
        IProgress<AnalyzeStageReport>? progress = null,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(() => RunCore(request, progress, log, cancellationToken), cancellationToken);
    }

    private ProductionAnalyzeResult RunCore(
        ProductionAnalyzeRequest request,
        IProgress<AnalyzeStageReport>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var timings = new List<AnalyzeStageTiming>();
        var totalWatch = Stopwatch.StartNew();
        var workflow = new DiffWorkflowService(_configDir);
        var snapshotRoot = string.IsNullOrWhiteSpace(request.SnapshotRoot)
            ? SnapshotRootResolver.Resolve(request.ProjectRoot, request.NewEnglishDirectory)
            : request.SnapshotRoot!;

        log?.Invoke($"[调试] Diff分析：开始（ManagedThreadId={Environment.CurrentManagedThreadId}｜快照根={snapshotRoot}）");

        T Stage<T>(string stage, int itemCount, Func<T> work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StageEnteredHookForTests?.Invoke(stage);
            progress?.Report(new AnalyzeStageReport(stage, 0, itemCount));
            var watch = Stopwatch.StartNew();
            var value = work();
            watch.Stop();
            timings.Add(new AnalyzeStageTiming(stage, watch.ElapsedMilliseconds, itemCount, Environment.CurrentManagedThreadId));
            return value;
        }

        // ① 文件级对比
        var fileResult = Stage(AnalyzeStages.ScanFiles, 0, () =>
            workflow.AnalyzeFiles(request.OldEnglishDirectory, request.OldChineseDirectory, request.NewEnglishDirectory));

        // ② 条目级对比（进度回调同时作为取消点，并做节流）
        var lastReportTick = 0L;
        var diffResult = Stage(AnalyzeStages.CompareEntries, fileResult.Entries.Count, () =>
            workflow.Analyze(
                request.OldEnglishDirectory,
                request.OldChineseDirectory,
                request.NewEnglishDirectory,
                (done, total, file) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var now = Environment.TickCount64;
                    if (now - lastReportTick >= 150 || done == total)
                    {
                        lastReportTick = now;
                        progress?.Report(new AnalyzeStageReport(AnalyzeStages.CompareEntries, done, total, file));
                    }
                }));

        // ③ 三语捕获 + Canonical Diff（复用已解析英文；快照按 snapshotRoot 隔离）
        var capture = Stage(AnalyzeStages.CaptureSources, diffResult.NewUnits.Count, () =>
            ProductionTranslationPlanBuilder.TryCapture(
                request.ProjectRoot,
                request.NewEnglishDirectory,
                _configDir,
                diffResult.NewUnits,
                log,
                snapshotRoot,
                cancellationToken));

        // ④ 旧中文范围（KR 权威文件集扩展，P2-η）
        var oldChineseScope = Stage(AnalyzeStages.ExpandOldChinese, diffResult.OldChineseUnits.Count, () =>
            OldChineseScopeLoader.Load(
                request.Mode,
                diffResult.OldChineseUnits,
                capture,
                request.OldChineseDirectory,
                _configDir));

        // ⑤ 生产计划（ApplyToEntries 接线 + 最终动作过滤 + 术语单次匹配）
        var plan = Stage(AnalyzeStages.BuildPlan, diffResult.Entries.Count, () =>
            ProductionTranslationPlanBuilder.Build(
                request.Mode,
                diffResult.Entries,
                capture,
                request.NewEnglishDirectory,
                oldChineseScope.Units));

        totalWatch.Stop();
        foreach (var timing in timings)
        {
            log?.Invoke(timing.Describe());
        }

        log?.Invoke($"[调试] Diff分析总耗时：{totalWatch.ElapsedMilliseconds}ms（ManagedThreadId={Environment.CurrentManagedThreadId}）");

        return new ProductionAnalyzeResult
        {
            FileResult = fileResult,
            DiffResult = diffResult,
            Capture = capture,
            OldChineseScope = oldChineseScope,
            Plan = plan,
            Timings = timings,
            SnapshotRoot = snapshotRoot,
            TotalElapsedMs = totalWatch.ElapsedMilliseconds,
        };
    }
}
