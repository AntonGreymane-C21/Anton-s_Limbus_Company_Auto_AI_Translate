using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Glossary;

/// <summary>
/// 术语来源（第8.88轮）：用于诊断与合并优先级判断。
/// </summary>
public enum GlossaryTermSource
{
    /// <summary>用户本地术语库（config/glossary.json）</summary>
    Local = 0,

    /// <summary>Paratranz 远程同步（仅缓存，不写入本地术语库）</summary>
    Paratranz = 1,
}

/// <summary>
/// 术语条目。与 config/glossary.json 的 canonical 格式（translation / locked）保持一致；
/// <see cref="Source"/> 只存在于内存，不会被写回文件（Save 显式构造字典）。
/// </summary>
public sealed class GlossaryEntry
{
    /// <summary>标准译法</summary>
    public required string Translation { get; init; }

    /// <summary>locked=true 表示 AI 禁止自行修改译法</summary>
    public bool Locked { get; init; }

    /// <summary>术语来源（本地 / Paratranz）</summary>
    public GlossaryTermSource Source { get; init; } = GlossaryTermSource.Local;
}

/// <summary>
/// 术语库服务。
///
/// 职责：
///   1. 加载 config/glossary.json
///   2. 动态术语选择：扫描一批文本中出现的英文术语，只提取命中的子集
///   3. 生成术语子集 Hash（用于缓存指纹，避免无关术语变化导致缓存失效）
///
/// 术语库格式：
///   { "Sinking": { "translation": "沉沦", "locked": true }, ... }
/// </summary>
public sealed class GlossaryService
{
    private readonly Dictionary<string, GlossaryEntry> _entries;
    private readonly string _sourcePath;

    /// <summary>
    /// 加载术语库。
    /// </summary>
    /// <param name="configDir">配置目录（含 glossary.json）</param>
    public GlossaryService(string? configDir = null)
    {
        _sourcePath = ResolvePath(configDir);
        _entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        Load();
    }

    /// <summary>所有术语条目（英文 → 译法）</summary>
    public IReadOnlyDictionary<string, GlossaryEntry> Entries => _entries;

