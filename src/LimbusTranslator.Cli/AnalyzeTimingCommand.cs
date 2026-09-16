using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;

namespace LimbusTranslator.Cli;

/// <summary>
/// 第9.0C.1轮：**真实数据阶段耗时诊断**（只读游戏目录；快照写在 TEMP，不污染生产基线）。
///
/// 用途：验证「分析在后台线程执行且各阶段耗时可控」，并可作为 GUI 卡顿问题的回归测量工具。
/// 不调用任何 API、不写游戏目录、不改生产快照。
/// </summary>
internal static class AnalyzeTimingCommand
{
    public static async Task<int> RunAsync(string projectRoot, string localizeRoot, string? modeCode)
    {
        var configDir = Path.Combine(projectRoot, "config");
        var newEnglishDir = Path.Combine(localizeRoot, "en");
        if (!Directory.Exists(newEnglishDir))
        {
            Console.WriteLine($"[错误] 未找到英文目录：{newEnglishDir}");
            return 1;
        }

        var mode = TranslationModeCodes.Default;
        if (!string.IsNullOrWhiteSpace(modeCode))
        {
            var parsed = TranslationModeCodes.TryParseCode(modeCode);
            if (parsed is null)
            {
                Console.WriteLine($"[错误] 不支持的模式：{modeCode}（可用 en_only / kr_en / kr_jp / kr_only）");
                return 1;
            }

            mode = parsed.Value;
        }

        // 旧中文目录使用 TEMP 空目录（本命令只测「扫描 + 解析 + Canonical Diff + 计划」耗时）
        var emptyOldChinese = Path.Combine(Path.GetTempPath(), $"limbus_timing_oldzh_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyOldChinese);

        // 快照必须隔离：绝不推进生产基线
        var snapshotRoot = Path.Combine(Path.GetTempPath(), $"limbus_timing_snap_{Guid.NewGuid():N}");
        Directory.CreateDirectory(snapshotRoot);

        Console.WriteLine("[调试] ===== 真实数据阶段耗时诊断（只读游戏目录；快照写在 TEMP） =====");
        Console.WriteLine($"[调试] 模式：{TranslationModePresentation.ShortName(mode)}（{TranslationModeCodes.ToCode(mode)}）");
        Console.WriteLine($"[调试] Localize：{localizeRoot}");
        Console.WriteLine($"[调试] 快照根（TEMP，隔离）：{snapshotRoot}");
        Console.WriteLine($"[调试] 调用线程 ManagedThreadId={Environment.CurrentManagedThreadId}（分析将在后台线程执行）");

        var service = new ProductionAnalyzeService(configDir);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await service.RunAsync(
                new ProductionAnalyzeRequest(
                    projectRoot,
                    newEnglishDir,
                    emptyOldChinese,
                    newEnglishDir,
                    mode,
                    configDir,
                    snapshotRoot),
                new Progress<AnalyzeStageReport>(report =>
                    Console.WriteLine(report.HasKnownTotal
                        ? $"[调试] 进度：{report.Stage} {report.Done}/{report.Total}"
                        : $"[调试] 进度：{report.Stage}")),
                Console.WriteLine);

            stopwatch.Stop();
            Console.WriteLine("[调试] ===== 阶段耗时（含线程 Id） =====");
            foreach (var timing in result.Timings)
            {
                Console.WriteLine($"    {timing.Describe()}");
            }

            Console.WriteLine("[调试] ===== 结果统计 =====");
            Console.WriteLine($"[调试] 文件级：新增 {result.FileResult.NewCount}｜缺失汉化 {result.FileResult.MissingChineseCount}"
                              + $"｜修改 {result.FileResult.ModifiedCount}｜未变化 {result.FileResult.UnchangedCount}"
                              + $"｜删除 {result.FileResult.DeletedCount}");
            Console.WriteLine($"[调试] 条目级：总 {result.DiffResult.Entries.Count}｜新增 {result.DiffResult.AddedCount}"
                              + $"｜修改 {result.DiffResult.ModifiedCount}｜未变化 {result.DiffResult.UnchangedCount}"
                              + $"｜删除 {result.DiffResult.DeletedCount}｜缺失旧译 {result.DiffResult.MissingTranslationCount}");
            Console.WriteLine($"[调试] 三语单元：KR {Count(result.Capture?.CurrentUnitCounts, SourceLanguage.Korean)}"
                              + $"｜EN {Count(result.Capture?.CurrentUnitCounts, SourceLanguage.English)}"
                              + $"｜JP {Count(result.Capture?.CurrentUnitCounts, SourceLanguage.Japanese)}");
            Console.WriteLine($"[调试] 待翻译（生产计划）：{result.Plan.NeedTranslate.Count}");
            Console.WriteLine($"[调试] 分析总耗时（含后台线程）：{result.TotalElapsedMs}ms｜命令端到端：{stopwatch.ElapsedMilliseconds}ms");
            Console.WriteLine("[调试] 说明：本命令不写游戏目录、不写生产快照、不调用 API。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 耗时诊断失败：{ex.Message}");
            return 2;
        }
        finally
        {
            TryDelete(emptyOldChinese);
            TryDelete(snapshotRoot);
        }
    }

    private static int Count(IReadOnlyDictionary<SourceLanguage, int>? counts, SourceLanguage language)
        => counts is not null && counts.TryGetValue(language, out var value) ? value : 0;

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响结果
        }
    }
}
