using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// 配置加载器。
/// 读取 config/appsettings.json；不存在时回退到默认值。
/// API Key 只允许来自配置文件，禁止硬编码。
/// </summary>
public static class AppSettingsLoader
{
    /// <summary>
    /// 加载 DeepSeek 配置。
    /// </summary>
    /// <param name="configDir">配置目录（默认取项目 config/ 目录）</param>
    public static DeepSeekOptions LoadDeepSeek(string? configDir = null)
    {
        var options = new DeepSeekOptions();
        var path = ResolveConfigPath(configDir);
        if (!File.Exists(path))
        {
            return options;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var ds = doc.RootElement.EnumerateObject()
                .FirstOrDefault(p => p.Name.Equals("deepSeek", StringComparison.OrdinalIgnoreCase)).Value;

            if (ds.ValueKind == JsonValueKind.Object)
            {
                // 用不区分大小写的字典读取所有属性
                var props = ds.EnumerateObject()
                    .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

                if (props.TryGetValue("apiUrl", out var v)) options.ApiUrl = v.GetString() ?? options.ApiUrl;
                if (props.TryGetValue("apiKey", out var k)) options.ApiKey = k.GetString() ?? string.Empty;
                if (props.TryGetValue("model", out var m)) options.Model = m.GetString() ?? options.Model;
                if (props.TryGetValue("thinking", out var t)) options.Thinking = t.ValueKind == JsonValueKind.True;
                if (props.TryGetValue("temperature", out var tp) && tp.ValueKind == JsonValueKind.Number) options.Temperature = tp.GetDouble();
                if (props.TryGetValue("maxTokens", out var mt) && mt.ValueKind == JsonValueKind.Number) options.MaxTokens = mt.GetInt32();
                if (props.TryGetValue("timeoutSeconds", out var ts) && ts.ValueKind == JsonValueKind.Number) options.TimeoutSeconds = ts.GetInt32();
                if (props.TryGetValue("maxRetry", out var mr) && mr.ValueKind == JsonValueKind.Number) options.MaxRetry = mr.GetInt32();
                if (props.TryGetValue("maxConcurrentRequests", out var mc) && mc.ValueKind == JsonValueKind.Number) options.MaxConcurrentRequests = mc.GetInt32();
            }
        }
        catch
        {
            // 配置解析失败时使用默认值
        }

        return options;
    }