    /// <summary>内置默认术语（恢复默认用，与 config/glossary.json 同步）</summary>
    public static readonly IReadOnlyDictionary<string, GlossaryEntry> DefaultEntries = new Dictionary<string, GlossaryEntry>
    {
        ["Sinking"] = new() { Translation = "沉沦", Locked = true },
        ["Bleed"] = new() { Translation = "流血", Locked = true },
        ["Rupture"] = new() { Translation = "破裂", Locked = true },
        ["Tremor"] = new() { Translation = "震颤", Locked = true },
        ["Burn"] = new() { Translation = "烧伤", Locked = true },
        ["Poise"] = new() { Translation = "呼吸法", Locked = true },
        ["Charge"] = new() { Translation = "充能", Locked = true },
        ["Haste"] = new() { Translation = "迅捷", Locked = true },
        ["Bind"] = new() { Translation = "束缚", Locked = true },
        ["Fragile"] = new() { Translation = "易损", Locked = true },
        ["Protection"] = new() { Translation = "守护", Locked = true },
        ["Paralyze"] = new() { Translation = "麻痹", Locked = true },
        ["Stun"] = new() { Translation = "晕眩", Locked = true },
        ["Stagger"] = new() { Translation = "眩晕", Locked = true },
        ["Taunt"] = new() { Translation = "嘲讽", Locked = true },
        ["Shock"] = new() { Translation = "感电", Locked = true },
        ["Marked"] = new() { Translation = "标记", Locked = true },
        ["Power Up"] = new() { Translation = "威力提升", Locked = true },
        ["Power Down"] = new() { Translation = "威力降低", Locked = true },
        ["Damage Up"] = new() { Translation = "伤害强化", Locked = true },
        ["Damage Down"] = new() { Translation = "伤害弱化", Locked = true },
        ["Attack Power Up"] = new() { Translation = "强壮", Locked = true },
        ["Attack Power Down"] = new() { Translation = "虚弱", Locked = true },
        ["Defense Power Up"] = new() { Translation = "忍耐", Locked = true },
        ["Defense Power Down"] = new() { Translation = "破绽", Locked = true },
        ["Offense Level Up"] = new() { Translation = "攻击等级提升", Locked = true },
        ["Offense Level Down"] = new() { Translation = "攻击等级降低", Locked = true },
        ["Defense Level Up"] = new() { Translation = "防御等级提升", Locked = true },
        ["Defense Level Down"] = new() { Translation = "防御等级降低", Locked = true },
        ["Ongoing Damage"] = new() { Translation = "持续损伤", Locked = true },
        ["Clash"] = new() { Translation = "拼点", Locked = true },
        ["Lunacy"] = new() { Translation = "狂气", Locked = true },
        ["Enkephalin"] = new() { Translation = "脑啡肽", Locked = true },
        ["Sinner"] = new() { Translation = "罪人", Locked = true },
        ["Manager"] = new() { Translation = "管理人", Locked = true },
        ["E.G.O"] = new() { Translation = "E.G.O", Locked = true },
        ["SP"] = new() { Translation = "理智值", Locked = true },
        ["HP"] = new() { Translation = "体力值", Locked = true },
        ["Speed"] = new() { Translation = "速度值", Locked = true },
        ["Slash"] = new() { Translation = "斩击", Locked = true },
        ["Pierce"] = new() { Translation = "突刺", Locked = true },
        ["Blunt"] = new() { Translation = "打击", Locked = true },
        ["Wrath"] = new() { Translation = "暴怒", Locked = true },
        ["Lust"] = new() { Translation = "色欲", Locked = true },
        ["Sloth"] = new() { Translation = "怠惰", Locked = true },
        ["Gluttony"] = new() { Translation = "暴食", Locked = true },
        ["Gloom"] = new() { Translation = "忧郁", Locked = true },
        ["Pride"] = new() { Translation = "傲慢", Locked = true },
        ["Envy"] = new() { Translation = "嫉妒", Locked = true },
        ["Blade Lineage"] = new() { Translation = "剑契组", Locked = true },
        ["Middle"] = new() { Translation = "中指", Locked = true },
        ["Ring"] = new() { Translation = "环指", Locked = true },
        ["Index"] = new() { Translation = "食指", Locked = true },
        ["Fixer"] = new() { Translation = "收尾人", Locked = true },
        ["The Head"] = new() { Translation = "头颅", Locked = true },
        ["Limbus Company"] = new() { Translation = "边狱巴士", Locked = true },
        ["Mirror Dungeon"] = new() { Translation = "镜面迷宫", Locked = true },
        ["Dante"] = new() { Translation = "但丁", Locked = true },
        ["Bloodfiend"] = new() { Translation = "血魔", Locked = true },
        // 第8.5轮确认的中英 / 韩中专名别名必须与 config/glossary.json 同步：
        // ResetToDefault() 不得把已锁定的同一角色译名回退掉。
        ["Sinclair"] = new() { Translation = "辛克莱", Locked = true },
        ["싱클레어"] = new() { Translation = "辛克莱", Locked = true },
        ["Philip"] = new() { Translation = "菲利普", Locked = true },
        ["필립"] = new() { Translation = "菲利普", Locked = true },
    };

