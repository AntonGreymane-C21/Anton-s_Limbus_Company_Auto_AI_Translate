using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>Thinking 决策原因（稳定机器码，进入 Trace 的 thinkingPolicyReason）。</summary>
public static class ThinkingPolicyReasons
{
    /// <summary>源语言异常（出现韩文等非英文源文）</summary>
    public const string SourceLanguageAnomaly = "SourceLanguageAnomaly";

    /// <summary>StoryData 分类文本</summary>
    public const string StoryData = "StoryData";

    /// <summary>普通英文文本（自适应默认关闭）</summary>
    public const string DefaultOff = "DefaultOff";

    /// <summary>强制开启（thinkingMode=always_on）</summary>
    public const string AlwaysOn = "AlwaysOn";

    /// <summary>强制关闭（thinkingMode=always_off）</summary>
    public const string AlwaysOff = "AlwaysOff";

    /// <summary>
    /// 韩文为准（第9.0B-P1轮）：KR_ONLY（源文即韩文）或 KR_EN / KR_JP 回退韩文时强制开启 Thinking。
    /// 依据 <see cref="TranslationModePolicy.ShouldForceThinking"/>，不另立第二套规则。
    /// </summary>
    public const string KoreanCanonical = "KoreanCanonical";
}

/// <summary>单条条目的 Thinking 决策。</summary>
public sealed record ThinkingDecision(bool Enabled, string Reason)
{
    /// <summary>决策后的 reasoningEffort（关闭时为 null）。</summary>
    public string? ReasoningEffort { get; init; }
}

/// <summary>
/// 自适应 Thinking 策略（第8.75轮）——**唯一**的 Thinking 决策实现。
///
/// 约束：
///   - 决策发生在 Provider 调用之前，因此**不得**依赖 ValidatorPipeline 的运行结果；
///   - 决策逻辑只允许出现在这里：TranslationAgent / DeepSeekTranslationProvider / RequestComposer
///     一律调用本策略，禁止各自写 if/else；
///   - 判定素材：源文本（韩文检测，复用 <see cref="SourceLanguageDetector"/>）
///     与文件分类（复用 <see cref="TextCategoryHelper"/>，不做脆弱文件名匹配）；
///   - 纯函数、无时间/环境依赖 ⇒ 相同输入必然得到相同决策。
///
/// 正式策略（优先级从高到低）：
///   1. SOURCE_LANGUAGE_ANOMALY（源文含韩文）→ Thinking ON（即使在 StoryData 之外）
///   2. StoryData 分类文本                → Thinking ON
///   3. 其它普通英文文本                  → Thinking OFF
/// </summary>
public sealed class TranslationThinkingPolicy
{
    private readonly TranslationThinkingMode _mode;
    private readonly string? _reasoningEffort;

    /// <param name="mode">Thinking 模式（生产默认 Adaptive）</param>
    /// <param name="reasoningEffort">思考强度（low / high / max；空表示使用服务端默认）</param>
    public TranslationThinkingPolicy(
        TranslationThinkingMode mode = TranslationThinkingMode.Adaptive,
        string? reasoningEffort = null)
    {
        _mode = mode;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
    }

    /// <summary>生产默认策略（Adaptive + 服务端默认强度）。</summary>
    public static TranslationThinkingPolicy Default { get; } = new();

    /// <summary>
    /// 从配置解析策略（唯一解析处，保证"配置 → 策略"没有第二套规则）：
    ///   1. <see cref="DeepSeekOptions.ThinkingMode"/> 已显式设置 → 使用它；
    ///   2. 否则 <see cref="DeepSeekOptions.Thinking"/> == true → AlwaysOn（旧 API / 旧配置语义）；
    ///   3. 否则 → <see cref="TranslationThinkingMode.Adaptive"/>（生产默认）。
    /// </summary>
    public static TranslationThinkingPolicy FromOptions(DeepSeekOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var mode = options.ThinkingMode
                   ?? (options.Thinking ? TranslationThinkingMode.AlwaysOn : TranslationThinkingMode.Adaptive);
        return new TranslationThinkingPolicy(mode, options.ReasoningEffort);
    }

    /// <summary>当前模式。</summary>
    public TranslationThinkingMode Mode => _mode;

    /// <summary>对单条条目作出 Thinking 决策。</summary>
    public ThinkingDecision Decide(DiffEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // 1) 强制模式：与来源无关，但仍给出可读原因（便于 Trace）
        if (_mode == TranslationThinkingMode.AlwaysOn)
        {
            return new ThinkingDecision(true, ThinkingPolicyReasons.AlwaysOn) { ReasoningEffort = _reasoningEffort };
        }

        if (_mode == TranslationThinkingMode.AlwaysOff)
        {
            return new ThinkingDecision(false, ThinkingPolicyReasons.AlwaysOff);
        }

        // 2) 第9.0B-P1轮：**模式感知** —— 韩文为准时强制开启（KR_ONLY 恒 ON；KR_EN / KR_JP 回退韩文 ON）。
        //    判定依据是生产计划注入的模式与生效语言（TranslationModePolicy.ShouldForceThinking 唯一定义）。
        if (TranslationModePolicy.ShouldForceThinking(
                entry.RunTranslationMode ?? TranslationMode.EnglishOnly,
                entry.EffectiveSourceLanguage ?? SourceLanguage.English))
        {
            return new ThinkingDecision(true, ThinkingPolicyReasons.KoreanCanonical) { ReasoningEffort = _reasoningEffort };
        }

        // 3) 自适应：源语言异常优先级最高
        if (SourceLanguageDetector.ContainsHangul(entry.NewSourceText))
        {
            return new ThinkingDecision(true, ThinkingPolicyReasons.SourceLanguageAnomaly) { ReasoningEffort = _reasoningEffort };
        }

        // 4) StoryData 分类文本（含 title / place 等其下所有可翻译字段）
        if (IsStoryData(entry))
        {
            return new ThinkingDecision(true, ThinkingPolicyReasons.StoryData) { ReasoningEffort = _reasoningEffort };
        }

        // 5) 普通英文文本
        return new ThinkingDecision(false, ThinkingPolicyReasons.DefaultOff);
    }

    /// <summary>
    /// 是否属于 StoryData 分类（复用项目既有分类器，不使用 Contains("Story") 之类的脆弱匹配）。
    /// </summary>
    public static bool IsStoryData(DiffEntry entry)
        => TextCategoryHelper.FromRelativePath(entry.Key.RelativeFilePath) == TextCategory.StoryData;
}
