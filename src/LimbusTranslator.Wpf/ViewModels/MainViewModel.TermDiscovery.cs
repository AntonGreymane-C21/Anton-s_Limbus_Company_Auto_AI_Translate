using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.CharacterStyle;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 第9.0C.10轮：「AI 找生词」与「中止所有 AI 对话」。
///
/// 背景（用户反馈）：原来的「扫描术语」是本地正则扫描（大写即候选 + 两张停用词名单），
/// 名单外的常用词（alright / thanks / maybe / …）会大量漏进候选。
///
/// 现在拆成两个按钮（按用户要求保留两条路）：
///   ① 「本地快速扫描术语」= 原本地扫描（免费、快、可能有噪声）；
///   ② 「AI 找生词（调 API）」= 把**被选中文件的文本**交给模型，由模型挑出它"不认识 / 不确定"的词
///      （含建议译名与原因）；真实调用 DeepSeek、消耗 Token、可随时中止。
///
/// 另加**独立按钮**「中止所有 AI 对话」：点下即取消当前所有 AI 活动
/// （翻译 / 找生词 / 生成解释 / 分析），不再发送新请求、不写半成品。
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>AI 找生词时发送的文本条数上限（防止一次点错把整份游戏发出去）。</summary>
    public const int AiTermDiscoveryMaxTexts = 300;

    private string _aiTermDiscoveryStatusText = string.Empty;

    /// <summary>「AI 找生词」状态文案。</summary>
    public string AiTermDiscoveryStatusText
    {
        get => _aiTermDiscoveryStatusText;
        private set { _aiTermDiscoveryStatusText = value; OnPropertyChanged(); }
    }

    /// <summary>「中止所有 AI 对话」是否可用（有任何操作在跑就可用）。</summary>
    public bool CanAbortAllAi => IsBusy;

    /// <summary>调用前的确认文案（code-behind 弹窗使用；含文本量、切块与费用提示）。</summary>
    public string BuildAiTermDiscoveryConfirmationText()
        => string.Join(Environment.NewLine, new[]
        {
            "即将把【当前选中文件的文本】发送给 DeepSeek，让 AI 挑出它不认识 / 不确定的专有名词。",
            string.Empty,
            $"待扫描文本：最多 {AiTermDiscoveryMaxTexts} 条（本轮任务选择里的待翻译条目）",
            $"请求切块：每 {TermExplainer.MaxCharsPerDiscoveryRequest} 字符一批",
            "会真实消耗 DeepSeek Token；运行中可随时点「中止所有 AI 对话」。",
            string.Empty,
            "确认要继续吗？",
        });

    /// <summary>
    /// 「AI 找生词」：由模型判断哪些词需要人工确认译法。
    /// 无可用 API Key / Mock 模式 ⇒ **自动回退本地扫描**（明确提示，不静默）。
    /// </summary>
    public async void DiscoverUnknownTermsAi()
    {
        if (IsBusy)
        {
            return;
        }

        var selection = ResolveTaskSelection();
        if (_lastPlan is null || selection is null)
        {
            StatusText = "请先点「分析更新」，再点「AI 找生词」";
            Log("[调试] AI 找生词：尚未分析（没有生产计划），已取消");
            return;
        }

        var texts = selection.SelectedEntries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.NewSourceText))
            .Select(entry => entry.NewSourceText!)
            .Take(AiTermDiscoveryMaxTexts)
            .ToList();

        if (texts.Count == 0)
        {
            AiTermDiscoveryStatusText = "当前任务选择里没有可扫描的文本";
            Log("[调试] AI 找生词：当前任务选择里没有可扫描的文本");
            return;
        }

        var settings = AppSettingsLoader.LoadProviderSettings(ConfigDir);
        if (!settings.Success || settings.Mode == TranslationProviderMode.Mock || string.IsNullOrWhiteSpace(settings.Options.ApiKey))
        {
            Log("[调试] AI 找生词：无可用 DeepSeek 配置（或 Mock 模式）⇒ 自动回退到本地快速扫描。");
            RunLocalTermScan(texts, "(本地回退)");
            return;
        }

        var token = BeginCancellableOperation();
        IsBusy = true;
        OnPropertyChanged(nameof(CanAbortAllAi));
        StatusText = "AI 找生词中...（可点「中止所有 AI 对话」随时停止）";
        AiTermDiscoveryStatusText = $"正在发送 {texts.Count} 条文本给 DeepSeek...";
        BeginProgress("AI 找生词", "准备请求");
        Log($"[调试] AI 找生词：开始（文本 {texts.Count} 条 / {texts.Sum(t => t.Length)} 字符，每 {TermExplainer.MaxCharsPerDiscoveryRequest} 字符一批）");

        try
        {
            using var explainer = new TermExplainer(settings.Options, handler: null, log: Log);
            var discovered = await explainer.DiscoverUnknownTermsAsync(
                texts,
                (done, _) => UpdateProgress(done, texts.Count, $"已扫描 {done}/{texts.Count} 条文本"),
                token);

            ApplyDiscoveredTerms(discovered, texts);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已中止 AI 找生词";
            AiTermDiscoveryStatusText = "已中止（未写入术语库）";
            Log("[调试] AI 找生词：已被用户中止（不会写入术语库；已发出的请求已尽量中止）");
            CompleteProgress("已中止 AI 找生词");
        }
        catch (Exception ex)
        {
            StatusText = "AI 找生词失败";
            AiTermDiscoveryStatusText = $"失败：{ex.Message}";
            Log($"[调试] AI 找生词失败: {ex.Message}");
            FailProgress("AI 找生词失败");
        }
        finally
        {
            EndCancellableOperation();
            IsBusy = false;
            OnPropertyChanged(nameof(CanAbortAllAi));
        }
    }

    /// <summary>
    /// 「中止所有 AI 对话」（独立按钮）：立刻取消当前所有 AI 活动。
    /// 与「取消当前操作」共用同一个取消源，因此翻译 / 找生词 / 生成解释 / 分析都会被中止。
    /// </summary>
    public void AbortAllAi()
    {
        if (!IsBusy)
        {
            AiTermDiscoveryStatusText = "当前没有正在进行的 AI 活动";
            Log("[调试] 中止所有 AI：当前没有正在进行的操作，忽略。");
            return;
        }

        Log("[调试] 用户点击「中止所有 AI 对话」：正在取消当前操作（不会再发送新的 AI 请求）...");
        CancelCurrentOperation();
        StatusText = "正在中止所有 AI 对话…";
        AiTermDiscoveryStatusText = "正在中止…";
        OnPropertyChanged(nameof(CanAbortAllAi));
    }

    /// <summary>把 AI 找到的词写入增量术语列表（出现次数本地统计，保留既有列的含义）。</summary>
    private void ApplyDiscoveredTerms(IReadOnlyList<DiscoveredTerm> discovered, IReadOnlyList<string> texts)
    {
        var glossary = new GlossaryService(ConfigDir);
        IncrementalTerms.Clear();

        foreach (var term in discovered.OrderBy(term => term.Original, StringComparer.OrdinalIgnoreCase))
        {
            var occurrences = CountOccurrences(texts, term.Original);
            var isExisting = glossary.Entries.TryGetValue(term.Original, out var existing);

            IncrementalTerms.Add(new IncrementalTermEntry
            {
                OriginalText = term.Original,
                Translation = term.SuggestedTranslation,
                ExistingTranslation = isExisting ? existing!.Translation : string.Empty,
                OccurrenceCount = occurrences,
                SourceFileCount = texts.Count(text => ReviewBatchReplace.CountOccurrences(text, term.Original, StringComparison.OrdinalIgnoreCase) > 0),
                IsExisting = isExisting,
                IsSelected = !isExisting,
            });
        }

        var newCount = IncrementalTerms.Count(entry => !entry.IsExisting);
        AiTermDiscoveryStatusText = discovered.Count == 0
            ? "AI 没有挑出需要人工确认的词"
            : $"AI 挑出 {discovered.Count} 个词（新词 {newCount} 个）";
        StatusText = $"AI 找生词完成：{discovered.Count} 个词";
        Log($"[调试] AI 找生词完成：{discovered.Count} 个词（新词 {newCount} 个，已收录 {IncrementalTerms.Count(entry => entry.IsExisting)} 个）");
        foreach (var entry in IncrementalTerms.Take(10))
        {
            Log($"[调试]   · {entry.OriginalText} → {entry.Translation}（出现 {entry.OccurrenceCount} 次，{entry.StatusText}）");
        }
    }

    /// <summary>本地扫描（免费；无 API 回退时也走这里）：复用既有 TermScanner。</summary>
    private void RunLocalTermScan(IReadOnlyList<string> texts, string label)
    {
        try
        {
            var glossary = new GlossaryService(ConfigDir);
            var characterStyles = new CharacterStyleService(ConfigDir);
            var candidates = TermScanner.ScanDetailed(
                texts,
                glossary.Entries,
                minOccurrence: 2,
                excludedTerms: characterStyles.Styles.Keys);

            IncrementalTerms.Clear();
            foreach (var candidate in candidates)
            {
                IncrementalTerms.Add(new IncrementalTermEntry
                {
                    OriginalText = candidate.OriginalText,
                    Translation = string.Empty,
                    ExistingTranslation = candidate.ExistingTranslation,
                    OccurrenceCount = candidate.OccurrenceCount,
                    SourceFileCount = candidate.SourceTextCount,
                    IsExisting = candidate.IsExisting,
                    IsSelected = !candidate.IsExisting,
                });
            }

            var newCount = candidates.Count(candidate => !candidate.IsExisting);
            AiTermDiscoveryStatusText = $"{label}候选 {candidates.Count} 个（新词 {newCount} 个）";
            StatusText = $"{label}扫描完成：{candidates.Count} 个候选";
            Log($"[调试] {label}扫描完成：{candidates.Count} 个候选（新词 {newCount} 个）");
        }
        catch (Exception ex)
        {
            AiTermDiscoveryStatusText = $"{label}扫描失败：{ex.Message}";
            Log($"[调试] {label}扫描失败: {ex.Message}");
        }
    }

    private static int CountOccurrences(IReadOnlyList<string> texts, string term)
        => texts.Sum(text => ReviewBatchReplace.CountOccurrences(text, term, StringComparison.OrdinalIgnoreCase));
}
