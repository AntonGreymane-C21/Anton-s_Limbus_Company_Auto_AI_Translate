using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// MainViewModel 的「人工审核」部分（第8.85轮）。
///
/// 筛选只读既有 <c>ValidationIssues</c>（不重跑 Validator）；邻句上下文读第5轮运行期索引
/// （不重新构造 Neighbor，也不使用本轮新生成译文）。
/// </summary>
public sealed partial class MainViewModel
{
    private string? _lastTraceFilePath;
    private TranslationContextBuilder? _reviewContextBuilder;

    public ObservableCollection<string> ReviewFilterOptions { get; } = new(ReviewFilter.DisplayNames);

    private string _selectedReviewFilter = ReviewFilter.DisplayNames[0];

    /// <summary>当前筛选类型（单选）。</summary>
    public string SelectedReviewFilter
    {
        get => _selectedReviewFilter;
        set
        {
            if (value == _selectedReviewFilter)
            {
                return;
            }

            _selectedReviewFilter = value;
            OnPropertyChanged();
            ApplyReviewFilter();
        }
    }

    /// <summary>筛选结果的只读视图（DataGrid 绑定此视图）。</summary>
    public ICollectionView ReviewEntriesView => CollectionViewSource.GetDefaultView(ReviewEntries);

    public string ReviewFilterSummaryText { get; private set; } = "（还没有待审核条目）";

    /// <summary>应用筛选并刷新统计（不重新运行 Validator）。</summary>
    public void ApplyReviewFilter()
    {
        var kind = ReviewFilter.Parse(SelectedReviewFilter);
        var view = ReviewEntriesView;
        view.Filter = item => item is DiffEntry entry
                              && ReviewFilter.Matches(entry, kind)
                              && MatchesSearch(entry, _reviewSearchText);

        var visible = _reviewAllEntries   // 第9.0C.12轮：筛选作用在完整集合上
            .Where(entry => ReviewFilter.Matches(entry, kind) && MatchesSearch(entry, _reviewSearchText))
            .ToList();
        var (error, warning) = ReviewFilter.CountSeverities(visible);
        ReviewFilterSummaryText =
            $"当前筛选结果：{visible.Count} / 全量 {_reviewAllEntries.Count}（待审 {_reviewAllEntries.Count(e => e.NeedsReview)}）；Error {error} / Warning {warning}"
            + (string.IsNullOrWhiteSpace(_reviewSearchText) ? string.Empty : $"；搜索「{_reviewSearchText}」")
            + $"；{_workflow.ReviewProgressText}";
        OnPropertyChanged(nameof(ReviewFilterSummaryText));

        if (SelectedReviewEntry is null || !visible.Contains(SelectedReviewEntry))
        {
            SelectedReviewEntry = visible.FirstOrDefault();
        }

        // 第9.0C.12轮：把筛选结果交给分页器，并把当前页填进 ReviewEntries（每页 5000 条，可翻页/跳页/跳文件）
        SetFilteredReviewEntries(visible);
        RefreshReviewPage();
    }

