namespace LimbusTranslator.Core.Text;

/// <summary>
/// 源文本可复用性判定（Translation Memory fail-safe 规则）。
///
/// 为什么必须存在：
///   空字符串同样会算出固定的 SHA256（所有空字段共用同一个 SourceHash），
///   若不显式排除，任意空字段之间都会互相“精确命中”，造成跨 Unit 串译。
///
/// 规则：null / 空串 / 纯空白 SourceText 一律不可作为 Translation Memory 复用依据。
/// </summary>
public static class SourceTextGuard
{
    /// <summary>
    /// 该源文本是否允许参与 Translation Memory 命中与写入。
    /// </summary>
    public static bool IsReusable(string? sourceText) => !string.IsNullOrWhiteSpace(sourceText);
}
