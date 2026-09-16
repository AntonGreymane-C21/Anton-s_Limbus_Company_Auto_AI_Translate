using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Multilingual;

/// <summary>参考译本（EN / JP）相对上一次的变化类型。</summary>
public enum ReferenceChangeKind
{
    /// <summary>内容未变</summary>
    Unchanged = 0,

    /// <summary>内容变化</summary>
    Changed,

    /// <summary>之前缺失、现在出现</summary>
    MissingBefore,

    /// <summary>之前存在、现在缺失</summary>
    MissingNow,
}

/// <summary>单个 Unit 的参考译本变化（诊断用，不影响 Canonical DiffKind）。</summary>
public sealed class ReferenceChange
{
    /// <summary>单元键</summary>
    public required string UnitKey { get; init; }

    /// <summary>变化类型</summary>
    public required ReferenceChangeKind Kind { get; init; }

    /// <summary>旧文本（可能为 null）</summary>
    public string? OldText { get; init; }

    /// <summary>新文本（可能为 null）</summary>
    public string? NewText { get; init; }
}

/// <summary>Canonical Diff 结果（第9.0A轮）。</summary>
public sealed class CanonicalDiffResult
{
    /// <summary>Canonical（韩文）决定的条目：Added / Modified / Unchanged / Deleted</summary>
    public required IReadOnlyList<DiffEntry> Entries { get; init; }

    /// <summary>新增</summary>
    public int Added { get; init; }

    /// <summary>修改（韩文原文变化）</summary>
    public int Modified { get; init; }

    /// <summary>删除</summary>
    public int Deleted { get; init; }

    /// <summary>未变</summary>
    public int Unchanged { get; init; }

    /// <summary>英文参考译本变化明细</summary>
    public IReadOnlyList<ReferenceChange> EnglishChanges { get; init; } = Array.Empty<ReferenceChange>();

    /// <summary>日文参考译本变化明细</summary>
    public IReadOnlyList<ReferenceChange> JapaneseChanges { get; init; } = Array.Empty<ReferenceChange>();

    /// <summary>英文参考变化数量</summary>
    public int EnglishChangedCount => EnglishChanges.Count;

    /// <summary>日文参考变化数量</summary>
    public int JapaneseChangedCount => JapaneseChanges.Count;

    /// <summary>一行式摘要（日志用）</summary>
    public string Describe() =>
        $"Canonical Diff：新增 {Added}｜修改 {Modified}｜删除 {Deleted}｜不变 {Unchanged}";
}