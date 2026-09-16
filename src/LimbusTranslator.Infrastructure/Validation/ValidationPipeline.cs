using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 统一校验流水线。
///
/// 接入位置（第2轮）：
///   Provider 返回 → Placeholder 恢复 → ValidatorPipeline → ValidationIssue → NeedsReview → TM Save
///
/// 职责边界：
///   - 负责选择校验器、注入术语需求、合并 Provider 已发现的问题；
///   - Validator 自身保持纯函数（不写库、不改 TM、不调 API、不改游戏文件）；
///   - Issues 与 NeedsReview 是两层：由 <see cref="ValidationNeedsReviewPolicy"/> 决定是否需要人工审核。
/// </summary>
public sealed class ValidationPipeline
{
    private readonly List<ITranslationValidator> _validators;
    private readonly GlossaryService? _glossary;
    private readonly ValidationOptions _options;

    public ValidationPipeline(
        IEnumerable<ITranslationValidator>? validators = null,
        GlossaryService? glossary = null,
        ValidationOptions? options = null)
    {
        _options = options ?? ValidationOptions.Default;
        _glossary = glossary;
        _validators = validators?.ToList() ?? CreateDefaultValidators(_options);
    }

    /// <summary>创建带项目 glossary 的默认流水线。</summary>
    public static ValidationPipeline CreateDefault(string? configDir = null, ValidationOptions? options = null)
        => new(null, new GlossaryService(configDir), options);

    /// <summary>
    /// 第8.875轮：使用**运行时术语快照**创建流水线（Prompt 与 Validator 共享同一术语版本）。
    /// </summary>
    /// <param name="snapshot">本次运行的术语快照</param>
    /// <param name="options">校验选项</param>
    public static ValidationPipeline CreateDefault(ActiveGlossarySnapshot snapshot, ValidationOptions? options = null)
        => new(null, GlossaryService.FromSnapshot(snapshot ?? ActiveGlossarySnapshot.Empty), options);

    /// <summary>默认校验器集合（全部 11 个）。</summary>
    public static List<ITranslationValidator> CreateDefaultValidators(ValidationOptions options) => new()
    {
        new EmptyTranslationValidator(),
        new PlaceholderValidator(),
        new TagValidator(),
        new SameAsSourceValidator(options),
        new NumberValidator(),
        new LineBreakValidator(),
        new EnglishResidueValidator(options),
        new KoreanResidueValidator(),
        new LengthValidator(options),
        new TerminologyValidator(options),
        // 第8.5轮：源语言异常（英文源文件中混入韩文）
        new SourceLanguageAnomalyValidator(),
        // 第9.0B-P1轮：日文残留（中文译文里出现假名）+ Canonical 韩文原文缺失（仅 KR 三模式）
        new JapaneseResidueValidator(),
        new CanonicalKoreanSourceMissingValidator(),
    };

    /// <summary>当前流水线包含的校验器。</summary>
    public IReadOnlyList<ITranslationValidator> Validators => _validators;

    /// <summary>
    /// 执行校验（纯查询，不产生副作用）。
    /// </summary>
    /// <param name="context">校验上下文</param>
    /// <param name="policyOverride">强制策略（冒烟扫描 / 测试用；生产传 null 由来源决定）</param>
    public ValidationReport Validate(ValidationContext context, ValidationPolicy? policyOverride = null)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var policy = policyOverride ?? ValidationPolicyResolver.Resolve(context.Provenance);
        var effective = Enrich(context);

        var issues = new List<ValidationIssue>(effective.PreexistingIssues);
        var executed = new List<string>();

        foreach (var validator in SelectValidators(policy))
        {
            executed.Add(validator.Name);
            try
            {
                var produced = validator.Validate(effective);
                if (produced is not null && produced.Count > 0)
                {
                    issues.AddRange(produced);
                }
            }
            catch (Exception ex)
            {
                // 安全网：单个 Validator 异常不得中断流水线（正常不应发生）
                issues.Add(new ValidationIssue
                {
                    Key = context.Key,
                    Code = ValidationIssueCodes.ValidatorFailure,
                    Severity = ValidationSeverity.Warning,
                    Category = ValidationCategory.Structure,
                    Validator = validator.Name,
                    Message = $"校验器执行失败: {ex.Message}",
                });
            }
        }

