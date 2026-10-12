using System.Windows;
using System.Windows.Controls;
using LimbusTranslator.Wpf.ViewModels;
using Microsoft.Win32;

namespace LimbusTranslator.Wpf.Views;

/// <summary>
/// 简易模式界面：只负责把按钮转发给 <see cref="MainViewModel"/> 的简易模式方法，
/// 需要弹窗（设置 / 部署确认 / 发布检查原因）的操作交给 <see cref="MainWindow"/>，与专家模式共用同一套对话框。
/// </summary>
public partial class SimpleModeView : UserControl
{
    public SimpleModeView()
    {
        InitializeComponent();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private MainWindow? Host => Window.GetWindow(this) as MainWindow;

    /// <summary>每次回到简易模式时把分段切换复位到「简易」。</summary>
    private void SimpleModeView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            SimpleModeRadio.IsChecked = true;
        }
    }

    private void SwitchToExpert_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm)
        {
            vm.IsSimpleMode = false;
        }
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
        => Host?.OpenSettingsFromSimpleMode();

    private void LocateGame_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleLocateGame();

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            Title = "选择《边狱巴士》安装目录（包含 LimbusCompany.exe 的文件夹）",
            Multiselect = false,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        var error = vm.SimpleApplyGameRoot(dialog.FolderName);
        if (error is not null)
        {
            MessageBox.Show(Window.GetWindow(this)!, error, "无法使用这个文件夹", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Analyze_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleAnalyze();

    private void Translate_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleTranslate();

    private void RetranslateFailed_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleRetranslateFailed();

    private void SimpleSave_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleSaveReview();

    private void SimpleApproveNext_Click(object sender, RoutedEventArgs e)
    {
        Vm?.SimpleApproveAndNext();
        SimpleTranslationBox.Focus();
        SimpleTranslationBox.CaretIndex = SimpleTranslationBox.Text.Length;
    }

    private void SkipReview_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleSkipReview();

    private void ResumeReview_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleResumeReview();

    private void ApplyReview_Click(object sender, RoutedEventArgs e)
        => Vm?.SimpleApplyReviewChanges();

    private void ShowGateReasons_Click(object sender, RoutedEventArgs e)
        => Host?.ShowGateReasonsFromSimpleMode();

    private async void Deploy_Click(object sender, RoutedEventArgs e)
    {
        if (Host is { } host)
        {
            await host.DeployFromSimpleModeAsync();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => Host?.CancelFromSimpleMode();

    /// <summary>日志自动滚动到底部（用户向上翻看时不打扰）。</summary>
    private void SimpleLogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var box = SimpleLogBox;
        if (!box.IsVisible)
        {
            return;
        }

        var nearBottom = box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 40;
        if (nearBottom || box.ExtentHeight <= box.ViewportHeight)
        {
            box.ScrollToEnd();
        }
    }
}
