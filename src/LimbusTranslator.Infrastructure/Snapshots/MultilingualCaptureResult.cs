using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Multilingual;

namespace LimbusTranslator.Infrastructure.Snapshots;

/// <summary>三语快照捕获结果（第9.0A.1轮）。</summary>
public sealed class MultilingualCaptureResult
{
    /// <summary>是否成功（KR 缺失导致 Canonical 无法执行时为 false，且**不写任何快照**）</summary>
    public required bool Success { get; init; }

    /// <summary>失败原因（成功为 null）</summary>
    public string? FailureReason { get; init; }

    /// <summary>各语言本次解析到的单元数</summary>
    public IReadOnlyDictionary<SourceLanguage, int> CurrentUnitCounts { get; init; } = new Dictionary<SourceLanguage, int>();

    /// <summary>各语言上一份快照单元数（-1 = 无 baseline）</summary>
    public IReadOnlyDictionary<SourceLanguage, int> PreviousUnitCounts { get; init; } = new Dictionary<SourceLanguage, int>();

    /// <summary>本次成功更新 baseline 的语言（顺序 KR → EN → JP）</summary>
    public IReadOnlyList<SourceLanguage> SavedLanguages { get; init; } = Array.Empty<SourceLanguage>();

    /// <summary>目录缺失而跳过的语言</summary>
    public IReadOnlyList<SourceLanguage> MissingDirectories { get; init; } = Array.Empty<SourceLanguage>();

    /// <summary>保存失败的语言（日志已明确，不假装全部成功）</summary>
    public IReadOnlyList<SourceLanguage> SaveFailures { get; init; } = Array.Empty<SourceLanguage>();

    /// <summary>Canonical Diff（由韩文决定）</summary>
    public CanonicalDiffResult? CanonicalDiff { get; init; }

    /// <summary>多语言对齐统计</summary>
    public MultilingualAlignmentStats? Alignment { get; init; }

    /// <summary>是否为首次建立 Canonical Baseline</summary>
    public bool IsFirstCanonicalBaseline { get; init; }

    /// <summary>
    /// 第9.0B收官轮：本次解析到的三语文本（UnitKey → 三语），供生产管线按 UnitKey 填充 DiffEntry。
    /// </summary>
    public IReadOnlyDictionary<string, MultilingualUnitSources> Sources { get; init; } =
        new Dictionary<string, MultilingualUnitSources>(StringComparer.Ordinal);

    /// <summary>第9.0B收官轮：上一份韩文快照（UnitKey → 旧韩文），用于 Modified 的 OldCanonicalKorean。</summary>
    public IReadOnlyDictionary<string, string> PreviousKoreanSources { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 第9.0B-P7轮：本次捕获时各语言**扫描到的逻辑文件路径**（语言前缀已归一化，与
    /// <see cref="Core.Models.UnitKey.RelativeFilePath"/> 一致）。
    ///
    /// 用途：KR 权威模式下确定旧中文解析范围（Current EN 文件集 ∪ Current KR 文件集）时，
    /// 直接复用本次捕获已经枚举过的文件集合，**不第二次遍历整个韩文目录**。
    /// </summary>
    public IReadOnlyDictionary<SourceLanguage, IReadOnlyList<string>> ScannedLogicalFiles { get; init; } =
        new Dictionary<SourceLanguage, IReadOnlyList<string>>();

    /// <summary>
    /// 第9.0B 最终轮：本次捕获**已解析的三语单元**（按语言）。
    ///
    /// 用途：邻句上下文索引按模式选择来源语言（<c>TranslationModePolicy.GetNeighborSourceLanguage</c>）时，
    /// 直接复用本次捕获结果，**不第二次解析磁盘**。
    /// </summary>
    public IReadOnlyDictionary<SourceLanguage, IReadOnlyList<TranslationUnit>> ParsedUnits { get; init; } =
        new Dictionary<SourceLanguage, IReadOnlyList<TranslationUnit>>();
}

/// <summary>单个 UnitKey 的三语源文（第9.0B收官轮）。</summary>
public sealed class MultilingualUnitSources
{
    /// <summary>当前韩文</summary>
    public string? Korean { get; init; }

    /// <summary>当前英文</summary>
    public string? English { get; init; }

    /// <summary>当前日文</summary>
    public string? Japanese { get; init; }
}