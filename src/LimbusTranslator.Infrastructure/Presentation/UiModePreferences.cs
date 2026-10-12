using System.Text.Json;
using System.Text.Json.Nodes;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>界面模式：简易模式（默认）/ 专家模式（原工作台）。</summary>
public enum UiMode
{
    Simple,
    Expert,
}

/// <summary>
/// 界面模式偏好（记住上次选择）：保存在 <c>data/ui_prefs.json</c>，格式 <c>{ "uiMode": "simple" | "expert" }</c>。
///
/// 约束：
///   - 读取宽松：文件缺失 / 损坏 / 未知值一律回退为简易模式（首次启动 = 简易模式）；
///   - 写入保留文件中的其它键，先写临时文件再替换；任何异常都吞掉（偏好写失败不能影响主流程）。
/// </summary>
public static class UiModePreferences
{
    private const string ModeKey = "uiMode";

    /// <summary>偏好文件路径（项目根下 <c>data/ui_prefs.json</c>）。</summary>
    public static string GetPath(string projectRoot)
        => Path.Combine(projectRoot, "data", "ui_prefs.json");

    /// <summary>读取界面模式（任何问题 ⇒ 简易模式）。</summary>
    public static UiMode Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return UiMode.Simple;
            }

            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is JsonObject obj
                && obj.TryGetPropertyValue(ModeKey, out var value)
                && value is JsonValue jsonValue
                && jsonValue.TryGetValue<string>(out var text))
            {
                return Parse(text);
            }
        }
        catch
        {
            // 损坏的偏好文件按首次启动处理
        }

        return UiMode.Simple;
    }

    /// <summary>保存界面模式（失败返回 false，不抛异常）。</summary>
    public static bool Save(string path, UiMode mode)
    {
        try
        {
            JsonObject root;
            try
            {
                root = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
                    ? existing
                    : new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }

            root[ModeKey] = ToText(mode);

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>文本 → 模式（不区分大小写；未知值 ⇒ 简易模式）。</summary>
    public static UiMode Parse(string? text)
        => string.Equals(text?.Trim(), "expert", StringComparison.OrdinalIgnoreCase) ? UiMode.Expert : UiMode.Simple;

    /// <summary>模式 → 文本。</summary>
    public static string ToText(UiMode mode) => mode == UiMode.Expert ? "expert" : "simple";
}
