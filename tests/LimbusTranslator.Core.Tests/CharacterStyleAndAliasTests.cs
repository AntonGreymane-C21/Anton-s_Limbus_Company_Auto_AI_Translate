using LimbusTranslator.Core.CharacterStyle;
using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.CharacterStyle;
using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 角色别名映射与角色风格提示词测试。
/// 验证：
///   1. 韩文 model ID（含带数字后缀如 료슈2）转换为中文角色名
///   2. CharacterStyleService.SelectStyles 传入中文名/韩文名/英文名均能命中风格
///   3. DiffEngine 中罪人 Speaker 转换为中文名，NPC 从 old_zh 继承中文 teller
///   4. 生成的 characterStylePrompt 包含正确的角色风格段落，且 CharacterStyleHash 非空串哈希
/// </summary>
public class CharacterStyleAndAliasTests
{
    private const string EmptyStringHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Theory]
    [InlineData("이상", "李箱", "Yi Sang")]
    [InlineData("파우스트", "浮士德", "Faust")]
    [InlineData("돈키호테", "堂吉诃德", "Don Quixote")]
    [InlineData("료슈", "良秀", "Ryoshu")]
    [InlineData("뫼르소", "默尔索", "Meursault")]
    [InlineData("홍루", "鸿璐", "Hong Lu")]
    [InlineData("히스클리프", "希斯克利夫", "Heathcliff")]
    [InlineData("이스마엘", "以实玛利", "Ishmael")]
    [InlineData("로쟈", "罗佳", "Rodion")]
    [InlineData("로디온", "罗佳", "Rodion")]
    [InlineData("싱클레어", "辛克莱", "Sinclair")]
    [InlineData("오티스", "奥提斯", "Outis")]
    [InlineData("그레고르", "格里高尔", "Gregor")]
    [InlineData("단테", "但丁", "Dante")]
    public void 韩文ModelID_正确映射到中文名与StyleKey(string korean, string expectedChinese, string expectedStyleKey)
    {
        Assert.True(CharacterAliasHelper.TryGetChineseName(korean, out var chinese));
        Assert.Equal(expectedChinese, chinese);

        Assert.True(CharacterAliasHelper.TryGetStyleKey(korean, out var styleKey));
        Assert.Equal(expectedStyleKey, styleKey);
    }

    [Theory]
    [InlineData("료슈2", "良秀", "Ryoshu")]
    [InlineData("료슈_2", "良秀", "Ryoshu")]
    [InlineData("료슈-3", "良秀", "Ryoshu")]
    [InlineData("그레고르1", "格里高尔", "Gregor")]
    [InlineData("그레고르_01", "格里高尔", "Gregor")]
    [InlineData("Ryoshu2", "良秀", "Ryoshu")]
    [InlineData("Ryoshu_2", "良秀", "Ryoshu")]
    [InlineData("Gregor1", "格里高尔", "Gregor")]
    [InlineData("단테2", "但丁", "Dante")]
    public void 带数字后缀的ModelID_正常归一化并解析(string modelWithSuffix, string expectedChinese, string expectedStyleKey)
    {
        Assert.True(CharacterAliasHelper.TryGetChineseName(modelWithSuffix, out var chinese));
        Assert.Equal(expectedChinese, chinese);

        Assert.True(CharacterAliasHelper.TryGetStyleKey(modelWithSuffix, out var styleKey));
        Assert.Equal(expectedStyleKey, styleKey);
    }

    [Theory]
    [InlineData("唐吉诃德", "堂吉诃德", "Don Quixote")]
    [InlineData("堂吉诃德", "堂吉诃德", "Don Quixote")]
    [InlineData("Don Quixote", "堂吉诃德", "Don Quixote")]
    [InlineData("don quixote", "堂吉诃德", "Don Quixote")]
    [InlineData("DonQuixote", "堂吉诃德", "Don Quixote")]
    [InlineData("Gregor", "格里高尔", "Gregor")]
    [InlineData("gregor", "格里高尔", "Gregor")]
    [InlineData("格里高尔", "格里高尔", "Gregor")]
    [InlineData("Rodya", "罗佳", "Rodion")]
    public void 英文名与中文名变体_兼容解析(string alias, string expectedChinese, string expectedStyleKey)
    {
        Assert.True(CharacterAliasHelper.TryGetChineseName(alias, out var chinese));
        Assert.Equal(expectedChinese, chinese);

        Assert.True(CharacterAliasHelper.TryGetStyleKey(alias, out var styleKey));
        Assert.Equal(expectedStyleKey, styleKey);
    }

    [Fact]
    public void 非罪人NPC_不误识别为罪人()
    {
        Assert.False(CharacterAliasHelper.TryGetChineseName("유리", out _));
        Assert.False(CharacterAliasHelper.TryGetChineseName("Yuri", out _));
        Assert.False(CharacterAliasHelper.TryGetChineseName("UnknownNPC", out _));
        Assert.False(CharacterAliasHelper.TryGetChineseName(null, out _));
        Assert.False(CharacterAliasHelper.TryGetChineseName("", out _));
    }

    [Theory]
    [InlineData("格里高尔")]
    [InlineData("그레고르")]
    [InlineData("Gregor")]
    [InlineData("그레고르1")]
    public void SelectStyles_中文名韩文名英文名及带后缀_均能命中风格且Key为中文(string speaker)
    {
        var service = new CharacterStyleService();
        var styles = service.SelectStyles(new[] { speaker });

        var kv = Assert.Single(styles);
        Assert.Equal("格里高尔", kv.Key);
        Assert.Equal("沧桑、自我调侃、士兵口吻", kv.Value);
    }

    [Fact]
    public void SelectStyles_带后缀良秀与唐吉诃德变体_均能命中()
    {
        var service = new CharacterStyleService();
        var styles = service.SelectStyles(new[] { "료슈2", "唐吉诃德" });

        var map = styles.ToDictionary(s => s.Key, s => s.Value);
        Assert.Equal(2, map.Count);
        Assert.Equal("狂气、措辞古怪", map["良秀"]);
        Assert.Equal("夸张、热情、骑士式表达", map["堂吉诃德"]);
    }

    [Fact]
    public void SelectStyles_同一罪人不同别名传入_去重为单个中文Key()
    {
        var service = new CharacterStyleService();
        var styles = service.SelectStyles(new[] { "Gregor", "그레고르", "格里高尔", "그레고르2" });

        var kv = Assert.Single(styles);
        Assert.Equal("格里高尔", kv.Key);
        Assert.Equal("沧桑、自我调侃、士兵口吻", kv.Value);
    }

    [Fact]
    public void DiffEngine_罪人ModelID转换为中文名()
    {
        var oldEn = new[]
        {
            CreateUnit("StoryData/1D101A.json", "1", "dataList[0].content", "Old text", "그레고르"),
        };
        var oldZh = new[]
        {
            CreateUnit("StoryData/1D101A.json", "1", "dataList[0].content", "旧文本", "格里高尔"),
        };
        var newEn = new[]
        {
            CreateUnit("StoryData/1D101A.json", "1", "dataList[0].content", "New text", "그레고르"),
        };

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("格里高尔", entry.Speaker);
        Assert.Equal("格里高尔", result.NewUnits[0].Speaker);
    }

    [Fact]
    public void DiffEngine_带数字后缀罪人ModelID转换为中文名()
    {
        var newEn = new[]
        {
            CreateUnit("StoryData/1D101A.json", "1", "dataList[0].content", "Ryoshu text", "료슈2"),
        };

        var engine = new DiffEngine();
        var result = engine.Compute(Array.Empty<TranslationUnit>(), Array.Empty<TranslationUnit>(), newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("良秀", entry.Speaker);
        Assert.Equal("良秀", result.NewUnits[0].Speaker);
    }

    [Fact]
    public void DiffEngine_NPC从old_zh继承中文Teller()
    {
        // newEn 中 speaker 是韩文 model "유리"
        var newEn = new[]
        {
            CreateUnit("StoryData/1D101A.json", "10", "dataList[0].content", "Yuri line", "유리"),
        };
        // oldZh 中相同 recordId 具有中文 teller "尤莉"
        var oldZh = new[]
        {
            CreateUnit("StoryData/1D101A.json", "10", "dataList[0].content", "尤莉旧台词", "尤莉"),
        };

        var engine = new DiffEngine();
        var result = engine.Compute(Array.Empty<TranslationUnit>(), oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("尤莉", entry.Speaker);
        Assert.Equal("尤莉", result.NewUnits[0].Speaker);
    }

    [Fact]
    public void DiffEngine_NPC从old_zh的teller字段继承中文Teller()
    {
        // newEn 中只有 content 单元，speaker 为 null
        var newEn = new[]
        {
            CreateUnit("StoryData/1D101A.json", "10", "dataList[0].content", "Yuri line", null),
        };
        // oldZh 包含 teller 字段单元
        var oldZh = new[]
        {
            CreateUnit("StoryData/1D101A.json", "10", "dataList[0].teller", "尤莉", null),
        };

        var engine = new DiffEngine();
        var result = engine.Compute(Array.Empty<TranslationUnit>(), oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("尤莉", entry.Speaker);
    }

    [Fact]
    public void BuildStylePrompt_生成包含中文角色名的提示词段落()
    {
        var service = new CharacterStyleService();
        var styles = service.SelectStyles(new[] { "그레고르" });
        var prompt = CharacterStyleService.BuildStylePrompt(styles);

        Assert.Contains("角色风格（本批涉及）：", prompt);
        Assert.Contains("角色「格里高尔」的说话风格：沧桑、自我调侃、士兵口吻。翻译该角色台词时请保持这种口吻。", prompt);
    }

    [Fact]
    public void RequestFingerprint_包含角色风格提示词时Hash非空串哈希()
    {
        var service = new CharacterStyleService();
        var styles = service.SelectStyles(new[] { "格里高尔" });
        var stylePrompt = CharacterStyleService.BuildStylePrompt(styles);

        var payload = new RequestFingerprintPayload
        {
            Provider = "deepseek",
            ProviderIdentity = "https://api.deepseek.com",
            Model = "deepseek-chat",
            SystemPrompt = "SYS\n\n" + stylePrompt,
            UserContent = "USER",
            CharacterStylePrompt = stylePrompt,
            Items = new[]
            {
                new RequestFingerprintItem
                {
                    Id = "item1",
                    UnitKey = "StoryData/1D101A.json|1|dataList[0].content",
                    TranslationMode = "TranslateNew",
                    Source = "Hello",
                    Speaker = "格里高尔",
                },
            },
        };

        var fingerprint = RequestFingerprintBuilder.Build(payload);

        Assert.NotEmpty(fingerprint.CharacterStyleHash);
        Assert.NotEqual(EmptyStringHash, fingerprint.CharacterStyleHash);
        Assert.Equal(RequestFingerprintBuilder.Sha256Hex(stylePrompt), fingerprint.CharacterStyleHash);
    }

    private static TranslationUnit CreateUnit(
        string file,
        string recordId,
        string fieldPath,
        string? text,
        string? speaker)
    {
        return new TranslationUnit
        {
            Key = new UnitKey
            {
                RelativeFilePath = file,
                RecordId = recordId,
                FieldPath = fieldPath,
            },
            FilePath = file,
            RecordId = recordId,
            FieldPath = fieldPath,
            SourceText = text ?? string.Empty,
            Speaker = speaker,
        };
    }
}
