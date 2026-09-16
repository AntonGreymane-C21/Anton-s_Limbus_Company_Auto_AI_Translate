using System.Text;
using System.Text.Json.Nodes;

namespace LimbusTranslator.IntegrationTests.Support;

/// <summary>
/// 端到端集成测试夹具（第6轮）。
///
/// 所有目录都在临时目录下：OldEnglish / OldChinese / NewEnglish / Output / Backup / Trace / GameRoot / Config / SQLite。
/// 绝不访问真实游戏目录、真实 Translation Memory、真实 API。
/// </summary>
public sealed partial class IntegrationFixture : IDisposable
{
    private IntegrationFixture(string root)
    {
        Root = root;
        OldEnglishDir = Path.Combine(root, "old_en");
        OldChineseDir = Path.Combine(root, "old_zh");
        NewEnglishDir = Path.Combine(root, "new_en");
        OutputDir = Path.Combine(root, "output");
        BackupDir = Path.Combine(root, "backup");
        TraceRoot = Path.Combine(root, "trace");
        GameRoot = Path.Combine(root, "game");
        GameChineseDir = Path.Combine(GameRoot, "Lang", "LLC_zh-CN");
        ConfigDir = Path.Combine(root, "config");
        TmDbPath = Path.Combine(root, "cache", "translation_memory.db");

        foreach (var dir in new[]
                 {
                     OldEnglishDir, OldChineseDir, NewEnglishDir, OutputDir, BackupDir,
                     TraceRoot, GameChineseDir, ConfigDir, Path.GetDirectoryName(TmDbPath)!,
                 })
        {
            Directory.CreateDirectory(dir);
        }
    }

    public string Root { get; }
    public string OldEnglishDir { get; }
    public string OldChineseDir { get; }
    public string NewEnglishDir { get; }
    public string OutputDir { get; }
    public string BackupDir { get; }
    public string TraceRoot { get; }
    public string GameRoot { get; }
    public string GameChineseDir { get; }
    public string ConfigDir { get; }
    public string TmDbPath { get; }

    public static IntegrationFixture Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "LT_IT_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new IntegrationFixture(root);
        fixture.WriteConfig();
        fixture.WriteGameFiles();
        return fixture;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, true);
        }
        catch
        {
            // 清理失败可忽略（临时目录）
        }
    }
}

/// <summary>夹具 JSON 文件写入（partial 扩展）。</summary>
public sealed partial class IntegrationFixture
{
    /// <summary>夹具内所有英文/中文文件名（与游戏发布结构一致：英文带 EN_ 前缀）。</summary>
    public const string GeneralChineseName = "General.json";
    public const string GeneralEnglishName = "EN_General.json";
    public const string StoryChineseName = "1D101A.json";
    public const string StoryEnglishName = "EN_1D101A.json";

    /// <summary>最小配置目录：提示词 + 字段规则 + 锁定术语（完全匹配夹具里的 fixture-glossary）。</summary>
    public void WriteConfig()
    {
        File.WriteAllText(Path.Combine(ConfigDir, "prompt.json"), """
            {
              "systemPrompt": "你是《Limbus Company / 边狱巴士》的中文汉化翻译。集成测试夹具提示词。",
              "outputFormat": "输出必须为 {\"items\":[{\"id\":\"...\",\"translation\":\"...\",\"needs_review\":false,\"reason\":\"\"}]}。"
            }
            """, new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(ConfigDir, "field_rules.json"), """
            {
              "translatableFields": ["name", "desc", "add", "min", "content", "teller", "title", "place", "flavor", "tooltip", "keyword", "text", "subtitle"],
              "forbiddenFields": ["id", "model"]
            }
            """, new UTF8Encoding(false));

        File.WriteAllText(Path.Combine(ConfigDir, "glossary.json"), """
            { "Sinking": { "translation": "沉沦", "locked": true } }
            """, new UTF8Encoding(false));
    }

    /// <summary>写入三份夹具目录（旧英文 / 旧中文 / 新英文）。</summary>
    public void WriteGameFiles()
    {
        WriteGeneralFiles();
        WriteStoryFiles();
    }

    private void WriteGeneralFiles()
    {
        // 新英文：id1..id6 与旧英文保持相同顺序（数组下标一致），末尾追加 id7 作为“新增”
        WriteJson(NewEnglishDir, GeneralEnglishName, """
            {"dataList":[
              {"id":1,"name":"Unchanged item"},
              {"id":2,"name":"Missing translation item"},
              {"id":3,"name":"Modified item new"},
              {"id":4,"name":"Missing translation item two"},
              {"id":5,"name":"Deal {0} damage to {1}."},
              {"id":6,"name":"Inflict 2 Sinking"},
              {"id":7,"name":"Added item"}
            ]}
            """);

        // 旧英文：与新版同序（不含 id7 → Added）；id3 不同 → Modified
        WriteJson(OldEnglishDir, GeneralEnglishName, """
            {"dataList":[
              {"id":1,"name":"Unchanged item"},
              {"id":2,"name":"Missing translation item"},
              {"id":3,"name":"Modified item old"},
              {"id":4,"name":"Missing translation item two"},
              {"id":5,"name":"Deal {0} damage to {1}."},
              {"id":6,"name":"Inflict 2 Sinking"}
            ]}
            """);

        // 旧中文：只有 id1（继承）与 id3（修改条目旧译文）→ id2/id4/id5/id6 缺失旧译、id7 新增
        WriteJson(OldChineseDir, GeneralChineseName, """
            {"dataList":[
              {"id":1,"name":"未变化条目"},
              {"id":3,"name":"已修改条目旧中文"}
            ]}
            """);
    }

