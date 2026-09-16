using System.Text;
using System.Text.Json;
using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Cli;

/// <summary>
/// 第8轮受控真实数据库 / DeepSeek 冒烟命令。
///
/// 与常规 --translate 严格隔离：固定小样本、固定安全输出目录、从不调用 DeployService。
/// </summary>
internal static class RealApiSmokeCommand
{
    private const string RoundDirectoryName = "20260914_第8轮";
    private const string PreMigrationBackupName = "真实库迁移前";
    private const string PostMigrationBackupName = "真实库迁移后_API前";
    private const string CacheReplayDirectoryName = "cache_replay_test";
    private const string PlanFileName = "smoke_plan.json";
    private const string SummaryFileName = "smoke_summary.json";
    private const string ReviewFileName = "smoke_review.md";

    public static int CreateBackup(string projectRoot, bool afterMigration)
    {
        try
        {
            var databasePath = GetLiveDatabasePath(projectRoot);
            var destination = Path.Combine(
                GetRoundBackupRoot(projectRoot),
                afterMigration ? PostMigrationBackupName : PreMigrationBackupName);
            var snapshot = RealApiSmokeDatabase.CreateOfflineBackup(databasePath, destination);
            Console.WriteLine($"[调试] 已创建真实库离线备份: {destination}");
            PrintSnapshot("备份快照", snapshot);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 创建真实库备份失败，已停止: {SanitizeError(ex.Message)}");
            return 2;
        }
    }

    public static int MigrateOnly(string projectRoot)
    {
        try
        {
            var databasePath = GetLiveDatabasePath(projectRoot);
            EnsureBackupMatchesLive(projectRoot, PreMigrationBackupName, databasePath);
            var result = RealApiSmokeDatabase.MigrateExistingDatabase(databasePath, Console.WriteLine);
            var resultPath = Path.Combine(GetRoundBackupRoot(projectRoot), "migration_result.json");
            RealApiSmokeDatabase.SaveMigrationResult(resultPath, result);
            Console.WriteLine("[调试] 真实数据库 Migration 已完成（未翻译、未访问网络）。");
            PrintSnapshot("迁移前", result.Before);
            PrintSnapshot("迁移后", result.After);
            Console.WriteLine($"[调试] Migration 对比记录: {resultPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 真实数据库 Migration 失败，未调用 API: {SanitizeError(ex.Message)}");
            return 2;
        }
    }

    public static async Task<int> RunFirstAsync(string projectRoot, RealApiSmokeCommandOptions options)
    {
        try
        {
            var configDir = Path.Combine(projectRoot, "config");
            var settings = RequireDeepSeekSettings(configDir);
            var databasePath = GetLiveDatabasePath(projectRoot);
            var databaseBefore = RealApiSmokeDatabase.Capture(databasePath);
            EnsureMigrated(databaseBefore);
            EnsureBackupMatchesLive(projectRoot, PostMigrationBackupName, databasePath);

            var inputs = ResolveInputs(options, projectRoot);
            var analysis = Analyze(inputs, configDir);
            var exactTmKeys = RealApiSmokeDatabase.FindExistingExactUnitKeys(databasePath, analysis.Diff.Entries);
            var candidates = analysis.Diff.Entries
                .Where(entry => !exactTmKeys.Contains(entry.Key.ToString()))
                .ToList();
            var selection = RealApiSmokeSelector.Select(
                candidates,
                analysis.ContextBuilder,
                new GlossaryService(configDir).Entries,
                options.ItemCount);
            EnsureSelectionIsSafe(selection);

            var runContext = TranslationRunContext.Create();
            var runDirectory = GetRunDirectory(projectRoot, runContext.RunId);
            if (Directory.Exists(runDirectory))
            {
                throw new InvalidOperationException("[错误] 本次 RunId 的冒烟输出目录已存在，拒绝覆盖。");
            }
            EnsureOutputIsOutsideGameDirectory(runDirectory, inputs.GameChineseDirectory);
            Directory.CreateDirectory(runDirectory);

            var plan = RealApiSmokeSelector.CreatePlan(runContext.RunId, selection, settings.Batch);
            var planPath = Path.Combine(runDirectory, PlanFileName);
            RealApiSmokeSelector.SavePlan(planPath, plan);

            PrintFinalConfirmation(settings, databasePath, runDirectory, selection, plan, exactTmKeys.Count);

            var traceWriter = new TranslationTraceWriter(projectRoot, runContext, Console.WriteLine);
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = databasePath }, Console.WriteLine);
            var cacheServices = TranslationCacheServices.Create(memory, runContext, traceWriter, Console.WriteLine);
            using var provider = new DeepSeekTranslationProvider(
                settings.Options, configDir, settings.Batch, cacheServices: cacheServices, log: Console.WriteLine);
            var validation = ValidationPipeline.CreateDefault(configDir);
            var coordinator = new Coordinator(
                provider,
                memory,
                maxConcurrentAgents: 1,
                maxConcurrentApiRequests: 1,
                log: Console.WriteLine,
                validation: validation,
                cacheServices: cacheServices,
                contextBuilder: analysis.ContextBuilder);

