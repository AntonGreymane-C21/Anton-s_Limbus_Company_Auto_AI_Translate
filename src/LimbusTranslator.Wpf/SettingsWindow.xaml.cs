using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using LimbusTranslator.Infrastructure.CharacterStyle;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Security;

namespace LimbusTranslator.Wpf;

/// <summary>
/// 配置设置窗口：AI 提示词 / 专业术语 / 角色风格 / API 设置。
/// 可编辑、保存（立即生效）、恢复默认。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly string _configDir;
    private readonly PromptOptions _promptOptions = new();
    private GlossaryService _glossary = null!;
    private CharacterStyleService _characterStyles = null!;

    public ObservableCollection<GlossaryRow> GlossaryRows { get; } = new();
    public ObservableCollection<CharacterRow> CharacterRows { get; } = new();

    public string SystemPrompt
    {
        get => _promptOptions.SystemPrompt;
        set => _promptOptions.SystemPrompt = value;
    }

    public string ApiUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string Temperature { get; set; } = "0.3";
    public string MaxTokens { get; set; } = "4096";
    public string TimeoutSeconds { get; set; } = "120";
    public string MaxConcurrentRequests { get; set; } = "10";

    /// <summary>旧字段（兼容保留；GUI 主界面只用 thinkingMode 下拉）。</summary>
    public bool Thinking { get; set; }

    /// <summary>思考模式下拉项（第8.8轮）</summary>
    public ObservableCollection<string> ThinkingModeOptions { get; } = new()
    {
        "自适应（推荐）", "始终开启", "始终关闭",
    };

    /// <summary>选中的思考模式（写回 deepSeek.thinkingMode）</summary>
    public string SelectedThinkingMode { get; set; } = "自适应（推荐）";

    /// <summary>思考强度下拉项</summary>
    public ObservableCollection<string> ReasoningEffortOptions { get; } = new()
    {
        "默认", "low", "high", "max",
    };

    /// <summary>选中的思考强度（“默认”= 不写入 reasoningEffort）</summary>
    public string SelectedReasoningEffort { get; set; } = "默认";

    /// <summary>Paratranz 是否启用（写入 paratranz.enabled）</summary>
    public bool ParatranzEnabled { get; set; }

    /// <summary>Paratranz 项目 ID（写入 paratranz.projectId）</summary>
    public string ParatranzProjectId { get; set; } = "6860";
    public string BatchMaxItems { get; set; } = BatchOptions.DefaultMaxItemsPerBatch.ToString();

    /// <summary>每批最大字符数（batch.maxCharactersPerBatch）</summary>
    public string BatchMaxCharacters { get; set; } = BatchOptions.DefaultMaxCharactersPerBatch.ToString();

    /// <summary>目标输入 Token（保留位，当前不参与切分）</summary>
    public string TargetInputTokens { get; set; } = BatchOptions.DefaultTargetInputTokens.ToString();

    /// <summary>第9.0C轮：术语库只读摘要（条数 / 强制 / 优先 / 来源）。</summary>
    public string GlossarySummary { get; private set; } = "（术语库未加载）";

    /// <summary>第9.0C轮：当前翻译模式（在主界面切换；这里只读展示）。</summary>
    public string CurrentTranslationModeSummary { get; private set; } = "（未读取）";

    /// <summary>第9.0C轮：API Key 输入同步（事件触发，ViewModel 不直接接触明文输入控件）。</summary>
    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
        {
            ApiKey = box.Password;
        }
    }

    public SettingsWindow(string configDir)
    {
        InitializeComponent();
        _configDir = configDir;
        DataContext = this;

        // 加载提示词
        var loadedPrompt = PromptLoader.Load(configDir);
        _promptOptions.SystemPrompt = loadedPrompt.SystemPrompt;
        _promptOptions.OutputFormat = loadedPrompt.OutputFormat;

        // 加载术语
        _glossary = new GlossaryService(configDir);
        foreach (var kvp in _glossary.Entries)
        {
            GlossaryRows.Add(new GlossaryRow
            {
                English = kvp.Key,
                Translation = kvp.Value.Translation,
                Locked = kvp.Value.Locked,
            });
        }
        GlossaryGrid.ItemsSource = GlossaryRows;

        // 第9.0C轮：术语库摘要 + 当前翻译模式（只读，便于普通用户确认状态）
        var locked = _glossary.Entries.Count(pair => pair.Value.Locked);
        var local = _glossary.Entries.Count(pair => pair.Value.Source == GlossaryTermSource.Local);
        var paratranz = _glossary.Entries.Count(pair => pair.Value.Source == GlossaryTermSource.Paratranz);
        GlossarySummary = $"共 {_glossary.Entries.Count} 条｜强制(Locked) {locked} / 优先 {_glossary.Entries.Count - locked}"
                          + $"｜来源：本地 {local} / Paratranz {paratranz}";

        var currentMode = AppSettingsLoader.TryLoadTranslationMode(configDir, out var mode, out _)
            ? TranslationModePresentation.Resolve(mode).Headline
            : "（读取失败，默认英文翻译）";
        CurrentTranslationModeSummary = $"当前翻译模式：{currentMode}（在主界面「翻译模式」处切换）";

        // 加载角色风格
        _characterStyles = new CharacterStyleService(configDir);
        foreach (var kvp in _characterStyles.Styles)
        {
            CharacterRows.Add(new CharacterRow { Name = kvp.Key, Style = kvp.Value });
        }
        CharacterGrid.ItemsSource = CharacterRows;

        // 加载 API 配置（第8.8轮：使用 fail-closed 结果，同时读取 thinkingMode 与 batch）
        var settings = AppSettingsLoader.LoadProviderSettings(configDir);
        var options = settings.Options;
        ApiUrl = options.ApiUrl;
        ApiKey = options.ApiKey;
        // 第9.0C轮：API Key 用 PasswordBox 承载（不在界面完整明文显示）
        ApiKeyBox.Password = ApiKey;
        Model = options.Model;
        Temperature = options.Temperature.ToString("0.##");
        MaxTokens = options.MaxTokens.ToString();
        TimeoutSeconds = options.TimeoutSeconds.ToString();
        MaxConcurrentRequests = options.MaxConcurrentRequests.ToString();
        Thinking = options.Thinking;

        SelectedThinkingMode = (options.ThinkingMode ??
            (options.Thinking ? TranslationThinkingMode.AlwaysOn : TranslationThinkingMode.Adaptive)) switch
        {
            TranslationThinkingMode.AlwaysOn => "始终开启",
            TranslationThinkingMode.AlwaysOff => "始终关闭",
            _ => "自适应（推荐）",
        };
        SelectedReasoningEffort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? "默认" : options.ReasoningEffort!;
        BatchMaxItems = settings.Batch.MaxItemsPerBatch.ToString();
        BatchMaxCharacters = settings.Batch.MaxCharactersPerBatch.ToString();
        TargetInputTokens = settings.Batch.TargetInputTokens.ToString();

        // 第8.89轮：Paratranz 区域初始化（默认 enabled=false / projectId=6860）
        var paratranzOptions = AppSettingsLoader.LoadParatranz(configDir);
        ParatranzEnabled = paratranzOptions.Enabled;
        ParatranzProjectId = string.IsNullOrWhiteSpace(paratranzOptions.ProjectId) ? "6860" : paratranzOptions.ProjectId!;
        RefreshParatranzStatus();
    }

    private void SavePrompt_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PromptLoader.Save(_configDir, _promptOptions);
            MessageBox.Show("提示词已保存，下次翻译立即生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetPrompt_Click(object sender, RoutedEventArgs e)
    {
        _promptOptions.SystemPrompt = new PromptOptions().SystemPrompt;
        _promptOptions.OutputFormat = new PromptOptions().OutputFormat;
        MessageBox.Show("已恢复默认提示词，点击「保存提示词」生效。", "恢复默认", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveGlossary_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var skipped = new List<string>();
            foreach (var row in GlossaryRows)
            {
                if (!string.IsNullOrWhiteSpace(row.English))
                {
                    // 第8.87轮：Update 返回 false 表示译名为空 —— 拒绝写入空译名，避免“只有原文”的坏数据
                    if (!_glossary.Update(row.English, row.Translation ?? string.Empty, row.Locked))
                    {
                        skipped.Add(row.English);
                    }
                }
            }

            _glossary.Save();

            var message = "术语库已更新，将在下一次翻译前重新匹配当前任务（无需重新分析）。";
            if (skipped.Count > 0)
            {
                message += $"\n\n以下 {skipped.Count} 条因译名为空未写入（请补全译名后重新保存）：\n"
                    + string.Join("、", skipped.Take(10))
                    + (skipped.Count > 10 ? " …" : string.Empty);
            }

            MessageBox.Show(message, "保存成功", MessageBoxButton.OK,
                skipped.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {LimbusTranslator.Infrastructure.Security.SecretRedactor.Summarize(ex.Message)}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetGlossary_Click(object sender, RoutedEventArgs e)
    {
        _glossary.ResetToDefault();
        GlossaryRows.Clear();
        foreach (var kvp in _glossary.Entries)
        {
            GlossaryRows.Add(new GlossaryRow
            {
                English = kvp.Key,
                Translation = kvp.Value.Translation,
                Locked = kvp.Value.Locked,
            });
        }
        MessageBox.Show("已恢复默认术语，点击「保存术语」生效。", "恢复默认", MessageBoxButton.OK, MessageBoxImage.Information);
    }


    private void SaveCharacters_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var row in CharacterRows)
            {
                if (!string.IsNullOrWhiteSpace(row.Name))
                {
                    _characterStyles.Update(row.Name, row.Style ?? string.Empty);
                }
            }
            _characterStyles.Save();
            MessageBox.Show("角色风格已保存，下次翻译立即生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ResetCharacters_Click(object sender, RoutedEventArgs e)
    {
        _characterStyles.ResetToDefault();
        CharacterRows.Clear();
        foreach (var kvp in _characterStyles.Styles)
        {
            CharacterRows.Add(new CharacterRow { Name = kvp.Key, Style = kvp.Value });
        }
        MessageBox.Show("已恢复默认角色风格，点击「保存风格」生效。", "恢复默认", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveApi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new DeepSeekOptions
            {
                ApiUrl = ApiUrl,
                ApiKey = ApiKey,
                Model = Model,
                Thinking = Thinking,
                ThinkingMode = SelectedThinkingMode switch
                {
                    "始终开启" => TranslationThinkingMode.AlwaysOn,
                    "始终关闭" => TranslationThinkingMode.AlwaysOff,
                    _ => TranslationThinkingMode.Adaptive,
                },
                ReasoningEffort = SelectedReasoningEffort == "默认" ? null : SelectedReasoningEffort,
                Temperature = double.TryParse(Temperature, out var t) ? t : 0.3,
                MaxTokens = int.TryParse(MaxTokens, out var mt) ? mt : 4096,
                TimeoutSeconds = int.TryParse(TimeoutSeconds, out var ts) ? ts : 120,
                MaxRetry = 5,
                MaxConcurrentRequests = int.TryParse(MaxConcurrentRequests, out var mc) ? mc : 10,
            };
            var batch = new BatchOptions
            {
                MaxItemsPerBatch = int.TryParse(BatchMaxItems, out var bi) && bi > 0 ? bi : BatchOptions.DefaultMaxItemsPerBatch,
                MaxCharactersPerBatch = int.TryParse(BatchMaxCharacters, out var bc) && bc > 0 ? bc : BatchOptions.DefaultMaxCharactersPerBatch,
                TargetInputTokens = int.TryParse(TargetInputTokens, out var tk) && tk > 0 ? tk : BatchOptions.DefaultTargetInputTokens,
            };
            AppSettingsLoader.SaveDeepSeek(_configDir, options, batch);

            // 保存后立刻用 fail-closed 读取验证（配置非法时明确提示，而不是静默生效）
            var reloaded = AppSettingsLoader.LoadProviderSettings(_configDir);
            if (!reloaded.Success)
            {
                MessageBox.Show(
                    "API 配置已写入，但校验未通过：\n" + string.Join("\n", reloaded.Errors)
                    + "\n\n翻译任务会拒绝启动，请修正后再试。",
                    "配置已保存但无效", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show("API 配置已保存，下次翻译立即生效。", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存失败: {SecretRedactor.Summarize(ex.Message, 200, ApiKey)}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>第8.89轮：Paratranz 区域状态刷新（启用与否 / 缓存 / 上次同步）。</summary>
    private void RefreshParatranzStatus()
    {
        var options = AppSettingsLoader.LoadParatranz(_configDir);
        options.Enabled = ParatranzEnabled;
        if (!string.IsNullOrWhiteSpace(ParatranzProjectId))
        {
            options.ProjectId = ParatranzProjectId.Trim();
        }

        var cachePath = Path.IsPathRooted(options.CachePath)
            ? options.CachePath
            : Path.Combine(Path.GetFullPath(Path.Combine(_configDir, "..")), options.CachePath);

        var cache = LimbusTranslator.Infrastructure.Paratranz.ParatranzGlossaryCacheStore.TryLoad(cachePath);
        var effective = cache is null
            ? "(无缓存)"
            : $"远程原始 {cache.Audit.RawCount} 条｜有效 {cache.Entries.Count} 条｜同源多译 {cache.Audit.MultiTargetConflicts} 组";

        ParatranzStatusText.Text =
            $"启用：{(ParatranzEnabled ? "是" : "否")}｜项目 ID：{options.ProjectId}｜" +
            $"最后同步：{(cache is null ? "—" : cache.FetchedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}｜" +
            $"{effective}｜缓存：{cachePath}";
    }

    private void ParatranzEnabled_Changed(object sender, RoutedEventArgs e) => RefreshParatranzStatus();

    /// <summary>保存 Paratranz 设置（只改 paratranz 段，不动其它配置）。</summary>
    private void SaveParatranz_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = new LimbusTranslator.Infrastructure.Paratranz.ParatranzOptions
            {
                Enabled = ParatranzEnabled,
                ProjectId = string.IsNullOrWhiteSpace(ParatranzProjectId) ? "6860" : ParatranzProjectId.Trim(),
            };

            AppSettingsLoader.SaveParatranz(_configDir, options);
            RefreshParatranzStatus();
            System.Windows.MessageBox.Show("Paratranz 设置已保存，下次翻译立即生效。", "保存成功",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("保存失败: " + SecretRedactor.Summarize(ex.Message, 200),
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 同步 Paratranz（第8.89轮）：只读远程 → 写自己的缓存；**绝不修改 config/glossary.json**。
    /// 失败时保留上一份有效缓存。
    /// </summary>
    private async void SyncParatranz_Click(object sender, RoutedEventArgs e)
    {
        ParatranzSyncButton.IsEnabled = false;
        ParatranzStatusText.Text = "正在同步…";
        try
        {
            var options = new LimbusTranslator.Infrastructure.Paratranz.ParatranzOptions
            {
                Enabled = ParatranzEnabled,
                ProjectId = string.IsNullOrWhiteSpace(ParatranzProjectId) ? "6860" : ParatranzProjectId.Trim(),
            };

            var cachePath = Path.IsPathRooted(options.CachePath)
                ? options.CachePath
                : Path.Combine(Path.GetFullPath(Path.Combine(_configDir, "..")), options.CachePath);

            var sync = new LimbusTranslator.Infrastructure.Paratranz.ParatranzGlossarySync(options);
            var result = await sync.SyncAsync(cachePath);

            var conflictInfo = RefreshParatranzConflictInfo(cachePath);
            ParatranzStatusText.Text = result.Success
                ? $"同步成功｜远程 {result.RawCount} 条｜有效 {result.EffectiveCount} 条｜" +
                  $"冲突 {conflictInfo.Conflicts} 组｜本地覆盖 {conflictInfo.LocalOverrides} 条｜" +
                  $"耗时 {result.DurationMs} ms｜最后同步 {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                : $"同步失败（{result.Status}）：{result.Message}｜{(result.KeptPreviousCache ? "仍继续使用上一份有效缓存" : "无可用缓存")}";
        }
        catch (Exception ex)
        {
            ParatranzStatusText.Text = "同步失败: " + SecretRedactor.Summarize(ex.Message, 200) + "（保留旧缓存）";
        }
        finally
        {
            ParatranzSyncButton.IsEnabled = true;
        }
    }

    /// <summary>合并统计（冲突 / 本地覆盖）：用真实缓存与本地术语做一次只读合并。</summary>
    private static (int Conflicts, int LocalOverrides) RefreshParatranzConflictInfo(string cachePath)
    {
        var cache = LimbusTranslator.Infrastructure.Paratranz.ParatranzGlossaryCacheStore.TryLoad(cachePath);
        if (cache is null)
        {
            return (0, 0);
        }

        var local = new LimbusTranslator.Infrastructure.Glossary.GlossaryService(
            Path.GetDirectoryName(Path.GetDirectoryName(cachePath)));
        var merged = LimbusTranslator.Infrastructure.Glossary.GlossaryMerger.Merge(local.Entries, cache.Entries);
        return (merged.Conflicts.Count, merged.LocalOverrides);
    }

    /// <summary>把界面上填写的 API 字段组装成配置对象（保存与测试连接共用，避免两套解析规则）。</summary>
    private DeepSeekOptions BuildOptionsFromFields() => new()
    {
        ApiUrl = ApiUrl?.Trim() ?? string.Empty,
        ApiKey = ApiKey?.Trim() ?? string.Empty,
        Model = Model?.Trim() ?? string.Empty,
        Thinking = Thinking,
        ThinkingMode = SelectedThinkingMode switch
        {
            "始终开启" => TranslationThinkingMode.AlwaysOn,
            "始终关闭" => TranslationThinkingMode.AlwaysOff,
            _ => TranslationThinkingMode.Adaptive,
        },
        ReasoningEffort = SelectedReasoningEffort == "默认" ? null : SelectedReasoningEffort,
        Temperature = double.TryParse(Temperature, out var t) ? t : 0.3,
        MaxTokens = int.TryParse(MaxTokens, out var mt) ? mt : 4096,
        TimeoutSeconds = int.TryParse(TimeoutSeconds, out var ts) ? ts : 120,
        MaxRetry = 5,
        MaxConcurrentRequests = int.TryParse(MaxConcurrentRequests, out var mc) ? mc : 10,
    };

    /// <summary>
    /// 测试连接（第8.87轮）：只有用户主动点击才会发起真实网络请求。
    ///
    /// 使用**界面上当前填写**的值（不必先保存），请求为极小探针；
    /// 不写 TranslationMemory / request_cache / Trace，也不修改任何配置文件。
    /// </summary>
    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        TestConnectionButton.IsEnabled = false;
        TestConnectionResult.Text = "正在测试…";
        try
        {
            var options = BuildOptionsFromFields();
            var timeout = options.TimeoutSeconds > 0 ? Math.Min(options.TimeoutSeconds, 60) : 30;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout + 5));

            var tester = new ApiConnectionTester(options);
            var result = await tester.TestAsync(cts.Token);

            TestConnectionResult.Text = ApiConnectionStatusText.Format(result);
        }
        catch (Exception ex)
        {
            TestConnectionResult.Text = "测试失败: " + SecretRedactor.Summarize(ex.Message, 200, ApiKey);
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}

/// <summary>术语表格行</summary>
public sealed class GlossaryRow
{
    public string English { get; set; } = string.Empty;
    public string? Translation { get; set; }
    public bool Locked { get; set; }
}

/// <summary>角色风格表格行</summary>
public sealed class CharacterRow
{
    public string Name { get; set; } = string.Empty;
    public string? Style { get; set; }
}