    /// <summary>
    /// 严格加载 Provider 配置（第7轮，fail-closed）。
    ///
    /// 规则：
    ///   - 只有 <c>"provider": "mock"</c> 才是「用户显式选择模拟翻译」；其余情况一律按真实 Provider 处理。
    ///   - 真实 Provider 模式下，配置文件缺失 / JSON 损坏 / 关键字段缺失或非法 / 提示词配置无效
    ///     一律返回 Success = false，由调用方终止任务，禁止回退 Mock。
    ///   - 任何情况下都不会因为配置问题而「静默换 Provider」。
    /// </summary>
    /// <param name="configDir">配置目录（默认取项目 config/ 目录）</param>
    public static SettingsLoadResult LoadProviderSettings(string? configDir = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var options = new DeepSeekOptions();
        var path = ResolveConfigPath(configDir);

        SettingsLoadResult Fail(TranslationProviderMode mode) => new()
        {
            Success = false,
            Options = options,
            Mode = mode,
            Errors = errors,
            Warnings = warnings,
            ConfigPath = path,
        };

        if (!File.Exists(path))
        {
            errors.Add(
                $"未找到配置文件: {path}。可复制 config/appsettings.example.json 为 config/appsettings.json " +
                "并填写 deepSeek.apiKey；若只想验证流程，请显式写入 \"provider\": \"mock\"。");
            return Fail(TranslationProviderMode.DeepSeek);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            errors.Add($"读取配置文件失败: {ex.Message}");
            return Fail(TranslationProviderMode.DeepSeek);
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            errors.Add($"config/appsettings.json 不是合法 JSON: {ex.Message}");
            return Fail(TranslationProviderMode.DeepSeek);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add("配置文件根节点必须是 JSON 对象。");
                return Fail(TranslationProviderMode.DeepSeek);
            }

            var root = doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

            // 1) Provider 选择：默认 deepseek；仅显式 mock 才使用模拟翻译
            var mode = TranslationProviderMode.DeepSeek;
            if (root.TryGetValue("provider", out var providerEl))
            {
                if (providerEl.ValueKind != JsonValueKind.String)
                {
                    errors.Add("provider 必须是字符串（\"deepseek\" 或 \"mock\"）。");
                    return Fail(mode);
                }

                var raw = providerEl.GetString()?.Trim() ?? string.Empty;
                if (raw.Equals("mock", StringComparison.OrdinalIgnoreCase))
                {
                    mode = TranslationProviderMode.Mock;
                }
                else if (raw.Length == 0 || raw.Equals("deepseek", StringComparison.OrdinalIgnoreCase))
                {
                    mode = TranslationProviderMode.DeepSeek;
                }
                else
                {
                    errors.Add($"provider 取值无效: \"{raw}\"（仅支持 \"deepseek\" 或 \"mock\"）。");
                    return Fail(TranslationProviderMode.DeepSeek);
                }
            }

            var hasDeepSeekSection = root.TryGetValue("deepSeek", out var deepSeekEl)
                                     && deepSeekEl.ValueKind == JsonValueKind.Object;

            if (mode == TranslationProviderMode.Mock)
            {
                // 显式 mock：不校验 API 配置（但仍然要求 JSON 合法）
                if (hasDeepSeekSection
                    && deepSeekEl.TryGetProperty("apiKey", out var mockKey)
                    && mockKey.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(mockKey.GetString()))
                {
                    warnings.Add("已显式选择模拟翻译（provider=mock），配置中的 API Key 不会被使用。");
                }

                return new SettingsLoadResult
                {
                    Success = true,
                    Options = options,
                    Mode = TranslationProviderMode.Mock,
                    Errors = errors,
                    Warnings = warnings,
                    ConfigPath = path,
                };
            }

            if (!hasDeepSeekSection)
            {
                errors.Add("缺少 deepSeek 配置段（至少需要 apiUrl / apiKey / model）。");
                return Fail(mode);
            }

            var ds = deepSeekEl.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);

            // apiKey
            if (!ds.TryGetValue("apiKey", out var keyEl))
            {
                errors.Add("缺少 deepSeek.apiKey（如需模拟翻译请显式设置 \"provider\": \"mock\"）。");
            }
            else if (keyEl.ValueKind != JsonValueKind.String)
            {
                errors.Add("deepSeek.apiKey 必须是字符串。");
            }
            else if (string.IsNullOrWhiteSpace(keyEl.GetString()))
            {
                errors.Add("deepSeek.apiKey 为空（如需模拟翻译请显式设置 \"provider\": \"mock\"）。");
            }
            else
            {
                options.ApiKey = keyEl.GetString()!.Trim();
            }

