using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Cli;

/// <summary>生产数据快照（第9.0B 最终 Smoke：运行前/后对比，零污染证明）。</summary>
internal sealed record ProductionSafetySnapshot(
    string? TranslationMemorySha256,
    long TranslationMemoryLength,
    int SnapshotFileCount,
    string? SnapshotMaxLastWriteUtc,
    int OutputFileCount,
    string? OutputMaxLastWriteUtc,
    int GameLocalizeFileCount,
    string? GameLocalizeLastWriteUtc);

/// <summary>生产安全探针（**只读**：哈希 / 计数 / 最后写入时间）。</summary>
internal static class ProductionSafetyProbe
{
    public static ProductionSafetySnapshot Capture(string projectRoot, string localizeRoot)
    {
        var tmPath = Path.Combine(projectRoot, "data", "cache", "translation_memory.db");
        var snapshotDirectory = Path.Combine(projectRoot, "data", "cache", "source_snapshots");
        var outputDirectory = Path.Combine(projectRoot, "data", "output");
        var localizeDirectory = localizeRoot;

        var (snapshotCount, snapshotMax) = SummarizeDirectory(snapshotDirectory);
        var (outputCount, outputMax) = SummarizeDirectory(outputDirectory);
        var (localizeCount, localizeMax) = SummarizeDirectory(localizeDirectory);

        string? hash = null;
        long length = 0;
        if (File.Exists(tmPath))
        {
            length = new FileInfo(tmPath).Length;
            using var stream = File.OpenRead(tmPath);
            hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        return new ProductionSafetySnapshot(
            hash,
            length,
            snapshotCount,
            snapshotMax,
            outputCount,
            outputMax,
            localizeCount,
            localizeMax);
    }

    public static IReadOnlyList<string> Compare(ProductionSafetySnapshot before, ProductionSafetySnapshot after)
    {
        var differences = new List<string>();
        if (before.TranslationMemorySha256 != after.TranslationMemorySha256)
        {
            differences.Add($"生产 translation_memory.db 哈希变化（{before.TranslationMemorySha256} → {after.TranslationMemorySha256}）");
        }

        if (before.TranslationMemoryLength != after.TranslationMemoryLength)
        {
            differences.Add($"生产 translation_memory.db 大小变化（{before.TranslationMemoryLength} → {after.TranslationMemoryLength}）");
        }

        if (before.SnapshotFileCount != after.SnapshotFileCount || before.SnapshotMaxLastWriteUtc != after.SnapshotMaxLastWriteUtc)
        {
            differences.Add($"生产 source_snapshots 变化（{before.SnapshotFileCount}/{before.SnapshotMaxLastWriteUtc} → {after.SnapshotFileCount}/{after.SnapshotMaxLastWriteUtc}）");
        }

        if (before.OutputFileCount != after.OutputFileCount || before.OutputMaxLastWriteUtc != after.OutputMaxLastWriteUtc)
        {
            differences.Add($"正式 data/output 变化（{before.OutputFileCount}/{before.OutputMaxLastWriteUtc} → {after.OutputFileCount}/{after.OutputMaxLastWriteUtc}）");
        }

        if (before.GameLocalizeFileCount != after.GameLocalizeFileCount || before.GameLocalizeLastWriteUtc != after.GameLocalizeLastWriteUtc)
        {
            differences.Add($"游戏 Localize 目录变化（{before.GameLocalizeFileCount}/{before.GameLocalizeLastWriteUtc} → {after.GameLocalizeFileCount}/{after.GameLocalizeLastWriteUtc}）");
        }

        return differences;
    }

    private static (int FileCount, string? MaxLastWriteUtc) SummarizeDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return (0, null);
        }

        var count = 0;
        DateTime? max = null;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            count++;
            var lastWrite = File.GetLastWriteTimeUtc(file);
            if (max is null || lastWrite > max)
            {
                max = lastWrite;
            }
        }

        return (count, max?.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }
}