            var coordinatorResult = await coordinator.ExecuteAsync(
                selection.Entries,
                (done, total, stage) => Console.WriteLine($"[调试] 真实 API 冒烟 Stage 进度: {done}/{total}，当前 {stage}"));
            EnsureCoordinatorSucceeded(coordinatorResult);
            EnsureAllSelectedEntriesTranslated(selection.Entries);

            var traceSummary = ReadAndValidateTrace(traceWriter.FilePath!, plan, settings, selection);
            var translations = Coordinator.CollectTranslations(selection.Entries);
            var outputResult = new MergeOutputService().MergeAllWithReport(
                inputs.NewEnglishDirectory,
                translations,
                runDirectory,
                selection.Entries.Select(entry => entry.Key.ToString()));
            if (!outputResult.IsComplete)
            {
                throw new InvalidOperationException($"[错误] 冒烟 Merge 核验失败，问题数: {outputResult.Issues.Count}。");
            }

            var gate = ReleaseGateService.Evaluate(selection.Entries, validation);
            var manifest = OutputManifestService.Save(runDirectory, outputResult, gate);
            var selectedMemory = RealApiSmokeDatabase.CaptureSelectedMemory(
                databasePath, selection.BuildUnitKeyToSourceHash());
            if (selectedMemory.ExactUnitHitCount != selection.Entries.Count)
            {
                throw new InvalidOperationException("[错误] 真实 API 冒烟后，部分样本未能以 UnitKey + SourceHash 命中 TM。");
            }

            var storyContext = await VerifyStoryContextFingerprintAsync(settings, configDir, selection);
            var reviewPath = Path.Combine(runDirectory, ReviewFileName);
            WriteReviewDocument(reviewPath, selection.Entries, selection.Contexts);
            var databaseAfter = RealApiSmokeDatabase.Capture(databasePath);
            var summary = new RealApiSmokeRunSummary
            {
                RunId = runContext.RunId,
                Mode = "首次真实 DeepSeek 冒烟",
                DatabaseBefore = databaseBefore,
                DatabaseAfter = databaseAfter,
                ItemCount = selection.Entries.Count,
                ExpectedProviderBatchCount = plan.ExpectedProviderBatchCount,
                ExactTmExcludedBeforeRun = exactTmKeys.Count,
                Provider = DeepSeekTranslationProvider.ProviderName,
                Model = settings.Options.Model,
                Endpoint = SanitizeEndpoint(settings.Options.ApiUrl),
                Trace = traceSummary,
                SelectedMemory = selectedMemory,
                ValidationIssueCounts = CountValidationIssues(selection.Entries),
                NeedsReviewCount = selection.Entries.Count(entry => entry.NeedsReview),
                GateStatus = gate.Status.ToString(),
                GateErrorCount = gate.ErrorCount,
                GateWarningCount = gate.WarningCount,
                GateBlockingErrorCount = gate.BlockingErrorCount,
                GateHistoricalInheritedErrorCount = gate.HistoricalInheritedErrorCount,
                MergeComplete = outputResult.IsComplete,
                MergeWrittenFileCount = outputResult.WrittenFileCount,
                MergeVerifiedEntryCount = outputResult.VerifiedEntryCount,
                ManifestComplete = manifest.IsComplete,
                SmokeReviewPath = reviewPath,
                DeployDisabled = true,
                StoryContext = storyContext,
            };
            var summaryPath = Path.Combine(runDirectory, SummaryFileName);
            SaveJson(summaryPath, summary);

