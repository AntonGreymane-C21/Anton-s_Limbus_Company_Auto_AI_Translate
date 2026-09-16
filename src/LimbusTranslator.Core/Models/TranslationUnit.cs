namespace LimbusTranslator.Core.Models;

/// <summary>
/// 系统最重要的数据模型 —— 规范化翻译单元。
/// Parser 将各种游戏 JSON 文件统一转换为该结构。
/// </summary>
public sealed class TranslationUnit
{
    /// <summary>复合逻辑主键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>文件路径（带 EN_ 前缀的英文文件名）</summary>
    public required string FilePath { get; init; }

    /// <summary>关卡 ID（StoryData 专用；其他文件可为空）</summary>
    public string? StageId { get; init; }

    /// <summary>记录 ID</summary>
    public required string RecordId { get; init; }

    /// <summary>字段路径</summary>
    public required string FieldPath { get; init; }

    /// <summary>源文本（英文原文）</summary>
    public required string SourceText { get; set; }

    /// <summary>译文（中文）</summary>
    public string? Translation { get; set; }

    /// <summary>说话人（取自 teller 字段；model 为韩文内部ID不翻译）</summary>
    public string? Speaker { get; init; }

    /// <summary>在记录中的排序</summary>
    public int Order { get; init; }

    /// <summary>额外元数据</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    /// <summary>
    /// SourceHash 盐（第9.0B轮）：把「同一文本、不同源语言模式」的 TM 记录隔离。
    /// null/空 → 与历史 EN-only 行为**完全一致**（旧 TM 仍可命中）；非空 → 哈希包含该盐，
    /// 因此 JP / KO 模式（含 EN/JP 同时回退到韩文的情形）**绝不命中**旧 EN 记录。
    /// </summary>
    public string? SourceHashSalt { get; init; }
}
