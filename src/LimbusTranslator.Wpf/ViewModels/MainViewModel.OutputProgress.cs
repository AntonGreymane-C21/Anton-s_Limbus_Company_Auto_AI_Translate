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

            // 第9.0C.12轮：待审核列表统一走**分页入口**（完整集合 + 每页 5000 条，可翻页）。
            // 修复前这里是"手工往 ReviewEntries 里塞 + ApplyReviewFilter()"：
            // 而分页版 ApplyReviewFilter 以 _reviewAllEntries 为准（本路径从未填充它）
            // ⇒ 过滤器把列表清空、界面显示"全量 0 条"，看起来载入进度后什么都没进来。
            // 第9.0C.19轮（口径统一）：列表来源改为 outcome.Applied（载入到的全部条目），
            // 与「开始汉化」完成后的 SetReviewSource(selectedEntries) 同口径；
            // 修复前用 ForReview（只含"需要 AI"的条目）⇒ 载入后列表从 1 万条缩到几百条。
            SetReviewSource(outcome.Applied);
            ApplyLoadedProgressStatistics(plan);

            OutputProgressStatusText =
                $"已载入 {outcome.Applied.Count} 条（全部进入逐条列表；其中需要 AI 的 {outcome.ForReview.Count} 条）"
                + $"；{outcome.Loaded.Describe()}；已写入 TM {outcome.TmWritten} 条"
                + "\n说明：output 只包含此前**写出过**的文件（未写出的文件不会被载入）——上面「命中/文件缺失」即实际覆盖范围。";
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
        // 第9.0C.19轮：这里仍然保留 forReview —— 但**只用于摘要计数**；
        // 逐条列表已改为 applied（与「开始汉化」后的口径一致），避免"载入后只剩几百条"的误判。
        var forReview = applied
            .Where(ProductionTranslationPlanBuilder.IsTranslationRequired)
            .ToList();

        return (loaded, applied, forReview, tmWritten);
    }

    /// <summary>
    /// 第9.0C.19轮：**载入进度后刷新工作流统计**（状态栏「待翻译」/ 仪表盘 / 审核徽章）。
    ///
    /// 为什么需要：<c>NeedTranslateCount</c> 只在"分析完成"时写入一次（<c>CompleteAnalyze</c>），
    /// 载入进度后不刷新 ⇒ 明明载入了十万条，界面仍显示分析时的旧值，看起来"什么都没载入"。
    ///
    /// 口径：把**已有译文**（本次载入的 Imported / 之前的 AI / 人工确认）从"待翻译"里扣除：
    /// <c>仍待翻译 = plan.NeedTranslate.Where(译文为空)</c>。
    /// 副作用（合理）：全部载入后「开始汉化」会自动禁用（<c>CanTranslate</c> 依赖该计数）。
    /// </summary>
    private void ApplyLoadedProgressStatistics(ProductionTranslationPlan plan)
    {
        var before = _workflow.NeedTranslateCount;
        var stillMissing = plan.NeedTranslate.Count(entry => string.IsNullOrWhiteSpace(entry.Translation));
        var withTranslation = plan.OutputEntries.Count(entry => !string.IsNullOrWhiteSpace(entry.Translation));

        _workflow.SetNeedTranslateCount(stillMissing);
        _workflow.SetNeedReviewCount(ReviewCount);
        NotifyWorkflowBindings();

        Log($"[调试] 载入后统计刷新：待翻译 {before} → {_workflow.NeedTranslateCount}"
            + $"（当前有译文 {withTranslation} 条）；待审核 {ReviewCount}");
    }
}