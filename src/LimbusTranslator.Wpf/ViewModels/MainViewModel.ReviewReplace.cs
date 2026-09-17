using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Review;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 待审核页的「批量替换」（第9.0C.7轮）。
///
/// 用途（真实需求）：用户逐条看译文时发现**自己确认过的名词被翻错**（例如"护士长"应为"护父"），
/// 希望一次性替换，并可同时把正确译名写进术语库，让**下一次翻译**自动生效。
///
/// 复用既有链路（不新造机制）：
///   ① 替换只改 <see cref="DiffEntry.Translation"/>（纯字面量，见 <see cref="ReviewBatchReplace"/>）；
///   ② 命中条目重跑既有 <see cref="RevalidateEntry"/>（结构被改坏会立刻暴露）；
///   ③ 保存走既有 <see cref="SaveReviewedEntries"/>（HumanReviewed 写回 TM）；
///   ④ 术语库写回走既有 <see cref="GlossaryService"/>（保存后下一次翻译生效）。
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly ReviewReplaceScope[] ReplaceScopes =
    {
        ReviewReplaceScope.AllLoaded,
        ReviewReplaceScope.CurrentFilter,
        ReviewReplaceScope.CurrentFile,
    };

    /// <summary>批量替换范围下拉项（默认「全部已加载」）。</summary>
    public IReadOnlyList<string> ReviewReplaceScopeOptions { get; } = new[] { "全部已加载", "当前筛选结果", "当前条目所在文件" };

    private int _reviewReplaceScopeIndex;
    private string _reviewReplaceFind = string.Empty;
    private string _reviewReplaceWith = string.Empty;
    private bool _reviewReplaceCaseSensitive;
    private bool _reviewReplaceWriteGlossary = true;
    private string _reviewReplaceGlossarySource = string.Empty;
    private bool _reviewReplaceGlossaryLocked = true;
    private string _reviewReplacePreviewText = "（尚未预览：填写「查找」后点「预览命中」）";
    private string _reviewReplaceStatusText = string.Empty;
    private List<(DiffEntry Entry, string Before)>? _reviewReplaceUndo;

    /// <summary>替换范围（0=全部已加载）。</summary>
    public int ReviewReplaceScopeIndex
    {
        get => _reviewReplaceScopeIndex;
        set { _reviewReplaceScopeIndex = value; OnPropertyChanged(); }
    }

    /// <summary>查找文本（当前译文里出现的错误译法）。</summary>
    public string ReviewReplaceFind
    {
        get => _reviewReplaceFind;
        set { _reviewReplaceFind = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(CanPreviewReviewReplace)); }
    }

    /// <summary>替换为（正确译法）。</summary>
    public string ReviewReplaceWith
    {
        get => _reviewReplaceWith;
        set { _reviewReplaceWith = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(ReviewReplaceGlossaryHint)); }
    }

    /// <summary>是否区分大小写（默认否）。</summary>
    public bool ReviewReplaceCaseSensitive
    {
        get => _reviewReplaceCaseSensitive;
        set { _reviewReplaceCaseSensitive = value; OnPropertyChanged(); }
    }

    /// <summary>是否同时写入术语库（默认是）。</summary>
    public bool ReviewReplaceWriteGlossary
    {
        get => _reviewReplaceWriteGlossary;
        set { _reviewReplaceWriteGlossary = value; OnPropertyChanged(); OnPropertyChanged(nameof(ReviewReplaceGlossaryHint)); }
    }

    /// <summary>术语库源文（英文 / 韩文原词，例如 Nursefather）。</summary>
    public string ReviewReplaceGlossarySource
    {
        get => _reviewReplaceGlossarySource;
        set { _reviewReplaceGlossarySource = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(ReviewReplaceGlossaryHint)); }
    }

    /// <summary>写入术语库时是否锁定（Locked=true ⇒ AI 不得改写）。</summary>
    public bool ReviewReplaceGlossaryLocked
    {
        get => _reviewReplaceGlossaryLocked;
        set { _reviewReplaceGlossaryLocked = value; OnPropertyChanged(); }
    }

    /// <summary>预览结果文本。</summary>
    public string ReviewReplacePreviewText
    {
        get => _reviewReplacePreviewText;
        private set { _reviewReplacePreviewText = value; OnPropertyChanged(); }
    }

    /// <summary>执行结果文本。</summary>
    public string ReviewReplaceStatusText
    {
        get => _reviewReplaceStatusText;
        private set { _reviewReplaceStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>术语库写入提示（勾选但未填源文时给出明确说明）。</summary>
    public string ReviewReplaceGlossaryHint
        => !ReviewReplaceWriteGlossary
            ? "（不写入术语库：只改当前这批译文）"
            : string.IsNullOrWhiteSpace(ReviewReplaceGlossarySource)
                ? "（已勾选写入术语库，但「术语库源文」为空 ⇒ 只替换译文，不会写术语库）"
                : $"（将写入术语库：{ReviewReplaceGlossarySource.Trim()} → {ReviewReplaceWith}）";

    /// <summary>是否可以预览（查找文本非空）。</summary>
    public bool CanPreviewReviewReplace => !string.IsNullOrWhiteSpace(ReviewReplaceFind);

    /// <summary>是否执行过替换（可撤销）。</summary>
    public bool CanUndoReviewReplace => _reviewReplaceUndo is { Count: > 0 };

    /// <summary>预览：命中多少条 / 多少处，以及前 10 条前后对照。</summary>
    public void PreviewReviewReplace()
    {
        var targets = ResolveReviewReplaceTargets();
        var preview = ReviewBatchReplace.Preview(targets, ReviewReplaceFind, ReviewReplaceWith, ReviewReplaceCaseSensitive);

        var lines = new List<string>
        {
            $"范围：{ReviewReplaceScopeOptions[Math.Clamp(ReviewReplaceScopeIndex, 0, ReplaceScopes.Length - 1)]}（{targets.Count} 条）",
            preview.Describe(),
        };
        if (ReviewReplaceWriteGlossary)
        {
            lines.Add(ReviewReplaceGlossaryHint);
        }

        foreach (var sample in preview.Samples)
        {
            lines.Add($"· {sample.UnitKey}");
            lines.Add($"   旧：{sample.Before}");
            lines.Add($"   新：{sample.After}");
        }

        ReviewReplacePreviewText = string.Join(Environment.NewLine, lines);
        ReviewReplaceStatusText = preview.IsEmpty ? string.Empty : "（确认无误后点「执行替换」）";
        OnPropertyChanged(nameof(CanUndoReviewReplace));
    }

    /// <summary>执行替换 → 重新校验 → 写回 TM（HumanReviewed）→（可选）写术语库。</summary>
    public async void ApplyReviewReplace()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ReviewReplaceFind))
            {
                ReviewReplaceStatusText = "请先填写「查找」文本。";
                return;
            }

            var targets = ResolveReviewReplaceTargets();
            var before = targets
                .Where(entry => !string.IsNullOrEmpty(entry.Translation))
                .ToDictionary(entry => entry, entry => entry.Translation!);

            var changed = ReviewBatchReplace.Apply(targets, ReviewReplaceFind, ReviewReplaceWith, ReviewReplaceCaseSensitive);
            if (changed.Count == 0)
            {
                ReviewReplaceStatusText = "没有命中任何条目，未做任何修改。";
                return;
            }

            // 撤销快照（只回滚译文，不回滚术语库）
            _reviewReplaceUndo = changed
                .Select(entry => (Entry: entry, Before: before.TryGetValue(entry, out var text) ? text : string.Empty))
                .ToList();

            // 重跑既有校验链：替换把 {0} / 标签 / 数字弄坏会立刻变成结构性 Error
            foreach (var entry in changed)
            {
                RevalidateEntry(entry);
            }

            var newErrors = changed.Count(ReviewFilter.HasHardSafetyError);
            var saved = SaveReviewedEntries(changed);

            var glossaryNote = ReviewReplaceWriteGlossary
                ? await WriteGlossaryForReplaceAsync()
                : string.Empty;

            ReviewReplaceStatusText =
                $"已替换 {changed.Count} 条（{saved} 条写回人工审核）"
                + (newErrors > 0
                    ? $"；⚠ {newErrors} 条替换后出现结构安全错误（Placeholder / 标签），请逐条查看"
                    : "；结构校验通过")
                + glossaryNote;

            Log($"[调试] 批量替换：'{ReviewReplaceFind}' → '{ReviewReplaceWith}'，命中 {changed.Count} 条"
                + $"（写回人工审核 {saved} 条，替换后结构错误 {newErrors} 条）{glossaryNote}");

            ApplyReviewFilter();
            PreviewReviewReplace();
            OnPropertyChanged(nameof(CanUndoReviewReplace));
        }
        catch (Exception ex)
        {
            ReviewReplaceStatusText = $"替换失败：{ex.Message}";
            Log($"[调试] 批量替换失败: {ex.Message}");
        }
    }

    /// <summary>撤销上一次替换（只回滚译文；术语库不回滚，日志会说明）。</summary>
    public void UndoReviewReplace()
    {
        if (_reviewReplaceUndo is not { Count: > 0 } snapshot)
        {
            ReviewReplaceStatusText = "没有可撤销的替换。";
            return;
        }

        foreach (var (entry, text) in snapshot)
        {
            entry.Translation = text;
            RevalidateEntry(entry);
        }

        var count = snapshot.Count;
        _reviewReplaceUndo = null;
        ReviewReplaceStatusText = $"已撤销 {count} 条替换（术语库不回滚；如需改回请执行一次反向替换）。";
        Log($"[调试] 批量替换撤销：已回滚 {count} 条译文（术语库不回滚）");

        ApplyReviewFilter();
        PreviewReviewReplace();
        OnPropertyChanged(nameof(CanUndoReviewReplace));
    }

    /// <summary>按用户选择写回术语库（复用既有 GlossaryService；保存后下一次翻译生效）。</summary>
    private async Task<string> WriteGlossaryForReplaceAsync()
    {
        var source = ReviewReplaceGlossarySource.Trim();
        var target = ReviewReplaceWith ?? string.Empty;

        if (source.Length == 0)
        {
            return "；术语库未写入（「术语库源文」为空：术语库的键必须是英文/韩文原词）";
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            return "；术语库未写入（「替换为」为空）";
        }

        var locked = ReviewReplaceGlossaryLocked;
        try
        {
            var outcome = await Task.Run(() =>
            {
                var glossary = new GlossaryService(ConfigDir);
                if (!glossary.Update(source, target, locked))
                {
                    return false;   // 译名为空时 Update 会拒绝
                }

                glossary.Save();
                return true;
            });

            var note = outcome
                ? $"；术语库已写入并保存（{source} → {target}{(locked ? "，锁定" : string.Empty)}，下一次翻译生效）"
                : "；术语库写入被拒绝（译名为空）";
            Log($"[调试] 批量替换-术语库：{source} → {target}（locked={locked}）结果={outcome}");
            return note;
        }
        catch (Exception ex)
        {
            Log($"[调试] 批量替换-术语库写入失败: {ex.Message}");
            return $"；术语库写入失败：{ex.Message}";
        }
    }

    /// <summary>按当前范围取替换目标（默认：全部已加载 = 本轮翻译条目）。</summary>
    private List<DiffEntry> ResolveReviewReplaceTargets()
    {
        var scope = ReplaceScopes[Math.Clamp(ReviewReplaceScopeIndex, 0, ReplaceScopes.Length - 1)];
        return scope switch
        {
            ReviewReplaceScope.CurrentFilter => ReviewEntries
                .Where(entry => ReviewFilter.Matches(entry, ReviewFilter.Parse(SelectedReviewFilter))
                                && MatchesSearch(entry, _reviewSearchText))
                .ToList(),

            ReviewReplaceScope.CurrentFile => SelectedReviewEntry is null
                ? new List<DiffEntry>()
                : ReviewEntries
                    .Where(entry => entry.Key.RelativeFilePath == SelectedReviewEntry.Key.RelativeFilePath)
                    .ToList(),

            _ => ReviewEntries.ToList(),
        };
    }
}
