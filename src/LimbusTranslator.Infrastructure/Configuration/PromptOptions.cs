namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// AI 提示词配置。从 config/prompt.json 加载。
/// </summary>
public sealed class PromptOptions
{
    /// <summary>系统提示词（基础翻译规则）</summary>
    public string SystemPrompt { get; set; } =
        "你是《Limbus Company / 边狱巴士》的中文汉化翻译。规则：\n" +
        "1. 优先遵守术语库译法；\n" +
        "2. 保留原游戏文本格式（换行、富文本标签如 <size>、<color>）；\n" +
        "3. 保留变量占位符（如 {0}、{1}）；\n" +
        "4. 不得擅自增删原文信息；\n" +
        "5. 尽量继承已有汉化风格；\n" +
        "6. 保留人物口吻；\n" +
        "7. 只输出指定的 JSON，禁止输出额外解释。";

    /// <summary>输出格式要求（拼在 systemPrompt 末尾）</summary>
    public string OutputFormat { get; set; } =
        "请求为 {\"items\":[{\"id\":\"...\",\"source\":\"...\"}]}，输出必须为 {\"items\":[{\"id\":\"...\",\"translation\":\"...\",\"needs_review\":false,\"reason\":\"\"}]}。";
}