            Console.WriteLine($"[调试] 真实 API 冒烟完成: RunId={runContext.RunId}，网络请求 {traceSummary.NetworkCalledCount}，输出目录: {runDirectory}");
            Console.WriteLine($"[调试] 冒烟摘要: {summaryPath}");
            Console.WriteLine("[调试] 本轮 Deploy = disabled，未调用任何游戏目录写入接口。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 真实 API 冒烟已停止: {SanitizeError(ex.Message)}");
            return 2;
        }
    }

    public static async Task<int> VerifyExactTmReplayAsync(string projectRoot, RealApiSmokeCommandOptions options)
    {
        try
        {
            var runId = RequireRunId(options.RunId);
            var configDir = Path.Combine(projectRoot, "config");
            var settings = RequireDeepSeekSettings(configDir);
            var plan = LoadPlan(projectRoot, runId);
            EnsurePlanMatchesCurrentBatch(plan, settings.Batch);
            var databasePath = GetLiveDatabasePath(projectRoot);
            EnsureMigrated(RealApiSmokeDatabase.Capture(databasePath));

            var inputs = ResolveInputs(options, projectRoot);
            var analysis = Analyze(inputs, configDir);
            var selection = RealApiSmokeSelector.ResolvePlan(plan, analysis.Diff.Entries, analysis.ContextBuilder);
            var before = RealApiSmokeDatabase.CaptureSelectedMemory(databasePath, selection.BuildUnitKeyToSourceHash());
            if (before.ExactUnitHitCount != selection.Entries.Count)
            {
                throw new InvalidOperationException("[错误] 第二次运行前，存在未命中 ExactUnit TM 的样本，已拒绝继续。");
            }

            var failIfNetwork = new FailIfNetworkCalledDeepSeekClient();
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = databasePath }, Console.WriteLine);
            using var provider = new DeepSeekTranslationProvider(
                settings.Options, configDir, settings.Batch, client: failIfNetwork, log: Console.WriteLine);
            var validation = ValidationPipeline.CreateDefault(configDir);
            var coordinator = new Coordinator(
                provider, memory, maxConcurrentAgents: 1, maxConcurrentApiRequests: 1,
                log: Console.WriteLine, validation: validation, contextBuilder: analysis.ContextBuilder);
            var result = await coordinator.ExecuteAsync(selection.Entries);
            EnsureCoordinatorSucceeded(result);
            if (failIfNetwork.CallCount != 0 || selection.Entries.Any(entry => entry.TmMatchType != TranslationMemoryMatchType.ExactUnit))
            {
                throw new InvalidOperationException("[错误] 第二次运行没有完全走 ExactUnit TM，已停止且未访问真实网络。");
            }

            var outputPath = Path.Combine(GetRunDirectory(projectRoot, runId), "tm_replay_result.json");
            SaveJson(outputPath, new ReplaySummary
            {
                RunId = runId,
                Mode = "ExactUnit TM 二次验证",
                ItemCount = selection.Entries.Count,
                NetworkAttemptCount = failIfNetwork.CallCount,
                ExactUnitHitCount = selection.Entries.Count(entry => entry.TmMatchType == TranslationMemoryMatchType.ExactUnit),
                RequestCacheWasUsed = false,
            });
            Console.WriteLine($"[调试] ExactUnit TM 二次验证通过：{selection.Entries.Count} 条全部命中，真实网络调用 0。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] ExactUnit TM 二次验证失败，未访问真实 API: {SanitizeError(ex.Message)}");
            return 2;
        }
    }

    public static int CreateCacheReplayClone(string projectRoot, string? runId)
    {
        try
        {
            _ = LoadPlan(projectRoot, RequireRunId(runId));
            var source = GetLiveDatabasePath(projectRoot);
            var destination = GetCacheReplayDirectory(projectRoot);
            var copied = RealApiSmokeDatabase.CreateOfflineBackup(source, destination);
            Console.WriteLine($"[调试] 已创建 RequestCache Replay 克隆库: {copied.DatabasePath}");
            PrintSnapshot("克隆库快照", copied);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 创建 Cache Replay 克隆库失败: {SanitizeError(ex.Message)}");
            return 2;
        }
    }

    public static async Task<int> VerifyRequestCacheReplayAsync(string projectRoot, RealApiSmokeCommandOptions options)
    {
        try
        {
            var runId = RequireRunId(options.RunId);
            var configDir = Path.Combine(projectRoot, "config");
            var settings = RequireDeepSeekSettings(configDir);
            var plan = LoadPlan(projectRoot, runId);
            EnsurePlanMatchesCurrentBatch(plan, settings.Batch);
            var liveDatabasePath = GetLiveDatabasePath(projectRoot);
            var cloneDatabasePath = Path.Combine(GetCacheReplayDirectory(projectRoot), "translation_memory.db");
            EnsureCloneIsSafe(liveDatabasePath, cloneDatabasePath);
            EnsureMigrated(RealApiSmokeDatabase.Capture(cloneDatabasePath));

            var markerPath = Path.Combine(GetCacheReplayDirectory(projectRoot), "cache_replay_result.json");
            if (File.Exists(markerPath))
            {
                throw new InvalidOperationException("[错误] 当前克隆库已经执行过 Cache Replay，拒绝重复删除 TM；如需重跑请创建新的克隆库。");
            }

            var inputs = ResolveInputs(options, projectRoot);
            var analysis = Analyze(inputs, configDir);
            var selection = RealApiSmokeSelector.ResolvePlan(plan, analysis.Diff.Entries, analysis.ContextBuilder);
            var removed = RealApiSmokeDatabase.DeleteExactTranslations(
                cloneDatabasePath, selection.BuildUnitKeyToSourceHash());
            var afterDeletion = RealApiSmokeDatabase.CaptureSelectedMemory(
                cloneDatabasePath, selection.BuildUnitKeyToSourceHash());
            if (afterDeletion.ExactUnitHitCount != 0)
            {
                throw new InvalidOperationException("[错误] 克隆库删除后仍存在 ExactUnit TM 命中，已停止 Cache Replay。");
            }

            var runContext = new TranslationRunContext { RunId = runId + "_cache_replay" };
            var traceWriter = new TranslationTraceWriter(projectRoot, runContext, Console.WriteLine);
            var failIfNetwork = new FailIfNetworkCalledDeepSeekClient();
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = cloneDatabasePath }, Console.WriteLine);
            var cacheServices = TranslationCacheServices.Create(memory, runContext, traceWriter, Console.WriteLine);
            using var provider = new DeepSeekTranslationProvider(
                settings.Options, configDir, settings.Batch,
                cacheServices: cacheServices, client: failIfNetwork, log: Console.WriteLine);
            var validation = ValidationPipeline.CreateDefault(configDir);
            var coordinator = new Coordinator(
                provider, memory, maxConcurrentAgents: 1, maxConcurrentApiRequests: 1,
                log: Console.WriteLine, validation: validation,
                cacheServices: cacheServices, contextBuilder: analysis.ContextBuilder);
            var result = await coordinator.ExecuteAsync(selection.Entries);
            EnsureCoordinatorSucceeded(result);
            EnsureAllSelectedEntriesTranslated(selection.Entries);

            var traceSummary = ReadAndValidateTrace(traceWriter.FilePath!, plan, settings, selection, requireCacheHit: true);
            if (failIfNetwork.CallCount != 0 || traceSummary.NetworkCalledCount != 0 || traceSummary.CacheHitCount != plan.ExpectedProviderBatchCount)
            {
                throw new InvalidOperationException("[错误] Cache Replay 试图调用网络或未完全命中 RequestCache，已停止。");
            }
            var afterReplay = RealApiSmokeDatabase.CaptureSelectedMemory(
                cloneDatabasePath, selection.BuildUnitKeyToSourceHash());
            if (afterReplay.ExactUnitHitCount != selection.Entries.Count)
            {
                throw new InvalidOperationException("[错误] Cache Replay 后未能把样本重新写回克隆 TM。");
            }

            SaveJson(markerPath, new CacheReplaySummary
            {
                RunId = runId,
                RemovedExactTranslationRows = removed,
                ItemCount = selection.Entries.Count,
                NetworkAttemptCount = failIfNetwork.CallCount,
                Trace = traceSummary,
                SelectedMemoryAfterReplay = afterReplay,
            });
            Console.WriteLine($"[调试] RequestCache Replay 验证通过：删除克隆 TM {removed} 行，真实网络调用 0，缓存命中 {traceSummary.CacheHitCount}。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] RequestCache Replay 验证失败，未访问真实 API: {SanitizeError(ex.Message)}");
            return 2;
        }
    }

    private static SettingsLoadResult RequireDeepSeekSettings(string configDir)
    {
        var settings = AppSettingsLoader.LoadProviderSettings(configDir);
        if (!settings.Success)
        {
            throw new InvalidOperationException("[错误] 真实 API 冒烟配置无效: " + string.Join("；", settings.Errors));
        }
        if (settings.Mode != TranslationProviderMode.DeepSeek)
        {
            throw new InvalidOperationException("[错误] 真实 API 冒烟要求 provider=deepseek，当前不是 DeepSeek 模式。");
        }

        return settings;
    }

    private static SmokeAnalysis Analyze(SmokeInputDirectories inputs, string configDir)
    {
        var service = new DiffWorkflowService(configDir);
        var diff = service.Analyze(inputs.OldEnglishDirectory, inputs.OldChineseDirectory, inputs.NewEnglishDirectory);
        var index = TranslationContextIndex.Build(diff.NewUnits, Console.WriteLine);
        return new SmokeAnalysis
        {
            Diff = diff,
            ContextBuilder = new TranslationContextBuilder(index),
        };
    }

    private static SmokeInputDirectories ResolveInputs(RealApiSmokeCommandOptions options, string projectRoot)
    {
        string? gameChinese = null;
        string oldEnglish;
        string oldChinese;
        string newEnglish;
        if (options.AutoLocate)
        {
            var located = GameDirectoryLocator.AutoLocate()
                          ?? throw new InvalidOperationException("[错误] 自动定位游戏目录失败。");
            oldEnglish = options.OldEnglishDirectory ?? located.NewEnglishDir;
            oldChinese = options.OldChineseDirectory ?? located.ChineseDir;
            newEnglish = options.NewEnglishDirectory ?? located.NewEnglishDir;
            gameChinese = located.ChineseDir;
        }
        else
        {
            oldEnglish = options.OldEnglishDirectory ?? Path.Combine(projectRoot, "data", "testdata", "old_en");
            oldChinese = options.OldChineseDirectory ?? Path.Combine(projectRoot, "data", "testdata", "old_zh");
            newEnglish = options.NewEnglishDirectory ?? Path.Combine(projectRoot, "data", "testdata", "new_en");
            var located = GameDirectoryLocator.AutoLocate();
            gameChinese = located?.ChineseDir;
        }

        foreach (var directory in new[] { oldEnglish, oldChinese, newEnglish })
        {
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException($"[错误] 真实 API 冒烟输入目录不存在: {directory}");
            }
        }

        return new SmokeInputDirectories
        {
            OldEnglishDirectory = Path.GetFullPath(oldEnglish),
            OldChineseDirectory = Path.GetFullPath(oldChinese),
            NewEnglishDirectory = Path.GetFullPath(newEnglish),
            GameChineseDirectory = gameChinese is null ? null : Path.GetFullPath(gameChinese),
        };
    }

    private static void EnsureSelectionIsSafe(RealApiSmokeSelection selection)
    {
        if (selection.Entries.Count < RealApiSmokeSelector.MinimumItemCount
            || selection.Entries.Count > RealApiSmokeSelector.MaximumItemCount)
        {
            throw new InvalidOperationException($"[错误] 真实 API 冒烟选择器拒绝启动：条目数 {selection.Entries.Count} 不在 10~30 范围内。");
        }
        if (selection.Entries.Count != selection.TargetItemCount)
        {
            throw new InvalidOperationException(
                $"[错误] 可用真实样本不足：目标 {selection.TargetItemCount} 条，实际仅 {selection.Entries.Count} 条。");
        }
    }

    private static void PrintFinalConfirmation(
        SettingsLoadResult settings,
        string databasePath,
        string outputDirectory,
        RealApiSmokeSelection selection,
        RealApiSmokePlan plan,
        int exactTmExcluded)
    {
        Console.WriteLine("[真实API冒烟]");
        Console.WriteLine($"Provider: DeepSeek");
        Console.WriteLine($"Model: {settings.Options.Model}");
        Console.WriteLine($"Endpoint: {SanitizeEndpoint(settings.Options.ApiUrl)}");
        Console.WriteLine($"条目数: {selection.Entries.Count}");
        Console.WriteLine($"Provider请求批次数: {plan.ExpectedProviderBatchCount}");
        Console.WriteLine($"真实数据库: {databasePath}");
        Console.WriteLine($"输出目录: {outputDirectory}");
        Console.WriteLine("真实游戏目录部署: 否（Deploy = disabled）");
        Console.WriteLine("受控并发: Agent=1，API=1");
        Console.WriteLine($"[调试] 首轮已排除 ExactUnit TM 命中样本: {exactTmExcluded} 条；空源文/超长文本不进入选择器。");
        Console.WriteLine("即将产生真实 API 费用。");
    }

    private static void EnsureCoordinatorSucceeded(CoordinatorResult result)
    {
        if (result.FailedCount > 0)
        {
            var summary = string.Join("；", result.Agents.Where(agent => !agent.IsSuccess)
                .Select(agent => $"{agent.StageId}: {agent.Error}"));
            throw new InvalidOperationException($"[错误] 冒烟翻译存在失败 Stage: {summary}");
        }
    }

    private static void EnsureAllSelectedEntriesTranslated(IEnumerable<DiffEntry> entries)
    {
        var missing = entries.Where(entry => entry.Translation is null).Select(entry => entry.Key.ToString()).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"[错误] 冒烟翻译结果缺失 {missing.Length} 条，拒绝 Merge。");
        }
    }

    private static RealApiSmokeTraceSummary ReadAndValidateTrace(
        string tracePath,
        RealApiSmokePlan plan,
        SettingsLoadResult settings,
        RealApiSmokeSelection selection,
        bool requireCacheHit = false)
    {
        if (!File.Exists(tracePath))
        {
            throw new InvalidOperationException("[错误] 未生成真实冒烟 Trace，已停止。");
        }

        var raw = File.ReadAllText(tracePath);
        if (raw.Contains(settings.Options.ApiKey, StringComparison.Ordinal)
            || raw.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
            || raw.Contains("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("[错误] Trace 疑似包含敏感认证信息，已停止。");
        }
        foreach (var entry in selection.Entries)
        {
            if ((entry.NewSourceText?.Length ?? 0) >= 32 && raw.Contains(entry.NewSourceText!, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("[错误] Trace 疑似包含完整 SourceText，已停止。");
            }
            if ((entry.Translation?.Length ?? 0) >= 32 && raw.Contains(entry.Translation!, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("[错误] Trace 疑似包含完整 Translation，已停止。");
            }
        }

        var traces = raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<TranslationTraceEntry>(line, JsonOptions)
                            ?? throw new InvalidOperationException("[错误] Trace 存在空 JSON 行。"))
            .ToList();
        if (traces.Count != plan.ExpectedProviderBatchCount)
        {
            throw new InvalidOperationException($"[错误] Trace 行数 {traces.Count} 与预计 Provider 批次 {plan.ExpectedProviderBatchCount} 不一致。");
        }
        if (traces.Any(trace => !trace.Success || string.IsNullOrWhiteSpace(trace.Fingerprint) || string.IsNullOrWhiteSpace(trace.PromptHash)))
        {
            throw new InvalidOperationException("[错误] Trace 存在失败请求或缺少 Fingerprint / PromptHash。");
        }
        if (selection.HasStoryMiddleSentence && !traces.Any(trace => !string.IsNullOrWhiteSpace(trace.ContextHash)))
        {
            throw new InvalidOperationException("[错误] 选择了 StoryData 中间句，但 Trace 未记录 ContextHash。");
        }

        var summary = new RealApiSmokeTraceSummary
        {
            TracePath = tracePath,
            LineCount = traces.Count,
            NetworkCalledCount = traces.Count(trace => trace.NetworkCalled),
            CacheHitCount = traces.Count(trace => trace.CacheHit),
            SuccessCount = traces.Count(trace => trace.Success),
            FailedCount = traces.Count(trace => !trace.Success),
            RetryCount = traces.Sum(trace => trace.RetryCount),
            InputTokens = traces.Sum(trace => trace.InputTokens ?? 0),
            OutputTokens = traces.Sum(trace => trace.OutputTokens ?? 0),
            TotalTokens = traces.Sum(trace => trace.TotalTokens ?? 0),
            DurationMs = traces.Sum(trace => trace.DurationMs),
            AllFingerprintsPresent = traces.All(trace => !string.IsNullOrWhiteSpace(trace.Fingerprint)),
            AllPromptHashesPresent = traces.All(trace => !string.IsNullOrWhiteSpace(trace.PromptHash)),
            StoryContextHashPresent = traces.Any(trace => !string.IsNullOrWhiteSpace(trace.ContextHash)),
            AllResponsesHaveModelWhenProvided = traces.All(trace => trace.ResponseModel is null || !string.IsNullOrWhiteSpace(trace.ResponseModel)),
        };

        if (requireCacheHit)
        {
            if (summary.NetworkCalledCount != 0 || summary.CacheHitCount != plan.ExpectedProviderBatchCount)
            {
                throw new InvalidOperationException("[错误] Cache Replay Trace 没有完全命中 RequestCache。");
            }
        }
        else if (summary.NetworkCalledCount == 0 || summary.CacheHitCount != 0)
        {
            throw new InvalidOperationException("[错误] 首次真实冒烟未形成全部 Cache Miss 的真实网络请求，已停止。");
        }

        return summary;
    }

    private static async Task<RealApiSmokeStoryContextSummary?> VerifyStoryContextFingerprintAsync(
        SettingsLoadResult settings,
        string configDir,
        RealApiSmokeSelection selection)
    {
        var entry = selection.Entries.FirstOrDefault(candidate =>
            selection.Contexts.TryGetValue(candidate.Key.ToString(), out var context)
            && context.Previous is not null
            && context.Next is not null);
        if (entry is null)
        {
            return null;
        }

        var context = selection.Contexts[entry.Key.ToString()];
        var client = new LocalEchoDeepSeekClient();
        using var provider = new DeepSeekTranslationProvider(
            settings.Options, configDir, settings.Batch, client: client);
        var one = new[] { entry };
        var withContext = await provider.TranslateAsync(
            one,
            contexts: new Dictionary<string, TranslationContext>(StringComparer.Ordinal)
            {
                [entry.Key.ToString()] = context,
            });
        var withoutContext = await provider.TranslateAsync(one, contexts: null);
        var withFingerprint = withContext[entry.Key.ToString()].RequestFingerprint;
        var withoutFingerprint = withoutContext[entry.Key.ToString()].RequestFingerprint;
        if (string.IsNullOrWhiteSpace(withFingerprint)
            || string.IsNullOrWhiteSpace(withoutFingerprint)
            || string.Equals(withFingerprint, withoutFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("[错误] StoryData 上下文与无上下文的 Fingerprint 未产生差异。");
        }

        return new RealApiSmokeStoryContextSummary
        {
            UnitKey = entry.Key.ToString(),
            PreviousPresent = context.Previous is not null,
            NextPresent = context.Next is not null,
            ContextScopePresent = !string.IsNullOrWhiteSpace(context.ContextScopeKey),
            ContextFingerprintDiffersFromNoContext = true,
        };
    }

    private static void WriteReviewDocument(
        string path,
        IReadOnlyList<DiffEntry> entries,
        IReadOnlyDictionary<string, TranslationContext> contexts,
        string title = "真实 API 冒烟人工抽查",
        string roundLabel = "第8.5轮")
    {
        // 第8.5轮：文档构造移到 Infrastructure（RealApiSmokeReviewDocument），便于单元测试覆盖
        // 「审核原因 / 结构化校验问题必须出现」这一要求。
        var content = RealApiSmokeReviewDocument.Build(entries, contexts, title, roundLabel);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static IReadOnlyDictionary<string, int> CountValidationIssues(IEnumerable<DiffEntry> entries)
        => entries
            .SelectMany(entry => entry.ValidationIssues)
            .GroupBy(issue => issue.Code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static void EnsureMigrated(RealApiSmokeDatabaseSnapshot snapshot)
    {
        if (snapshot.UserVersion != 1
            || !snapshot.TranslationsColumns.Contains("ReviewReason", StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("[错误] 真实数据库尚未完成第8轮 Migration；请先完成迁移前备份与 --real-api-smoke-migrate。");
        }
    }

    private static void EnsureBackupMatchesLive(string projectRoot, string backupName, string liveDatabasePath)
    {
        var backupPath = Path.Combine(GetRoundBackupRoot(projectRoot), backupName, "translation_memory.db");
        var live = RealApiSmokeDatabase.Capture(liveDatabasePath);
        var backup = RealApiSmokeDatabase.Capture(backupPath);
        if (!string.Equals(live.DatabaseSha256, backup.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"[错误] 恢复点 {backupName} 与当前真实库 SHA256 不一致，拒绝继续。");
        }
    }

    private static void EnsurePlanMatchesCurrentBatch(RealApiSmokePlan plan, BatchOptions batch)
    {
        if (plan.BatchMaxItemsPerBatch != batch.MaxItemsPerBatch
            || plan.BatchMaxCharactersPerBatch != batch.MaxCharactersPerBatch)
        {
            throw new InvalidOperationException("[错误] 当前 batch 配置与首次冒烟计划不一致，拒绝 TM / Cache Replay。");
        }
    }

    private static void EnsureCloneIsSafe(string liveDatabasePath, string cloneDatabasePath)
    {
        var live = Path.GetFullPath(liveDatabasePath);
        var clone = Path.GetFullPath(cloneDatabasePath);
        var allowedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GetCacheReplayDirectory(FindProjectRootFromDatabasePath(live))))
                          + Path.DirectorySeparatorChar;
        if (string.Equals(live, clone, StringComparison.OrdinalIgnoreCase)
            || !clone.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("[错误] Cache Replay 克隆库路径不安全，拒绝删除任何 translations。");
        }
    }

    private static string FindProjectRootFromDatabasePath(string databasePath)
    {
        var cacheDirectory = Directory.GetParent(databasePath)?.FullName
                             ?? throw new InvalidOperationException("[错误] 无法解析真实数据库路径。");
        var dataDirectory = Directory.GetParent(cacheDirectory)?.FullName
                            ?? throw new InvalidOperationException("[错误] 无法解析真实数据库路径。");
        return Directory.GetParent(dataDirectory)?.FullName
               ?? throw new InvalidOperationException("[错误] 无法解析项目根目录。");
    }

    private static void EnsureOutputIsOutsideGameDirectory(string outputDirectory, string? gameChineseDirectory)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (gameChineseDirectory is not null && IsUnder(output, gameChineseDirectory))
        {
            throw new InvalidOperationException("[错误] 真实 API 冒烟输出目录落入游戏中文目录，已拒绝启动。");
        }
    }

    private static bool IsUnder(string candidate, string root)
    {
        var fullCandidate = Path.GetFullPath(candidate);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase)
               || fullCandidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static RealApiSmokePlan LoadPlan(string projectRoot, string runId)
        => RealApiSmokeSelector.LoadPlan(Path.Combine(GetRunDirectory(projectRoot, runId), PlanFileName));

    private static string GetLiveDatabasePath(string projectRoot)
        => Path.Combine(projectRoot, "data", "cache", "translation_memory.db");

    private static string GetRoundBackupRoot(string projectRoot)
        => Path.Combine(projectRoot, "data", "dev_backup", RoundDirectoryName);

    private static string GetCacheReplayDirectory(string projectRoot)
        => Path.Combine(GetRoundBackupRoot(projectRoot), CacheReplayDirectoryName);

    private static string GetRunDirectory(string projectRoot, string runId)
        => Path.Combine(projectRoot, "data", "output", "real_api_smoke", RequireRunId(runId));

    private static string RequireRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || runId.Contains('/')
            || runId.Contains('\\'))
        {
            throw new InvalidOperationException("[错误] 冒烟 RunId 无效。");
        }

        return runId;
    }

    private static string SanitizeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "<无效地址>";
        }

        var authority = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        return $"{uri.Scheme}://{authority}{uri.AbsolutePath}";
    }

    private static string SanitizeError(string message)
        => TranslationTraceWriter.Sanitize(message, maxLength: 500);

    private static void PrintSnapshot(string title, RealApiSmokeDatabaseSnapshot snapshot)
    {
        Console.WriteLine($"[调试] {title}: user_version={snapshot.UserVersion}，translations={snapshot.TranslationsRowCount}，request_cache={snapshot.RequestCacheRowCount}，NeedsReview={snapshot.NeedsReviewCount}");
        Console.WriteLine($"[调试] {title}: db={snapshot.DatabaseSizeBytes} bytes，SHA256={snapshot.DatabaseSha256[..Math.Min(16, snapshot.DatabaseSha256.Length)]}…，wal={snapshot.WalSizeBytes} bytes，shm={snapshot.ShmSizeBytes} bytes");
    }

    private static void SaveJson<T>(string path, T payload)
        => File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOptions), new UTF8Encoding(false));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class FailIfNetworkCalledDeepSeekClient : IDeepSeekBatchClient
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
        {
            Interlocked.Increment(ref _callCount);
            throw new InvalidOperationException("[错误] FailIfNetworkCalledDeepSeekClient 阻止了潜在真实网络请求。");
        }
    }

    private sealed class LocalEchoDeepSeekClient : IDeepSeekBatchClient
    {
        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
            => Task.FromResult(new DeepSeekBatchResult
            {
                Items = items.ToDictionary(
                    item => item.Id,
                    item => new DeepSeekTranslateItem(item.Id, item.Source, false, string.Empty),
                    StringComparer.Ordinal),
                ResponseId = "local-fingerprint-check",
                ResponseModel = "local-fingerprint-check",
                RetryCount = 0,
                DurationMs = 0,
            });
    }

    private sealed class SmokeAnalysis
    {
        public required DiffResult Diff { get; init; }
        public required TranslationContextBuilder ContextBuilder { get; init; }
    }

    private sealed class SmokeInputDirectories
    {
        public required string OldEnglishDirectory { get; init; }
        public required string OldChineseDirectory { get; init; }
        public required string NewEnglishDirectory { get; init; }
        public string? GameChineseDirectory { get; init; }
    }

    private sealed class RealApiSmokeRunSummary
    {
        public required string RunId { get; init; }
        public required string Mode { get; init; }
        public required RealApiSmokeDatabaseSnapshot DatabaseBefore { get; init; }
        public required RealApiSmokeDatabaseSnapshot DatabaseAfter { get; init; }
        public int ItemCount { get; init; }
        public int ExpectedProviderBatchCount { get; init; }
        public int ExactTmExcludedBeforeRun { get; init; }
        public required string Provider { get; init; }
        public required string Model { get; init; }
        public required string Endpoint { get; init; }
        public required RealApiSmokeTraceSummary Trace { get; init; }
        public required RealApiSmokeSelectedMemoryStats SelectedMemory { get; init; }
        public required IReadOnlyDictionary<string, int> ValidationIssueCounts { get; init; }
        public int NeedsReviewCount { get; init; }
        public required string GateStatus { get; init; }
        public int GateErrorCount { get; init; }
        public int GateWarningCount { get; init; }
        public int GateBlockingErrorCount { get; init; }
        public int GateHistoricalInheritedErrorCount { get; init; }
        public bool MergeComplete { get; init; }
        public int MergeWrittenFileCount { get; init; }
        public int MergeVerifiedEntryCount { get; init; }
        public bool ManifestComplete { get; init; }
        public required string SmokeReviewPath { get; init; }
        public bool DeployDisabled { get; init; }
        public RealApiSmokeStoryContextSummary? StoryContext { get; init; }
    }

    private sealed class ReplaySummary
    {
        public required string RunId { get; init; }
        public required string Mode { get; init; }
        public int ItemCount { get; init; }
        public int NetworkAttemptCount { get; init; }
        public int ExactUnitHitCount { get; init; }
        public bool RequestCacheWasUsed { get; init; }
    }

    private sealed class CacheReplaySummary
    {
        public required string RunId { get; init; }
        public int RemovedExactTranslationRows { get; init; }
        public int ItemCount { get; init; }
        public int NetworkAttemptCount { get; init; }
        public required RealApiSmokeTraceSummary Trace { get; init; }
        public required RealApiSmokeSelectedMemoryStats SelectedMemoryAfterReplay { get; init; }
    }
}

