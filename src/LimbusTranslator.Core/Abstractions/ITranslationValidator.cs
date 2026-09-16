using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// 翻译校验器（纯函数化）。
///
/// 输入：<see cref="ValidationContext"/>
/// 输出：0..N 个 <see cref="ValidationIssue"/>
///
/// 【禁止】Validator 自己写数据库、改 Translation Memory、调用 DeepSeek、
/// 修改游戏文件 / Diff / Manifest。所有副作用由调用方（Pipeline / Agent）负责。
/// </summary>
public interface ITranslationValidator
{
    /// <summary>校验器名称（用于 Issue.Validator 字段，保持稳定）</summary>
    string Name { get; }

    /// <summary>规则性质：硬安全 或 启发式</summary>
    ValidationRuleKind Kind { get; }

    /// <summary>
    /// 执行校验。必须容忍 null / empty / whitespace，不得抛异常。
    /// </summary>
    IReadOnlyList<ValidationIssue> Validate(ValidationContext context);
}
