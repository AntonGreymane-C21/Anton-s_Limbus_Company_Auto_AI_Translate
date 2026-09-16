using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// 文件解析器。负责将游戏 JSON 文件解析为规范化 TranslationUnit 列表。
/// 第一阶段只有 JsonGameFileParser 实现。
/// </summary>
public interface IFileParser
{
    /// <summary>
    /// 解析单个游戏 JSON 文件。
    /// </summary>
    /// <param name="filePath">文件绝对路径</param>
    /// <param name="relativePath">相对文件路径（作为 UnitKey 一部分）</param>
    /// <returns>规范化 TranslationUnit 列表</returns>
    IReadOnlyList<TranslationUnit> Parse(string filePath, string relativePath);

    /// <summary>
    /// 判断某字段是否属于需要翻译的字段（依据字段白名单/黑名单规则）。
    /// 例如 StoryData 的 model 字段为韩文内部ID，不应翻译。
    /// </summary>
    bool IsTranslatableField(string fieldPath);
}
