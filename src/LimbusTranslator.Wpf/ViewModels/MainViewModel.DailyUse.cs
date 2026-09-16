using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows.Data;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Translation;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// MainViewModel 的「日常使用收口」部分（第8.8轮）。
///
/// 本文件只**消费**既有生产能力：配置状态、运行统计、Token 统计、Review 筛选、
/// 日志过滤、待确认术语、高级详情。不重新实现翻译 / 校验 / 部署逻辑。
/// </summary>
public sealed partial class MainViewModel
{
    // ---------------- 顶部项目状态（§四） ----------------

    private string _providerStatusText = "未检查";
    private string _modelStatusText = "—";
    private string _databaseStatusText = "未检查";
    private string _configStatusText = "未检查";
    private string _gameDirStatusText = "未找到";

    public string ProviderStatusText { get => _providerStatusText; private set { _providerStatusText = value; OnPropertyChanged(); } }
    public string ModelStatusText { get => _modelStatusText; private set { _modelStatusText = value; OnPropertyChanged(); } }
    public string DatabaseStatusText { get => _databaseStatusText; private set { _databaseStatusText = value; OnPropertyChanged(); } }
    public string ConfigStatusText { get => _configStatusText; private set { _configStatusText = value; OnPropertyChanged(); } }
    public string GameDirStatusText { get => _gameDirStatusText; private set { _gameDirStatusText = value; OnPropertyChanged(); } }

    /// <summary>思考模式显示文本（只读展示；编辑在设置窗口）。</summary>
    public string ThinkingModeStatusText { get; private set; } = "未检查";

    /// <summary>
    /// 刷新顶部状态（只读读取配置与目录；不调用 API、不创建翻译任务）。
    /// 配置错误与后端 fail-closed 一致：明确显示错误，绝不自动切 Mock。
    /// </summary>
    public void RefreshEnvironmentStatus()
    {
        try
        {
            var projectRoot = FindProjectRoot();
            var settings = AppSettingsLoader.LoadProviderSettings(Path.Combine(projectRoot, "config"));

            if (!settings.Success)
            {
                ProviderStatusText = "未启动（配置错误）";
                ModelStatusText = "—";
                ConfigStatusText = "错误：" + settings.ErrorSummary;
            }
            else
            {
                ProviderStatusText = settings.Mode == TranslationProviderMode.Mock ? "Mock（测试模式）" : "DeepSeek";
                ModelStatusText = settings.Options.Model;
                ConfigStatusText = settings.Warnings.Count == 0 ? "正常" : $"正常（{settings.Warnings.Count} 条提示）";
                var mode = settings.Options.ThinkingMode
                           ?? (settings.Options.Thinking ? TranslationThinkingMode.AlwaysOn : TranslationThinkingMode.Adaptive);
                ThinkingModeStatusText = mode switch
                {
                    TranslationThinkingMode.AlwaysOn => "始终开启",
                    TranslationThinkingMode.AlwaysOff => "始终关闭",
                    _ => "自适应（推荐）",
                };
                OnPropertyChanged(nameof(ThinkingModeStatusText));
            }

            var tmPath = Path.Combine(projectRoot, "data", "cache", "translation_memory.db");
            DatabaseStatusText = File.Exists(tmPath)
                ? $"已连接（{new FileInfo(tmPath).Length / 1024 / 1024} MB）"
                : "尚未创建（首次翻译时建立）";

            var englishOk = !string.IsNullOrWhiteSpace(NewEnglishDir) && Directory.Exists(NewEnglishDir);
            var chineseOk = !string.IsNullOrWhiteSpace(OldChineseDir) && Directory.Exists(OldChineseDir);
            GameDirStatusText = englishOk && chineseOk ? "已找到" : englishOk || chineseOk ? "部分找到" : "未找到";
        }
        catch (Exception ex)
        {
            ConfigStatusText = "读取失败：" + ex.Message;
            ProviderStatusText = "未检查";
        }
    }

