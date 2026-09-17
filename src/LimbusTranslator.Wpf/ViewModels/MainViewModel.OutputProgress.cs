using System.IO;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Translation;

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
    /// <summary>完整快照写文件时的进度日志间隔（每 N 个文件记一行；避免 2000+ 行刷屏）（第9.0C.21轮）。</summary>
    private const int SnapshotProgressLogInterval = 200;

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

        await LoadProgressFromOutputCoreAsync(manageProgress: true);
    }

    /// <summary>
    /// 第9.0C.20轮：载入进度**主体**（抽出以便「生成完整快照」复用同一条路径）。
    /// <paramref name="manageProgress"/> = false ⇒ 不切换 IsBusy / 进度条（由调用方负责）。
    /// </summary>
    private async Task<bool> LoadProgressFromOutputCoreAsync(bool manageProgress)
    {

        if (_lastPlan is null)
        {
            StatusText = "请先点「分析更新」，再点本按钮（需要生产计划才能把 output 译文对回到条目）";
            Log("[调试] 载入 output 进度：尚未分析（没有生产计划），已取消");
            return false;
        }

        var outputRoot = Path.Combine(FindProjectRoot(), "data", "output");
        if (!Directory.Exists(outputRoot))
        {
            StatusText = "没有找到 data/output（请先翻译，或点「从缓存恢复 output」）";
            Log($"[调试] 载入 output 进度：目录不存在 {outputRoot}");
            return false;
        }

        if (manageProgress)
        {
            IsBusy = true;
            StatusText = "从 output 载入当前汉化进度中...";
            BeginProgress("从 output 载入进度", "读取 output 译文");
        }
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
                + $"\n来源：output {outcome.OutputLoadedCount} 条 + 翻译记忆补充 {outcome.TmSupplementedCount} 条；{outcome.Loaded.Describe()}"
                + $"；本次回写 TM {outcome.TmWritten} 条"
                + "\n说明：output 只包含此前**写出过**的文件；若仍不全，可点「生成完整快照」把全部权威文件写出后再载入。";
            StatusText = $"已从 output 载入进度：{outcome.Applied.Count} 条（TM {outcome.TmWritten} 条）";

            Log($"[调试] 从 output 载入进度：{outcome.Loaded.Describe()}");
            Log($"[调试] 载入条目 {outcome.Applied.Count} 条（output {outcome.OutputLoadedCount} + TM 补充 {outcome.TmSupplementedCount}；NeedsReview {ReviewCount}）；回写 TM {outcome.TmWritten} 条"
                + "（注意：更换翻译模式后，因模式盐不同，导入的 TM 不会命中）");
            Log("[调试] 现在可直接逐条查看 / 批量替换 /「审核后重新输出」/「部署到游戏」，无需重新翻译。");

            if (manageProgress)
            {
                CompleteProgress("已载入 output 进度");
            }

            return true;
        }
        catch (Exception ex)
        {
            StatusText = "载入 output 进度失败";
            OutputProgressStatusText = $"载入失败：{ex.Message}";
            Log($"[调试] 从 output 载入进度失败: {ex.Message}");
            FailProgress("载入 output 进度失败");
            return false;
        }
        finally
        {
            if (manageProgress)
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>
    /// 载入进度的工作体（**在后台线程执行**：读 900+ 个 output 文件、覆盖条目、重新校验、单事务写 TM）。
    /// </summary>
    private (OutputProgressLoadResult Loaded, List<DiffEntry> Applied, List<DiffEntry> ForReview, int TmWritten, int OutputLoadedCount, int TmSupplementedCount)
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

        var outputLoadedCount = applied.Count;

        // 第9.0C.20轮（P1）：output 只包含"此前写出过"的文件 ⇒ 其余条目再从 **翻译记忆（TM）** 补一次。
        // TM 才是本工具真正的累计进度（AI / 人工确认过的全部条目），且按当前模式的盐精确命中；
        // 命中后**保留 TM 里的原始来源**（AI / HumanReviewed），便于审核页正确显示。
        SetProgressStage("从翻译记忆补充载入");
        var tmSupplemented = 0;
        var tmLoadedKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var candidates = plan.OutputEntries
                .Where(entry => string.IsNullOrWhiteSpace(entry.Translation))
                .Where(entry => !string.IsNullOrWhiteSpace(entry.NewSourceText))
                .Select(entry => (
                    Entry: entry,
                    Hash: SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!, entry.SourceHashSalt)))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Hash))
                .ToList();

            if (candidates.Count > 0)
            {
                using var memory = new SqliteTranslationMemory(
                    new TranslationMemoryOptions { DatabasePath = tmDbPath },
                    msg => Log(msg));

                var wanted = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in candidates)
                {
                    wanted[pair.Entry.Key.ToString()] = pair.Hash;
                }

                var hits = memory.FindExactUnits(wanted);
                foreach (var pair in candidates)
                {
                    var key = pair.Entry.Key.ToString();
                    if (!hits.TryGetValue(key, out var hit) || string.IsNullOrWhiteSpace(hit.Translation))
                    {
                        continue;
                    }

                    pair.Entry.Translation = hit.Translation;
                    pair.Entry.Provenance = hit.Source;
                    pair.Entry.NeedsReview = hit.NeedsReview;
                    pair.Entry.ReviewReason = hit.ReviewReason;
                    pair.Entry.TmMatchType = TranslationMemoryMatchType.ExactUnit;
                    applied.Add(pair.Entry);
                    tmLoadedKeys.Add(key);
                    tmSupplemented++;
                }

                Log($"[调试] 载入进度：TM 补充候选 {candidates.Count} 条 ⇒ 精确命中 {tmSupplemented} 条（按当前模式盐匹配）");
            }
        }
        catch (Exception ex)
        {
            Log($"[调试] 载入进度：从翻译记忆补充失败（output 载入结果仍然有效）：{ex.Message}");
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
            tmWritten = memory.SaveMany(applied
                // 第9.0C.20轮：从 TM 补充进来的条目**不回写** TM —— 否则会把它们的原始来源
                //（AI / HumanReviewed）覆盖成 Imported，丢失"已人工确认"的可信标记。
                .Where(entry => !tmLoadedKeys.Contains(entry.Key.ToString()))
                .Select(entry => (
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

        return (loaded, applied, forReview, tmWritten, outputLoadedCount, tmSupplemented);
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

    /// <summary>
    /// 第9.0C.20轮（P2）：**生成完整快照** —— 把权威结构里的**全部**文件写进 <c>data/output</c>
    ///（不只本轮勾选范围），随后**立刻按同一份数据载入界面**（"抓进来"）。
    ///
    /// 为什么需要：Merge 只写"本轮选中范围"，所以 output 天然只覆盖"曾写出过"的文件，
    /// 「从 output 载入进度」最多还原这一部分。此按钮用全量范围重新输出一次，
    /// 之后 output 自己就代表完整进度（含所有继承条目）。
    ///
    /// 不调用 API：使用当前计划里已有译文（载入的 / 继承的 / 本轮翻的）；
    /// 没有译文的条目按既定 fail-open 策略**保留模板原文写入**并由发布门禁标记（非阻断的"待人工确认"）。
    /// </summary>
    public async void GenerateFullSnapshot()
    {
        if (IsBusy)
        {
            return;
        }

        if (_lastPlan is null)
        {
            StatusText = "请先执行 Diff 分析，再生成完整快照";
            Log("[调试] 完整快照：尚未分析（没有生产计划）");
            return;
        }

        var plan = _lastPlan;
        var allEntries = plan.OutputEntries;
        var confirmed = ConfirmAction?.Invoke(
            "将生成【完整快照】：把权威结构里的**全部**文件写入 data/output（本轮勾选范围之外的文件也会写出）。\n\n"
            + $"· 条目 {allEntries.Count} 条；会覆盖 data/output 里对应文件，并重建输出清单与发布门禁结论\n"
            + "· 不调用 API（使用当前已载入 / 继承 / 本轮翻译的译文；没有译文的条目保留原文写入并标记待人工确认）\n\n是否继续？") ?? false;
        if (!confirmed)
        {
            StatusText = "已取消生成完整快照";
            Log("[调试] 完整快照：用户取消");
            return;
        }

        var token = BeginCancellableOperation();
        IsBusy = true;
        StatusText = "正在生成完整快照...";
        BeginGuiGenerateOutput();
        BeginProgress("生成完整快照", "写出全部权威文件");
        try
        {
            // 第9.0C.21轮（R6）：先清掉 output 下的**非权威产物**（例如 P6 冒烟写的 real_api_smoke/），
            // 让目录内容与"清单 = 部署范围"保持一致（清单/部署本来就不含它们）。
            OutputWorkspaceHygiene.Cleanup(
                Path.Combine(FindProjectRoot(), "data", "output"), msg => Log(msg));

            var translations = Coordinator.CollectTranslations(allEntries);

            // 第9.0C.21轮（R1）：2000+ 文件的长任务必须有进度（否则界面长时间静默，像卡死）
            var lastLoggedFile = 0;
            var result = await Task.Run(() => MergeAndRecordOutput(
                allEntries, translations, "完整快照", plan,
                progress: (done, total) =>
                {
                    if (done == 1 || done == total || done - lastLoggedFile >= SnapshotProgressLogInterval)
                    {
                        lastLoggedFile = done;
                        Log($"[调试] 完整快照进度: {done}/{total} 个文件");
                    }

                    UpdateProgress(done, total, $"写出文件 {done}/{total}");
                }), token);

            // 第9.0C.21轮（R2）：把"没有译文"的条目数明确说出来 ——
            // 它们按既定 fail-open 策略保留权威源原文写入，门禁会标成"未取得译文（待人工确认）"（非阻断）。
            var untranslated = result.Issues
                .Count(issue => issue.Kind == OutputMergeIssueKind.MissingTranslation);

            Log($"[调试] 完整快照：写出 {result.WrittenFileCount}/{result.RequestedFileCount} 个文件"
                + $"（条目 {allEntries.Count} 条；权威语言 {plan.AuthoritativeLanguage}）");
            if (untranslated > 0)
            {
                Log($"[调试] 完整快照：其中 {untranslated} 条尚无译文 ⇒ 已保留权威源原文写入，"
                    + "发布门禁会标记为「未取得译文（待人工确认）」（非阻断；可在审核页按问题类型筛选后处理）");
            }

            StatusText = untranslated > 0
                ? $"完整快照已生成：{result.WrittenFileCount} 个文件（其中 {untranslated} 条无译文，需人工确认）"
                : $"完整快照已生成：{result.WrittenFileCount} 个文件";
            CompleteProgress("完整快照已生成");

            // 立刻"抓进来"：复用同一条载入路径（此时 output 已是全量）
            Log("[调试] 完整快照：开始载入到界面（复用「从 output 载入进度」路径）");
            await LoadProgressFromOutputCoreAsync(manageProgress: false);
        }
        catch (OperationCanceledException)
        {
            Log("[调试] 生成完整快照已取消：不生成半成品输出");
            StatusText = "操作已取消";
        }
        catch (Exception ex)
        {
            FailGui("生成完整快照失败，data/output 可能未完整写入，请查看运行日志。", ex);
        }
        finally
        {
            EndCancellableOperation();
            IsBusy = false;
        }
    }

}