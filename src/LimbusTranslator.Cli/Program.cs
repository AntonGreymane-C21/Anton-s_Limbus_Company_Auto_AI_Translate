using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.CharacterStyle;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Validation;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Translation;

namespace LimbusTranslator.Cli;

/// <summary>
/// 命令行入口。
///
/// 用法：
///   LimbusTranslator.Cli [--old-en <目录>] [--old-zh <目录>] [--new-en <目录>] [--auto-locate] [--extract]
///   不传参数时使用 data/testdata/ 下的默认测试数据。
///   --auto-locate: 自动定位游戏目录（优先于手动参数）
///   --extract:     将新增/缺失/修改文件复制到 data/work/pending/
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 解析参数
        var opts = ParseArgs(args);
        if (opts is null)
        {
            PrintUsage();
            return 1;
        }

        var (oldEnOpt, oldZhOpt, newEnOpt, autoLocate, extract, translate, scanTerms,
            smokeMode, smokeItemCount, smokeRunId, p6Smoke, p6FourModes, p6FourModeLocalizeRoot, p6DryRun,
            auditStructure, auditLocalizeRoot, auditOldZhRoot, guiDemoWorkspace, guiDemoRoot,
            analyzeTiming, analyzeTimingLocalizeRoot) = opts.Value;
        var projectRoot = FindProjectRoot();

        // 第9.0C.1轮：真实数据阶段耗时诊断（只读游戏目录；TEMP 快照；不调用 API）
        if (analyzeTiming)
        {
            var localizeRoot = analyzeTimingLocalizeRoot ?? ResolveDefaultLocalizeRoot();
            if (string.IsNullOrWhiteSpace(localizeRoot))
            {
                Console.WriteLine("[错误] 无法确定 Localize 根目录，请显式传入 --analyze-timing-localize-root <目录>。");
                return 1;
            }

            return await AnalyzeTimingCommand.RunAsync(projectRoot, localizeRoot, smokeRunId);
        }

        // 第9.0C轮：GUI 验收演示工作区（TEMP；绝不写生产数据 / 游戏目录）。
        if (guiDemoWorkspace)
        {
            var root = string.IsNullOrWhiteSpace(guiDemoRoot)
                ? Path.Combine(Path.GetTempPath(), $"limbus_gui_demo_{DateTime.Now:yyyyMMdd_HHmmss}")
                : guiDemoRoot!;
            var workspace = LimbusTranslator.Infrastructure.Presentation.GuiDemoWorkspaceBuilder.Create(root);
            Console.WriteLine("[调试] 已创建 GUI 演示工作区（TEMP；不写生产数据）：");
            Console.WriteLine(workspace.Describe());
            Console.WriteLine("[调试] 在 GUI 中依次填写上面三个目录，然后点「分析更新」即可看到 6 类条目。");
            return 0;
        }

        // 第9.0B-P7轮：真实多语言 JSON 结构差异**只读**审计（显式命令；绝不修改游戏目录）。
        if (auditStructure)
        {
            var localizeRoot = auditLocalizeRoot ?? ResolveDefaultLocalizeRoot();
            if (string.IsNullOrWhiteSpace(localizeRoot))
            {
                Console.WriteLine("[错误] 无法确定 Localize 根目录，请显式传入 --audit-localize-root <目录>。");
                return 1;
            }

            return LocalizeStructureAuditCommand.Run(localizeRoot, auditOldZhRoot);
        }

        // 第9.0B-P6轮：真实 DeepSeek 8 条 E2E Smoke（**显式命令**，绝不进入 dotnet test）。
        // 安全边界：TEMP 全隔离 + 硬上限 8 单元 / 8 请求 + 禁止 Deploy + 不打印凭据。
        if (p6Smoke)
        {
            return await P6RealSmokeCommand.RunAsync(projectRoot, smokeRunId);
        }

        // 第9.0B 最终真实四模式 Smoke（**显式命令**）：2 条真实单元 × 4 模式 = 8 单元；TEMP 全隔离。
        if (p6FourModes)
        {
            var localizeRoot = p6FourModeLocalizeRoot ?? ResolveDefaultLocalizeRoot();
            if (string.IsNullOrWhiteSpace(localizeRoot))
            {
                Console.WriteLine("[错误] 无法确定 Localize 根目录，请显式传入 --p6-real-smoke-localize-root <目录>。");
                return 1;
            }

            return await P6RealSmokeCommand.RunFourModesAsync(projectRoot, smokeRunId, localizeRoot, p6DryRun);
        }

        // 第8轮：真实数据库 / DeepSeek 受控冒烟与常规 --translate 完全隔离。
        // 该分支固定禁用 Deploy，首次真实调用只有显式 --real-api-smoke-first 才会进入。
        if (smokeMode != RealApiSmokeMode.None)
        {
            var smokeOptions = new RealApiSmokeCommandOptions
            {
                OldEnglishDirectory = oldEnOpt,
                OldChineseDirectory = oldZhOpt,
                NewEnglishDirectory = newEnOpt,
                AutoLocate = autoLocate,
                ItemCount = smokeItemCount,
                RunId = smokeRunId,
            };
            return smokeMode switch
            {
                RealApiSmokeMode.BackupPreMigration => RealApiSmokeCommand.CreateBackup(projectRoot, afterMigration: false),
                RealApiSmokeMode.MigrateOnly => RealApiSmokeCommand.MigrateOnly(projectRoot),
                RealApiSmokeMode.BackupPostMigration => RealApiSmokeCommand.CreateBackup(projectRoot, afterMigration: true),
                RealApiSmokeMode.FirstRealApi => await RealApiSmokeCommand.RunFirstAsync(projectRoot, smokeOptions),
                RealApiSmokeMode.ExactTmReplay => await RealApiSmokeCommand.VerifyExactTmReplayAsync(projectRoot, smokeOptions),
                RealApiSmokeMode.CreateCacheReplayClone => RealApiSmokeCommand.CreateCacheReplayClone(projectRoot, smokeRunId),
                RealApiSmokeMode.CacheReplay => await RealApiSmokeCommand.VerifyRequestCacheReplayAsync(projectRoot, smokeOptions),
                _ => 1,
            };
        }

        string oldEnDir, oldZhDir, newEnDir;

        // 自动定位游戏目录
        if (autoLocate)
        {
            var located = GameDirectoryLocator.AutoLocate();
            if (located is null)
            {
                Console.WriteLine("[错误] 自动定位游戏目录失败，请手动指定路径。");
                return 1;
            }
            Console.WriteLine($"[调试] 自动定位到游戏根目录: {located.GameRoot}");
            oldEnDir = oldEnOpt ?? located.NewEnglishDir;   // 旧英文缺省时用新英文
            oldZhDir = oldZhOpt ?? located.ChineseDir;
            newEnDir = newEnOpt ?? located.NewEnglishDir;
        }
        else
        {
            oldEnDir = oldEnOpt ?? Path.Combine(projectRoot, "data", "testdata", "old_en");
            oldZhDir = oldZhOpt ?? Path.Combine(projectRoot, "data", "testdata", "old_zh");
            newEnDir = newEnOpt ?? Path.Combine(projectRoot, "data", "testdata", "new_en");
        }

        Console.WriteLine("《Limbus Company / 边狱巴士》自动增量汉化工具");
        Console.WriteLine("=============================================");
        Console.WriteLine($"[调试] 旧英文目录: {oldEnDir}");
        Console.WriteLine($"[调试] 旧中文目录: {oldZhDir}");
        Console.WriteLine($"[调试] 新版英文目录: {newEnDir}");
        Console.WriteLine();

        if (!Directory.Exists(oldEnDir) || !Directory.Exists(oldZhDir) || !Directory.Exists(newEnDir))
        {
            Console.WriteLine("[错误] 目录不存在，请检查路径或先准备 data/testdata/ 测试数据。");
            return 1;
        }

        var service = new DiffWorkflowService(Path.Combine(projectRoot, "config"));

        // ===== 文件级对比 =====
        Console.WriteLine("========== 文件级对比结果 ==========");
        var fileResult = service.AnalyzeFiles(oldEnDir, oldZhDir, newEnDir);
        Console.WriteLine($"新增文件：{fileResult.NewCount}");
        Console.WriteLine($"缺失汉化：{fileResult.MissingChineseCount}");
        Console.WriteLine($"修改文件：{fileResult.ModifiedCount}");
        Console.WriteLine($"未变化文件：{fileResult.UnchangedCount}");
        Console.WriteLine($"删除文件：{fileResult.DeletedCount}");
        Console.WriteLine();

        // ===== 按分类统计 =====
        Console.WriteLine("========== 分类统计（需汉化文件数） ==========");
        var needFiles = fileResult.Entries
            .Where(e => e.Kind == FileDiffKind.New || e.Kind == FileDiffKind.MissingChinese || e.Kind == FileDiffKind.Modified)
            .ToList();
        foreach (TextCategory category in Enum.GetValues<TextCategory>())
        {
            var count = needFiles.Count(f => TextCategoryHelper.FromRelativePath(f.EnglishPath) == category);
            if (count > 0)
            {
                Console.WriteLine($"  {TextCategoryHelper.GetDisplayName(category)}: {count} 个文件");
            }
        }
        Console.WriteLine();


        foreach (var entry in fileResult.Entries
                     .Where(e => e.Kind == FileDiffKind.New || e.Kind == FileDiffKind.MissingChinese || e.Kind == FileDiffKind.Modified)
                     .Take(30))
        {
            Console.WriteLine($"  [{entry.Kind}] {entry.EnglishPath}");
        }

        // 提取新文件
        if (extract)
        {
            var pendingDir = Path.Combine(projectRoot, "data", "work", "pending");
            var extractResult = NewFileExtractor.Extract(newEnDir, fileResult.Entries, pendingDir);
            Console.WriteLine();
            Console.WriteLine($"[调试] 已提取 {extractResult.CopiedCount} 个需要汉化的文件到: {pendingDir}");
            foreach (var f in extractResult.Files.Take(30))
            {
                Console.WriteLine($"  -> {f}");
            }
        }

        // ===== 扫描术语 =====
        if (scanTerms)
        {
            Console.WriteLine("========== 候选术语扫描 ==========");
            var scanResult = service.Analyze(oldEnDir, oldZhDir, newEnDir);
            var entriesToScan = scanResult.Entries
                .Where(ProductionTranslationPlanBuilder.IsTranslationRequired) // P3：共享谓词
                // 旧内联动作过滤（TranslateModified / TranslateMissing）已由共享谓词取代
                .Where(entry => !string.IsNullOrWhiteSpace(entry.NewSourceText))
                .ToList();

            var glossary = new LimbusTranslator.Infrastructure.Glossary.GlossaryService(
                Path.Combine(projectRoot, "config"));
            var characterStyles = new CharacterStyleService(Path.Combine(projectRoot, "config"));
            var candidates = TermScanner.ScanDetailed(
                entriesToScan.Select(entry => entry.NewSourceText!),
                glossary.Entries,
                minOccurrence: 2,
                excludedTerms: characterStyles.Styles.Keys);

            var newCount = candidates.Count(candidate => !candidate.IsExisting);
            Console.WriteLine($"扫描待翻译字段: {entriesToScan.Count} 条，新术语: {newCount} 个，已收录: {candidates.Count - newCount} 个");
            Console.WriteLine();
            foreach (var candidate in candidates.Take(60))
            {
                var status = candidate.IsExisting
                    ? $"已收录：{candidate.ExistingTranslation}"
                    : "新术语";
                Console.WriteLine($"  [{status}] {candidate.OriginalText}  (出现 {candidate.OccurrenceCount} 次)");
            }
            return 0;
        }

        Console.WriteLine();

        // ===== 条目级对比 =====
        Console.WriteLine("========== 条目级 Diff 结果 ==========");
        var progressCount = 0;
        var result = service.Analyze(oldEnDir, oldZhDir, newEnDir, (done, total, file) =>
        {
            progressCount++;
            if (progressCount % 100 == 0 || done == total)
            {
                Console.WriteLine($"[调试] 处理进度: {done}/{total}  当前文件: {file}");
            }
        });

        // 第9.0B-P3轮：三语快照 + 韩文 Canonical Diff（与 WPF 共用 ProductionTranslationPlanBuilder.TryCapture）。
        // 失败 / 缺 KR 目录只记录日志并返回 null，不影响 EN_ONLY 主链。
        var capture = ProductionTranslationPlanBuilder.TryCapture(
            projectRoot,
            newEnDir,
            Path.Combine(projectRoot, "config"),
            result.NewUnits,
            msg => Console.WriteLine(msg));

        Console.WriteLine();
        Console.WriteLine("========== Diff 分析结果 ==========");
        Console.WriteLine($"新增文本：{result.AddedCount}");
        Console.WriteLine($"修改文本：{result.ModifiedCount}");
        Console.WriteLine($"未变化文本：{result.UnchangedCount}");
        Console.WriteLine($"删除文本：{result.DeletedCount}");
        Console.WriteLine($"缺失旧译：{result.MissingTranslationCount}");
        Console.WriteLine();
        Console.WriteLine($"总计条目：{result.Entries.Count}");

        // 输出部分示例
        Console.WriteLine();
        Console.WriteLine("========== 前 10 条 Inherit 示例 ==========");
        foreach (var entry in result.Entries.Where(e => e.Action == Core.Models.TranslationAction.Inherit).Take(10))
        {
            Console.WriteLine($"  [{entry.Key.RelativeFilePath}] {entry.OldSourceText}");
            Console.WriteLine($"    -> {entry.Translation}");
        }

        Console.WriteLine();
        Console.WriteLine("========== 前 10 条 TranslateMissing 示例 ==========");
        foreach (var entry in result.Entries.Where(e => e.Action == Core.Models.TranslationAction.TranslateMissing).Take(10))
        {
            Console.WriteLine($"  [{entry.Key.RelativeFilePath}] {entry.NewSourceText}");
        }

        // ===== 翻译执行（--translate）=====
        if (translate)
        {
            Console.WriteLine();
            Console.WriteLine("========== 汉化翻译（多 Agent 并发） ==========");
            // 第7轮 fail-closed：配置无效 → 明确报错并非零退出。
            // 绝不自动切换到 Mock（否则用户会以为「DeepSeek 翻译成功」）。
            var configDir = Path.Combine(projectRoot, "config");
            var settings = AppSettingsLoader.LoadProviderSettings(configDir);
            if (!settings.Success)
            {
                Console.WriteLine("[错误] 无法启动翻译：翻译配置无效。");
                foreach (var error in settings.Errors)
                {
                    Console.WriteLine($"[错误]   配置错误：{error}");
                }

                Console.WriteLine("[错误] 已停止翻译：不会自动改用模拟翻译（Mock）。");
                return 2;
            }

            // 第4轮：request_cache / Trace（TM 命中不会进入 request_cache）
            var runContext = TranslationRunContext.Create();
            Console.WriteLine($"[调试] 本次运行 RunId: {runContext.RunId}");

            var tmDbPath = Path.Combine(projectRoot, "data", "cache", "translation_memory.db");
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = tmDbPath },
                msg => Console.WriteLine(msg));
            var traceWriter = new TranslationTraceWriter(projectRoot, runContext, msg => Console.WriteLine(msg));

            var cacheServices = TranslationCacheServices.Create(memory, runContext, traceWriter, msg => Console.WriteLine(msg));
            Console.WriteLine($"[调试] Request Cache: {tmDbPath}");
            Console.WriteLine($"[调试] 翻译 Trace: {traceWriter.FilePath}");

            var bootstrap = TranslationRunBootstrap.CreateProvider(
                settings, configDir, cacheServices, msg => Console.WriteLine(msg));
            if (!bootstrap.CanStart || bootstrap.Provider is null)
            {
                Console.WriteLine("[错误] 已停止翻译：Provider 未能创建。");
                return 2;
            }

            var provider = bootstrap.Provider;

            // 第5轮：邻句上下文索引（每次运行一次；失败则本次运行无上下文）
            // 第9.0B 最终轮：来源语言与模式一致（KR_JP → 日文；KR_ONLY → 韩文；EN_ONLY / KR_EN → 英文）
            TranslationContextBuilder contextBuilder;
            try
            {
                var neighborUnits = ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(
                    bootstrap.TranslationMode, capture, result.NewUnits);
                var contextIndex = TranslationContextIndex.Build(neighborUnits, msg => Console.WriteLine(msg));
                Console.WriteLine($"[调试] 邻句上下文索引: 作用域 {contextIndex.ScopeCount}，对话行 {contextIndex.NodeCount}（跳过 {contextIndex.SkippedUnitCount}）"
                                  + $"｜来源语言={SourceLanguageHelper.ToCode(TranslationModePolicy.GetNeighborSourceLanguage(bootstrap.TranslationMode))}");
                contextBuilder = new TranslationContextBuilder(contextIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[调试] 邻句上下文索引构建失败，本次运行不使用上下文: {ex.Message}");
                contextBuilder = TranslationContextBuilder.Disabled;
            }

            // Coordinator 多 Agent 并发
            var coordinator = new Coordinator(
                provider, memory,
                maxConcurrentAgents: 8,
                maxConcurrentApiRequests: settings.Options.MaxConcurrentRequests,
                // 第2轮：统一 ValidatorPipeline（术语需求来自 config/glossary.json）
                validation: ValidationPipeline.CreateDefault(Path.Combine(projectRoot, "config")),
                cacheServices: cacheServices,
                contextBuilder: contextBuilder,
                log: msg => Console.WriteLine(msg));

            try
            {
                // 第9.0B-P3轮：统一生产顺序（修复 P1-1 / P1-2；与 WPF / 测试同源）
                //   候选（英文输出结构 + KR 独有 Key）→ ApplyToEntries（Canonical 韩文决定最终动作）
                //   → 按最终动作过滤 → 只有 TranslateNew / TranslateModified / TranslateMissing 进入 Agent
                // 第9.0B-P7轮：旧中文解析范围（EN_ONLY → EN 权威；KR 三模式 → Current EN ∪ Current KR 文件集）
                var oldChineseScope = OldChineseScopeLoader.Load(
                    bootstrap.TranslationMode,
                    result.OldChineseUnits,
                    capture,
                    oldZhDir,
                    configDir);
                Console.WriteLine($"[调试] {oldChineseScope.Describe()}");

                var plan = ProductionTranslationPlanBuilder.Build(
                    bootstrap.TranslationMode,
                    result.Entries,
                    capture,
                    newEnDir,
                    oldChineseScope.Units,
                    bootstrap.GlossarySnapshot);
                Console.WriteLine($"[调试] 术语匹配（单次共享）：注入条目 {plan.MatchedTermsInjectedCount} 条");
                Console.WriteLine($"[调试] {plan.Describe()}");
                if (!plan.HasCanonicalCapture)
                {
                    Console.WriteLine("[调试] 未获得本次分析的三语捕获结果：四模式接线已跳过（KR 模式将退回纯源文语义）。");
                }

                var needTranslate = plan.NeedTranslate;

                Console.WriteLine($"[调试] 需要翻译的条目: {needTranslate.Count}, 按文件分组为 {needTranslate.Select(e => e.Key.RelativeFilePath).Distinct().Count()} 个 Stage");

                var coordResult = await coordinator.ExecuteAsync(needTranslate,
                    (done, total, stage) =>
                    {
                        if (done % 10 == 0 || done == total)
                        {
                            Console.WriteLine($"[调试] Stage 进度: {done}/{total}  当前: {stage}");
                        }
                    });

                Console.WriteLine($"[调试] Agent 汇总: 成功 {coordResult.SuccessCount}, 失败 {coordResult.FailedCount}, 总翻译 {coordResult.TotalTranslated}");
                foreach (var failed in coordResult.Agents.Where(a => !a.IsSuccess))
                {
                    Console.WriteLine($"[调试]   失败 Stage: {failed.StageId} - {failed.Error}");
                }

                // 合并 Inherit 译文（第9.0B-P4轮：output 条目 = 权威结构下允许/必须输出的条目）
                var final = Coordinator.CollectTranslations(plan.OutputEntries);
                Console.WriteLine($"[调试] 最终译文数: {final.Count}（output 条目 {plan.ExpectedOutputKeys.Count}）");

                Console.WriteLine();
                Console.WriteLine("========== 前 10 条翻译示例 ==========");
                foreach (var entry in result.Entries.Where(e => e.Translation is not null).Take(10))
                {
                    Console.WriteLine($"  [{entry.Key.RelativeFilePath}] {entry.NewSourceText}");
                    Console.WriteLine($"    -> {entry.Translation}");
                }

                // 输出新版中文文件到 data/output/，缺少任一预期条目时整文件不写入。
                // 第9.0B-P4轮：模板 = 权威结构（EN_ONLY → 英文目录；KR 三模式 → 韩文目录）
                var outputRoot = Path.Combine(projectRoot, "data", "output");
                var merge = new MergeOutputService();
                var outputResult = merge.MergeAllWithReport(
                    plan.AuthoritativeDirectory,
                    final,
                    outputRoot,
                    plan.ExpectedOutputKeys,
                    plan.AuthoritativeLanguage);
                // 第3轮：先计算发布门禁（补齐未校验的历史继承条目），再写入清单
                // 第9.0B-P4轮：Gate 使用与 Merge 完全相同的权威 Key 集（Missing / Unexpected）
                var keySet = new ReleaseGateKeySet
                {
                    ExpectedKeys = plan.ExpectedOutputKeys,
                    OutputKeys = outputResult.WrittenKeys,
                    AuthoritativeSourceCode = SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage),
                };
                var gateResult = ReleaseGateService.Evaluate(
                    plan.OutputEntries,
                    ValidationPipeline.CreateDefault(Path.Combine(projectRoot, "config")),
                    null,
                    keySet);
                var manifest = OutputManifestService.Save(outputRoot, outputResult, gateResult);
                Console.WriteLine($"[调试] 发布门禁: {gateResult.Status}；Error {gateResult.ErrorCount} / Warning {gateResult.WarningCount} / 待审核 {gateResult.NeedsReviewCount} / 阻断性 Error {gateResult.BlockingErrorCount} / 历史继承结构问题 {gateResult.HistoricalInheritedErrorCount}");
                foreach (var reason in gateResult.Reasons)
                {
                    Console.WriteLine($"[调试]   门禁原因 [{reason.Kind}/{reason.Code}] {reason.Message}");
                }
                Console.WriteLine();
                Console.WriteLine($"[调试] 输出核验: 预期文件 {outputResult.RequestedFileCount}，已写入 {outputResult.WrittenFileCount}，预期条目 {outputResult.RequestedEntryCount}，已校验 {outputResult.VerifiedEntryCount}，问题 {outputResult.Issues.Count}");
                Console.WriteLine($"[调试] 输出清单: {OutputManifestService.GetManifestPath(outputRoot)}，完整={manifest.IsComplete}");
                foreach (var f in outputResult.Files.Take(10))
                {
                    Console.WriteLine($"  -> {f}");
                }
                foreach (var issue in outputResult.Issues.Take(20))
                {
                    var target = issue.TranslationKey ?? issue.RelativeFilePath ?? "未定位目标";
                    Console.WriteLine($"[调试] 输出问题 [{issue.Kind}] {target}: {issue.Message}");
                }
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }

        return 0;
    }

    private static (string? OldEnDir, string? OldZhDir, string? NewEnDir, bool AutoLocate, bool Extract, bool Translate, bool ScanTerms,
        RealApiSmokeMode SmokeMode, int SmokeItemCount, string? SmokeRunId, bool P6Smoke, bool P6FourModes, string? P6FourModeLocalizeRoot, bool P6DryRun,
        bool AuditStructure, string? AuditLocalizeRoot, string? AuditOldZhRoot,
        bool GuiDemoWorkspace, string? GuiDemoRoot,
        bool AnalyzeTiming, string? AnalyzeTimingLocalizeRoot)? ParseArgs(string[] args)
    {
        string? oldEn = null, oldZh = null, newEn = null;
        var autoLocate = false;
        var extract = false;
        var translate = false;
        var scanTerms = false;
        var p6Smoke = false;
        var p6FourModes = false;
        string? p6FourModeLocalizeRoot = null;
        var p6DryRun = false;
        var auditStructure = false;
        var guiDemoWorkspace = false;
        string? guiDemoRoot = null;
        var analyzeTiming = false;
        string? analyzeTimingLocalizeRoot = null;
        string? auditLocalizeRoot = null;
        string? auditOldZhRoot = null;
        var smokeMode = RealApiSmokeMode.None;
        var smokeItemCount = RealApiSmokeSelector.DefaultItemCount;
        var smokeItemCountSpecified = false;
        string? smokeRunId = null;

        bool SetSmokeMode(RealApiSmokeMode value)
        {
            if (smokeMode != RealApiSmokeMode.None)
            {
                return false;
            }

            smokeMode = value;
            return true;
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--old-en" when i + 1 < args.Length: oldEn = args[++i]; break;
                case "--old-zh" when i + 1 < args.Length: oldZh = args[++i]; break;
                case "--new-en" when i + 1 < args.Length: newEn = args[++i]; break;
                case "--auto-locate": autoLocate = true; break;
                case "--extract": extract = true; break;
                case "--translate": translate = true; break;
                case "--scan-terms": scanTerms = true; break;
                case "--diagnose": break;
                case "--p6-real-smoke": p6Smoke = true; break;
                case "--p6-real-smoke-four-modes": p6FourModes = true; break;
                case "--p6-real-smoke-dry-run": p6DryRun = true; break;
                case "--p6-real-smoke-localize-root" when i + 1 < args.Length: p6FourModeLocalizeRoot = args[++i]; break;
                case "--audit-localize-structure": auditStructure = true; break;
                case "--gui-demo-workspace": guiDemoWorkspace = true; break;
                case "--gui-demo-root" when i + 1 < args.Length: guiDemoRoot = args[++i]; break;
                case "--analyze-timing": analyzeTiming = true; break;
                case "--analyze-timing-localize-root" when i + 1 < args.Length: analyzeTimingLocalizeRoot = args[++i]; break;
                case "--audit-localize-root" when i + 1 < args.Length: auditLocalizeRoot = args[++i]; break;
                case "--audit-old-zh-root" when i + 1 < args.Length: auditOldZhRoot = args[++i]; break;
                case "--real-api-smoke-backup-pre" when SetSmokeMode(RealApiSmokeMode.BackupPreMigration): break;
                case "--real-api-smoke-migrate" when SetSmokeMode(RealApiSmokeMode.MigrateOnly): break;
                case "--real-api-smoke-backup-post" when SetSmokeMode(RealApiSmokeMode.BackupPostMigration): break;
                case "--real-api-smoke-first" when SetSmokeMode(RealApiSmokeMode.FirstRealApi): break;
                case "--real-api-smoke-tm-replay" when SetSmokeMode(RealApiSmokeMode.ExactTmReplay): break;
                case "--real-api-smoke-create-cache-clone" when SetSmokeMode(RealApiSmokeMode.CreateCacheReplayClone): break;
                case "--real-api-smoke-cache-replay" when SetSmokeMode(RealApiSmokeMode.CacheReplay): break;
                case "--real-api-smoke-count" when i + 1 < args.Length
                    && int.TryParse(args[i + 1], out var parsedCount):
                    smokeItemCount = parsedCount;
                    smokeItemCountSpecified = true;
                    i++;
                    break;
                case "--real-api-smoke-run-id" when i + 1 < args.Length:
                    smokeRunId = args[++i];
                    break;
                default:
                    return null;
            }
        }
        if (smokeMode != RealApiSmokeMode.None
            && (extract || translate || scanTerms || p6Smoke || p6FourModes || auditStructure
                || (smokeItemCountSpecified && smokeMode != RealApiSmokeMode.FirstRealApi)))
        {
            return null;
        }

        if (p6Smoke && (extract || translate || scanTerms || auditStructure || p6FourModes))
        {
            return null;
        }

        if (p6FourModes && (extract || translate || scanTerms || auditStructure))
        {
            return null;
        }

        if (!p6FourModes && (p6FourModeLocalizeRoot is not null || p6DryRun))
        {
            return null;
        }

        if (auditStructure && (extract || translate || scanTerms))
        {
            return null;
        }

        if (!auditStructure && (auditLocalizeRoot is not null || auditOldZhRoot is not null))
        {
            return null;
        }

        if (!guiDemoWorkspace && guiDemoRoot is not null)
        {
            return null;
        }

        if (!analyzeTiming && analyzeTimingLocalizeRoot is not null)
        {
            return null;
        }

        return (oldEn, oldZh, newEn, autoLocate, extract, translate, scanTerms,
            smokeMode, smokeItemCount, smokeRunId, p6Smoke, p6FourModes, p6FourModeLocalizeRoot, p6DryRun,
            auditStructure, auditLocalizeRoot, auditOldZhRoot, guiDemoWorkspace, guiDemoRoot,
            analyzeTiming, analyzeTimingLocalizeRoot);
    }

    private static void PrintUsage()
    {
        Console.WriteLine("用法:");
        Console.WriteLine("  LimbusTranslator.Cli [--old-en <目录>] [--old-zh <目录>] [--new-en <目录>]");
        Console.WriteLine("                     [--auto-locate] [--extract] [--translate]");
        Console.WriteLine("  --auto-locate  自动定位游戏安装目录");
        Console.WriteLine("  --extract      将新增/缺失/修改文件复制到 data/work/pending/");
        Console.WriteLine("  --translate    执行汉化翻译（配置无效时会拒绝启动，不会自动改用模拟翻译）");
        Console.WriteLine();
        Console.WriteLine("第8轮受控真实冒烟（与 --translate 互斥，始终禁止 Deploy）：");
        Console.WriteLine("  --real-api-smoke-backup-pre");
        Console.WriteLine("  --real-api-smoke-migrate");
        Console.WriteLine("  --real-api-smoke-backup-post");
        Console.WriteLine("  --real-api-smoke-first --auto-locate [--real-api-smoke-count 10~30]");
        Console.WriteLine("  --real-api-smoke-tm-replay --auto-locate --real-api-smoke-run-id <RunId>");
        Console.WriteLine("  --real-api-smoke-create-cache-clone --real-api-smoke-run-id <RunId>");
        Console.WriteLine("  --real-api-smoke-cache-replay --auto-locate --real-api-smoke-run-id <RunId>");
        Console.WriteLine();
        Console.WriteLine("第9.0B-P6 真实 DeepSeek 8 条 E2E Smoke（显式命令；TEMP 全隔离；硬上限 8 单元 / 8 请求；禁止 Deploy）：");
        Console.WriteLine("  --p6-real-smoke [--real-api-smoke-run-id <后缀>]");
        Console.WriteLine();
        Console.WriteLine("  第9.0B 最终真实四模式 Smoke（显式命令；2 条真实单元 × 4 模式 = 8 单元；TEMP 全隔离；禁止 Deploy）：");
        Console.WriteLine("  --p6-real-smoke-four-modes [--p6-real-smoke-localize-root <Localize目录>] [--real-api-smoke-run-id <后缀>] [--p6-real-smoke-dry-run]");
        Console.WriteLine();
        Console.WriteLine("  GUI 验收演示工作区（TEMP；不写生产数据，不调用 API）：");
        Console.WriteLine("  --gui-demo-workspace [--gui-demo-root <目录>]");
        Console.WriteLine();
        Console.WriteLine("  真实数据阶段耗时诊断（只读游戏目录；快照写在 TEMP；不调用 API）：");
        Console.WriteLine("  --analyze-timing [--analyze-timing-localize-root <Localize目录>] [--real-api-smoke-run-id <模式码>]");
        Console.WriteLine();
        Console.WriteLine("第9.0B-P7 真实多语言结构差异只读审计（显式命令；绝不修改游戏目录；产物写 %TEMP%）：");
        Console.WriteLine("  --audit-localize-structure [--audit-localize-root <Localize 目录>] [--audit-old-zh-root <旧中文目录>]");
    }

    /// <summary>本地化目录默认位置（游戏根/LimbusCompany_Data/Assets/Resources_moved/Localize）。</summary>
    private static string? ResolveDefaultLocalizeRoot()
    {
        var located = GameDirectoryLocator.AutoLocate();
        var englishDir = located?.NewEnglishDir;
        return string.IsNullOrWhiteSpace(englishDir) ? null : Directory.GetParent(englishDir)?.FullName;
    }

    private enum RealApiSmokeMode
    {
        None,
        BackupPreMigration,
        MigrateOnly,
        BackupPostMigration,
        FirstRealApi,
        ExactTmReplay,
        CreateCacheReplayClone,
        CacheReplay,
    }

    /// <summary>
    /// 向上查找项目根目录（含 LimbusTranslator.sln 或 .slnx 的目录）。
    /// </summary>
    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.sln"))
                || File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

}
