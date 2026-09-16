using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Infrastructure.Audit;

/// <summary>多语言 JSON 结构差异分级（第9.0B-P7轮）。</summary>
public enum LocalizeStructureClass
{
    /// <summary>完全同构（UnitKey 一致 + 文本一致）</summary>
    Class0Identical = 0,

    /// <summary>仅文本内容不同，结构完全一致（正常的多语言差异）</summary>
    Class1TextOnly = 1,

    /// <summary>UnitKey 数量不同（一方多 / 少），权威模板可以独立输出</summary>
    Class2UnitCountDiffers = 2,

    /// <summary>同一语义单元（RecordId + 字段）存在，但数组 Index / FieldPath 不同（重点风险）</summary>
    Class3FieldPathMismatch = 3,

    /// <summary>JSON 根类型 / 结构明显不同（高风险）</summary>
    Class4SchemaMismatch = 4,

    /// <summary>ParserUnsupported / 异常 JSON / 一侧无可翻译单元（无法归类）</summary>
    Class5ParserUnsupported = 5,
}

/// <summary>单个文件在两种语言之间的结构差异（只读审计结果）。</summary>
public sealed class LocalizeStructurePairDiff
{
    public required string LogicalFile { get; init; }
    public required SourceLanguage LeftLanguage { get; init; }
    public required SourceLanguage RightLanguage { get; init; }
    public required LocalizeStructureClass Class { get; init; }
    public int LeftUnits { get; init; }
    public int RightUnits { get; init; }
    public int LeftRecords { get; init; }
    public int RightRecords { get; init; }
    public int LeftOnlyUnits { get; init; }
    public int RightOnlyUnits { get; init; }
    public int FieldPathMismatchCount { get; init; }
    public IReadOnlyList<string> FieldPathMismatchExamples { get; init; } = Array.Empty<string>();
    public string? Note { get; init; }

    public string Describe() =>
        $"[{Class}] {LogicalFile}（{SourceLanguageHelper.ToCode(LeftLanguage)} vs {SourceLanguageHelper.ToCode(RightLanguage)}）"
        + $"：单元 {LeftUnits}/{RightUnits}｜记录 {LeftRecords}/{RightRecords}"
        + $"｜仅左 {LeftOnlyUnits}｜仅右 {RightOnlyUnits}"
        + (FieldPathMismatchCount > 0 ? $"｜FieldPath 错位 {FieldPathMismatchCount}" : string.Empty)
        + (Note is null ? string.Empty : $"｜{Note}");
}

/// <summary>只读结构审计选项。</summary>
public sealed class LocalizeStructureAuditOptions
{
    /// <summary>Localize 根目录（其下含 kr / en / jp 子目录）。只读。</summary>
    public required string LocalizeRoot { get; init; }

    /// <summary>可选：旧中文树根目录（用于统计 KR-only 文件是否真的能继承旧中文）。只读。</summary>
    public string? OldChineseRoot { get; init; }

    /// <summary>字段规则目录（与生产解析保持一致；null → 内嵌默认规则）。</summary>
    public string? ConfigDir { get; init; }

    /// <summary>每个分级最多保留的示例数。</summary>
    public int MaxExamplesPerClass { get; init; } = LocalizeStructureAuditor.MaxExamplesPerClass;
}

/// <summary>只读结构审计报告（不写任何游戏文件）。</summary>
public sealed class LocalizeStructureAuditReport
{
    public required DateTime TimestampUtc { get; init; }
    public required string LocalizeRoot { get; init; }
    public required int KrFileCount { get; init; }
    public required int EnFileCount { get; init; }
    public required int JpFileCount { get; init; }
    public required IReadOnlyList<string> KrOnlyFiles { get; init; }
    public required IReadOnlyList<string> EnOnlyFiles { get; init; }
    public required IReadOnlyList<string> JpOnlyFiles { get; init; }
    public required IReadOnlyList<string> CommonKrEnFiles { get; init; }
    public required IReadOnlyList<string> CommonKrJpFiles { get; init; }
    public required IReadOnlyList<string> CommonAllFiles { get; init; }
    public required IReadOnlyDictionary<int, int> ClassCountsKrEn { get; init; }
    public required IReadOnlyDictionary<int, int> ClassCountsKrJp { get; init; }
    public required IReadOnlyDictionary<int, int> AffectedUnitCountsKrEn { get; init; }
    public required IReadOnlyDictionary<int, int> AffectedUnitCountsKrJp { get; init; }
    public required IReadOnlyList<LocalizeStructurePairDiff> HighRiskExamples { get; init; }
    public required IReadOnlyList<string> ParserUnsupportedFiles { get; init; }
    public required IReadOnlyList<LocalizeStructurePairDiff> FieldPathMismatchExamples { get; init; }
    public required IReadOnlyList<string> RootKindMismatchFiles { get; init; }
    public required int AffectedUnitKeyTotal { get; init; }
    public string? OldChineseRoot { get; init; }
    public int KrOnlyFilesWithOldChinese { get; init; }
    public int KrOnlyFilesAllKeysMatched { get; init; }
    public int KrOnlyFilesWithKeyMismatch { get; init; }
    public int KrOnlyFileMatchedKeyTotal { get; init; }
    public int KrOnlyFileMismatchedKeyTotal { get; init; }
    public IReadOnlyList<string> KrOnlyFileOldChineseExamples { get; init; } = Array.Empty<string>();
    public required IReadOnlyList<string> Notes { get; init; }
}
