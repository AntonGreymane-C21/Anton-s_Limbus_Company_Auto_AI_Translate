using System.Text.Json;

namespace LimbusTranslator.Infrastructure.CharacterStyle;

/// <summary>
/// 角色风格服务。
///
/// 职责：
///   1. 加载 config/character_styles.json
///   2. 动态选择：扫描 Batch 内的 Speaker，只注入本批出现的角色风格
///   3. 保存回 json（UI 编辑用）
/// </summary>
public sealed class CharacterStyleService
{
    private readonly string _sourcePath;
    private Dictionary<string, string> _styles;

    /// <summary>内置默认风格（恢复默认用）</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultStyles = new Dictionary<string, string>
    {
        ["Don Quixote"] = "夸张、热情、骑士式表达",
        ["Faust"] = "冷静、理性、知识性",
        ["Gregor"] = "沧桑、自我调侃、士兵口吻",
        ["Heathcliff"] = "粗鲁、直接、情绪化",
        ["Hong Lu"] = "轻松、天然、有距离感",
        ["Ishmael"] = "认真、克制、略带抱怨",
        ["Rodion"] = "开朗、市井、爱讲故事",
        ["Ryoshu"] = "狂气、措辞古怪",
        ["Sinclair"] = "内向、胆怯、逐渐成长",
        ["Yi Sang"] = "诗意、抽象、平静",
        ["Outis"] = "军人、严肃、战略思维",
        ["Merusault"] = "沉默、简短、面无表情",
    };

    public CharacterStyleService(string? configDir = null)
    {
        _sourcePath = ResolvePath(configDir);
        _styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Load();
    }

    /// <summary>角色数</summary>
    public int Count => _styles.Count;

    /// <summary>所有角色风格（角色名 → 描述）</summary>
    public IReadOnlyDictionary<string, string> Styles => _styles;

    /// <summary>
    /// 加载 character_styles.json。
    /// </summary>
    private void Load()
    {
        _styles.Clear();
        if (!File.Exists(_sourcePath))
        {
            foreach (var kvp in DefaultStyles)
            {
                _styles[kvp.Key] = kvp.Value;
            }
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_sourcePath));
            if (doc.RootElement.TryGetProperty("characters", out var chars))
            {
                foreach (var prop in chars.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        _styles[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }
            }
        }
        catch
        {
            // 加载失败时使用默认
            foreach (var kvp in DefaultStyles)
            {
                _styles[kvp.Key] = kvp.Value;
            }
        }
    }

    /// <summary>
    /// 动态选择：从给定的角色集合中提取命中的风格。
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>> SelectStyles(IEnumerable<string?> speakers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var speaker in speakers)
        {
            if (string.IsNullOrWhiteSpace(speaker))
            {
                continue;
            }
            // teller 可能是中文名，尝试匹配韩文 model 或英文名
            if (_styles.TryGetValue(speaker, out var style))
            {
                result[speaker] = style;
            }
        }
        return result.ToList();
    }

    /// <summary>
    /// 构造角色风格提示词段落（注入 System Prompt 用）。
    /// </summary>
    public static string BuildStylePrompt(IEnumerable<KeyValuePair<string, string>> styles)
    {
        var lines = styles.Select(kv => $"角色「{kv.Key}」的说话风格：{kv.Value}。翻译该角色台词时请保持这种口吻。");
        return "角色风格（本批涉及）：\n" + string.Join("\n", lines);
    }

    /// <summary>
    /// 保存回 character_styles.json。
    /// </summary>
    public void Save()
    {
        var payload = new
        {
            说明 = "人物风格配置。每个角色的说话风格提示词，翻译时自动注入。用户可自行编辑。",
            characters = _styles,
        };
        File.WriteAllText(_sourcePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>
    /// 恢复默认风格。
    /// </summary>
    public void ResetToDefault()
    {
        _styles.Clear();
        foreach (var kvp in DefaultStyles)
        {
            _styles[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// 更新单个角色风格。
    /// </summary>
    public void Update(string character, string style)
    {
        _styles[character] = style;
    }

    /// <summary>
    /// 解析 character_styles.json 路径。
    /// </summary>
    private static string ResolvePath(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir))
        {
            return Path.Combine(configDir, "character_styles.json");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "character_styles.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return Path.Combine(configDir ?? ".", "character_styles.json");
    }
}
