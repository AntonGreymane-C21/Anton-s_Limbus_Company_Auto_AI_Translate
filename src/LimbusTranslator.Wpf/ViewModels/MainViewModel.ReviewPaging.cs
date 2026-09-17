using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Presentation;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 待审核列表的**分页**（第9.0C.12轮）。
///
/// 设计（按用户要求）：
///   - 每页 **5000 条**（保持原数量级，避免一次把十几万条塞进表格）；
///   - **完整集合**先做筛选 / 搜索，再分页 ⇒ 不再"先截断后筛选"，也不会整文件看不见；
///   - 提供：上一页 / 下一页 / 跳到第 N 页 / **跳到文件** / 跳到第一个待审条目；
///   - 计数诚实：显示"第 p/c 页 · 本页 n 条 · 筛选命中 X / 全量 M（其中待审 Z）"。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly List<DiffEntry> _reviewAllEntries = new();
    private List<DiffEntry> _reviewFilteredEntries = new();
    private int _reviewPageIndex = 1;
    private string _reviewJumpPageText = string.Empty;
    private string _reviewJumpFileText = string.Empty;
    private string _reviewPagerText = "（还没有可审核的条目）";

    /// <summary>每页条目数（5000）。</summary>
    public int ReviewPageSize => ReviewPaging.DefaultPageSize;

    /// <summary>当前页（1 起）。</summary>
    public int ReviewPageIndex => _reviewPageIndex;

    /// <summary>总页数（按筛选后的完整集合计算）。</summary>
    public int ReviewPageCount => ReviewPaging.PageCount(_reviewFilteredEntries.Count, ReviewPageSize);

    /// <summary>是否有上一页 / 下一页。</summary>
    public bool CanGoPreviousReviewPage => _reviewPageIndex > 1;

    public bool CanGoNextReviewPage => _reviewPageIndex < ReviewPageCount;

    /// <summary>分页状态文案。</summary>
    public string ReviewPagerText
    {
        get => _reviewPagerText;
        private set { _reviewPagerText = value; OnPropertyChanged(); }
    }

    /// <summary>「跳到第 N 页」输入框。</summary>
    public string ReviewJumpPageText
    {
        get => _reviewJumpPageText;
        set { _reviewJumpPageText = value ?? string.Empty; OnPropertyChanged(); }
    }

    /// <summary>「跳到文件」输入框（文件名片段，如 S1000B）。</summary>
    public string ReviewJumpFileText
    {
        get => _reviewJumpFileText;
        set { _reviewJumpFileText = value ?? string.Empty; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设置待审核列表的**完整集合**（本轮输出的全部条目；不再截断）。
    /// 由「开始汉化」结束与「从 output 载入进度」调用。
    /// </summary>
    public void SetReviewSource(IReadOnlyList<DiffEntry> entries)
    {
        _reviewAllEntries.Clear();
        if (entries is not null)
        {
            _reviewAllEntries.AddRange(entries);
        }

        _reviewPageIndex = 1;
        ApplyReviewFilter();
    }

    /// <summary>按当前筛选结果刷新当前页（由 <see cref="ApplyReviewFilter"/> 调用）。</summary>
    internal void RefreshReviewPage()
    {
        _reviewPageIndex = ReviewPaging.ClampPage(_reviewPageIndex, _reviewFilteredEntries.Count, ReviewPageSize);

        var page = ReviewPaging.GetPage(_reviewFilteredEntries, _reviewPageIndex, ReviewPageSize);
        ReviewEntries.Clear();
        foreach (var entry in page)
        {
            ReviewEntries.Add(entry);
        }

        var needsReviewTotal = _reviewAllEntries.Count(entry => entry.NeedsReview);
        ReviewPagerText =
            $"第 {_reviewPageIndex}/{ReviewPageCount} 页 · 本页 {page.Count} 条"
            + $" · 筛选命中 {_reviewFilteredEntries.Count} / 全量 {_reviewAllEntries.Count} 条（其中待审 {needsReviewTotal} 条）";

        OnPropertyChanged(nameof(ReviewPageIndex));
        OnPropertyChanged(nameof(ReviewPageCount));
        OnPropertyChanged(nameof(CanGoPreviousReviewPage));
        OnPropertyChanged(nameof(CanGoNextReviewPage));
    }

    /// <summary>把当前筛选结果（完整集合）设置为分页来源。</summary>
    internal void SetFilteredReviewEntries(IReadOnlyList<DiffEntry> filtered)
    {
        _reviewFilteredEntries = filtered?.ToList() ?? new List<DiffEntry>();
    }

    public void NextReviewPage() => GoToReviewPage(_reviewPageIndex + 1);

    public void PreviousReviewPage() => GoToReviewPage(_reviewPageIndex - 1);

    public void GoToReviewPage(int pageIndex)
    {
        var target = ReviewPaging.ClampPage(pageIndex, _reviewFilteredEntries.Count, ReviewPageSize);
        if (target == _reviewPageIndex)
        {
            return;
        }

        _reviewPageIndex = target;
        RefreshReviewPage();
        ApplyReviewFilterSelection();
        Log($"[调试] 待审核翻页：第 {_reviewPageIndex}/{ReviewPageCount} 页");
    }

    /// <summary>「跳到第 N 页」（输入非数字时提示，不改状态）。</summary>
    public void JumpToReviewPage()
    {
        if (!int.TryParse(ReviewJumpPageText?.Trim(), out var page) || page < 1)
        {
            StatusText = "请输入有效的页码（正整数）";
            return;
        }

        if (page > ReviewPageCount)
        {
            StatusText = $"页码超出范围：当前共 {ReviewPageCount} 页";
            return;
        }

        GoToReviewPage(page);
    }

    /// <summary>「跳到文件」：按文件名片段找到所在页并切过去。</summary>
    public void JumpToReviewFile()
    {
        var fragment = ReviewJumpFileText?.Trim() ?? string.Empty;
        if (fragment.Length == 0)
        {
            StatusText = "请输入文件名片段（例如 S1000B）";
            return;
        }

        var files = _reviewFilteredEntries.Select(entry => entry.Key.RelativeFilePath).ToList();
        var page = ReviewPaging.FindPageForFile(files, fragment, ReviewPageSize);
        if (page == 0)
        {
            StatusText = $"当前筛选结果里没有匹配「{fragment}」的文件（可先用搜索框收敛）";
            Log($"[调试] 跳转文件失败：当前筛选结果里没有匹配「{fragment}」的文件");
            return;
        }

        GoToReviewPage(page);
        StatusText = $"已跳到包含「{fragment}」的页面（第 {page}/{ReviewPageCount} 页）";
    }

    /// <summary>跳到第一个待审条目所在页（避免"待审条目被翻页藏住"）。</summary>
    public void JumpToFirstNeedsReview()
    {
        var index = _reviewFilteredEntries.FindIndex(entry => entry.NeedsReview);
        if (index < 0)
        {
            StatusText = "当前筛选结果里没有待审条目";
            return;
        }

        GoToReviewPage(index / ReviewPageSize + 1);
        StatusText = $"已跳到第一个待审条目（第 {_reviewPageIndex}/{ReviewPageCount} 页）";
    }

    /// <summary>翻页后把选中条目收敛到当前页（否则选中项会落在别的页上，详情面板看起来"空"）。</summary>
    private void ApplyReviewFilterSelection()
    {
        if (SelectedReviewEntry is null || !ReviewEntries.Contains(SelectedReviewEntry))
        {
            SelectedReviewEntry = ReviewEntries.FirstOrDefault();
        }
    }
}
