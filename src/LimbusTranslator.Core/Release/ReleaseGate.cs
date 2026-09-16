using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Release;

/// <summary>
/// 发布门禁评估器（纯函数：只读 DiffEntry 的 ValidationIssues / Provenance / NeedsReview）。
///
/// 不修改任何条目、不访问磁盘、不写数据库；结论完全由 <see cref="ReleaseGatePolicy"/> 决定。
/// </summary>
public static class ReleaseGate
{
    /// <summary>每个原因最多保留的可定位样本数</summary>
    private const int MaxSamplesPerReason = 5;

    /// <summary>
    /// 评估门禁。
    /// </summary>
    /// <param name="entries">本次输出涉及的条目（内部会跳过 SkipDeleted）</param>
    /// <param name="policy">门禁策略；null 使用默认策略</param>
    /// <param name="keySet">
    /// 输出结构 Key 集（第9.0B-P4轮）；传入后额外校验 Missing Expected Key / Unexpected Output Key。
    /// 必须与 Merge 使用**同一个**权威 Key 集（EN_ONLY → 英文；KR 模式 → 韩文）。null = 不做 Key 集校验。
    /// </param>
    public static ReleaseGateResult Evaluate(
        IEnumerable<DiffEntry> entries,
        ReleaseGatePolicy? policy = null,
        ReleaseGateKeySet? keySet = null)
    {
        if (entries is null)
        {
            throw new ArgumentNullException(nameof(entries));
        }

        var effective = policy ?? ReleaseGatePolicy.Default;
        var buckets = new Dictionary<string, Bucket>(StringComparer.Ordinal);

        var status = ReleaseGateStatus.Passed;
        var evaluated = 0;
        var errorCount = 0;
        var warningCount = 0;
        var needsReviewCount = 0;
        var blockingErrorCount = 0;
        var historicalInheritedErrorCount = 0;

        foreach (var entry in entries)
        {
            if (entry is null || entry.Action == TranslationAction.SkipDeleted)
            {
                continue;
            }

            evaluated++;
            var provenance = entry.Provenance;
            var errors = entry.ValidationIssues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
            var warnings = entry.ValidationIssues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();

            errorCount += errors.Count;
            warningCount += warnings.Count;

            if (errors.Count > 0)
            {
                var (kind, escalation, bucketKey) = Classify(errors, provenance, effective);
                status = ReleaseGatePolicy.Escalate(status, escalation);
                AddReason(buckets, bucketKey, kind, errors[0].Severity, provenance, escalation, errors, entry);

                if (provenance == TranslationSource.Inherited)
                {
                    historicalInheritedErrorCount++;
                }
                else if (escalation == ReleaseGateStatus.Blocked)
                {
                    blockingErrorCount++;
                }
            }

            if (warnings.Count > 0)
            {
                var (kind, escalation, bucketKey, countIt) = ClassifyWarning(provenance, effective);
                if (countIt)
                {
                    status = ReleaseGatePolicy.Escalate(status, escalation);
                    AddReason(buckets, bucketKey, kind, ValidationSeverity.Warning, provenance, escalation, warnings, entry);
                }
            }

            if (entry.NeedsReview)
            {
                needsReviewCount++;
                status = ReleaseGatePolicy.Escalate(status, effective.NeedsReview);
                AddReason(
                    buckets,
                    "NEEDS_REVIEW",
                    ReleaseGateReasonKinds.NeedsReview,
                    ValidationSeverity.Warning,
                    provenance,
                    effective.NeedsReview,
                    Array.Empty<ValidationIssue>(),
                    entry);
            }
        }

        // 第9.0B-P4轮：输出结构 Key 集校验（与 Merge 使用同一权威 Key 集）
        var missingExpectedKeyCount = 0;
        var unexpectedOutputKeyCount = 0;
        if (keySet is not null)
        {
            var expected = new HashSet<string>(keySet.ExpectedKeys, StringComparer.Ordinal);
            var actual = new HashSet<string>(keySet.OutputKeys, StringComparer.Ordinal);

            var missing = expected.Except(actual, StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();
            var unexpected = actual.Except(expected, StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            missingExpectedKeyCount = missing.Count;
            unexpectedOutputKeyCount = unexpected.Count;

            if (missing.Count > 0)
            {
                status = ReleaseGatePolicy.Escalate(status, effective.MissingExpectedKey);
                AddKeySetBucket(
                    buckets,
                    "MISSING_EXPECTED_KEY",
                    ReleaseGateReasonKinds.MissingExpectedKey,
                    effective.MissingExpectedKey,
                    missing);
            }

            if (unexpected.Count > 0)
            {
                status = ReleaseGatePolicy.Escalate(status, effective.UnexpectedOutputKey);
                AddKeySetBucket(
                    buckets,
                    "UNEXPECTED_OUTPUT_KEY",
                    ReleaseGateReasonKinds.UnexpectedOutputKey,
                    effective.UnexpectedOutputKey,
                    unexpected);
            }
        }

        var reasons = buckets.Values
            .OrderByDescending(b => (int)b.Escalation)
            .ThenBy(b => b.Kind, StringComparer.Ordinal)
            .ThenBy(b => b.Code, StringComparer.Ordinal)
            .Select(b => b.ToReason())
            .ToArray();

        return new ReleaseGateResult
        {
            Status = status,
            EvaluatedEntryCount = evaluated,
            ErrorCount = errorCount,
            WarningCount = warningCount,
            NeedsReviewCount = needsReviewCount,
            BlockingErrorCount = blockingErrorCount,
            HistoricalInheritedErrorCount = historicalInheritedErrorCount,
            MissingExpectedKeyCount = missingExpectedKeyCount,
            UnexpectedOutputKeyCount = unexpectedOutputKeyCount,
            Reasons = reasons,
        };
    }

    /// <summary>硬安全 Error 按来源分类。</summary>
    private static (string Kind, ReleaseGateStatus Escalation, string BucketKey) Classify(
        IReadOnlyList<ValidationIssue> errors,
        TranslationSource? provenance,
        ReleaseGatePolicy policy)
    {
        switch (provenance)
        {
            case TranslationSource.HumanReviewed:
            case TranslationSource.Official:
            case TranslationSource.Imported:
                return (ReleaseGateReasonKinds.ReviewedHardError, policy.ReviewedHardError, "REVIEWED_HARD_ERROR");

            case TranslationSource.Inherited:
                return (ReleaseGateReasonKinds.HistoricalInheritedError,
                    policy.InheritedHardSafetyError,
                    "HISTORICAL_INHERITED_ERROR");

            default:
                return (ReleaseGateReasonKinds.NewTranslationHardError,
                    policy.NewTranslationHardError,
                    "NEW_TRANSLATION_HARD_ERROR");
        }
    }

    /// <summary>启发式 Warning 按来源分类。</summary>
    private static (string Kind, ReleaseGateStatus Escalation, string BucketKey, bool Count) ClassifyWarning(
        TranslationSource? provenance,
        ReleaseGatePolicy policy)
    {
        switch (provenance)
        {
            case TranslationSource.HumanReviewed:
            case TranslationSource.Official:
            case TranslationSource.Imported:
                // 人工确认 / 官方译文的启发式 Warning 只统计，不改变门禁结论
                return (ReleaseGateReasonKinds.ReviewedWarning,
                    ReleaseGateStatus.Passed,
                    "REVIEWED_WARNING",
                    policy.CountReviewedWarnings);

            case TranslationSource.Inherited:
                return (ReleaseGateReasonKinds.HistoricalInheritedWarning,
                    ReleaseGateStatus.Passed,
                    "HISTORICAL_INHERITED_WARNING",
                    policy.CountInheritedWarnings);

            default:
                return (ReleaseGateReasonKinds.NewTranslationWarning,
                    policy.NewTranslationWarning,
                    "NEW_TRANSLATION_WARNING",
                    true);
        }
    }

    /// <summary>
    /// 记录「输出结构 Key 集」原因（第9.0B-P4轮）：每个原因最多保留若干可定位样本。
    /// </summary>
    private static void AddKeySetBucket(
        Dictionary<string, Bucket> buckets,
        string bucketKey,
        string kind,
        ReleaseGateStatus escalation,
        IReadOnlyList<string> unitKeys)
    {
        if (!buckets.TryGetValue(bucketKey, out var bucket))
        {
            bucket = new Bucket(kind, kind, ValidationSeverity.Error, null, escalation);
            buckets[bucketKey] = bucket;
        }

        bucket.Count += unitKeys.Count;

        foreach (var unitKey in unitKeys)
        {
            if (bucket.Samples.Count >= MaxSamplesPerReason)
            {
                break;
            }

            bucket.Samples.Add(new ReleaseGateTarget
            {
                UnitKey = unitKey,
                RelativeFilePath = unitKey.Split('|', 2)[0],
                Code = kind,
                Severity = ValidationSeverity.Error,
                Message = escalation == ReleaseGateStatus.Blocked ? "输出结构不完整或被污染（结构阻断）" : "输出结构需要人工确认",
            });
        }
    }

    private static void AddReason(
        Dictionary<string, Bucket> buckets,
        string bucketKey,
        string kind,
        ValidationSeverity severity,
        TranslationSource? provenance,
        ReleaseGateStatus escalation,
        IReadOnlyList<ValidationIssue> issues,
        DiffEntry entry)
    {
        var code = issues.Count > 0 ? issues[0].Code : "*";
        var key = $"{bucketKey}|{code}";
        if (!buckets.TryGetValue(key, out var bucket))
        {
            bucket = new Bucket(kind, code, severity, provenance, escalation);
            buckets[key] = bucket;
        }

        bucket.Count++;
        if (bucket.Samples.Count >= MaxSamplesPerReason)
        {
            return;
        }

        if (issues.Count > 0)
        {
            var issue = issues[0];
            bucket.Samples.Add(new ReleaseGateTarget
            {
                UnitKey = entry.Key.ToString(),
                RelativeFilePath = entry.Key.RelativeFilePath,
                Code = issue.Code,
                Severity = issue.Severity,
                Message = Shorten(issue.Message),
            });
        }
        else
        {
            bucket.Samples.Add(new ReleaseGateTarget
            {
                UnitKey = entry.Key.ToString(),
                RelativeFilePath = entry.Key.RelativeFilePath,
                Code = ValidationIssueCodes.EmptyTranslation,
                Severity = ValidationSeverity.Warning,
                Message = "等待人工审核",
            });
        }
    }

    private static string Shorten(string message)
        => message.Length <= 60 ? message : message[..60] + "...";

    /// <summary>同一 来源类型 + Code 的汇总桶。</summary>
    private sealed class Bucket
    {
        public Bucket(
            string kind,
            string code,
            ValidationSeverity severity,
            TranslationSource? provenance,
            ReleaseGateStatus escalation)
        {
            Kind = kind;
            Code = code;
            Severity = severity;
            Provenance = provenance;
            Escalation = escalation;
        }

        public string Kind { get; }
        public string Code { get; }
        public ValidationSeverity Severity { get; }
        public TranslationSource? Provenance { get; }
        public ReleaseGateStatus Escalation { get; }
        public int Count { get; set; }
        public List<ReleaseGateTarget> Samples { get; } = new();

        public ReleaseGateReason ToReason() => new()
        {
            Kind = Kind,
            Code = Code,
            Severity = Severity,
            Provenance = Provenance,
            Count = Count,
            Escalation = Escalation,
            Message = BuildMessage(),
            Samples = Samples.ToArray(),
        };

        /// <summary>生成面向用户的中文摘要。</summary>
        private string BuildMessage()
        {
            var scope = Provenance switch
            {
                TranslationSource.Inherited => "历史继承译文",
                TranslationSource.HumanReviewed => "人工确认译文",
                TranslationSource.Official => "官方译文",
                TranslationSource.Imported => "导入译文",
                TranslationSource.Mock => "模拟翻译",
                TranslationSource.TranslationMemory => "Translation Memory 命中译文",
                TranslationSource.Passthrough => "原样保留文本（纯符号）",
                _ => "AI 译文",
            };

            var level = Severity == ValidationSeverity.Error ? "结构安全错误" : "警告";

            return Kind switch
            {
                ReleaseGateReasonKinds.HistoricalInheritedError =>
                    $"发现 {Count} 条历史继承译文存在结构安全差异（{Code}）。"
                    + "这些问题在本次 Validator 上线前已经存在，建议审核后再部署。",
                ReleaseGateReasonKinds.ReviewedHardError =>
                    $"{scope}存在 {Count} 条{level}（{Code}）：人工审核不能跳过结构安全校验。",
                ReleaseGateReasonKinds.NeedsReview => $"存在 {Count} 条未审核结果（待人工确认）。",
                ReleaseGateReasonKinds.MissingExpectedKey =>
                    $"权威输出结构要求的 {Count} 个条目未出现在最终 output 中（{Code}），禁止直接部署。",
                ReleaseGateReasonKinds.UnexpectedOutputKey =>
                    $"最终 output 出现 {Count} 个权威输出结构中不存在的条目（{Code}；例如英文残留的旧 Key），禁止直接部署。",
                ReleaseGateReasonKinds.NewTranslationHardError =>
                    $"{scope}存在 {Count} 条{level}（{Code}），禁止直接部署。",
                _ => $"{scope}存在 {Count} 条{level}（{Code}）。",
            };
        }
    }

}
