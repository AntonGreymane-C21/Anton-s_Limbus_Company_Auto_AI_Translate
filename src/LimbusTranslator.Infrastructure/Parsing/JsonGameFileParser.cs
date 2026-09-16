using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Parsing;

public sealed class JsonGameFileParser : IFileParser
{
    public const string PathSeparator = ".";

    private readonly FieldRules _rules;

    public JsonGameFileParser(string? configDir = null)
    {
        _rules = FieldRulesLoader.Load(configDir);
    }

    public IReadOnlyList<TranslationUnit> Parse(string filePath, string relativePath)
    {
        var units = new List<TranslationUnit>();
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("dataList", out var dataList) || dataList.ValueKind != JsonValueKind.Array)
        {
            return units;
        }

        var recordIndex = 0;
        foreach (var record in dataList.EnumerateArray())
        {
            var recordId = GetRecordId(record);
            ExtractRecord(record, relativePath, recordId, recordIndex, "dataList[" + recordIndex + "]", units);
            recordIndex++;
        }

        return units;
    }

    private void ExtractRecord(
        JsonElement element,
        string relativePath,
        string recordId,
        int order,
        string pathPrefix,
        List<TranslationUnit> units)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in element.EnumerateObject())
        {
            var fieldPath = $"{pathPrefix}.{property.Name}";

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    if (IsTranslatableField(fieldPath))
                    {
                        units.Add(new TranslationUnit
                        {
                            Key = new UnitKey
                            {
                                RelativeFilePath = relativePath,
                                RecordId = recordId,
                                FieldPath = fieldPath,
                            },
                            FilePath = relativePath,
                            RecordId = recordId,
                            FieldPath = fieldPath,
                            SourceText = property.Value.GetString() ?? string.Empty,
                            Speaker = GetSpeaker(element),
                            Order = order,
                        });
                    }
                    break;

                case JsonValueKind.Object:
                    ExtractRecord(property.Value, relativePath, recordId, order, fieldPath, units);
                    break;

                case JsonValueKind.Array:
                    var arrayIndex = 0;
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        var childPath = $"{fieldPath}[{arrayIndex}]";
                        ExtractRecord(item, relativePath, recordId, order, childPath, units);
                        arrayIndex++;
                    }
                    break;
            }
        }
    }

    private static string GetRecordId(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("id", out var id))
        {
            return id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString() ?? string.Empty,
                _ => string.Empty,
            };
        }
        return string.Empty;
    }

    private static string? GetSpeaker(JsonElement record)
    {
        if (record.ValueKind == JsonValueKind.Object)
        {
            if (record.TryGetProperty("teller", out var teller) && teller.ValueKind == JsonValueKind.String)
            {
                return teller.GetString();
            }
            if (record.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
            {
                return model.GetString();
            }
        }
        return null;
    }

    public bool IsTranslatableField(string fieldPath)
    {
        var lastSegment = fieldPath.Split(PathSeparator, StringSplitOptions.RemoveEmptyEntries)[^1];
        var fieldName = lastSegment;
        var bracketIdx = fieldName.IndexOf('[');
        if (bracketIdx > 0)
        {
            fieldName = fieldName[..bracketIdx];
        }

        return _rules.IsTranslatable(fieldName);
    }
}