        var ordered = issues
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Code, StringComparer.Ordinal)
            .ThenBy(i => i.Message, StringComparer.Ordinal)
            .ToList();

        return new ValidationReport
        {
            Key = context.Key,
            Policy = policy,
            Issues = ordered,
            Validators = executed,
        };
    }

    /// <summary>按策略选择要执行的校验器。</summary>
    private IEnumerable<ITranslationValidator> SelectValidators(ValidationPolicy policy) => policy switch
    {
        // 继承旧中文：本轮默认只跑硬结构安全检查，避免历史条目海量误报
        ValidationPolicy.InheritedStructureOnly when !_options.RunHeuristicsForInherited
            => _validators.Where(v => v.Kind == ValidationRuleKind.HardSafety),

        // 系统原样保留（纯符号）：只跑硬结构安全检查（不产生 SAME_AS_SOURCE 等启发式噪声）
        ValidationPolicy.PassthroughStructureOnly
            => _validators.Where(v => v.Kind == ValidationRuleKind.HardSafety),

        // 完整 QA / 人工审核 / 官方：硬安全 + 启发式（启发式只记录，是否改变状态由策略层决定）
        _ => _validators,
    };
    /// <summary>
    /// 对单个 DiffEntry 执行校验并应用 NeedsReview 决策（本类唯一产生副作用的地方）。
    /// </summary>
    /// <param name="entry">Diff 条目（需已填充 Translation）</param>
    /// <param name="preexistingIssues">进入流水线前已发现的问题（如 Provider 的 Placeholder 宽容恢复）</param>
    /// <param name="policyOverride">强制策略（测试 / 冒烟用）</param>
    public ValidationReport ValidateAndApply(
        DiffEntry entry,
        IReadOnlyList<ValidationIssue>? preexistingIssues = null,
        ValidationPolicy? policyOverride = null)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        var context = new ValidationContext
        {
            Key = entry.Key,
            SourceText = entry.NewSourceText ?? entry.OldSourceText ?? string.Empty,
            Translation = entry.Translation ?? string.Empty,
            OldSourceText = entry.OldSourceText,
            OldTranslation = entry.OldTranslation,
            Speaker = entry.Speaker,
            Provenance = entry.Provenance,
            PreexistingIssues = preexistingIssues ?? Array.Empty<ValidationIssue>(),
            // 第9.0B-P1轮：语言感知 / 术语单次共享所需的元数据（全部来自生产计划）
            RunTranslationMode = entry.RunTranslationMode,
            EffectiveSourceLanguage = entry.EffectiveSourceLanguage,
            CanonicalKoreanPresent = entry.RunTranslationMode is { } mode
                                     && TranslationModePolicy.UsesCanonicalKoreanDiff(mode)
                ? entry.CanonicalKoreanText is not null
                : null,
            MatchedTerms = entry.MatchedTerms,
        };

        var report = Validate(context, policyOverride);

        entry.ValidationIssues = report.Issues;
        entry.NeedsReview = ValidationNeedsReviewPolicy.Resolve(report, entry.NeedsReview);

        if (entry.NeedsReview)
        {
            if (report.Issues.Count > 0)
            {
                entry.ReviewReason = report.Summary;
            }
        }
        else
        {
            // 不因启发式 Warning 抹掉“已人工确认”的语义，也不留下误导性的审核原因
            entry.ReviewReason = null;
        }

        return report;
    }

    /// <summary>注入术语需求与允许英文词（Validator 本身不读取配置）。</summary>
    private ValidationContext Enrich(ValidationContext context)
    {
        var allowed = new HashSet<string>(context.AllowedEnglishTerms, StringComparer.OrdinalIgnoreCase);
        foreach (var extra in _options.ExtraAllowedEnglishTerms)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                allowed.Add(extra.Trim());
            }
        }

        // 第9.0B-P1轮：术语**只匹配一次** —— 优先级：
        //   1. 生产计划注入的 MatchedTerms（Prompt / Validator / Trace 共用的那一份）；
        //   2. 调用方显式提供的 Terminology；
        //   3. 兼容旧路径：本类按 SourceText 自行匹配。
        IReadOnlyList<TerminologyRequirement> requirements;
        if (context.MatchedTerms is { } matched)
        {
            requirements = matched;
        }
        else if (context.Terminology.Count > 0)
        {
            requirements = context.Terminology;
        }
        else if (_glossary is not null)
        {
            requirements = MatchFromGlossary(context.SourceText);
        }
        else
        {
            requirements = Array.Empty<TerminologyRequirement>();
        }

        foreach (var term in requirements)
        {
            // Glossary 明确保留英文（如 E.G.O）→ 计入允许英文词
            if (term.Locked && term.PreservedAsEnglish)
            {
                allowed.Add(term.Source);
            }
        }

        return new ValidationContext
        {
            Key = context.Key,
            SourceText = context.SourceText ?? string.Empty,
            Translation = context.Translation ?? string.Empty,
            OldSourceText = context.OldSourceText,
            OldTranslation = context.OldTranslation,
            Speaker = context.Speaker,
            Provenance = context.Provenance,
            PreexistingIssues = context.PreexistingIssues,
            Terminology = requirements,
            AllowedEnglishTerms = allowed,
            // 第9.0B-P1轮：语言元数据必须原样透传给 Validator（否则语言感知失效）
            RunTranslationMode = context.RunTranslationMode,
            EffectiveSourceLanguage = context.EffectiveSourceLanguage,
            CanonicalKoreanPresent = context.CanonicalKoreanPresent,
            MatchedTerms = context.MatchedTerms,
        };
    }

    /// <summary>兼容旧路径：按源文匹配术语表（唯一的匹配实现仍是 GlossaryService + TermMatcher）。</summary>
    private IReadOnlyList<TerminologyRequirement> MatchFromGlossary(string? sourceText)
    {
        if (_glossary is null)
        {
            return Array.Empty<TerminologyRequirement>();
        }

        var hits = _glossary.SelectTerms(new[] { sourceText ?? string.Empty });
        var requirements = new List<TerminologyRequirement>(hits.Count);
        foreach (var hit in hits)
        {
            requirements.Add(new TerminologyRequirement
            {
                Source = hit.Key,
                Target = hit.Value.Translation ?? string.Empty,
                Locked = hit.Value.Locked,
            });
        }

        return requirements;
    }
}