/// <summary>CLI 传递给第8轮受控冒烟命令的非敏感参数。</summary>
internal sealed class RealApiSmokeCommandOptions
{
    public string? OldEnglishDirectory { get; init; }
    public string? OldChineseDirectory { get; init; }
    public string? NewEnglishDirectory { get; init; }
    public bool AutoLocate { get; init; }
    public int ItemCount { get; init; } = RealApiSmokeSelector.DefaultItemCount;
    public string? RunId { get; init; }
}

/// <summary>Trace 脱敏验收统计。</summary>
internal sealed class RealApiSmokeTraceSummary
{
    public required string TracePath { get; init; }
    public int LineCount { get; init; }
    public int NetworkCalledCount { get; init; }
    public int CacheHitCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public int RetryCount { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public int TotalTokens { get; init; }
    public long DurationMs { get; init; }
    public bool AllFingerprintsPresent { get; init; }
    public bool AllPromptHashesPresent { get; init; }
    public bool StoryContextHashPresent { get; init; }
    public bool AllResponsesHaveModelWhenProvided { get; init; }
}

/// <summary>真实 StoryData 中间句上下文的脱敏验证结果。</summary>
internal sealed class RealApiSmokeStoryContextSummary
{
    public required string UnitKey { get; init; }
    public bool PreviousPresent { get; init; }
    public bool NextPresent { get; init; }
    public bool ContextScopePresent { get; init; }
    public bool ContextFingerprintDiffersFromNoContext { get; init; }
}