            // apiUrl
            if (!ds.TryGetValue("apiUrl", out var urlEl))
            {
                errors.Add("缺少 deepSeek.apiUrl。");
            }
            else if (urlEl.ValueKind != JsonValueKind.String)
            {
                errors.Add("deepSeek.apiUrl 必须是字符串。");
            }
            else
            {
                var url = urlEl.GetString()?.Trim() ?? string.Empty;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    errors.Add($"deepSeek.apiUrl 不是合法的 http/https 绝对地址: \"{url}\"");
                }
                else
                {
                    options.ApiUrl = url;
                }
            }

            // model
            if (!ds.TryGetValue("model", out var modelEl))
            {
                errors.Add("缺少 deepSeek.model。");
            }
            else if (modelEl.ValueKind != JsonValueKind.String)
            {
                errors.Add("deepSeek.model 必须是字符串。");
            }
            else if (string.IsNullOrWhiteSpace(modelEl.GetString()))
            {
                errors.Add("deepSeek.model 为空。");
            }
            else
            {
                options.Model = modelEl.GetString()!.Trim();
            }

            // 采样 / 超时 / 并发参数：类型与范围必须合法（非法值会导致请求行为不可预期）
            var configuredOptionals = 0;
            configuredOptionals += ReadNumber(ds, "temperature", 0d, 2d, errors, value => options.Temperature = value);
            configuredOptionals += ReadNumber(ds, "maxTokens", 1d, 1_000_000d, errors, value => options.MaxTokens = (int)value);
            configuredOptionals += ReadNumber(ds, "timeoutSeconds", 1d, 3600d, errors, value => options.TimeoutSeconds = (int)value);
            configuredOptionals += ReadNumber(ds, "maxRetry", 0d, 100d, errors, value => options.MaxRetry = (int)value);
            configuredOptionals += ReadNumber(ds, "maxConcurrentRequests", 1d, 1000d, errors, value => options.MaxConcurrentRequests = (int)value);

            // Thinking 模式（第8.75轮）：thinkingMode（deepSeek 段或根级）> 旧 thinking > 默认 adaptive
            var thinkingModeSpecified = false;
            JsonElement thinkingModeEl;
            if (ds.TryGetValue("thinkingMode", out thinkingModeEl)
                || root.TryGetValue("thinkingMode", out thinkingModeEl))
            {
                thinkingModeSpecified = true;
                if (thinkingModeEl.ValueKind != JsonValueKind.String)
                {
                    errors.Add("thinkingMode 必须是字符串（adaptive / always_on / always_off）。");
                }
                else
                {
                    var raw = thinkingModeEl.GetString()?.Trim().ToLowerInvariant() ?? string.Empty;
                    switch (raw)
                    {
                        case "adaptive":
                            options.ThinkingMode = TranslationThinkingMode.Adaptive;
                            configuredOptionals++;
                            break;
                        case "always_on" or "always-on" or "on":
                            options.ThinkingMode = TranslationThinkingMode.AlwaysOn;
                            configuredOptionals++;
                            break;
                        case "always_off" or "always-off" or "off":
                            options.ThinkingMode = TranslationThinkingMode.AlwaysOff;
                            configuredOptionals++;
                            break;
                        case "":
                            options.ThinkingMode = TranslationThinkingMode.Adaptive;
                            break;
                        default:
                            errors.Add($"thinkingMode 取值无效: \"{raw}\"（仅支持 adaptive / always_on / always_off）。");
                            break;
                    }
                }
            }

            if (ds.TryGetValue("thinking", out var thinkingEl))
            {
                if (thinkingEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    options.Thinking = thinkingEl.GetBoolean();
                    configuredOptionals++;

                    // 向后兼容：旧配置只有 thinking 布尔值时映射为 always_on / always_off；
                    // 显式 thinkingMode 优先（不被旧字段覆盖）。
                    if (!thinkingModeSpecified)
                    {
                        options.ThinkingMode = options.Thinking
                            ? TranslationThinkingMode.AlwaysOn
                            : TranslationThinkingMode.AlwaysOff;
                    }
                }
                else
                {
                    errors.Add("deepSeek.thinking 必须是 true / false。");
                }
            }

            // 思考强度（第8.5轮）：仅在 thinking=true 时随请求发送
            if (ds.TryGetValue("reasoningEffort", out var effortEl))
            {
                if (effortEl.ValueKind != JsonValueKind.String)
                {
                    errors.Add("deepSeek.reasoningEffort 必须是字符串（low / high / max）。");
                }
                else
                {
                    var effort = effortEl.GetString()?.Trim() ?? string.Empty;
                    if (effort.Length == 0)
                    {
                        options.ReasoningEffort = null;
                    }
                    else if (effort is "low" or "high" or "max")
                    {
                        options.ReasoningEffort = effort;
                        configuredOptionals++;
                    }
                    else
                    {
                        errors.Add($"deepSeek.reasoningEffort 取值无效: \"{effort}\"（仅支持 low / high / max）。");
                    }
                }
            }

            if (configuredOptionals == 0)
            {
                warnings.Add("deepSeek 未配置采样 / 超时 / 并发参数，将使用内置默认值。");
            }

            // batch 段：Provider 请求分批（第7.5轮，唯一分批来源）
            // 缺失 → 默认值 + Warning；存在但非法 → Error（fail-closed，禁止偷偷用硬编码值）
            var batch = new BatchOptions();
            var hasBatchSection = root.TryGetValue("batch", out var batchEl);
            if (hasBatchSection && batchEl.ValueKind != JsonValueKind.Object)
            {
                errors.Add("batch 段必须是 JSON 对象。");
            }
            else
            {
                var batchProps = hasBatchSection
                    ? batchEl.EnumerateObject()
                        .ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

                batch.MaxItemsPerBatch = ReadBatchValue(
                    batchProps, "maxItemsPerBatch", BatchOptions.MaxItemsFieldName,
                    BatchOptions.DefaultMaxItemsPerBatch, BatchOptions.MaxAllowedItemsPerBatch,
                    errors, warnings);

                batch.MaxCharactersPerBatch = ReadBatchValue(
                    batchProps, "maxCharactersPerBatch", BatchOptions.MaxCharactersFieldName,
                    BatchOptions.DefaultMaxCharactersPerBatch, BatchOptions.MaxAllowedCharactersPerBatch,
                    errors, warnings);

                // targetInputTokens 为保留位：读取并校验，但当前分批仍以字符预算为准（缺失不告警）
                batch.TargetInputTokens = ReadBatchValue(
                    batchProps, "targetInputTokens", BatchOptions.TargetInputTokensFieldName,
                    BatchOptions.DefaultTargetInputTokens, BatchOptions.MaxAllowedTargetInputTokens,
                    errors, warnings, warnWhenMissing: false);
            }

            // 提示词配置：真实 Provider 必须可读（prompt.json 直接决定实际请求内容）
            if (!PromptLoader.TryLoad(configDir, out _, out var promptError))
            {
                errors.Add(promptError);
            }

            return new SettingsLoadResult
            {
                Success = errors.Count == 0,
                Options = options,
                Batch = batch,
                Mode = mode,
                Errors = errors,
                Warnings = warnings,
                ConfigPath = path,
            };
        }
    }

    /// <summary>
    /// 读取 batch 数值字段（第7.5轮）：
    ///   缺失 → 默认值（可选 Warning）
    ///   类型错误 / 非整数 / 超出范围 → 记 Error（fail-closed，绝不静默套用默认值）
    /// </summary>
    private static int ReadBatchValue(
        IReadOnlyDictionary<string, JsonElement> props,
        string jsonName,
        string displayName,
        int defaultValue,
        int maxAllowed,
        List<string> errors,
        List<string> warnings,
        bool warnWhenMissing = true)
    {
        if (!props.TryGetValue(jsonName, out var element))
        {
            if (warnWhenMissing)
            {
                warnings.Add($"未配置 {displayName}，使用默认值 {defaultValue}。");
            }

            return defaultValue;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            errors.Add($"{displayName} 必须是数字。");
            return defaultValue;
        }

        var value = element.GetDouble();
        if (double.IsNaN(value) || value < 1 || value > maxAllowed || value != Math.Floor(value))
        {
            errors.Add($"{displayName} 必须是 1 ~ {maxAllowed} 之间的整数（当前：{element.GetRawText()}）。");
            return defaultValue;
        }

        return (int)value;
    }

    /// <summary>
    /// 读取可选数值字段：缺失算「已处理」（返回 1），类型或范围非法记错误（返回 0）。
    /// </summary>
    private static int ReadNumber(
        IReadOnlyDictionary<string, JsonElement> props,
        string name,
        double min,
        double max,
        List<string> errors,
        Action<double> assign)
    {
        if (!props.TryGetValue(name, out var element))
        {
            return 1;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            errors.Add($"deepSeek.{name} 必须是数字。");
            return 0;
        }

        var value = element.GetDouble();
        if (double.IsNaN(value) || value < min || value > max)
        {
            errors.Add($"deepSeek.{name} 超出允许范围（{min} ~ {max}）：{value}");
            return 0;
        }

        assign(value);
        return 1;
    }

    /// <summary>
    /// 读取翻译模式配置（第9.0B.1轮）：<c>translationMode</c>（en_only / kr_en / kr_jp / kr_only），默认 en_only。
    ///
    /// 旧配置兼容：若没有 <c>translationMode</c> 但存在 <c>translationSourceLanguage</c>，按迁移规则解释
    /// （**旧 en → EN_ONLY，绝不解释为 KR_EN**；旧 ko → 纯韩文；旧 ja → 韩文 + 日文）。
    ///
    /// **Fail-closed**：非法值返回 false，不静默回默认。
    /// </summary>
    public static bool TryLoadTranslationMode(string? configDir, out TranslationMode mode, out string? error)
    {
        mode = TranslationModeCodes.Default;
        error = null;

        try
        {
            var path = ResolveConfigPath(configDir);
            if (!File.Exists(path))
            {
                return true;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));

            if (doc.RootElement.TryGetProperty("translationMode", out var modeElement))
            {
                if (modeElement.ValueKind != System.Text.Json.JsonValueKind.String)
                {
                    error = "translationMode 必须是字符串（en_only / kr_en / kr_jp / kr_only）。";
                    return false;
                }

                var raw = modeElement.GetString();
                var parsed = TranslationModeCodes.TryParseCode(raw);
                if (parsed is null)
                {
                    error = $"Unsupported translation mode: {raw}（仅支持 en_only / kr_en / kr_jp / kr_only）。";
                    return false;
                }

                mode = parsed.Value;
                return true;
            }

            // 旧配置迁移
            if (doc.RootElement.TryGetProperty("translationSourceLanguage", out var legacyElement)
                && legacyElement.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var legacyRaw = legacyElement.GetString();
                var legacy = SourceLanguageHelper.TryParseCode(legacyRaw);
                if (legacy is null)
                {
                    error = $"Unsupported source language: {legacyRaw}（仅支持 ko / en / ja）。";
                    return false;
                }

                mode = TranslationModeCodes.FromLegacySourceLanguage(legacy);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = "读取 translationMode 失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取翻译源语言配置（第9.0B轮）：<c>translationSourceLanguage</c>，取值 ko / en / ja，默认 en。
    /// **Fail-closed**：非法值返回错误（不静默变成 en）。
    /// </summary>
    /// <param name="configDir">配置目录</param>
    /// <param name="language">解析结果（失败时为默认值，调用方需检查返回值）</param>
    /// <param name="error">错误信息（成功为 null）</param>
    public static bool TryLoadTranslationSourceLanguage(
        string? configDir,
        out SourceLanguage language,
        out string? error)
    {
        language = SourceLanguageHelper.Default;
        error = null;

        try
        {
            var path = ResolveConfigPath(configDir);
            if (!File.Exists(path))
            {
                return true; // 无配置文件 → 使用默认（不属于非法配置）
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("translationSourceLanguage", out var element))
            {
                return true; // 未配置 → 默认
            }

            if (element.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                error = "translationSourceLanguage 必须是字符串（ko / en / ja）。";
                return false;
            }

            var raw = element.GetString();
            var parsed = SourceLanguageHelper.TryParseCode(raw);
            if (parsed is null)
            {
                error = $"Unsupported source language: {raw}（仅支持 ko / en / ja）。";
                return false;
            }

            language = parsed.Value;
            return true;
        }
        catch (Exception ex)
        {
            error = "读取 translationSourceLanguage 失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 读取 Paratranz 配置（第8.88轮；最小配置，不硬编码任何凭据）。
    /// 配置缺失或非法时回退为「未启用」，不影响翻译主链。
    /// </summary>
    public static Paratranz.ParatranzOptions LoadParatranz(string? configDir = null)
    {
        var options = new Paratranz.ParatranzOptions();
        try
        {
            var path = ResolveConfigPath(configDir);
            if (!File.Exists(path))
            {
                return options;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("paratranz", out var section)
                || section.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return options;
            }

            if (section.TryGetProperty("enabled", out var enabled))
            {
                options.Enabled = enabled.ValueKind == System.Text.Json.JsonValueKind.True;
            }

            if (section.TryGetProperty("projectId", out var projectId) && projectId.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                options.ProjectId = projectId.GetString();
            }

            if (section.TryGetProperty("cachePath", out var cachePath) && cachePath.ValueKind == System.Text.Json.JsonValueKind.String
                && !string.IsNullOrWhiteSpace(cachePath.GetString()))
            {
                options.CachePath = cachePath.GetString()!;
            }

            if (section.TryGetProperty("timeoutSeconds", out var timeout) && timeout.TryGetInt32(out var seconds) && seconds > 0)
            {
                options.TimeoutSeconds = seconds;
            }
        }
        catch
        {
            // 配置损坏 → 保持「未启用」，不抛出（不影响翻译）
        }

        return options;
    }

    /// <summary>
    /// 保存 Paratranz 配置（第8.89轮）：只增改 <c>paratranz</c> 段，**保留文件中的其它段**（如 deepSeek / batch / paths）。
    /// </summary>
    public static void SaveParatranz(string configDir, Paratranz.ParatranzOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(configDir);
        var path = Path.Combine(configDir, "appsettings.json");

        System.Text.Json.Nodes.JsonNode root;
        if (File.Exists(path))
        {
            root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))
                   ?? new System.Text.Json.Nodes.JsonObject();
        }
        else
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        if (root is not System.Text.Json.Nodes.JsonObject obj)
        {
            throw new InvalidOperationException("[错误] appsettings.json 顶层不是 JSON 对象，已拒绝写入。");
        }

        obj["paratranz"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enabled"] = options.Enabled,
            ["projectId"] = options.ProjectId ?? string.Empty,
            ["cachePath"] = options.CachePath,
            ["timeoutSeconds"] = options.TimeoutSeconds,
        };

        File.WriteAllText(path, obj.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>
    /// 保存 DeepSeek 配置到 config/appsettings.json。
    ///
    /// 第7轮：保留文件中已有的 <c>provider</c> 字段。
    /// 否则在设置窗口点一次「保存」就会把用户显式选择的 <c>"provider": "mock"</c> 抹掉，
    /// 让 Provider 模式在用户不知情的情况下被改变。
    /// </summary>
    public static void SaveDeepSeek(string configDir, DeepSeekOptions options, BatchOptions? batch = null)
    {
        var path = Path.Combine(configDir, "appsettings.json");
        var provider = ReadProviderValue(path) ?? "deepseek";

        // 第8.8轮：GUI 保存设置时不得丢失 batch / paths 等既有配置段。
        var existing = ReadExistingSections(path);
        var effectiveBatch = batch ?? existing.Batch ?? BatchOptions.Default;
        var thinkingMode = (options.ThinkingMode ??
                            (options.Thinking ? TranslationThinkingMode.AlwaysOn : TranslationThinkingMode.Adaptive))
            .ToString().ToLowerInvariant()
            switch
            {
                "alwayson" => "always_on",
                "alwaysoff" => "always_off",
                _ => "adaptive",
            };

        var payload = new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["deepSeek"] = new Dictionary<string, object?>
            {
                // 保持既有 PascalCase 字段名，避免改变用户配置文件的既有格式（加载端不区分大小写）
                ["ApiUrl"] = options.ApiUrl,
                ["ApiKey"] = options.ApiKey,
                ["Model"] = options.Model,
                // 第8.75轮起：思考模式以 ThinkingMode 为准；同时写入旧 Thinking 以兼容旧版本程序
                ["ThinkingMode"] = thinkingMode,
                ["Thinking"] = options.ThinkingMode is null ? options.Thinking : thinkingMode == "always_on",
                ["ReasoningEffort"] = options.ReasoningEffort ?? string.Empty,
                ["Temperature"] = options.Temperature,
                ["MaxTokens"] = options.MaxTokens,
                ["TimeoutSeconds"] = options.TimeoutSeconds,
                ["MaxRetry"] = options.MaxRetry,
                ["MaxConcurrentRequests"] = options.MaxConcurrentRequests,
            },
            ["batch"] = new Dictionary<string, object?>
            {
                ["maxItemsPerBatch"] = effectiveBatch.MaxItemsPerBatch,
                ["maxCharactersPerBatch"] = effectiveBatch.MaxCharactersPerBatch,
                // 保留位：当前不参与切分（GUI 需明确标注）
                ["targetInputTokens"] = effectiveBatch.TargetInputTokens,
            },
        };

        if (existing.Paths is not null)
        {
            payload["paths"] = existing.Paths;
        }

        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>
    /// 保存翻译模式到 <c>config/appsettings.json</c>（第9.0C轮：GUI 切换模式，用户无需手改 JSON）。
    ///
    /// 行为：
    ///   - 只改写 <c>translationMode</c> 字段，**其它段落原样保留**（deepSeek / batch / paths / prompt 等）；
    ///   - 文件不存在时创建（只写 translationMode，其余由既有保存入口负责）；
    ///   - 写入使用与 <see cref="SaveDeepSeek"/> 相同的缩进与编码策略。
    /// </summary>
    public static void SaveTranslationMode(string configDir, TranslationMode mode)
    {
        ArgumentNullException.ThrowIfNull(configDir);

        var path = ResolveConfigPath(configDir);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        System.Text.Json.Nodes.JsonNode? root = null;
        if (File.Exists(path))
        {
            try
            {
                root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            }
            catch
            {
                root = null;   // 文件损坏时重建（只保证模式字段可写，不销毁原文件内容以外的信息）
            }
        }

        if (root is not System.Text.Json.Nodes.JsonObject obj)
        {
            obj = new System.Text.Json.Nodes.JsonObject();
        }

        obj["translationMode"] = TranslationModeCodes.ToCode(mode);

        // 同一份配置里若残留旧字段，一并清理，避免下次加载走旧迁移分支
        obj.Remove("translationSourceLanguage");

        File.WriteAllText(path, obj.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    /// <summary>已有配置中需要保留的段落（provider 由 <see cref="ReadProviderValue"/> 单独读取）。</summary>
    private sealed class ExistingSections
    {
        public BatchOptions? Batch { get; init; }

        public Dictionary<string, object?>? Paths { get; init; }
    }

    /// <summary>
    /// 读取既有配置里的 batch / paths 段（第8.8轮：GUI 保存不得丢段）。
    /// 解析失败时返回空（保存动作本身不应失败）。
    /// </summary>
    private static ExistingSections ReadExistingSections(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new ExistingSections();
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ExistingSections();
            }

            BatchOptions? batch = null;
            Dictionary<string, object?>? paths = null;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("batch", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.Object)
                {
                    var options = new BatchOptions();
                    if (property.Value.TryGetProperty("maxItemsPerBatch", out var mi)
                        && mi.ValueKind == JsonValueKind.Number)
                    {
                        options.MaxItemsPerBatch = mi.GetInt32();
                    }
                    if (property.Value.TryGetProperty("maxCharactersPerBatch", out var mc)
                        && mc.ValueKind == JsonValueKind.Number)
                    {
                        options.MaxCharactersPerBatch = mc.GetInt32();
                    }
                    if (property.Value.TryGetProperty("targetInputTokens", out var tk)
                        && tk.ValueKind == JsonValueKind.Number)
                    {
                        options.TargetInputTokens = tk.GetInt32();
                    }
                    batch = options;
                }
                else if (property.Name.Equals("paths", StringComparison.OrdinalIgnoreCase)
                         && property.Value.ValueKind == JsonValueKind.Object)
                {
                    paths = property.Value.EnumerateObject()
                        .ToDictionary(p => p.Name, p => (object?)p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
                }
            }

            return new ExistingSections { Batch = batch, Paths = paths };
        }
        catch
        {
            return new ExistingSections();
        }
    }

    /// <summary>读取已有配置里的 provider 字段（不存在或无法解析时返回 null）。</summary>
    private static string? ReadProviderValue(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("provider", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString()?.Trim();
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
        }
        catch
        {
            // 旧配置损坏时按默认 deepseek 处理：保存动作本身不应失败
        }

        return null;
    }

    /// <summary>
    /// 解析配置文件路径。
    /// 优先使用显式目录，其次查找项目 config/ 目录。
    /// </summary>
    private static string ResolveConfigPath(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir))
        {
            return Path.Combine(configDir, "appsettings.json");
        }

        // 向上查找项目根目录
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return Path.Combine(configDir ?? ".", "appsettings.json");
    }
}
