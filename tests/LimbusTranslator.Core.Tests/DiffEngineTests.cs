using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// DiffEngine 核心逻辑测试。
/// 覆盖：Inherit / TranslateMissing / Added / Modified / Deleted 五种情况。
/// </summary>
public class DiffEngineTests
{
    private static UnitKey Key(string file, string id, string field = "content")
        => new() { RelativeFilePath = file, RecordId = id, FieldPath = field };

    private static TranslationUnit Unit(string file, string id, string text, string field = "content")
        => new()
        {
            Key = Key(file, id, field),
            FilePath = file,
            RecordId = id,
            FieldPath = field,
            SourceText = text,
        };

    [Fact]
    public void 英文没变且有旧中文_应继承()
    {
        var oldEn = new[] { Unit("Enemies.json", "1", "Hello") };
        var oldZh = new[] { Unit("Enemies.json", "1", "你好") };
        var newEn = new[] { Unit("Enemies.json", "1", "Hello") };

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(DiffKind.Unchanged, entry.DiffKind);
        Assert.Equal(TranslationAction.Inherit, entry.Action);
        Assert.Equal("你好", entry.Translation);
        Assert.Equal(1, result.UnchangedCount);
        Assert.Equal(0, result.MissingTranslationCount);
    }

    [Fact]
    public void 英文没变但旧中文缺失_应标记缺失旧译()
    {
        var oldEn = new[] { Unit("Enemies.json", "1", "Hello") };
        var oldZh = Array.Empty<TranslationUnit>();
        var newEn = new[] { Unit("Enemies.json", "1", "Hello") };

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(DiffKind.Unchanged, entry.DiffKind);
        Assert.Equal(TranslationAction.TranslateMissing, entry.Action);
        Assert.Equal(1, result.MissingTranslationCount);
    }

    [Fact]
    public void 新增ID_应标记TranslateNew()
    {
        var oldEn = Array.Empty<TranslationUnit>();
        var oldZh = Array.Empty<TranslationUnit>();
        var newEn = new[] { Unit("Enemies.json", "100", "New Text") };

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(DiffKind.Added, entry.DiffKind);
        Assert.Equal(TranslationAction.TranslateNew, entry.Action);
        Assert.Equal(1, result.AddedCount);
    }

    [Fact]
    public void 英文发生变化_应标记TranslateModified()
    {
        var oldEn = new[] { Unit("Enemies.json", "1", "Old Text") };
        var oldZh = new[] { Unit("Enemies.json", "1", "旧文本") };
        var newEn = new[] { Unit("Enemies.json", "1", "New Text") };

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(DiffKind.Modified, entry.DiffKind);
        Assert.Equal(TranslationAction.TranslateModified, entry.Action);
        Assert.Equal("旧文本", entry.OldTranslation);
        Assert.Equal(1, result.ModifiedCount);
    }

    [Fact]
    public void 旧版存在新版已删除_应标记SkipDeleted()
    {
        var oldEn = new[] { Unit("Enemies.json", "1", "Old Text") };
        var oldZh = new[] { Unit("Enemies.json", "1", "旧文本") };
        var newEn = Array.Empty<TranslationUnit>();

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(DiffKind.Deleted, entry.DiffKind);
        Assert.Equal(TranslationAction.SkipDeleted, entry.Action);
        Assert.Equal(1, result.DeletedCount);
    }

    [Fact]
    public void 复合主键不匹配_不应误判继承()
    {
        // 相同 ID 但不同文件，不应匹配
        var oldEn = new[] { Unit("Enemies.json", "1", "Hello") };
        var oldZh = new[] { Unit("Other.json", "1", "你好") };
        var newEn = new[] { Unit("Enemies.json", "1", "Hello") };

        var engine = new DiffEngine();
        var result = engine.Compute(oldEn, oldZh, newEn);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(TranslationAction.TranslateMissing, entry.Action);
    }

    [Fact]
    public void 模型字段不翻译_由字段规则控制()
    {
        var parser = new Infrastructure.Parsing.JsonGameFileParser();
        Assert.False(parser.IsTranslatableField("model"));
        Assert.False(parser.IsTranslatableField("dataList.$.model"));
        Assert.True(parser.IsTranslatableField("content"));
        Assert.True(parser.IsTranslatableField("dataList.$.content"));
        Assert.True(parser.IsTranslatableField("dataList.$.levelList.$.desc"));
    }
}
