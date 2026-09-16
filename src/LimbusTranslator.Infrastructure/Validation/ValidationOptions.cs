namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 校验阈值与开关（集中常量化，便于后续改为配置文件）。
///
/// 【第2轮原则】宁可漏报，不要制造海量误报：
///   中英文天然长度不同，历史译文风格不统一，首版阈值全部保持保守。
/// </summary>
public sealed class ValidationOptions
{
    /// <summary>默认配置</summary>
    public static ValidationOptions Default { get; } = new();

    // ---------- EnglishResidue ----------

    /// <summary>触发英文残留的最少连续英文词数</summary>
    public int EnglishResidueMinWords { get; init; } = 3;

    /// <summary>触发英文残留的最少连续英文总字符数</summary>
    public int EnglishResidueMinChars { get; init; } = 12;

    /// <summary>允许保留英文的合法词（项目内固定豁免：缩写 / 专有写法）</summary>
    public IReadOnlySet<string> AllowedEnglishTerms { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HP", "SP", "E.G.O", "EGO", "ID", "UI", "EXP", "LV", "MAX", "MIN",
    };

    /// <summary>用户可配置的额外允许词（默认空，避免凭空放宽）</summary>
    public IReadOnlyCollection<string> ExtraAllowedEnglishTerms { get; init; } = Array.Empty<string>();

    // ---------- SameAsSource ----------

    /// <summary>触发“与源文相同”的最少英文词数</summary>
    public int SameAsSourceMinWords { get; init; } = 3;

    /// <summary>触发“与源文相同”的最少英文字母数</summary>
    public int SameAsSourceMinLetters { get; init; } = 12;

    // ---------- Length ----------

    /// <summary>源文短于该长度时不做比例判断</summary>
    public int LengthMinSourceLength { get; init; } = 12;

    /// <summary>译文 / 源文 长度比下限（低于则 Warning）</summary>
    public double LengthMinRatio { get; init; } = 0.2;

    /// <summary>译文 / 源文 长度比上限（高于则 Warning）</summary>
    public double LengthMaxRatio { get; init; } = 4.0;

    // ---------- Terminology ----------

    /// <summary>
    /// 术语是否必须以“独立单词”形式出现在源文才强制译法。
    /// GlossaryService 的匹配是 Contains 子串匹配（Prompt 需要宽松），
    /// 但校验若沿用子串匹配会产生大量误报（如 Ring ⊂ During、Middle ⊂ Middleware）。
    /// </summary>
    public bool TerminologyRequireWordBoundary { get; init; } = true;

    // ---------- 策略开关 ----------

    /// <summary>继承的旧中文是否运行启发式规则（默认否，避免历史条目海量误报）</summary>
    public bool RunHeuristicsForInherited { get; init; }
}