/// <summary>四模式对比产物（<c>four_mode_comparison.md</c>）。</summary>
internal static class FourModeComparisonReport
{
    public static string Build(
        string runId,
        SmokeSession session,
        IReadOnlyList<FourModeSample> samples,
        IReadOnlyList<SmokeStageResult> stage,
        ProductionSafetySnapshot before,
        ProductionSafetySnapshot after,
        IReadOnlyList<string> safetyDifferences)
    {
        var builder = new System.Text.StringBuilder();

        builder.AppendLine("# 第9.0B 最终真实四模式 Smoke · 四模式对比");
        builder.AppendLine();
        builder.AppendLine("## 运行信息");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 值 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| RunId | {runId} |");
        builder.AppendLine($"| Provider / Model | deepseek / {session.Options.Model} |");
        builder.AppendLine($"| Base URL Host | {new Uri(session.Options.ApiUrl).Host} |");
        builder.AppendLine($"| Thinking 配置 | mode={session.Options.ThinkingMode?.ToString() ?? "(null)"}；thinking={session.Options.Thinking} |");
        builder.AppendLine($"| 真实 API 请求数 | {session.Budget.Requests}（上限 {SmokeBudget.MaxNetworkRequests}） |");
        builder.AppendLine($"| 真实翻译单元数 | {session.Budget.Units}（上限 {SmokeBudget.MaxTranslationUnits}） |");
        builder.AppendLine($"| 客户端重试次数 | {session.Budget.ObservedRetryCount} |");
        builder.AppendLine($"| 总 Token | {stage.Sum(result => result.Units.Sum(unit => unit.TotalTokens ?? 0))} |");
        builder.AppendLine($"| TEMP Root | {session.TempRoot} |");
        builder.AppendLine();

        foreach (var sample in samples)
        {
            AppendSample(builder, sample, stage);
        }

        builder.AppendLine("## 横向观察（仅限本次样本）");
        builder.AppendLine();
        foreach (var sample in samples)
        {
            builder.AppendLine($"### {sample.Label}");
            builder.AppendLine();
            foreach (var result in stage)
            {
                var unit = result.Units.FirstOrDefault(item => item.UnitKey.Contains($"|{sample.RecordId}|", StringComparison.Ordinal));
                if (unit is null)
                {
                    continue;
                }

                builder.AppendLine(
                    $"- {result.TranslationMode}：`{Escape(unit.Translation)}`"
                    + $"（In {unit.InputTokens ?? 0} / Out {unit.OutputTokens ?? 0} / Reasoning {unit.ReasoningTokens ?? 0} / Total {unit.TotalTokens ?? 0}）");
            }

            builder.AppendLine();
        }

        AppendSafety(builder, before, after, safetyDifferences);
        AppendSummary(builder, session, stage);
        return builder.ToString();
    }

    private static void AppendSample(
        System.Text.StringBuilder builder,
        FourModeSample sample,
        IReadOnlyList<SmokeStageResult> stage)
    {
        builder.AppendLine($"## 样本：{sample.Label}");
        builder.AppendLine();
        builder.AppendLine($"- UnitKey 前缀：`{sample.LogicalFile}|{sample.RecordId}|{sample.Field}`");
        builder.AppendLine($"- KR：{sample.Korean}");
        builder.AppendLine($"- EN：{sample.English}");
        builder.AppendLine($"- JP：{sample.Japanese}");
        foreach (var neighbor in sample.Neighbors)
        {
            builder.AppendLine(
                $"- 邻句 {neighbor.RecordId}（model={neighbor.Model ?? "-"}）：KR={neighbor.Korean}｜EN={neighbor.English}｜JP={neighbor.Japanese}");
        }

        builder.AppendLine();
        builder.AppendLine("| 模式 | Action | Selected Source | Translation | Thinking | MatchedTerms | ValidationIssues | Review | Tokens(In/Out/Reasoning/Total) | TM | Cache | 邻接上下文 |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var result in stage)
        {
            var unit = result.Units.FirstOrDefault(item => item.UnitKey.Contains($"|{sample.RecordId}|", StringComparison.Ordinal));
            if (unit is null)
            {
                builder.AppendLine($"| {result.TranslationMode} | （缺失） | | | | | | | | | |");
                continue;
            }

            builder.AppendLine(
                $"| {result.TranslationMode} | {unit.Action} | {Escape(unit.SelectedSourceText)} | **{Escape(unit.Translation)}** "
                + $"| {(unit.ThinkingEnabled == true ? "ON" : "OFF")}({unit.ThinkingPolicyReason ?? "-"}) "
                + $"| {(unit.MatchedTerms.Count == 0 ? "-" : string.Join(",", unit.MatchedTerms))} "
                + $"| {(unit.ValidationIssueCodes.Count == 0 ? "-" : string.Join(",", unit.ValidationIssueCodes))} "
                + $"| {(unit.NeedsReview ? (unit.ReviewReason is null ? "需审核(AI自报)" : "需审核:" + Escape(unit.ReviewReason)) : "-")} "
                + $"| {unit.InputTokens ?? 0}/{unit.OutputTokens ?? 0}/{unit.ReasoningTokens ?? 0}/{unit.TotalTokens ?? 0} "
                + $"| {unit.TmMatchType} | {(unit.CacheHit ? "Hit" : "Miss")} | {DescribeNeighbors(unit)} |");
        }

        builder.AppendLine();
        builder.AppendLine("真实请求语义（SafeRequestSemanticSnapshot）：");
        builder.AppendLine();
        builder.AppendLine("| 模式 | Trace Mode | 生效语言 | Canonical 文本 | Canonical 入请求 | 旧 Canonical | Salt | 指纹 |");
        builder.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var result in stage)
        {
            var unit = result.Units.FirstOrDefault(item => item.UnitKey.Contains($"|{sample.RecordId}|", StringComparison.Ordinal));
            if (unit is null)
            {
                continue;
            }

            builder.AppendLine(
                $"| {result.TranslationMode} | {unit.TraceTranslationMode ?? "-"} | {unit.EffectiveSourceLanguage ?? "-"} "
                + $"| {Escape(unit.CanonicalKoreanText)} | {(unit.CanonicalKoreanSent ? "有" : "无")} "
                + $"| {Escape(unit.OldCanonicalKoreanText)} | {(unit.SourceHashSalt is null ? "null" : "非空")} "
                + $"| {Shorten(unit.Fingerprint)} |");
        }

        builder.AppendLine();
    }

