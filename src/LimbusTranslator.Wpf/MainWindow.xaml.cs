using System.IO;
using System.Windows;
using LimbusTranslator.Wpf.ViewModels;

namespace LimbusTranslator.Wpf;

/// <summary>
/// 主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private bool _autoScrollLogs = true;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // 第8.86轮：批量审核的确认框由 View 注入（ViewModel 只做业务判断）
        _viewModel.ConfirmAction = message =>
            MessageBox.Show(message, "批量确认人工审核", MessageBoxButton.OKCancel, MessageBoxImage.Question)
                == MessageBoxResult.OK;
        // 第9.0C轮：可恢复错误统一弹「用户可读 + 技术详情」对话框
        _viewModel.ErrorDialogRequested += ShowFriendlyError;
    }

    /// <summary>第9.0C轮：错误对话框（先说发生什么，技术详情可展开）。</summary>
    private void ShowFriendlyError(string summary, string details)
    {
        var text = string.IsNullOrWhiteSpace(summary) ? "操作失败。" : summary;
        if (!string.IsNullOrWhiteSpace(details))
        {
            text += Environment.NewLine + Environment.NewLine + "技术详情：" + Environment.NewLine
                    + (details.Length > 1200 ? details[..1200] + "…" : details);
        }

        MessageBox.Show(text, "操作未完成", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>第9.0C轮：门禁问题双击 → 定位到对应审核条目。</summary>
    /// <summary>第9.0C.1轮：取消当前操作（分析 / 翻译 / 生成 / 部署）。</summary>
    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.CancelCurrentOperation();
        }
        catch (Exception ex)
        {
            _viewModel.FailGui("取消失败：请稍后重试。", ex);
        }
    }

    /// <summary>
    /// 第9.0C.1轮：关闭窗口时若仍有任务在运行 ⇒ 先确认，再请求取消并等待安全退出
    /// （避免强杀正在写 SQLite / 快照的操作）。
    /// </summary>
    private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || !_viewModel.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        var choice = MessageBox.Show(
            "当前仍有任务正在运行（分析 / 翻译 / 生成 / 部署）。" + Environment.NewLine + Environment.NewLine
            + "是否取消任务并退出？" + Environment.NewLine
            + "取消会在安全点停止，不会写入半成品输出，也不会部署。",
            "退出确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        _viewModel.CancelCurrentOperation();
        var stopped = await _viewModel.WaitForIdleAsync(TimeSpan.FromSeconds(30));
        if (!stopped)
        {
            MessageBox.Show(
                "后台任务仍未退出（可能正在等待网络返回）。为避免数据损坏，程序将保持打开状态，请稍后再关闭。",
                "暂时无法退出",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _closeConfirmed = true;
        Close();
    }


    private void GateIssues_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid
            || grid.SelectedItem is not LimbusTranslator.Wpf.ViewModels.GateIssueRow row)
        {
            return;
        }

        if (!_viewModel.FocusReviewEntry(row.UnitKey))
        {
            MessageBox.Show(
                "该问题对应的条目不在当前待审核列表中（可能已解决或未进入审核列表）。",
                "无法定位",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>第8.86轮：保存当前审核（写回 HumanReviewed + 重新校验 + 刷新筛选）。</summary>
    private void SaveReview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.SaveCurrentReview();
        }
        catch (Exception ex)
        {
            // 第9.0C.12c轮：把异常类型与首个相关栈帧一起显示，避免只看到 "Object reference not set" 无法定位。
            var frame = (ex.StackTrace ?? string.Empty)
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Contains("LimbusTranslator", StringComparison.Ordinal));
            MessageBox.Show(
                $"保存审核失败: {ex.GetType().Name}: {ex.Message}"
                + (frame is null ? string.Empty : $"\n\n位置: {frame}"),
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ───────── 第9.0C.7轮：批量替换 ─────────

    private void PreviewReviewReplace_Click(object sender, RoutedEventArgs e)
        => _viewModel.PreviewReviewReplace();

    private void ApplyReviewReplace_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApplyReviewReplace();

    private void UndoReviewReplace_Click(object sender, RoutedEventArgs e)
        => _viewModel.UndoReviewReplace();

    // ───────── 第9.0C.10轮：AI 找生词 + 中止所有 AI 对话 ─────────

    /// <summary>AI 找生词：先确认（会消耗 Token），再调用。</summary>
    private void AiScanTerms_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            _viewModel.BuildAiTermDiscoveryConfirmationText(),
            "AI 找生词确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _viewModel.DiscoverUnknownTermsAi();
    }

    /// <summary>独立按钮：立刻中止当前所有 AI 活动（翻译 / 找生词 / 生成解释 / 分析）。</summary>
    private void AbortAllAi_Click(object sender, RoutedEventArgs e)
        => _viewModel.AbortAllAi();

    // ───────── 第9.0C.12轮：待审核分页（每页 5000 条 + 跳页 / 跳文件 / 跳到待审）─────────

    private void ReviewPrevPage_Click(object sender, RoutedEventArgs e)
        => _viewModel.PreviousReviewPage();

    private void ReviewNextPage_Click(object sender, RoutedEventArgs e)
        => _viewModel.NextReviewPage();

    private void ReviewJumpPage_Click(object sender, RoutedEventArgs e)
        => _viewModel.JumpToReviewPage();

    private void ReviewJumpFile_Click(object sender, RoutedEventArgs e)
        => _viewModel.JumpToReviewFile();

    private void ReviewJumpNeedsReview_Click(object sender, RoutedEventArgs e)
        => _viewModel.JumpToFirstNeedsReview();

    // ───────── 第9.0C.8轮：从 output 载入进度 ─────────

    private void LoadProgressFromOutput_Click(object sender, RoutedEventArgs e)
        => _viewModel.LoadProgressFromOutput();

    /// <summary>第8.86轮：批量确认当前筛选中无 Error 的条目（弹确认框，不修改译文）。</summary>
    private void BulkConfirmReview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel.ConfirmVisibleWithoutErrors();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"批量确认失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 日志智能滚动：在底部时自动跟随；向上浏览时暂停跟随；回到底部恢复。
    /// </summary>
    private void LogTextBox_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0)
        {
            // 用户手动滚动：判断是否在底部
            var scroller = FindVisualChild<System.Windows.Controls.ScrollViewer>(LogTextBox);
            if (scroller is null)
            {
                return;
            }
            _autoScrollLogs = scroller.VerticalOffset >= scroller.ScrollableHeight - 5;
        }
        else if (_autoScrollLogs)
        {
            // 新日志到达且允许跟随：滚到底部
            var scroller = FindVisualChild<System.Windows.Controls.ScrollViewer>(LogTextBox);
            scroller?.ScrollToEnd();
        }
    }

    /// <summary>文本更新时沿用原有“仅在底部自动跟随”的逻辑。</summary>
    private void LogTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_autoScrollLogs)
        {
            LogTextBox.ScrollToEnd();
        }
    }

    /// <summary>一键选中并复制完整运行日志。</summary>
    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_viewModel.LogText))
        {
            return;
        }

        LogTextBox.Focus();
        LogTextBox.SelectAll();
        LogTextBox.Copy();
    }

    /// <summary>
    /// 查找可视子元素。
    /// </summary>
    private static T? FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                return typed;
            }
            var result = FindVisualChild<T>(child);
            if (result is not null)
            {
                return result;
            }
        }
        return null;
    }

    private void RunDiff_Click(object sender, RoutedEventArgs e)
        => _viewModel.RunDiff();

    private void LocateGameDir_Click(object sender, RoutedEventArgs e)
        => _viewModel.LocateGameDir();

    private void ExtractNewFiles_Click(object sender, RoutedEventArgs e)
        => _viewModel.ExtractNewFiles();

    private void RunTranslate_Click(object sender, RoutedEventArgs e)
        => _viewModel.RunTranslate();

    private void RegenerateOutput_Click(object sender, RoutedEventArgs e)
        => _viewModel.RegenerateOutput();

    private void RecoverOutput_Click(object sender, RoutedEventArgs e)
        => _viewModel.RecoverOutputFromCache();

    /// <summary>
    /// 第9.0C.17轮：只重译「本批翻译失败」的条目（结构化标记 ProviderBatchFailed）；
    /// 其余条目与文件保持不动，避免整轮重烧 API。
    /// </summary>
    private void RetranslateFailedEntries_Click(object sender, RoutedEventArgs e)
        => _viewModel.RetranslateFailedEntries();

    private void SelectAllCategories_Click(object sender, RoutedEventArgs e)
        => _viewModel.SelectAllCategories();

    private void DeselectAllCategories_Click(object sender, RoutedEventArgs e)
        => _viewModel.DeselectAllCategories();

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
        => _viewModel.SelectAllVisibleFiles();

    private void SelectNoneFiles_Click(object sender, RoutedEventArgs e)
        => _viewModel.SelectNoneVisibleFiles();

    private void InvertFiles_Click(object sender, RoutedEventArgs e)
        => _viewModel.InvertVisibleFiles();

    /// <summary>
    /// 第9.0C.3.1修复：文件勾选**只由真实点击写入**（复选框是单向绑定 + Click）。
    /// 这样彻底避开两个 WPF 陷阱：
    ///   ① 模板列可编辑时，第一次点击会被 DataGrid 用于"进入编辑模式"，复选框勾不上；
    ///   ② 行虚拟化回收容器时，双向绑定会把容器上的旧值回写到新行（把"默认全选"洗成 0 个）。
    /// </summary>
    private void FileTaskCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox box
            && box.DataContext is LimbusTranslator.Wpf.ViewModels.FileTaskRow row)
        {
            row.IsSelected = box.IsChecked == true;
            box.IsChecked = row.IsSelected;   // 与权威状态保持一致
        }
    }

    // ───────── 第9.0C.15轮：文件列表的右键菜单 + Ctrl/Shift 多选 ─────────

    /// <summary>
    /// 右键落在**未选中**的行上时，先把选择切到该行（与资源管理器一致）；
    /// 落在已选中的行上则保留当前多选集合，供右键菜单批量操作。
    /// </summary>
    private void FileTasksGrid_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.DataGrid grid)
        {
            return;
        }

        var row = FindRow(e.OriginalSource as System.Windows.DependencyObject);
        if (row is null)
        {
            return;
        }

        if (!row.IsSelected)
        {
            grid.SelectedItems.Clear();
            row.IsSelected = true;
        }

        grid.Focus();   // 让右键菜单里的动作有明确的作用对象
    }

    private static System.Windows.Controls.DataGridRow? FindRow(System.Windows.DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is System.Windows.Controls.DataGridRow row)
            {
                return row;
            }

            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private List<LimbusTranslator.Wpf.ViewModels.FileTaskRow> SelectedFileRows()
        => FileTasksGrid.SelectedItems
            .OfType<LimbusTranslator.Wpf.ViewModels.FileTaskRow>()
            .ToList();

    private void MarkFilesSelected_Click(object sender, RoutedEventArgs e)
        => _viewModel.MarkFilesSelected(SelectedFileRows());

    private void MarkFilesUnselected_Click(object sender, RoutedEventArgs e)
        => _viewModel.MarkFilesUnselected(SelectedFileRows());

    private void InvertFilesSelection_Click(object sender, RoutedEventArgs e)
        => _viewModel.InvertFilesSelection(SelectedFileRows());

    /// <summary>第9.0C.18轮：清除文件列表搜索（只影响呈现，不改变勾选）。</summary>
    private void ClearFileTaskSearch_Click(object sender, RoutedEventArgs e)
        => _viewModel.ClearFileTaskSearch();

    /// <summary>
    /// 第9.0C.22轮：按上次「提取待汉化文件」的清单恢复勾选（全局只留这批文件），
    /// 让重开程序后也能直接接着上次的活干。
    /// </summary>
    private void RestoreExtractSelection_Click(object sender, RoutedEventArgs e)
        => _viewModel.RestoreSelectionFromLastExtract();

    private void ScanTerms_Click(object sender, RoutedEventArgs e)
        => _viewModel.ScanIncrementalTerms();

    private void ExplainTerms_Click(object sender, RoutedEventArgs e)
        => _viewModel.ExplainIncrementalTerms();

    private void WriteTerms_Click(object sender, RoutedEventArgs e)
        => _viewModel.WriteIncrementalTerms();

    private void BrowseOldEn_Click(object sender, RoutedEventArgs e)
        => _viewModel.BrowseDirectory("old_en");

    private void BrowseOldZh_Click(object sender, RoutedEventArgs e)
        => _viewModel.BrowseDirectory("old_zh");

    private void BrowseNewEn_Click(object sender, RoutedEventArgs e)
        => _viewModel.BrowseDirectory("new_en");

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var configDir = Path.Combine(FindProjectRoot(), "config");
        var window = new SettingsWindow(configDir)
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    /// <summary>显示发布门禁结论与可定位的问题样本。</summary>
    private void ShowGateReasons_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.MessageBox.Show(
            _viewModel.GateReasonText,
            "发布门禁原因",
            MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }

    private async void DeployToGame_Click(object sender, RoutedEventArgs e)
    {
        // 第9.0C轮：确认框由 DeployPresentation 统一生成（目标目录 / 文件数 / 备份 / 回滚 / 门禁）
        try
        {
            var confirm = MessageBox.Show(
                _viewModel.BuildDeployConfirmationText(),
                "部署到游戏确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            ShowDeployResult(await _viewModel.DeployToGameAsync());
        }
        catch (Exception ex)
        {
            _viewModel.FailGui("部署过程中出现未预期的错误，游戏目录可能未完整更新，请查看运行日志。", ex);
        }
    }

    /// <summary>
    /// 第9.0C.13轮：部署到**汉化文件夹**——目标目录**自动定位**（与「部署到游戏」同源），
    /// 且**只部署本轮「任务范围」勾选的文件**（增量，按原有目录结构写入；其他文件不被触碰）。
    /// 自动定位失败时才让用户手动选择目录。安全流程与「部署到游戏」完全共用。
    /// </summary>
    private async void DeployToFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var targetDir = await _viewModel.ResolveDeployFolderAsync();
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "未能自动定位汉化文件夹，请手动选择目标目录",
                    Multiselect = false,
                };

                if (Directory.Exists(_viewModel.LastDeployFolderPath))
                {
                    dialog.InitialDirectory = _viewModel.LastDeployFolderPath;
                }

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                targetDir = dialog.FolderName;
            }

            var selectedFiles = _viewModel.SelectedDeployFiles();
            var restrict = selectedFiles.Count > 0 ? selectedFiles : null;

            var confirm = MessageBox.Show(
                _viewModel.BuildDeployConfirmationText(targetDir, "汉化文件夹", restrict),
                "部署到汉化文件夹确认",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            _viewModel.LastDeployFolderPath = targetDir;
            ShowDeployResult(await _viewModel.DeployToFolderAsync(targetDir));
        }
        catch (Exception ex)
        {
            _viewModel.FailGui("部署过程中出现未预期的错误，目标文件夹可能未完整更新，请查看运行日志。", ex);
        }
    }

    /// <summary>
    /// 第9.0C.20轮（P2）：生成**完整快照**（全部权威文件写入 data/output）并立刻载入界面。
    /// 用于「从 output 载入进度」仍然不全时的兜底。
    /// </summary>
    private void GenerateFullSnapshot_Click(object sender, RoutedEventArgs e)
        => _viewModel.GenerateFullSnapshot();

    /// <summary>把部署结果统一展示出来（成功/失败、是否回滚、备份位置）。</summary>
    private void ShowDeployResult(string result)
    {
        var isFailure = result.StartsWith("[错误]", StringComparison.Ordinal);
        var title = isFailure
            ? (_viewModel.LastDeployOutcomeCritical ? "部署失败（需要人工检查）" : "部署失败")
            : "部署完成";
        var message = string.IsNullOrWhiteSpace(_viewModel.LastDeployOutcomeText)
            ? result
            : _viewModel.LastDeployOutcomeText + Environment.NewLine + Environment.NewLine + result;

        MessageBox.Show(message, title, MessageBoxButton.OK,
            isFailure ? MessageBoxImage.Error : MessageBoxImage.Information);
    }

    /// <summary>
    /// 向上查找项目根目录。
    /// </summary>
    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.sln"))
                || File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    // ================= 第8.88轮：逐条审校工作台 =================

    /// <summary>存在未保存修改时的导航确认（保存 / 放弃 / 取消导航：取消返回 false）。</summary>
    private bool ConfirmDiscardUnsavedEdit()
    {
        if (!_viewModel.HasUnsavedSequentialEdit)
        {
            return true;
        }

        var choice = System.Windows.MessageBox.Show(
            "当前译文有未保存修改。\n\n是否放弃修改并继续导航？\n（选择「否」可先点「保存」再导航）",
            "未保存修改",
            MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        return choice == MessageBoxResult.Yes;
    }

    private void BeginSequentialReview_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.BeginSequentialReview();
        System.Windows.MessageBox.Show(
            "已进入逐条审校。\n\n快捷键：Ctrl+S 保存；Ctrl+Enter 通过并下一条；Alt+←/→ 上一条/下一条。",
            "逐条审校", MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    private void SequentialPrev_Click(object sender, RoutedEventArgs e)
        => _viewModel.SequentialPrevious(ConfirmDiscardUnsavedEdit);

    private void SequentialNext_Click(object sender, RoutedEventArgs e)
        => _viewModel.SequentialNext(ConfirmDiscardUnsavedEdit);

    private void SequentialApprove_Click(object sender, RoutedEventArgs e)
        => _viewModel.ApproveAndNext(ConfirmDiscardUnsavedEdit);

    private void SequentialMarkPending_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.MarkSequentialPending();
        _viewModel.SequentialNext(ConfirmDiscardUnsavedEdit);
    }

    /// <summary>逐条审校快捷键（Ctrl+S 保存 / Ctrl+Enter 通过并下一条 / Alt+←·→ 导航）。</summary>
    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;

        if (ctrl && e.Key == System.Windows.Input.Key.S)
        {
            _viewModel.SaveSequential();
            e.Handled = true;
        }
        else if (ctrl && (e.Key == System.Windows.Input.Key.Enter || e.Key == System.Windows.Input.Key.Return))
        {
            _viewModel.ApproveAndNext(ConfirmDiscardUnsavedEdit);
            e.Handled = true;
        }
        else if (alt && e.Key == System.Windows.Input.Key.Left)
        {
            _viewModel.SequentialPrevious(ConfirmDiscardUnsavedEdit);
            e.Handled = true;
        }
        else if (alt && e.Key == System.Windows.Input.Key.Right)
        {
            _viewModel.SequentialNext(ConfirmDiscardUnsavedEdit);
            e.Handled = true;
        }
    }
}
