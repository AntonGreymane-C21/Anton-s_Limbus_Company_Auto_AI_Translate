using LimbusTranslator.Core.Models;

namespace LimbusTranslator.IntegrationTests.Support;

/// <summary>集成测试用的 DiffEntry 构造助手。</summary>
public static class TestEntries
{
    public static UnitKey Key(string file, string recordId, string fieldPath)
        => new() { RelativeFilePath = file, RecordId = recordId, FieldPath = fieldPath };

    public static DiffEntry Entry(
        string file,
        string recordId,
        string fieldPath,
        string? source,
        string? translation = null,
        TranslationSource? provenance = null,
        int order = 0,
        string? speaker = null)
        => new()
        {
            Key = Key(file, recordId, fieldPath),
            NewSourceText = source,
            OldSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Translation = translation,
            Provenance = provenance,
            Order = order,
            Speaker = speaker,
        };
}
