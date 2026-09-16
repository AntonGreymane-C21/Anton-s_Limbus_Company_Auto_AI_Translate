using LimbusTranslator.Infrastructure.Audit;

namespace LimbusTranslator.Cli;

/// <summary>
/// 第9.0B-P7轮：真实多语言 JSON 结构差异**只读**审计命令（仅显式执行）。
///
/// 安全边界：
///   - 只读（Enumerate / Read / Parse），绝不写入 / 移动 / 删除 / 重命名游戏目录；
///   - 不调用任何翻译或网络接口；
///   - 产物只写到 <c>%TEMP%/limbus_structure_audit_&lt;时间戳&gt;/</c>（不写项目 data）。
/// </summary>
internal static class LocalizeStructureAuditCommand
{
    private const string AuditJsonFileName = "localize_structure_audit.json";
    private const string AuditSummaryFileName = "localize_structure_audit_summary.md";

    public static int Run(string localizeRoot, string? oldChineseRoot)
    {
        Console.WriteLine("[调试] ===== 第9.0B-P7 真实多语言结构差异只读审计 =====");
        if (!Directory.Exists(localizeRoot))
        {
            Console.WriteLine($"[错误] Localize 根目录不存在：{localizeRoot}");
            return 2;
        }

        var before = CaptureDirectoryState(localizeRoot);
        Console.WriteLine($"[调试] Localize 根：{localizeRoot}");
        Console.WriteLine($"[调试] 审计前：JSON 文件 {before.JsonFileCount} 个｜目录 LastWriteTime {before.LastWriteTime:o}");
        Console.WriteLine($"[调试] 旧中文树：{(string.IsNullOrWhiteSpace(oldChineseRoot) ? "（未提供）" : oldChineseRoot)}");
        Console.WriteLine("[调试] 只读模式：仅 Enumerate / Read / Parse，不写入游戏目录。");

        var report = LocalizeStructureAuditor.Run(new LocalizeStructureAuditOptions
        {
            LocalizeRoot = localizeRoot,
            OldChineseRoot = oldChineseRoot,
            ConfigDir = Path.Combine(FindProjectRoot(), "config"),
        });

        var after = CaptureDirectoryState(localizeRoot);
        Console.WriteLine($"[调试] 审计后：JSON 文件 {after.JsonFileCount} 个｜目录 LastWriteTime {after.LastWriteTime:o}");
        var unchanged = before.JsonFileCount == after.JsonFileCount
                        && before.LastWriteTime == after.LastWriteTime
                        && string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal);
        Console.WriteLine($"[调试] 游戏目录未发生变化：{unchanged}（文件数 + 目录时间戳 + 文件清单指纹）");

        var artifactRoot = Path.Combine(
            Path.GetTempPath(),
            $"limbus_structure_audit_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(artifactRoot);
        var jsonPath = Path.Combine(artifactRoot, AuditJsonFileName);
        var summaryPath = Path.Combine(artifactRoot, AuditSummaryFileName);
        SmokeReportWriter.Save(jsonPath, new
        {
            Round = "9.0B-P7",
            Stage = "read-only-localize-structure-audit",
            Timestamp = DateTime.UtcNow,
            LocalizeRoot = localizeRoot,
            OldChineseRoot = oldChineseRoot,
            ReadOnly = true,
            GameDirectoryUnchanged = unchanged,
            GameDirectoryBefore = new { before.JsonFileCount, LastWriteTime = before.LastWriteTime.ToString("o") },
            GameDirectoryAfter = new { after.JsonFileCount, LastWriteTime = after.LastWriteTime.ToString("o") },
            Report = report,
        });
        File.WriteAllText(summaryPath, BuildSummary(report, unchanged, artifactRoot));

        PrintSummary(report, unchanged);
        Console.WriteLine($"[调试] 审计产物：{jsonPath}");
        Console.WriteLine($"[调试] 审计摘要：{summaryPath}");
        return 0;
    }

