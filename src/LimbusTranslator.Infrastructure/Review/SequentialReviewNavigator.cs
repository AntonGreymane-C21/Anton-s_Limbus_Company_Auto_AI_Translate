using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Review;

/// <summary>
/// 逐条审校导航器（第8.89轮）：**纯逻辑**（无 UI / 无 SQLite），便于自动化测试，
/// 由 WPF ViewModel 直接调用。
/// </summary>
public sealed class SequentialReviewNavigator
{
    private readonly List<DiffEntry> _entries = new();
    private int _index;

    /// <summary>当前集合（筛选后的顺序即导航顺序）。</summary>
    public IReadOnlyList<DiffEntry> Entries => _entries;

    /// <summary>当前下标（空集合时为 0）。</summary>
    public int CurrentIndex => _entries.Count == 0 ? 0 : Math.Clamp(_index, 0, _entries.Count - 1);

    /// <summary>当前条目（空集合 → null）。</summary>
    public DiffEntry? Current => _entries.Count == 0 ? null : _entries[CurrentIndex];

    /// <summary>位置文本（第 X / Y 条）。</summary>
    public string PositionText => _entries.Count == 0
        ? "第 0 / 0 条"
        : $"第 {CurrentIndex + 1} / {_entries.Count} 条";

    /// <summary>是否可上一条。</summary>
    public bool CanPrevious => _entries.Count > 0 && CurrentIndex > 0;

    /// <summary>是否可下一条。</summary>
    public bool CanNext => _entries.Count > 0 && CurrentIndex < _entries.Count - 1;

    /// <summary>重置到第一条（空集合保持空）。</summary>
    public void Reset() => _index = 0;

    /// <summary>应用（新的）筛选集合：尽量保持当前条目；条目被过滤掉时下标收敛到合法值。</summary>
    public void ApplyFilteredEntries(IEnumerable<DiffEntry>? entries)
    {
        var previous = Current;

        _entries.Clear();
        if (entries is not null)
        {
            _entries.AddRange(entries);
        }

        _index = 0;
        if (previous is not null)
        {
            var found = _entries.FindIndex(e => ReferenceEquals(e, previous) || Equals(e.Key, previous.Key));
            _index = found >= 0 ? found : 0;
        }
    }

    /// <summary>上一条（越界返回 false，不改变下标）。</summary>
    public bool MovePrevious()
    {
        if (!CanPrevious)
        {
            return false;
        }

        _index = CurrentIndex - 1;
        return true;
    }

    /// <summary>下一条（越界返回 false，不改变下标）。</summary>
    public bool MoveNext()
    {
        if (!CanNext)
        {
            return false;
        }

        _index = CurrentIndex + 1;
        return true;
    }

    /// <summary>移动到指定下标（越界收敛）。</summary>
    public void MoveTo(int index)
    {
        if (_entries.Count == 0)
        {
            _index = 0;
            return;
        }

        _index = Math.Clamp(index, 0, _entries.Count - 1);
    }
}

/// <summary>「通过并下一条」的判定（第8.89轮，纯逻辑便于测试）。</summary>
public static class ReviewApprovalDecision
{
    /// <summary>保存后是否可以前进：存在 HardSafety Error 时**不允许**自动跳转。</summary>
    public static bool ShouldAdvanceAfterApprove(DiffEntry? entry)
        => entry is not null && !ReviewFilter.HasHardSafetyError(entry);

    /// <summary>不可前进的原因（可读文本；可前进时返回 null）。</summary>
    public static string? DescribeBlock(DiffEntry? entry)
    {
        if (entry is null)
        {
            return "没有选中条目";
        }

        if (!ReviewFilter.HasHardSafetyError(entry))
        {
            return null;
        }

        var codes = entry.ValidationIssues
            .Where(i => i.Severity == ValidationSeverity.Error)
            .Select(i => i.Code)
            .Distinct()
            .ToArray();
        return "仍存在结构安全错误（Error）：" + string.Join(", ", codes);
    }
}

/// <summary>未保存修改判定（第8.89轮）。</summary>
public static class ReviewDirtyState
{
    /// <summary>进入本条时的译文与当前编辑框内容是否不同。</summary>
    public static bool IsDirty(string? enteredTranslation, string? currentTranslation)
        => !string.Equals(enteredTranslation ?? string.Empty, currentTranslation ?? string.Empty, StringComparison.Ordinal);
}