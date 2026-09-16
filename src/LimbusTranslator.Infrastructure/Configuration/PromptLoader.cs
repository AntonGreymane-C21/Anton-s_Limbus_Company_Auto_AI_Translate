using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// 提示词加载器。读取 config/prompt.json；文件不存在时使用内置默认值。
/// </summary>
public static class PromptLoader
{
    /// <summary>
    /// 加载提示词配置。
    /// </summary>
    public static PromptOptions Load(string? configDir = null)
    {
        var options = new PromptOptions();
        var path = ResolvePath(configDir);
        if (!File.Exists(path))
        {
            return options;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            if (root.TryGetProperty("systemPrompt", out var sp) && sp.ValueKind == JsonValueKind.String)
            {
                options.SystemPrompt = sp.GetString() ?? options.SystemPrompt;
            }
            if (root.TryGetProperty("outputFormat", out var of) && of.ValueKind == JsonValueKind.String)
            {
                options.OutputFormat = of.GetString() ?? options.OutputFormat;
            }
        }
        catch
        {
            // 加载失败时使用默认值
        }

        return options;
    }

    /// <summary>
    /// 严格加载提示词配置（第7轮）：失败时返回原因，不静默回退默认值。
    /// 用于「配置错误必须 fail-closed」的启动校验；成功时 <paramref name="options"/> 非 null。
    /// </summary>
    public static bool TryLoad(string? configDir, out PromptOptions options, out string error)
    {
        options = new PromptOptions();
        error = string.Empty;
        var path = ResolvePath(configDir);
        if (!File.Exists(path))
        {
            error = $"未找到提示词配置: {path}";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"提示词配置根节点必须是 JSON 对象: {path}";
                return false;
            }

            var hasSystemPrompt = false;
            if (root.TryGetProperty("systemPrompt", out var sp))
            {
                if (sp.ValueKind != JsonValueKind.String)
                {
                    error = "提示词配置的 systemPrompt 必须是字符串。";
                    return false;
                }

                options.SystemPrompt = sp.GetString() ?? string.Empty;
                hasSystemPrompt = true;
            }

            if (root.TryGetProperty("outputFormat", out var of))
            {
                if (of.ValueKind != JsonValueKind.String)
                {
                    error = "提示词配置的 outputFormat 必须是字符串。";
                    return false;
                }

                options.OutputFormat = of.GetString() ?? string.Empty;
            }

            if (!hasSystemPrompt || string.IsNullOrWhiteSpace(options.SystemPrompt))
            {
                error = "提示词配置缺少 systemPrompt。";
                return false;
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = $"提示词配置不是合法 JSON: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"读取提示词配置失败: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 保存提示词到 config/prompt.json。
    /// </summary>
    public static void Save(string configDir, PromptOptions options)
    {
        var path = Path.Combine(configDir, "prompt.json");
        var payload = new
        {
            options.SystemPrompt,
            options.OutputFormat,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>
    /// 解析 prompt.json 路径。
    /// </summary>
    private static string ResolvePath(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir))
        {
            return Path.Combine(configDir, "prompt.json");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "prompt.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return Path.Combine(configDir ?? ".", "prompt.json");
    }
}
