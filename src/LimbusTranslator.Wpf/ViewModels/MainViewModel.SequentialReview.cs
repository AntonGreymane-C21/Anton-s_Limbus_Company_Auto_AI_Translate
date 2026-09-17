using System.IO;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Review;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 逐条审校工作台（第8.88轮）。
///
/// 定位：把「条目级 Diff」升级为可逐条上下翻阅的审校流程。
///   - 直接操作现有 <see cref="DiffEntry"/> 投影（<c>ReviewEntriesView</c>），**不复制第二份翻译状态**；
///   - 保存仍走 <c>SaveCurrentReview()</c> → HumanReviewService（不直接写 SQLite）；
///   - 术语展示使用本次运行的 <c>ActiveGlossarySnapshot</c>（与 Prompt/Validator 同源）；
///   - 导航顺序 = 现有筛选视图顺序（默认按文件/记录/字段原顺序），筛选变化即重建导航集合。
/// </summary>
public partial class MainViewModel
{
    private int _sequentialIndex;
    private string _sequentialOriginalTranslation = string.Empty;
    private string _reviewMatchedTermsText = "（无）";

    /// <summary>当前筛选视图的条目快照（导航集合；顺序即筛选视图顺序）。</summary>
    private IReadOnlyList<DiffEntry> SequentialItems
        => ReviewEntriesView is null ? Array.Empty<DiffEntry>() : ReviewEntriesView.Cast<DiffEntry>().ToArray();

    /// <summary>当前位置文本（第 X / Y 条）。</summary>
    public string SequentialPositionText
    {
        get
        {
            var items = SequentialItems;
            return items.Count == 0
                ? "第 0 / 0 条"
                : $"第 {Math.Min(_sequentialIndex, items.Count - 1) + 1} / {items.Count} 条";
        }
    }

    /// <summary>上一条是否可用。</summary>
    public bool CanGoPrevious => _sequentialIndex > 0 && SequentialItems.Count > 0;

    /// <summary>下一条是否可用。</summary>
    public bool CanGoNext => _sequentialIndex < SequentialItems.Count - 1;

    /// <summary>当前译文是否与进入本条时不同（未保存修改）。</summary>
    public bool HasUnsavedSequentialEdit
        => SelectedReviewEntry is not null
           && !string.Equals(
               _sequentialOriginalTranslation ?? string.Empty,
               ReviewCurrentTranslation ?? string.Empty,
               StringComparison.Ordinal);

    /// <summary>本条命中的术语（含来源标注）。</summary>
    public string ReviewMatchedTermsText
    {
        get => _reviewMatchedTermsText;
        private set
        {
            _reviewMatchedTermsText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>进入逐条审校模式（重置位置并同步详情面板）。</summary>
    public void BeginSequentialReview()
    {
        // 第9.0C.7轮：列表现在包含**全部本轮译文** ⇒ 在"全部"筛选下默认跳到第一条 NeedsReview，
        // 保证"审问题"的效率不下降（用户显式筛选时尊重其筛选，从第一条开始）。
        _sequentialIndex = 0;
        if (LimbusTranslator.Infrastructure.Review.ReviewFilter.Parse(SelectedReviewFilter)
            == LimbusTranslator.Infrastructure.Review.ReviewFilterKind.All)
        {
            var items = SequentialItems;
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index].NeedsReview)
                {
                    _sequentialIndex = index;
                    Log($"[调试] 逐条审校：列表含全部本轮译文 {items.Count} 条，已跳到第一条待审核（第 {index + 1} 条）");
                    break;
                }
            }
        }