    /// <summary>
    /// 搜索匹配（第9.0C轮）：中文译文 / 英文 / 韩文 / 日文 / UnitKey / 文件名 / Speaker。
    /// 只做字符串包含判断，不重新解析任何文件。
    /// </summary>
    private static bool MatchesSearch(DiffEntry entry, string? searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var needle = searchText.Trim();
        foreach (var candidate in new[]
                 {
                     entry.Translation,
                     entry.NewSourceText,
                     entry.OldSourceText,
                     entry.OldTranslation,
                     entry.CanonicalKoreanText,
                     entry.Speaker,
                     entry.Key.ToString(),
                     entry.Key.RelativeFilePath,
                 })
        {
            if (!string.IsNullOrEmpty(candidate)
                && candidate.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private DiffEntry? _selectedReviewEntry;

    public DiffEntry? SelectedReviewEntry
    {
        get => _selectedReviewEntry;
        set
        {
            _selectedReviewEntry = value;
            foreach (var name in new[]
                     {
                         nameof(SelectedReviewEntry), nameof(ReviewDetailHeaderText), nameof(ReviewMetaText),
                         nameof(ReviewSourceText), nameof(ReviewOldSourceText), nameof(ReviewOldTranslationText),
                         nameof(ReviewCurrentTranslation), nameof(ReviewProvenanceText), nameof(ReviewStatusText),
                         nameof(ReviewNeighborText),
                     })
            {
                OnPropertyChanged(name);
            }

            RefreshReviewDetailIssues();
            RefreshReviewDisplay();
        }
    }

    public string ReviewDetailHeaderText => SelectedReviewEntry is null ? "（未选中条目）" : SelectedReviewEntry.Key.ToString();

    public string ReviewMetaText => SelectedReviewEntry is null
        ? string.Empty
        : $"文件：{SelectedReviewEntry.Key.RelativeFilePath}\n"
          + $"RecordId：{SelectedReviewEntry.Key.RecordId}\n"
          + $"FieldPath：{SelectedReviewEntry.Key.FieldPath}\n"
          + $"Speaker：{SelectedReviewEntry.Speaker ?? "—"}";

    public string ReviewSourceText => SelectedReviewEntry?.NewSourceText ?? string.Empty;

    public string ReviewOldSourceText => SelectedReviewEntry?.OldSourceText ?? "（无）";

    public string ReviewOldTranslationText => string.IsNullOrWhiteSpace(SelectedReviewEntry?.OldTranslation)
        ? "（无）"
        : SelectedReviewEntry!.OldTranslation!;

    /// <summary>可编辑的当前译文（详情面板多行编辑，two-way）。</summary>
    public string ReviewCurrentTranslation
    {
        get => SelectedReviewEntry?.Translation ?? string.Empty;
        set
        {
            if (SelectedReviewEntry is not null && SelectedReviewEntry.Translation != value)
            {
                SelectedReviewEntry.Translation = value;
                OnPropertyChanged();
            }
        }
    }

    public string ReviewProvenanceText => SelectedReviewEntry is null
        ? string.Empty
        : $"来源：{SelectedReviewEntry.Provenance?.ToString() ?? "未确定"}    TM 命中级别：{SelectedReviewEntry.TmMatchType}";

    public string ReviewStatusText => SelectedReviewEntry is null
        ? string.Empty
        : $"需要人工审核：{SelectedReviewEntry.NeedsReview}\n"
          + $"审核原因：{(string.IsNullOrWhiteSpace(SelectedReviewEntry.ReviewReason) ? "无" : SelectedReviewEntry.ReviewReason)}";

    /// <summary>结构化校验问题（Severity / Code / Category / Message）。</summary>
    public ObservableCollection<ReviewIssueRow> ReviewIssues { get; } = new();

    /// <summary>刷新详情面板中的结构化 Issues（只读投影）。</summary>
    public void RefreshReviewDetailIssues()
    {
        ReviewIssues.Clear();
        if (SelectedReviewEntry is null)
        {
            return;
        }

        foreach (var issue in SelectedReviewEntry.ValidationIssues)
        {
            ReviewIssues.Add(new ReviewIssueRow
            {
                Severity = issue.Severity.ToString(),
                Code = issue.Code,
                Category = issue.Category.ToString(),
                Message = issue.Message,
            });
        }
    }

    /// <summary>邻句上下文（只读）：来自第5轮运行期上下文索引。</summary>
    public string ReviewNeighborText
    {
        get
        {
            if (SelectedReviewEntry is null || _reviewContextBuilder is null)
            {
                return "无邻句上下文";
            }

            var context = _reviewContextBuilder.Build(SelectedReviewEntry);
            if (!context.HasNeighbors)
            {
                return "无邻句上下文";
            }

            var previous = context.Previous is null
                ? "（无）"
                : $"[{context.Previous.Speaker ?? "旁白"}] {context.Previous.SourceText}";
            var next = context.Next is null
                ? "（无）"
                : $"[{context.Next.Speaker ?? "旁白"}] {context.Next.SourceText}";

            return $"上一句（只读）：{previous}\n当前句：{SelectedReviewEntry.NewSourceText}\n下一句（只读）：{next}";
        }
    }

    public string ReviewSaveStatusText { get; private set; } = "（尚未保存）";

    /// <summary>批量确认前的确认回调（由 code-behind 注入 MessageBox；未注入时拒绝批量）。</summary>
    public Func<string, bool>? ConfirmAction { get; set; }

    private string ConfigDir => Path.Combine(FindProjectRoot(), "config");

    private string TmDatabasePath => Path.Combine(FindProjectRoot(), "data", "cache", "translation_memory.db");

    /// <summary>
    /// 保存当前条目的人工审核（复用 <c>HumanReviewService</c>），随后用既有 ValidatorPipeline 重跑校验。
    /// 硬安全 Error 不会被清除；NeedsReview 只在无 Error 时置 false（由服务与策略共同决定）。
    /// </summary>
    public int SaveCurrentReview()
    {
        if (SelectedReviewEntry is null)
        {
            ReviewSaveStatusText = "（未选中条目）";
            OnPropertyChanged(nameof(ReviewSaveStatusText));
            return 0;
        }

        var saved = SaveReviewedEntries(new[] { SelectedReviewEntry });

        // 第9.0C.12c轮：写 TM（保存）与"刷新界面"必须分开——
        // 真实反馈：这里曾抛 NullReferenceException，弹窗只写"保存审核失败"，
        // 让用户以为译文没保存（其实已写入 TM）。刷新失败只记录，不影响保存结果。
        try
        {
            RevalidateEntry(SelectedReviewEntry);
            ApplyReviewFilter();
            RefreshReviewDetailIssues();
        }
        catch (Exception ex)
        {
            Log($"[调试] 保存审核后界面刷新失败（译文已写入 TM，不影响保存结果）: {ex}");
        }

        ReviewSaveStatusText = saved > 0
            ? HardSafetyNote(SelectedReviewEntry)
            : "保存失败（空源文 / 空译文不会写入）";
        OnPropertyChanged(nameof(ReviewSaveStatusText));
        return saved;
    }

    /// <summary>
    /// 保守版批量审核：只把当前筛选中**无 Error** 的条目标记为人工审核通过，不修改任何译文。
    /// </summary>
    public int ConfirmVisibleWithoutErrors()
    {
        var kind = ReviewFilter.Parse(SelectedReviewFilter);
        var candidates = ReviewEntries
            .Where(e => ReviewFilter.Matches(e, kind))
            .Where(e => !ReviewFilter.HasHardSafetyError(e))
            .ToList();

        if (candidates.Count == 0)
        {
            ReviewSaveStatusText = "当前筛选结果中没有可确认（无 Error）的条目";
            OnPropertyChanged(nameof(ReviewSaveStatusText));
            return 0;
        }

        var confirmed = ConfirmAction is not null
            && ConfirmAction($"将 {candidates.Count} 条无 Error 译文标记为人工审核通过。\n不会修改译文内容。是否继续？");
        if (!confirmed)
        {
            ReviewSaveStatusText = "已取消批量确认";
            OnPropertyChanged(nameof(ReviewSaveStatusText));
            return 0;
        }

        var saved = SaveHumanReviewedBulk(candidates);
        foreach (var entry in candidates)
        {
            RevalidateEntry(entry);
        }

        ApplyReviewFilter();
        RefreshReviewDetailIssues();
        ReviewSaveStatusText = $"批量确认完成：{saved}/{candidates.Count} 条标记为 HumanReviewed（译文未改动）";
        OnPropertyChanged(nameof(ReviewSaveStatusText));
        return saved;
    }

    /// <summary>通过既有服务层写回 HumanReviewed（不写第二套 SQL / TM 逻辑）。</summary>
    private int SaveReviewedEntries(IReadOnlyList<DiffEntry> entries)
    {
        try
        {
            using var memory = new LimbusTranslator.Infrastructure.Persistence.SqliteTranslationMemory(
                new LimbusTranslator.Infrastructure.Persistence.TranslationMemoryOptions
                {
                    DatabasePath = TmDatabasePath,
                },
                msg => Log(msg));
            var saved = LimbusTranslator.Infrastructure.Review.HumanReviewService.SaveReviewedEntries(entries, memory);
            Log($"[调试] 人工审核写回：{saved}/{entries.Count} 条（来源=HumanReviewed，NeedsReview=false）");
            return saved;
        }
        catch (Exception ex)
        {
            Log($"[调试] 人工审核保存失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// 第9.0C.24轮：批量写回 HumanReviewed（**单事务**，不逐条开连接/事务）。
    ///
    /// 供「批量确认无 Error 条目」「审核后重新输出」等批量场景使用；
    /// 单条保存仍走 SaveReviewedEntries（语义完全一致，只是批次不同）。
    /// </summary>
    private int SaveHumanReviewedBulk(IReadOnlyList<DiffEntry> entries)
    {
        try
        {
            using var memory = new LimbusTranslator.Infrastructure.Persistence.SqliteTranslationMemory(
                new LimbusTranslator.Infrastructure.Persistence.TranslationMemoryOptions
                {
                    DatabasePath = TmDatabasePath,
                },
                msg => Log(msg));
            var saved = LimbusTranslator.Infrastructure.Review.HumanReviewService.SaveReviewedEntriesBulk(entries, memory);
            Log($"[调试] 人工审核批量写回：{saved}/{entries.Count} 条（来源=HumanReviewed，单事务）");
            return saved;
        }
        catch (Exception ex)
        {
            Log($"[调试] 人工审核批量写回失败: {ex.Message}");
            return 0;
        }
    }

    /// <summary>人工修改后重新运行既有 ValidatorPipeline（HardSafety 仍然生效）。</summary>
    private void RevalidateEntry(DiffEntry entry)
    {
        try
        {
            ValidationPipeline.CreateDefault(ConfigDir).ValidateAndApply(entry);
        }
        catch (Exception ex)
        {
            Log($"[调试] 人工审核后重新校验失败: {ex.Message}");
        }
    }

    private static string HardSafetyNote(DiffEntry entry)
        => ReviewFilter.HasHardSafetyError(entry)
            ? "已保存，但仍存在 HardSafety Error（Placeholder / Tag / 空译文），发布门禁仍会拦下"
            : "已保存为 HumanReviewed（NeedsReview=false）";

    // ---------------- 部署信息面板（§18~§19） ----------------

    private string _lastDeployStatusText = "（尚未部署）";
    private string _lastDeployBackupDir = "—";
    private bool _lastDeployCritical;

    /// <summary>最近一次部署的用户友好状态。</summary>
    public string LastDeployStatusText
    {
        get => _lastDeployStatusText;
        private set { _lastDeployStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>最近一次部署是否为高危状态（回滚不完整），用于高亮。</summary>
    public bool LastDeployCritical
    {
        get => _lastDeployCritical;
        private set { _lastDeployCritical = value; OnPropertyChanged(); }
    }

    /// <summary>发布信息面板（输出目录 / 目标目录 / 清单 / 门禁 / 备份 / 最近部署）。</summary>
    public string DeployInfoText
    {
        get
        {
            var root = FindProjectRoot();
            var outputDir = Path.Combine(root, "data", "output");
            var manifest = ReadManifestInfo();
            var targetDir = string.IsNullOrWhiteSpace(GameRootDir)
                ? "（未定位游戏目录）"
                : Path.Combine(GameRootDir, "LimbusCompany_Data", "Lang", "LLC_zh-CN");

            return $"输出目录：{outputDir}\n"
                   + $"目标游戏中文目录：{targetDir}\n"
                   + $"清单 Id：{manifest.ManifestId ?? "—"}\n"
                   + $"清单状态：{manifest.Describe}\n"
                   + $"发布门禁：{GateStatusText}\n"
                   + $"备份目录：{_lastDeployBackupDir}\n"
                   + $"最近部署：{LastDeployStatusText}";
        }
    }

    /// <summary>记录部署结果（复用既有 DeployResult；状态文案由 DeployStatusText 统一映射）。</summary>
    public void ApplyDeployResult(DeployResult result)
    {
        LastDeployStatusText = DeployStatusText.Describe(result.Status);
        LastDeployCritical = DeployStatusText.IsCritical(result.Status);
        _lastDeployBackupDir = string.IsNullOrWhiteSpace(result.BackupDir) ? "—" : result.BackupDir;

        // 第9.0C轮：部署结果面向用户的中文摘要（成功给备份位置与文件数；失败给是否回滚与原因）
        var (summary, isCritical) = DeployPresentation.BuildOutcome(result);
        LastDeployOutcomeText = summary;
        LastDeployOutcomeCritical = isCritical;

        CompleteGuiDeploy();
        OnPropertyChanged(nameof(DeployInfoText));
        OnPropertyChanged(nameof(LastDeployOutcomeText));
    }

    /// <summary>部署结果的中文摘要（第9.0C轮）。</summary>
    public string LastDeployOutcomeText { get; private set; } = "（尚未部署）";

    /// <summary>部署是否处于高危状态（回滚未全部完成）。</summary>
    public bool LastDeployOutcomeCritical { get; private set; }

    private sealed record ManifestInfo(string? ManifestId, string Describe);

    /// <summary>读取输出清单的只读摘要（不存在时给出明确文案）。</summary>
    private ManifestInfo ReadManifestInfo()
    {
        try
        {
            var path = Path.Combine(FindProjectRoot(), "data", "output", "output_manifest.json");
            if (!File.Exists(path))
            {
                return new ManifestInfo(null, "（尚未生成，请先翻译/生成输出）");
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            string? id = null;
            foreach (var name in new[] { "manifestId", "ManifestId" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    id = value.GetString();
                    break;
                }
            }

            var complete = root.TryGetProperty("isComplete", out var ic) && ic.ValueKind == JsonValueKind.True;
            return new ManifestInfo(id, complete ? "完整" : "不完整（有文件未通过写后校验）");
        }
        catch
        {
            return new ManifestInfo(null, "（读取失败）");
        }
    }

    // ---------------- 实际 Thinking 统计（§16~§17） ----------------

    /// <summary>真实 Provider 请求的 Thinking 分布（来自 Trace，非 Policy 重算）。</summary>
    public string ActualThinkingStatsText { get; private set; } = "（尚无请求）";

    /// <summary>由 Trace 统计刷新"实际请求"Thinking 分布（无请求时明确提示）。</summary>
    public void RefreshActualThinkingStats()
    {
        var stats = TranslationTraceStats.Read(_lastTraceFilePath);
        ActualThinkingStatsText = stats.RequestCount == 0
            ? "本轮无 Provider 请求"
            : $"实际 Provider 请求：{stats.RequestCount}（网络 {stats.NetworkCalled} / 缓存 {stats.CacheHit}）\n"
              + $"实际 Thinking ON（源语言异常）：{stats.ThinkingOnSourceLanguageAnomaly}\n"
              + $"实际 Thinking ON（StoryData）：{stats.ThinkingOnStoryData}\n"
              + $"实际 Thinking ON（其它）：{stats.ThinkingOnOther}\n"
              + $"实际 Thinking OFF：{stats.ThinkingOff}";
        OnPropertyChanged(nameof(ActualThinkingStatsText));
    }
}

/// <summary>待审核详情中的单条结构化校验问题（只读展示）。</summary>
public sealed class ReviewIssueRow
{
    public required string Severity { get; init; }

    public required string Code { get; init; }

    public required string Category { get; init; }

    public required string Message { get; init; }
}
