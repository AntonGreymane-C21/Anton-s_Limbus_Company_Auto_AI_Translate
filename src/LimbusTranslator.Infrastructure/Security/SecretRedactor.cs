namespace LimbusTranslator.Infrastructure.Security;

/// <summary>
/// 机密脱敏工具（第8.87轮）。
///
/// 任何进入 <b>日志 / Trace / 异常消息 / 界面错误框</b> 的文本都必须先经过这里，
/// 保证 API Key、Authorization 头不会泄露到用户可见位置。
/// </summary>
public static class SecretRedactor
{
    /// <summary>密钥掩码：只保留尾部 4 位，例如 <c>sk-****abcd</c>。</summary>
    public static string MaskKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return "(未配置)";
        }

        var key = apiKey.Trim();
        if (key.Length <= 4)
        {
            return "****";
        }

        var prefix = key.StartsWith("sk-", StringComparison.OrdinalIgnoreCase) ? "sk-" : string.Empty;
        return $"{prefix}****{key[^4..]}";
    }

    /// <summary>
    /// 脱敏任意文本：清除 <c>sk-xxxx</c> 形式的密钥、Bearer 令牌，以及显式传入的密钥原文。
    /// </summary>
    public static string Redact(string? text, string? apiKey = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var result = text;

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var key = apiKey.Trim();
            if (key.Length > 0)
            {
                result = result.Replace(key, MaskKey(key), StringComparison.Ordinal);
            }
        }

        // Authorization: Bearer xxx / Bearer xxx
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"(?i)bearer\s+[A-Za-z0-9_\-\.]{8,}",
            "Bearer ****");

        // sk- 开头的密钥
        result = System.Text.RegularExpressions.Regex.Replace(
            result,
            @"sk-[A-Za-z0-9_\-]{8,}",
            "sk-****");

        return result;
    }

    /// <summary>限制长度并脱敏（用于错误摘要）。</summary>
    public static string Summarize(string? text, int maxLength = 200, string? apiKey = null)
    {
        var redacted = Redact(text, apiKey).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return redacted.Length <= maxLength ? redacted : redacted[..maxLength] + "…";
    }
}