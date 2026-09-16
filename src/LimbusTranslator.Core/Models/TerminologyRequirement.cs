namespace LimbusTranslator.Core.Models;

/// <summary>
/// 术语需求项（由 ValidationPipeline 依据 GlossaryService 的匹配结果填充）。
/// </summary>
public sealed class TerminologyRequirement
{
    /// <summary>英文术语（glossary key）</summary>
    public required string Source { get; init; }

    /// <summary>规定译法</summary>
    public required string Target { get; init; }

    /// <summary>是否锁定（locked=true）。校验只针对锁定术语。</summary>
    public bool Locked { get; init; }

    /// <summary>该术语是否本来就要求保留英文（如 E.G.O）</summary>
    public bool PreservedAsEnglish =>
        string.Equals(Source, Target, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => $"{Source} → {Target}{(Locked ? "(locked)" : string.Empty)}";
}