    private void WriteStoryFiles()
    {
        // 新英文 StoryData：4 条 content
        //   id0 未变化（有旧中文）→ Inherit，同时作为当前句的 Previous
        //   id1 英文变化 → Modified（需要翻译，作为“当前句”）
        //   id2 未变化（有旧中文）→ Inherit，同时作为当前句的 Next
        //   id3 新增 → Added
        WriteJson(Path.Combine(NewEnglishDir, "StoryData"), StoryEnglishName, StoryJson(
            lineOne: "Line one needs translation now."));

        // 旧英文：与新版同序但**不含 id3** → 新版 id3 视为新增；id1 不同 → Modified
        WriteJson(Path.Combine(OldEnglishDir, "StoryData"), StoryEnglishName, StoryJson(
            lineOne: "Line one old english.",
            includeAddedLine: false));

        // 旧中文：id0 / id1 / id2 有旧译文（id3 缺失 → Added）；不含 place 字段以避免无关条目
        WriteJson(Path.Combine(OldChineseDir, "StoryData"), StoryChineseName, """
            {"dataList":[
              {"id":0,"content":"第零行旧中文。"},
              {"id":1,"model":"그레고르","teller":"Gregor","content":"第一行旧中文。"},
              {"id":2,"content":"第二行旧中文。"}
            ]}
            """);
    }

    private static string StoryJson(string lineOne, bool includeAddedLine = true)
    {
        // 记录顺序必须与新版一致（FieldPath 含数组下标），新增行只出现在新版（追加在末尾）
        var addedLine = includeAddedLine ? ",{\"id\":3,\"content\":\"Line three is brand new.\"}" : string.Empty;
        return $$"""{"dataList":[{"id":0,"content":"Line zero unchanged."},{"id":1,"model":"그레고르","teller":"Gregor","content":"{{lineOne}}"},{"id":2,"content":"Line two unchanged."}{{addedLine}}]}""";
    }

    /// <summary>场景 D：只修改 Previous（id0）的英文，当前句（id1）保持不变。</summary>
    public void ChangeStoryPreviousSource()
    {
        var storyDir = Path.Combine(NewEnglishDir, "StoryData");
        var json = StoryJson("Line one needs translation now.")
            .Replace("Line zero unchanged.", "Line zero CHANGED source.");
        WriteJson(storyDir, StoryEnglishName, json);
    }

    /// <summary>
    /// 第7轮（T-5）夹具：General.json 中包含「空源文」条目。
    ///
    ///   id1 英文未变 + 有旧中文        → Inherit（不需要翻译）
    ///   id2 新版英文为空（旧英不同）    → Modified，旧中文缺失 → 情况 B（跳过 Provider，译文空串）
    ///   id3 新版英文为纯空白（旧英不同）→ Modified，旧中文非空 → 情况 A（保留旧中文，跳过 Provider）
    ///   id4 正常英文修改               → Modified（必须调用 Provider）
    /// </summary>
    public void WriteEmptySourceFiles()
    {
        WriteJson(NewEnglishDir, GeneralEnglishName, """
            {"dataList":[
              {"id":1,"name":"Unchanged item"},
              {"id":2,"name":""},
              {"id":3,"name":"   "},
              {"id":4,"name":"Real new sentence"}
            ]}
            """);

        WriteJson(OldEnglishDir, GeneralEnglishName, """
            {"dataList":[
              {"id":1,"name":"Unchanged item"},
              {"id":2,"name":"Empty again in new english"},
              {"id":3,"name":"Was replaced by blank"},
              {"id":4,"name":"Old real sentence"}
            ]}
            """);

        WriteJson(OldChineseDir, GeneralChineseName, """
            {"dataList":[
              {"id":1,"name":"未变化条目"},
              {"id":2,"name":""},
              {"id":3,"name":"空字段应保留的旧中文"}
            ]}
            """);
    }

    /// <summary>
    /// 第8.75轮夹具：General.json 同时包含「普通英文」与「韩文异常源」，用于验证自适应 Thinking 分组。
    ///
    ///   id1 英文未变 + 有旧中文           → Inherit（不进 Provider）
    ///   id2 普通英文（旧英不同，无旧中文） → TranslateMissing → Thinking OFF
    ///   id3 韩文异常源（旧英不同，无旧中文）→ TranslateMissing → Thinking ON
    /// StoryData 使用默认夹具（其 content 走 Thinking ON）。
    /// </summary>
    public void WriteAdaptiveThinkingFiles()
    {
        WriteJson(NewEnglishDir, GeneralEnglishName, """
            {"dataList":[
              {"id":1,"name":"Unchanged item"},
              {"id":2,"name":"Inflict 2 Sinking."},
              {"id":3,"name":"필립 싱클레어가 탈출장치로 후퇴"}
            ]}
            """);

        WriteJson(OldEnglishDir, GeneralEnglishName, """
            {"dataList":[
              {"id":1,"name":"Unchanged item"},
              {"id":2,"name":"Old plain english."},
              {"id":3,"name":"Old korean english."}
            ]}
            """);

        WriteJson(OldChineseDir, GeneralChineseName, """
            {"dataList":[
              {"id":1,"name":"未变化条目"}
            ]}
            """);
    }

    private static void WriteJson(string directory, string fileName, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content, new UTF8Encoding(false));
    }
}

