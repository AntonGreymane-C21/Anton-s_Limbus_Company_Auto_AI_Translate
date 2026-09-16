using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Parsing;

/// <summary>
/// 字段规则：控制哪些字段需要翻译、哪些禁止翻译。
/// 从 config/field_rules.json 加载；黑名单优先于白名单。
/// </summary>
public sealed class FieldRules
{
    /// <summary>需要翻译的字段白名单</summary>
    public HashSet<string> TranslatableFields { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "desc", "add", "min", "content",
        "teller", "title", "place", "flavor",
        "tooltip", "keyword", "text", "subtitle",
    };

    /// <summary>禁止翻译的字段黑名单（优先）</summary>
    public HashSet<string> ForbiddenFields { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "id", "model",
    };

    /// <summary>
    /// 判断字段是否需要翻译。
    /// </summary>
    public bool IsTranslatable(string fieldName)
    {
        if (ForbiddenFields.Contains(fieldName))
        {
            return false;
        }
        return TranslatableFields.Contains(fieldName);
    }
}

/// <summary>
/// 字段规则加载器。
/// 读取 config/field_rules.json；文件不存在时使用内置默认值。
/// </summary>
public static class FieldRulesLoader
{
    /// <summary>
    /// 加载字段规则。
    /// </summary>
    public static FieldRules Load(string? configDir = null)
    {
        var rules = new FieldRules();
        var path = ResolvePath(configDir);
        if (!File.Exists(path))
        {
            return rules;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("translatableFields", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                rules.TranslatableFields.Clear();
                foreach (var item in t.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        rules.TranslatableFields.Add(item.GetString()!);
                    }
                }
            }

            if (root.TryGetProperty("forbiddenFields", out var f) && f.ValueKind == JsonValueKind.Array)
            {
                rules.ForbiddenFields.Clear();
                foreach (var item in f.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        rules.ForbiddenFields.Add(item.GetString()!);
                    }
                }
            }
        }
        catch
        {
            // 加载失败时使用默认规则
        }

        return rules;
    }

    /// <summary>
    /// 解析 field_rules.json 路径。
    /// </summary>
    private static string ResolvePath(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir))
        {
            return Path.Combine(configDir, "field_rules.json");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "field_rules.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return Path.Combine(configDir ?? ".", "field_rules.json");
    }
}