    private static DirectoryState CaptureDirectoryState(string localizeRoot)
    {
        var files = Directory.EnumerateFiles(localizeRoot, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var fingerprint = string.Join("|", files.Select(Path.GetFileName));
        return new DirectoryState
        {
            JsonFileCount = files.Count,
            LastWriteTime = new DirectoryInfo(localizeRoot).LastWriteTimeUtc,
            Fingerprint = fingerprint,
        };
    }

    private static void PrintSummary(LocalizeStructureAuditReport report, bool unchanged)
    {
        Console.WriteLine($"[调试] 文件数：KR {report.KrFileCount}｜EN {report.EnFileCount}｜JP {report.JpFileCount}");
        Console.WriteLine($"[调试] 文件集合：KR-only {report.KrOnlyFiles.Count}｜EN-only {report.EnOnlyFiles.Count}｜JP-only {report.JpOnlyFiles.Count}"
                          + $"｜KR∩EN {report.CommonKrEnFiles.Count}｜KR∩JP {report.CommonKrJpFiles.Count}｜三语共同 {report.CommonAllFiles.Count}");
        Console.WriteLine("[调试] KR↔EN 文件分级：" + DescribeClasses(report.ClassCountsKrEn));
        Console.WriteLine("[调试] KR↔JP 文件分级：" + DescribeClasses(report.ClassCountsKrJp));
        Console.WriteLine("[调试] KR↔EN 受影响单元：" + DescribeClasses(report.AffectedUnitCountsKrEn));
        Console.WriteLine("[调试] KR↔JP 受影响单元：" + DescribeClasses(report.AffectedUnitCountsKrJp));
        Console.WriteLine($"[调试] 受影响 UnitKey 合计（两侧新增 + FieldPath 错位）：{report.AffectedUnitKeyTotal}");
        Console.WriteLine($"[调试] ParserUnsupported 文件 {report.ParserUnsupportedFiles.Count} 个｜根类型不一致 {report.RootKindMismatchFiles.Count} 个");
        Console.WriteLine($"[调试] KR-only 文件旧中文覆盖：有旧中文 {report.KrOnlyFilesWithOldChinese} 个"
                          + $"（全部 Key 匹配 {report.KrOnlyFilesAllKeysMatched}｜存在未匹配 {report.KrOnlyFilesWithKeyMismatch}）"
                          + $"｜匹配 Key {report.KrOnlyFileMatchedKeyTotal}｜未匹配 Key {report.KrOnlyFileMismatchedKeyTotal}");
        Console.WriteLine($"[调试] 游戏目录未变化：{unchanged}");
        foreach (var note in report.Notes)
        {
            Console.WriteLine($"[调试]   {note}");
        }

        foreach (var example in report.HighRiskExamples.Take(20))
        {
            Console.WriteLine($"[调试]   高风险示例 {example.Describe()}");
        }
    }

    private static string DescribeClasses(IReadOnlyDictionary<int, int> counts)
        => string.Join("｜", Enumerable.Range(0, 6).Select(index => $"Class{index}={counts[index]}"));

    private static string BuildSummary(LocalizeStructureAuditReport report, bool unchanged, string artifactRoot)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# 多语言 JSON 结构差异只读审计摘要（第9.0B-P7轮）");
        builder.AppendLine();
        builder.AppendLine($"- 时间（UTC）：{DateTime.UtcNow:o}");
        builder.AppendLine($"- Localize 根：`{report.LocalizeRoot}`");
        builder.AppendLine($"- 旧中文树：{(string.IsNullOrWhiteSpace(report.OldChineseRoot) ? "（未提供）" : report.OldChineseRoot)}");
        builder.AppendLine($"- 只读保证：仅 Enumerate / Read / Parse；游戏目录未变化 = **{unchanged}**");
        builder.AppendLine($"- 审计产物目录：`{artifactRoot}`");
        builder.AppendLine();
        builder.AppendLine("## 文件统计");
        builder.AppendLine();
        builder.AppendLine("| 语言 | JSON 文件数 |");
        builder.AppendLine("|---|---|");
        builder.AppendLine($"| KR | {report.KrFileCount} |");
        builder.AppendLine($"| EN | {report.EnFileCount} |");
        builder.AppendLine($"| JP | {report.JpFileCount} |");
        builder.AppendLine();
        builder.AppendLine($"- KR-only：{report.KrOnlyFiles.Count}｜EN-only：{report.EnOnlyFiles.Count}｜JP-only：{report.JpOnlyFiles.Count}");
        builder.AppendLine($"- KR∩EN：{report.CommonKrEnFiles.Count}｜KR∩JP：{report.CommonKrJpFiles.Count}｜三语共同：{report.CommonAllFiles.Count}");
        builder.AppendLine();
        builder.AppendLine("## 分级统计（文件数 / 受影响单元数）");
        builder.AppendLine();
        builder.AppendLine("| 配对 | " + string.Join(" | ", Enumerable.Range(0, 6).Select(i => $"Class{i}")) + " |");
        builder.AppendLine("|---|" + string.Concat(Enumerable.Repeat("---|", 6)));
        builder.AppendLine("| KR↔EN 文件 | " + string.Join(" | ", Enumerable.Range(0, 6).Select(i => report.ClassCountsKrEn[i])) + " |");
        builder.AppendLine("| KR↔JP 文件 | " + string.Join(" | ", Enumerable.Range(0, 6).Select(i => report.ClassCountsKrJp[i])) + " |");
        builder.AppendLine("| KR↔EN 受影响单元 | " + string.Join(" | ", Enumerable.Range(0, 6).Select(i => report.AffectedUnitCountsKrEn[i])) + " |");
        builder.AppendLine("| KR↔JP 受影响单元 | " + string.Join(" | ", Enumerable.Range(0, 6).Select(i => report.AffectedUnitCountsKrJp[i])) + " |");
        builder.AppendLine();
        builder.AppendLine($"受影响 UnitKey 合计：**{report.AffectedUnitKeyTotal}**");
        builder.AppendLine();
        builder.AppendLine("## 高风险示例（每个分级最多 20 条）");
        builder.AppendLine();
        if (report.HighRiskExamples.Count == 0)
        {
            builder.AppendLine("- （无 Class3 / Class4 / Class5）");
        }

        foreach (var example in report.HighRiskExamples)
        {
            builder.AppendLine($"- {example.Describe()}");
        }

        builder.AppendLine();
        builder.AppendLine("## KR-only 文件 × 旧中文覆盖");
        builder.AppendLine();
        builder.AppendLine($"- 有同名旧中文：{report.KrOnlyFilesWithOldChinese}"
                           + $"（全部 Key 匹配 {report.KrOnlyFilesAllKeysMatched} / 存在未匹配 {report.KrOnlyFilesWithKeyMismatch}）");
        builder.AppendLine($"- 匹配 Key：{report.KrOnlyFileMatchedKeyTotal}｜未匹配 Key：{report.KrOnlyFileMismatchedKeyTotal}");
        foreach (var example in report.KrOnlyFileOldChineseExamples)
        {
            builder.AppendLine($"- {example}");
        }

        return builder.ToString();
    }

    private static string FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private sealed class DirectoryState
    {
        public int JsonFileCount { get; init; }
        public DateTime LastWriteTime { get; init; }
        public required string Fingerprint { get; init; }
    }
}
