using System.IO;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 「从 output 载入当前汉化进度」（第9.0C.8轮）。
///
/// 解决的真实问题：界面状态（Diff 统计 / 待审核列表 / 门禁 / 部署清单）全部绑定"本轮 Run"，
/// 重启程序后此前翻译的进度在界面上就看不见了（只在 TM 与 data/output 里）。
///
/// 本按钮做两件事：
///   ① **载入界面**：把 `data/output/**.json` 里已写好的译文按 UnitKey 覆盖到生产计划条目
///      （来源=<see cref="TranslationSource.Imported"/>）⇒ 可继续逐条查看 / 批量替换 /
///      「审核后重新输出」/「部署到游戏」，**不需要重新翻译、不调用 API**；
///   ② **导入 TM**：同一批条目用**单事务**写入翻译记忆（`SaveMany`），以后翻译直接命中、不重复花钱。
///
/// 语义边界：只读 output，不改任何文件；不改 Diff / 动作语义（Action 保持不变）；
/// 只覆盖"当前权威结构里存在且 output 里找得到"的条目。
/// </summary>
public sealed partial class MainViewModel
{
    private string _outputProgressStatusText = "（尚未载入 output 进度）";

    /// <summary>最近一次载入进度的摘要。</summary>
    public string OutputProgressStatusText
    {
        get => _outputProgressStatusText;
        private set { _outputProgressStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>从 output 载入当前汉化进度（载入界面 + 导入 TM）。</summary>
    public async void LoadProgressFromOutput()
    {
        if (IsBusy)
        {
            return;
        }

        if (_lastPlan is null)
        {
            StatusText = "请先点「分析更新」，再点本按钮（需要生产计划才能把 output 译文对回到条目）";
            Log("[调试] 载入 output 进度：尚未分析（没有生产计划），已取消");
            return;
        }

        var outputRoot = Path.Combine(FindProjectRoot(), "data", "output");
        if (!Directory.Exists(outputRoot))
        {
            StatusText = "没有找到 data/output（请先翻译，或点「从缓存恢复 output」）";
            Log($"[调试] 载入 output 进度：目录不存在 {outputRoot}");
            return;
        }

        IsBusy = true;
        StatusText = "从 output 载入当前汉化进度中...";
        BeginProgress("从 output 载入进度", "读取 output 译文");
        try
        {
            var plan = _lastPlan;
            var tmDbPath = TmDatabasePath;
            var outcome = await Task.Run(() => LoadProgressCore(plan, outputRoot, tmDbPath));

            ReviewEntries.Clear();
            foreach (var entry in outcome.ForReview)
            {
                ReviewEntries.Add(entry);
            }

            ReviewCount = outcome.ForReview.Count(entry => entry.NeedsReview);
            ApplyReviewFilter();

            OutputProgressStatusText =
                $"已载入 {outcome.Applied.Count} 条（其中需 AI 的 {outcome.ForReview.Count} 条进入逐条列表）"
                + $"；output 文件 {outcome.Loaded.FileCount} 个；写入 TM {outcome.TmWritten} 条";
            StatusText = $"已从 output 载入进度：{outcome.Applied.Count} 条（TM {outcome.TmWritten} 条）";

            Log($"[调试] 从 output 载入进度：{outcome.Loaded.Describe()}");
            Log($"[调试] 覆盖条目 {outcome.Applied.Count} 条（来源=Imported，NeedsReview {ReviewCount} 条）；写入 TM {outcome.TmWritten} 条"
                + "（注意：更换翻译模式后，因模式盐不同，导入的 TM 不会命中）");
            Log("[调试] 现在可直接逐条查看 / 批量替换 /「审核后重新输出」/「部署到游戏」，无需重新翻译。");

            CompleteProgress("已载入 output 进度");
        }
        catch (Exception ex)
        {
            StatusText = "载入 output 进度失败";
            OutputProgressStatusText = $"载入失败：{ex.Message}";
            Log($"[调试] 从 output 载入进度失败: {ex.Message}");
            FailProgress("载入 output 进度失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 载入进度的工作体（**在后台线程执行**：读 900+ 个 output 文件、覆盖条目、重新校验、单事务写 TM）。
    /// </summary>
    private (OutputProgressLoadResult Loaded, List<DiffEntry> Applied, List<DiffEntry> ForReview, int TmWritten)
        LoadProgressCore(ProductionTranslationPlan plan, string outputRoot, string tmDbPath)
    {
        SetProgressStage("读取 output 译文");
        var loaded = OutputProgressLoader.Load(outputRoot, plan.OutputEntries);

        SetProgressStage("覆盖到生产计划条目");
        var applied = new List<DiffEntry>();
        foreach (var entry in plan.OutputEntries)
        {
            if (!loaded.Translations.TryGetValue(entry.Key.ToString(), out var text))
            {
                continue;
            }

            entry.Translation = text;
            entry.Provenance = TranslationSource.Imported;
            entry.TmMatchType = TranslationMemoryMatchType.None;
            applied.Add(entry);
        }

        SetProgressStage("重新校验载入的条目");
        var pipeline = CreateValidationPipeline();
        foreach (var entry in applied)
        {
            if (entry.ValidationIssues.Count > 0)
            {
                continue;
            }

            try
            {
                pipeline.ValidateAndApply(entry);
            }
            catch (Exception ex)
            {
                Log($"[调试] 载入进度：条目重新校验失败（已跳过）：{ex.Message}");
            }
        }

        SetProgressStage("写入翻译记忆");
        var tmWritten = 0;
        try
        {
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = tmDbPath },
                msg => Log(msg));
            tmWritten = memory.SaveMany(applied.Select(entry => (
                Unit: TranslationUnitFactory.FromDiffEntry(entry),
                Result: new TranslationResult
                {
                    Key = entry.Key,
                    Translation = entry.Translation ?? string.Empty,
                    Source = TranslationSource.Imported,
                    NeedsReview = entry.NeedsReview,
                    ReviewReason = entry.ReviewReason,
                    TmMatchType = TranslationMemoryMatchType.None,
                    Issues = entry.ValidationIssues,
                })));
        }
        catch (Exception ex)
        {
            Log($"[调试] 载入进度：写入翻译记忆失败（界面载入仍然有效）：{ex.Message}");
        }

        // 逐条列表只放"需要 AI"的条目（继承条目属于既有汉化，不需要逐条看）
        var forReview = applied
            .Where(ProductionTranslationPlanBuilder.IsTranslationRequired)
            .ToList();

        return (loaded, applied, forReview, tmWritten);
    }
}