    // ---------------- 分析统计（§五） ----------------

    private int _passthroughCandidateCount;
    private int _aiNeededCount;

    /// <summary>纯符号 / 空源文条目数（不调用 Provider）。</summary>
    public int PassthroughCandidateCount { get => _passthroughCandidateCount; private set { _passthroughCandidateCount = value; OnPropertyChanged(); } }

    /// <summary>真正需要调用 AI 的条目数。</summary>
    public int AiNeededCount { get => _aiNeededCount; private set { _aiNeededCount = value; OnPropertyChanged(); } }

    /// <summary>依据 Diff 结果补齐统计（复用既有分类器，不重新扫描文件）。</summary>
    public void ApplyDiffStatistics(IReadOnlyList<DiffEntry> queueEntries)
    {
        var passthrough = queueEntries.Count(e => !Core.Text.SourceTextGuard.IsReusable(e.NewSourceText));
        var symbolOnly = queueEntries.Count(e =>
            Core.Text.SourceTextGuard.IsReusable(e.NewSourceText)
            && Core.Text.SourceTextClassification.IsSymbolOnly(e.NewSourceText));
        PassthroughCandidateCount = passthrough + symbolOnly;
        AiNeededCount = queueEntries.Count - PassthroughCandidateCount;
    }

    // ---------------- 运行 / Token / Thinking 统计（§九~§十一） ----------------

    private string _runStatsText = "尚未运行";
    private string _tokenStatsText = "尚未运行";
    private string _thinkingStatsText = "尚未运行";
    private string _advancedDetailText = "（暂无）";

    public string RunStatsText { get => _runStatsText; private set { _runStatsText = value; OnPropertyChanged(); } }
    public string TokenStatsText { get => _tokenStatsText; private set { _tokenStatsText = value; OnPropertyChanged(); } }
    public string ThinkingStatsText { get => _thinkingStatsText; private set { _thinkingStatsText = value; OnPropertyChanged(); } }

    /// <summary>高级诊断详情（指纹 / 清单 Id），默认折叠显示。</summary>
    public string AdvancedDetailText { get => _advancedDetailText; private set { _advancedDetailText = value; OnPropertyChanged(); } }

    /// <summary>
    /// 汇总一次翻译运行：Agent/Coordinator 已有计数 + Trace 文件（缓存/网络/Token/耗时/重试）。
    /// </summary>
    public void ApplyRunSummary(
        CoordinatorResult result,
        int queueCount,
        string? traceFilePath,
        (int OnAnomaly, int OnStory, int Off) thinkingCounts)
    {
        var trace = ReadTraceSummary(traceFilePath);
        _lastTraceFilePath = traceFilePath;
        // 第8.85轮：用 Trace 实际结果刷新"真实 Provider 请求"的 Thinking 分布
        RefreshActualThinkingStats();

        RunStatsText =
            $"Exact TM 命中：{result.TotalTmHits}\n"
            + $"RequestCache 命中：{trace.CacheHit}\n"
            + $"网络翻译请求：{trace.NetworkCalled}\n"
            + $"纯符号直通：{result.TotalPassthrough}\n"
            + $"空源文跳过：{result.TotalEmptySourceSkipped}（继承旧中文 {result.TotalEmptySourceInherited}）\n"
            + $"需要人工审核：{result.TotalNeedsReview}\n"
            + $"校验 Error：{result.TotalValidationErrors}\n"
            + $"校验 Warning：{result.TotalValidationWarnings}\n"
            + $"队列条目：{queueCount}";

        TokenStatsText = trace.NetworkCalled + trace.CacheHit == 0
            ? "本轮没有 Provider 请求（全部命中 TM / 缓存 或 直通）"
            : $"API 请求数：{trace.NetworkCalled + trace.CacheHit}（网络 {trace.NetworkCalled} / 缓存 {trace.CacheHit}）\n"
              + $"Input Tokens：{FormatNullable(trace.InputTokens)}\n"
              + $"Output Tokens：{FormatNullable(trace.OutputTokens)}\n"
              + $"Reasoning Tokens：{FormatNullable(trace.ReasoningTokens)}\n"
              + $"Visible Output Tokens：{FormatNullable(trace.VisibleOutputTokens)}\n"
              + $"Total Tokens：{FormatNullable(trace.TotalTokens)}\n"
              + $"耗时：{FormatNullable(trace.DurationMs)} ms\n"
              + $"重试次数：{FormatNullable(trace.RetryCount)}";

        ThinkingStatsText =
            $"Thinking ON（源语言异常）：{thinkingCounts.OnAnomaly}\n"
            + $"Thinking ON（StoryData）：{thinkingCounts.OnStory}\n"
            + $"Thinking OFF（普通文本）：{thinkingCounts.Off}";

        AdvancedDetailText =
            $"Trace 文件：{traceFilePath ?? "（无）"}\n"
            + $"首个请求指纹：{(string.IsNullOrWhiteSpace(trace.FirstFingerprint) ? "—" : trace.FirstFingerprint)}\n"
            + $"输出清单 ManifestId：{ReadManifestId() ?? "—"}\n"
            + "说明：指纹 / 清单 Id / 请求 Id 属高级诊断信息，默认折叠显示。";
    }

