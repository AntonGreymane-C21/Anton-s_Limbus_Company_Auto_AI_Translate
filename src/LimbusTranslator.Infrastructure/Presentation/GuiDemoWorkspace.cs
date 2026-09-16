using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>GUI 演示工作区的路径（第9.0C轮：仅用于验收演示，绝不写生产数据）。</summary>
public sealed record GuiDemoWorkspace(string Root, string LocalizeEnglishDir, string OldEnglishDir, string OldChineseDir)
{
    /// <summary>给用户看的说明文本（GUI 里三个路径填什么）。</summary>
    public string Describe()
        => $"演示工作区：{Root}{Environment.NewLine}"
           + $"新版英文目录：{LocalizeEnglishDir}{Environment.NewLine}"
           + $"旧英文目录：{OldEnglishDir}{Environment.NewLine}"
           + $"旧中文目录：{OldChineseDir}{Environment.NewLine}"
           + "（KR / JP 会从 Localize/en 的同级目录自动解析）";
}

/// <summary>
/// GUI 验收用**演示数据**生成器（第9.0C轮 §61/§62）。
///
/// 目录布局与真实游戏一致（<c>&lt;root&gt;/Localize/{en,kr,jp}</c> + 旧英文 / 旧中文），
/// 覆盖 6 种条目：新增 / 已修改 / 继承 / 缺少翻译 / 需要人工确认（韩文残留）/ StoryData（带邻句）。
///
/// 安全约束：只写调用方指定的目录；测试与 CLI 都使用 TEMP 根，绝不触碰生产数据与游戏目录。
/// </summary>
public static class GuiDemoWorkspaceBuilder
{
    private const string DemoFile = "Demo.json";
    private const string StoryFile = "StoryData/1D101A.json";

    /// <summary>在 <paramref name="root"/> 下创建演示工作区（已存在则覆盖同名文件）。</summary>
    public static GuiDemoWorkspace Create(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var localize = Path.Combine(root, "Localize");
        var en = Path.Combine(localize, "en");
        var kr = Path.Combine(localize, "kr");
        var jp = Path.Combine(localize, "jp");
        var oldEn = Path.Combine(root, "old_en");
        var oldZh = Path.Combine(root, "old_zh");
        foreach (var directory in new[] { en, kr, jp, oldEn, oldZh })
        {
            Directory.CreateDirectory(directory);
        }

        // 普通文本：新增 / 已修改 / 继承 / 缺少翻译 / 需要人工确认（旧中文含韩文残留）
        WriteFile(Path.Combine(en, "EN_" + DemoFile), new (int Id, string? Text)[]
        {
            (1, "Brand new line added this patch"),
            (2, "Modified line (new wording)"),
            (3, "Unchanged line with old translation"),
            (4, "Unchanged line without old translation"),
            (5, "Unchanged line with suspicious old translation"),
        });
        WriteFile(Path.Combine(oldEn, DemoFile), new (int Id, string? Text)[]
        {
            (1, null),                                        // 与当前英文保持行索引一致（该 Key 视为新增）
            (2, "Modified line (old wording)"),
            (3, "Unchanged line with old translation"),
            (4, "Unchanged line without old translation"),
            (5, "Unchanged line with suspicious old translation"),
        });
        WriteFile(Path.Combine(oldZh, DemoFile), new (int Id, string? Text)[]
        {
            (1, null),                                        // 旧中文按行索引对齐：缺失行不写可翻译字段
            (2, null),
            (3, "继承的旧中文"),
            (4, null),
            (5, "残留韩文的旧中文 안녕하세요"),
        });

        // 韩文 / 日文（KR 三模式演示用；与英文同 Key）
        WriteFile(Path.Combine(kr, "KR_" + DemoFile), new (int Id, string? Text)[]
        {
            (1, "이번 패치에 새로 추가된 문장"),
            (2, "수정된 문장(새 표현)"),
            (3, "변경되지 않은 문장"),
            (4, "변경되지 않았지만 번역이 없는 문장"),
            (5, "번역이 의심스러운 문장"),
        });
        WriteFile(Path.Combine(jp, "JP_" + DemoFile), new (int Id, string? Text)[]
        {
            (1, "今回のパッチで新しく追加された文"),
            (2, "修正された文（新しい表現）"),
            (3, "変更されていない文"),
            (4, "変更されていないが翻訳がない文"),
            (5, "翻訳が疑わしい文"),
        });

        // StoryData：目标行（id=6）前后各一条真实对白 → 可验证邻句上下文（字段必须是 content）
        WriteStory(Path.Combine(en, "StoryData"), "EN_", Field.Content, new (int Id, string? Text)[]
        {
            (5, "Oh... Sorry about that."),
            (6, "No, it’s fine."),
            (7, "The breaking of a Wing is a turbulent affair."),
        });
        WriteStory(Path.Combine(kr, "StoryData"), "KR_", Field.Content, new (int Id, string? Text)[]
        {
            (5, "아… 미안하다."),
            (6, "아니에요. 괜찮아요."),
            (7, "날개가 부러진다는 건 혼란스러운 사건이니까요."),
        });
        WriteStory(Path.Combine(jp, "StoryData"), "JP_", Field.Content, new (int Id, string? Text)[]
        {
            (5, "あ…ごめん。"),
            (6, "いえ、大丈夫です。"),
            (7, "翼が折れるというのは混乱を招く事件ですからね。"),
        });

        File.WriteAllText(
            Path.Combine(root, "README_DEMO.txt"),
            new GuiDemoWorkspace(root, en, oldEn, oldZh).Describe()
            + Environment.NewLine
            + "用途：GUI 验收演示（新增 / 已修改 / 继承 / 缺少翻译 / 含韩文残留的旧中文（可验证 Validator 展示） / StoryData 邻句）。"
            + Environment.NewLine
            + "注意：这是演示数据，不要把它当成真实游戏目录；不要用于生产部署。");

        return new GuiDemoWorkspace(root, en, oldEn, oldZh);
    }

    private static void WriteStory(string directory, string prefix, Field field, IReadOnlyList<(int Id, string? Text)> rows)
    {
        Directory.CreateDirectory(directory);
        WriteFile(Path.Combine(directory, prefix + "1D101A.json"), rows, field);
    }

    private static void WriteFile(string path, IReadOnlyList<(int Id, string? Text)> rows, Field field = Field.Name)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var fieldName = field == Field.Content ? "content" : "name";
        var payload = new Dictionary<string, object?>
        {
            ["dataList"] = rows
                .Select(row =>
                {
                    var record = new Dictionary<string, object?> { ["id"] = row.Id };
                    if (row.Text is not null)
                    {
                        record[fieldName] = row.Text;
                    }

                    return record;
                })
                .ToList(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
    }

    private enum Field
    {
        Name,
        Content,
    }
}