        SyncSequentialSelection();
    }

    /// <summary>
    /// 选中第 <paramref name="index"/> 条（越界自动收敛）。
    /// 存在未保存修改且 <paramref name="confirmDiscard"/> 返回 false 时**不导航**。
    /// </summary>
    public bool GoToSequentialIndex(int index, Func<bool>? confirmDiscard = null)
    {
        var items = SequentialItems;
        if (items.Count == 0)
        {
            _sequentialIndex = 0;
            SyncSequentialSelection();
            return true;
        }

        var target = Math.Clamp(index, 0, items.Count - 1);
        if (target != _sequentialIndex && HasUnsavedSequentialEdit)
        {
            if (confirmDiscard is null || !confirmDiscard())
            {
                Log("[调试] 当前译文有未保存修改，已取消导航。");
                return false;
            }

            Log("[调试] 已放弃未保存修改并导航。");
        }

        _sequentialIndex = target;
        SyncSequentialSelection();
        return true;
    }

    /// <summary>下一条。</summary>
    public bool SequentialNext(Func<bool>? confirmDiscard = null)
        => GoToSequentialIndex(_sequentialIndex + 1, confirmDiscard);

    /// <summary>上一条。</summary>
    public bool SequentialPrevious(Func<bool>? confirmDiscard = null)
        => GoToSequentialIndex(_sequentialIndex - 1, confirmDiscard);

    /// <summary>保存当前译文（不导航）。</summary>
    public void SaveSequential()
    {
        if (SelectedReviewEntry is null)
        {
            return;
        }

        var edited = HasUnsavedSequentialEdit;
        SaveCurrentReview();
        NoteReviewOutcome(edited);
        _sequentialOriginalTranslation = ReviewCurrentTranslation ?? string.Empty;
        OnPropertyChanged(nameof(HasUnsavedSequentialEdit));
    }

    /// <summary>
    /// 通过并下一条：保存 → HumanReviewService → 重新 Validator；
    /// 若仍存在 HardSafety Error（结构安全）则**不自动跳转**。
    /// </summary>
    public bool ApproveAndNext(Func<bool>? confirmDiscard = null)
    {
        var entry = SelectedReviewEntry;
        if (entry is null)
        {
            return false;
        }

        SaveSequential();

        if (ReviewFilter.HasHardSafetyError(entry))
        {
            Log($"[调试] 该条仍存在结构安全错误（Error），已阻止自动跳转：{entry.Key}");
            return false;
        }

        return GoToSequentialIndex(_sequentialIndex + 1, confirmDiscard);
    }

    /// <summary>标记待处理（不改变译文；沿用现有 NeedsReview / ReviewReason 机制）。</summary>
    public void MarkSequentialPending()
    {
        var entry = SelectedReviewEntry;
        if (entry is null)
        {
            return;
        }

        entry.NeedsReview = true;
        entry.ReviewReason = string.IsNullOrWhiteSpace(entry.ReviewReason)
            ? "用户标记待处理"
            : entry.ReviewReason + "；用户标记待处理";
        Log($"[调试] 已标记待处理：{entry.Key}");
    }

    private void SyncSequentialSelection()
    {
        var items = SequentialItems;
        if (items.Count == 0)
        {
            SelectedReviewEntry = null;
            OnPropertyChanged(nameof(SequentialPositionText));
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
            return;
        }

        _sequentialIndex = Math.Clamp(_sequentialIndex, 0, items.Count - 1);
        SelectedReviewEntry = items[_sequentialIndex];
        _sequentialOriginalTranslation = SelectedReviewEntry.Translation ?? string.Empty;

        RefreshMatchedTerms(SelectedReviewEntry);

        OnPropertyChanged(nameof(SequentialPositionText));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(HasUnsavedSequentialEdit));
    }

    /// <summary>刷新本条命中术语（使用本次运行快照；与 Prompt/Validator 同源）。</summary>
    private void RefreshMatchedTerms(DiffEntry entry)
    {
        var snapshot = _activeGlossarySnapshot
                       ?? ActiveGlossarySnapshot.Load(Path.Combine(FindProjectRoot(), "config"));

        var texts = new[] { entry.NewSourceText, entry.OldSourceText, entry.OldTranslation }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToArray();

        var hits = snapshot.SelectTerms(texts);
        ReviewMatchedTermsText = hits.Count == 0
            ? "（无）"
            : string.Join(
                "\n",
                hits.Select(kv => $"{kv.Key} → {kv.Value.Translation}　[{(kv.Value.Locked ? "强制" : "优先")}｜{kv.Value.Source}]"));
    }
}