    private static string FormatNullable(int? value) => value.HasValue ? value.Value.ToString() : "—";
    private static string FormatNullable(long? value) => value.HasValue ? value.Value.ToString() : "—";

    internal sealed class TraceSummary
    {
        public int NetworkCalled { get; set; }
        public int CacheHit { get; set; }
        public int? InputTokens { get; set; }
        public int? OutputTokens { get; set; }
        public int? ReasoningTokens { get; set; }
        public int? VisibleOutputTokens { get; set; }
        public int? TotalTokens { get; set; }
        public long? DurationMs { get; set; }
        public int? RetryCount { get; set; }
        public string? FirstFingerprint { get; set; }
    }

    /// <summary>从 Trace JSONL 聚合统计（只读；失败时返回空统计，不影响主流程）。</summary>
    internal static TraceSummary ReadTraceSummary(string? traceFilePath)
    {
        var summary = new TraceSummary();
        try
        {
            if (string.IsNullOrWhiteSpace(traceFilePath) || !File.Exists(traceFilePath))
            {
                return summary;
            }

            foreach (var line in File.ReadLines(traceFilePath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("networkCalled", out var nc) && nc.ValueKind == JsonValueKind.True)
                {
                    summary.NetworkCalled++;
                }
                if (root.TryGetProperty("cacheHit", out var ch) && ch.ValueKind == JsonValueKind.True)
                {
                    summary.CacheHit++;
                }

                summary.InputTokens = Add(summary.InputTokens, GetInt(root, "inputTokens"));
                summary.OutputTokens = Add(summary.OutputTokens, GetInt(root, "outputTokens"));
                summary.ReasoningTokens = Add(summary.ReasoningTokens, GetInt(root, "reasoningTokens"));
                summary.VisibleOutputTokens = Add(summary.VisibleOutputTokens, GetInt(root, "visibleOutputTokens"));
                summary.TotalTokens = Add(summary.TotalTokens, GetInt(root, "totalTokens"));
                summary.DurationMs = AddLong(summary.DurationMs, GetLong(root, "durationMs"));
                summary.RetryCount = Add(summary.RetryCount, GetInt(root, "retryCount"));

                if (summary.FirstFingerprint is null
                    && root.TryGetProperty("fingerprint", out var fp)
                    && fp.ValueKind == JsonValueKind.String)
                {
                    summary.FirstFingerprint = fp.GetString();
                }
            }
        }
        catch
        {
            // Trace 只用于展示
        }

        return summary;
    }

    private static int? GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null;

