using System.ComponentModel;
using System.Runtime.CompilerServices;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 分类统计条目（UI 展示用）。
/// </summary>
public sealed class CategoryStat : INotifyPropertyChanged
{
    private bool _isSelected = true;

    /// <summary>分类</summary>
    public required TextCategory Category { get; init; }

    /// <summary>分类显示名</summary>
    public required string DisplayName { get; init; }

    /// <summary>该分类总文件数</summary>
    public int TotalFileCount { get; set; }

    /// <summary>该分类需要汉化的文件数（新增+缺失汉化+修改）</summary>
    public int NeedTranslateFileCount { get; set; }

    /// <summary>该分类已就绪文件数（未变化，已有汉化，无需处理）</summary>
    public int ReadyCount => TotalFileCount - NeedTranslateFileCount;

    /// <summary>是否勾选（决定是否汉化该分类）</summary>
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
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>
    /// 第9.0C.3轮：勾选变化回调（由 ViewModel 挂载；用于「任务范围 → 文件列表」即时联动）。
    /// </summary>
    public Action? SelectionChanged { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