    /// <summary>
    /// 保存术语库回 glossary.json。
    ///
    /// 第8.87轮修复（严重 Bug）：
    ///   原实现用匿名类型 + 默认序列化选项写出 **PascalCase**（<c>Translation</c>/<c>Locked</c>），
    ///   而 <see cref="Load"/> 只认小写 <c>translation</c>/<c>locked</c>（TryGetProperty 大小写敏感），
    ///   导致「GUI 只要保存过一次术语库，整个术语库的译名就全部读成空串、locked 全部变成 false」，
    ///   进而表现为「术语表不生效」与「术语库条目只有原文、译文为空」。
    ///
    /// 现约定 <c>config/glossary.json</c> 的 canonical 格式为 **camelCase**：
    ///   { "Sinking": { "translation": "沉沦", "locked": true } }
    ///
    /// 同时：空译名条目一律不落盘（避免把未完成项写成“只有原文”的坏数据），
    /// 并采用「临时文件 + 原子替换」写入，避免写一半把用户术语库截断。
    /// </summary>
    public void Save()
    {
        var payload = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var skippedEmpty = new List<string>();
        foreach (var kv in _entries.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(kv.Value.Translation))
            {
                skippedEmpty.Add(kv.Key);
                continue;
            }

            payload[kv.Key] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["translation"] = kv.Value.Translation,
                ["locked"] = kv.Value.Locked,
            };
        }

        var json = System.Text.Json.JsonSerializer.Serialize(payload, CanonicalJsonOptions);
        WriteAtomic(_sourcePath, json);

        if (skippedEmpty.Count > 0)
        {
            LastSaveSkippedEmptyKeys = skippedEmpty;
        }
    }

    /// <summary>canonical 序列化选项：camelCase + 缩进 + 中文不转义。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions CanonicalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>上一次 Save 因空译名被跳过的术语（诊断用；正常应为空）。</summary>
    public IReadOnlyList<string> LastSaveSkippedEmptyKeys { get; private set; } = Array.Empty<string>();

    /// <summary>原子写入：先写临时文件，再替换目标，避免中断时截断用户术语库。</summary>
    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// 恢复默认术语库。
    /// </summary>
    public void ResetToDefault()
    {
        _entries.Clear();
        foreach (var kvp in DefaultEntries)
        {
            _entries[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// 更新术语。
    ///
    /// 第8.87轮：<paramref name="translation"/> 为 null/空/纯空白时**拒绝写入空译名**
    /// （保留已有译名或作为未完成项），返回 false。避免“只有原文、译文为空”的坏数据进入术语库。
    /// </summary>
    public bool Update(string english, string translation, bool locked)
    {
        if (string.IsNullOrWhiteSpace(english))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        _entries[english] = new GlossaryEntry { Translation = translation.Trim(), Locked = locked };
        return true;
    }

    /// <summary>
    /// 删除术语。
    /// </summary>
    public void Remove(string english)
    {
        _entries.Remove(english);
    }


    /// <summary>术语总数</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 加载 glossary.json。
    ///
    /// 第8.87轮：兼容 camelCase（canonical）与 PascalCase（历史 GUI 写出的格式），
    /// 并记录「译名为空的条目」数量供诊断 —— 不再静默把整库读成空串。
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_sourcePath))
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_sourcePath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var translation = string.Empty;
                var locked = false;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetPropertyIgnoreCase(prop.Value, "translation", out var t) && t.ValueKind == JsonValueKind.String)
                    {
                        translation = t.GetString() ?? string.Empty;
                    }

                    if (TryGetPropertyIgnoreCase(prop.Value, "locked", out var l))
                    {
                        locked = l.ValueKind == JsonValueKind.True;
                    }
                }

                _entries[prop.Name] = new GlossaryEntry { Translation = translation, Locked = locked };
            }

            EmptyTranslationKeys = _entries
                .Where(kv => string.IsNullOrWhiteSpace(kv.Value.Translation))
                .Select(kv => kv.Key)
                .ToList();
        }
        catch (Exception ex)
        {
            LoadError = ex.Message;
        }
    }

    /// <summary>大小写不敏感地读取对象属性（兼容 translation/Translation）。</summary>
    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>加载时解析失败的异常摘要（诊断用）。</summary>
    public string? LoadError { get; private set; }

    /// <summary>加载后译名为空的术语键（诊断用；正常应为空）。</summary>
    public IReadOnlyList<string> EmptyTranslationKeys { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// 动态术语选择：扫描文本中出现的术语，返回命中子集。
    ///
    /// 第8.875轮：匹配统一走 <see cref="Core.Text.TermMatcher"/>（带词边界），
    /// 与 TerminologyValidator 使用**同一套匹配实现**，不再出现「Prompt 认为命中、校验认为未命中」。
    /// </summary>
    /// <param name="texts">要扫描的文本集合</param>
    public IReadOnlyList<KeyValuePair<string, GlossaryEntry>> SelectTerms(IEnumerable<string> texts)
        => ActiveGlossarySnapshot.SelectTermsFrom(_entries, texts);

    /// <summary>术语库文件路径（快照诊断用）。</summary>
    public string SourcePath => _sourcePath;

    /// <summary>
    /// 第8.875轮：由**运行时术语快照**构造 GlossaryService（不改动快照本身，只读使用）。
    /// </summary>
    public static GlossaryService FromSnapshot(ActiveGlossarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var copy = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in snapshot.Entries)
        {
            copy[kv.Key] = kv.Value;
        }

        return new GlossaryService(snapshot.SourcePath ?? string.Empty, copy);
    }

    /// <summary>供快照构造使用的内部构造函数（跳过文件加载）。</summary>
    private GlossaryService(string sourcePath, Dictionary<string, GlossaryEntry> entries)
    {
        _sourcePath = sourcePath;
        _entries = entries;
    }

    /// <summary>
    /// 创建**不可变术语快照**（第8.875轮）：一次运行创建一次，供 Prompt / Validator / 诊断共用。
    /// </summary>
    public ActiveGlossarySnapshot CreateSnapshot() => ActiveGlossarySnapshot.FromEntries(_entries, _sourcePath);

    /// <summary>
    /// 生成术语子集 Hash（SHA256 前缀）。
    /// </summary>
    public static string ComputeSubsetHash(IEnumerable<KeyValuePair<string, GlossaryEntry>> subset)
    {
        var sorted = subset
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => $"{kv.Key}={kv.Value.Translation}({(kv.Value.Locked ? "L" : "U")})");
        var joined = string.Join('|', sorted);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// 构造术语提示词段落（注入 System Prompt 用）。
    ///
    /// 第8.87轮：**Locked（强制）与 Preferred（优先）必须使用不同措辞**，
    /// 不能把「优先参考」也写成「必须遵守」，否则模型会对非锁定术语做不必要的强制替换。
    /// </summary>
    public static string BuildGlossaryPrompt(IEnumerable<KeyValuePair<string, GlossaryEntry>> subset)
    {
        var list = subset.ToList();
        var locked = list.Where(kv => kv.Value.Locked).ToList();
        var preferred = list.Where(kv => !kv.Value.Locked).ToList();

        var sections = new List<string>();

        if (locked.Count > 0)
        {
            sections.Add(
                "【强制术语（Locked）】只要原文中的对应词具有该术语含义，就必须使用指定中文译名，" +
                "不得自行音译、改写、同义替换或省略：\n" +
                string.Join("\n", locked.Select(kv => $"{kv.Key} → {kv.Value.Translation}")));
        }

        if (preferred.Count > 0)
        {
            sections.Add(
                "【优先术语（参考）】默认优先使用以下译法；若语境明确不是该术语含义，可按语境处理：\n" +
                string.Join("\n", preferred.Select(kv => $"{kv.Key} → {kv.Value.Translation}")));
        }

        return sections.Count == 0 ? string.Empty : string.Join("\n\n", sections);
    }

    /// <summary>
    /// 命中术语的可诊断摘要（第8.87轮）：<c>Philip→菲利普[L]|Sinclair→辛克莱[L]</c>。
    /// 仅用于 Trace / 高级详情排错，不进入主界面。
    /// </summary>
    public static string DescribeSubset(IEnumerable<KeyValuePair<string, GlossaryEntry>> subset, int maxTerms = 20)
    {
        var list = subset.ToList();
        if (list.Count == 0)
        {
            return string.Empty;
        }

        var ordered = list
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxTerms)
            .Select(kv => $"{kv.Key}→{kv.Value.Translation}{(kv.Value.Locked ? "[L]" : string.Empty)}");
        var text = string.Join("|", ordered);
        return list.Count > maxTerms ? $"{text}|+{list.Count - maxTerms}" : text;
    }

    /// <summary>
    /// 解析 glossary.json 路径。
    /// </summary>
    private static string ResolvePath(string? configDir)
    {
        if (!string.IsNullOrWhiteSpace(configDir) && Directory.Exists(configDir))
        {
            return Path.Combine(configDir, "glossary.json");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config", "glossary.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        return Path.Combine(configDir ?? ".", "glossary.json");
    }
}