    private static long? GetLong(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;

    private static int? Add(int? current, int? delta) => delta.HasValue ? (current ?? 0) + delta.Value : current;

    private static long? AddLong(long? current, long? delta) => delta.HasValue ? (current ?? 0) + delta.Value : current;

    /// <summary>读取输出清单 Id（不存在或解析失败返回 null）。</summary>
    private static string? ReadManifestId()
    {
        try
        {
            var path = Path.Combine(FindProjectRootStatic(), "data", "output", "output_manifest.json");
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var name in new[] { "manifestId", "ManifestId" })
            {
                if (doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch
        {
            // 仅展示用
        }

        return null;
    }

    // ---------------- 日志过滤（§二十三） ----------------

    private bool _showDebugLogs;

    /// <summary>是否显示 [调试] 日志（默认关闭，只显示普通信息 / Warning / Error）。</summary>
    public bool ShowDebugLogs
    {
        get => _showDebugLogs;
        set
        {
            _showDebugLogs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayLogText));
        }
    }

    /// <summary>日志区显示文本（按 ShowDebugLogs 过滤）。</summary>
    public string DisplayLogText => ShowDebugLogs ? LogText : FilterNonDebug(LogText);

    /// <summary>过滤掉 [调试] 行（保留 Warning / Error / 普通信息）。</summary>
    internal static string FilterNonDebug(string? logText)
    {
        if (string.IsNullOrEmpty(logText))
        {
            return string.Empty;
        }

        var lines = logText.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Contains("[调试]", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(line);
        }

        return string.Join('\n', kept);
    }

    // ---------------- 待确认术语（§二十六，只读展示） ----------------

    /// <summary>待用户确认的术语（只读；GUI 不自动写入 glossary）。</summary>
    public ObservableCollection<string> PendingTerms { get; } = new();

    /// <summary>
    /// 载入待确认术语（优先读取 config/pending_terms.json；文件缺失时使用内置清单）。
    /// 只读展示，不做任何 glossary 写入。
    /// </summary>
    public void LoadPendingTerms()
    {
        PendingTerms.Clear();
        try
        {
            var path = Path.Combine(FindProjectRoot(), "config", "pending_terms.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                if (doc.RootElement.TryGetProperty("terms", out var terms) && terms.ValueKind == JsonValueKind.Array)
                {
                    foreach (var term in terms.EnumerateArray())
                    {
                        if (term.ValueKind == JsonValueKind.String)
                        {
                            var value = term.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                PendingTerms.Add(value!);
                            }
                        }
                    }

                    return;
                }
            }
        }
        catch
        {
            // 读取失败时退回内置清单
        }

        foreach (var term in new[]
                 {
                     "Hong Lu（旧汉化无权威译名）",
                     "Move-in Reg.（候选译法全为 0）",
                     "E.G.O Spinning（与 Threadspinning 疑义）",
                     "Threadspinning（证据不足）",
                     "Takeoff Module（无候选）",
                 })
        {
            PendingTerms.Add(term);
        }
    }

    /// <summary>
    /// 统计本轮条目的 Thinking 决策分布（复用唯一策略实现，GUI 不重新判断）。
    /// </summary>
    public static (int OnAnomaly, int OnStory, int Off) CountThinkingDecisions(
        IReadOnlyList<DiffEntry> entries, TranslationThinkingPolicy policy)
    {
        var onAnomaly = 0;
        var onStory = 0;
        var off = 0;
        foreach (var entry in entries)
        {
            var decision = policy.Decide(entry);
            if (!decision.Enabled)
            {
                off++;
            }
            else if (decision.Reason == ThinkingPolicyReasons.SourceLanguageAnomaly)
            {
                onAnomaly++;
            }
            else
            {
                onStory++;
            }
        }

        return (onAnomaly, onStory, off);
    }

    /// <summary>向上查找项目根目录（仅用于读取只读诊断文件）。</summary>
    private static string FindProjectRootStatic()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
