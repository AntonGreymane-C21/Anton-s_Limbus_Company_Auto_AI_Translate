namespace LimbusTranslator.Core.Models;

/// <summary>
/// 复合逻辑主键。
/// 由于不同文件之间可能存在相同 ID、同一 ID 下多个文本字段、相同英文但上下文不同，
/// 因此禁止使用 ID + Text 作为唯一定位方式。
/// 主键 = RelativeFilePath + RecordId + FieldPath
/// </summary>
public sealed record UnitKey
{
    /// <summary>相对文件路径，如 StoryData/EN_1D101A.json</summary>
    public required string RelativeFilePath { get; init; }

    /// <summary>记录 ID（统一用字符串承载，原始值可能是数字也可能是字符串）</summary>
    public required string RecordId { get; init; }

    /// <summary>字段路径，如 dataList.$.content 或 dataList.$.levelList.$.coinlist.$.coindescs.$.desc</summary>
    public required string FieldPath { get; init; }

    public override string ToString() => $"{RelativeFilePath}|{RecordId}|{FieldPath}";
}
