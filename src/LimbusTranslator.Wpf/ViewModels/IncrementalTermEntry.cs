using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 增量术语库条目。
/// </summary>
public sealed class IncrementalTermEntry : INotifyPropertyChanged
{
    private string _translation = string.Empty;
    private string _explanation = string.Empty;
    private bool _isSelected = true;

    /// <summary>英文原文本</summary>
    public required string OriginalText { get; init; }

    /// <summary>是否已存在于主术语库</summary>
    public bool IsExisting { get; init; }

    /// <summary>主术语库中已有的译名</summary>
    public string ExistingTranslation { get; init; } = string.Empty;

    /// <summary>用于界面展示的术语状态</summary>
    public string StatusText => IsExisting ? "已收录" : "新术语";

    /// <summary>只有新术语允许勾选写入或编辑推荐译名。</summary>
    public bool CanWrite => !IsExisting;

    /// <summary>准备写入术语库的译名（用户可编辑）</summary>
    public string Translation
    {
        get => _translation;
        set
        {
            if (_translation == value)
            {
                return;
            }

            _translation = value;
            OnPropertyChanged();
        }
    }

    /// <summary>DeepSeek 上下文解释（含义 + 是否有由来）</summary>
    public string Explanation
    {
        get => _explanation;
        set
        {
            if (_explanation == value)
            {
                return;
            }

            _explanation = value;
            OnPropertyChanged();
        }
    }

    /// <summary>是否勾选（决定是否生成解释或写入术语库）</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>出现次数</summary>
    public int OccurrenceCount { get; init; }

    /// <summary>来源文本数</summary>
    public int SourceFileCount { get; init; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