    private static void AppendSafety(
        System.Text.StringBuilder builder,
        ProductionSafetySnapshot before,
        ProductionSafetySnapshot after,
        IReadOnlyList<string> safetyDifferences)
    {
        builder.AppendLine("## 安全检查");
        builder.AppendLine();
        builder.AppendLine("| 检查项 | 运行前 | 运行后 | 结论 |");
        builder.AppendLine("|---|---|---|---|");
        builder.AppendLine($"| 生产 TM（SHA256） | {Shorten(before.TranslationMemorySha256)} | {Shorten(after.TranslationMemorySha256)} | {(before.TranslationMemorySha256 == after.TranslationMemorySha256 ? "未变化" : "已变化")} |");
        builder.AppendLine($"| 生产 request_cache（同库） | {before.TranslationMemoryLength} B | {after.TranslationMemoryLength} B | {(before.TranslationMemoryLength == after.TranslationMemoryLength ? "未变化" : "已变化")} |");
        builder.AppendLine($"| 生产 source_snapshots | {before.SnapshotFileCount} 文件 | {after.SnapshotFileCount} 文件 | {(before.SnapshotFileCount == after.SnapshotFileCount && before.SnapshotMaxLastWriteUtc == after.SnapshotMaxLastWriteUtc ? "未变化" : "已变化")} |");
        builder.AppendLine($"| 正式 data/output | {before.OutputFileCount} 文件 | {after.OutputFileCount} 文件 | {(before.OutputFileCount == after.OutputFileCount && before.OutputMaxLastWriteUtc == after.OutputMaxLastWriteUtc ? "未变化" : "已变化")} |");
        builder.AppendLine($"| 游戏 Localize | {before.GameLocalizeFileCount} 文件 | {after.GameLocalizeFileCount} 文件 | {(before.GameLocalizeFileCount == after.GameLocalizeFileCount && before.GameLocalizeLastWriteUtc == after.GameLocalizeLastWriteUtc ? "未变化（零写入）" : "已变化")} |");
        builder.AppendLine("| Deploy | — | — | 未发生 |");
        builder.AppendLine();

        if (safetyDifferences.Count > 0)
        {
            builder.AppendLine("生产安全差异：");
            foreach (var difference in safetyDifferences)
            {
                builder.AppendLine($"- {difference}");
            }

            builder.AppendLine();
        }
    }

    private static void AppendSummary(
        System.Text.StringBuilder builder,
        SmokeSession session,
        IReadOnlyList<SmokeStageResult> stage)
    {
        builder.AppendLine("## 汇总");
        builder.AppendLine();
        builder.AppendLine($"- 违规项：{session.Violations.Count}");
        foreach (var result in stage)
        {
            builder.AppendLine(
                $"- {result.TranslationMode}：待翻译 {result.AgentEntryCount} 条｜真实请求 {result.RequestDelta} 次"
                + $"｜Trace {result.TraceLineCount} 行（网络 {result.TraceNetworkCalledCount} / 缓存命中 {result.TraceCacheHitCount}）"
                + $"｜TM 命中 {result.TmHitCount}｜Merge 写入 {result.MergeWrittenEntryCount} 条"
                + $"｜Gate {result.GateStatus}（Missing {result.GateMissingExpectedKeyCount} / Unexpected {result.GateUnexpectedOutputKeyCount}）");
        }

        builder.AppendLine();
    }

    private static string DescribeNeighbors(SmokeUnitSnapshot unit)
        => unit.NeighborPrevious is null && unit.NeighborNext is null
            ? "-"
            : $"prev={Escape(unit.NeighborPrevious)}; next={Escape(unit.NeighborNext)}";

    private static string Escape(string? text)
        => string.IsNullOrEmpty(text)
            ? "-"
            : text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string Shorten(string? value)
        => string.IsNullOrEmpty(value) ? "-" : value.Length <= 16 ? value : value[..16] + "…";
}


