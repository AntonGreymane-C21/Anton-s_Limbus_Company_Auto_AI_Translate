using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 提取清单（第9.0C.22轮）：记录"上一次提取到底拿了哪些文件"。
///
/// 为什么需要：勾选状态只存在于当前会话内存里，重启程序就回到"默认全选"。
/// 提取清单落在 <c>data/work/pending/.limbus-extract.manifest.json</c>，
/// 因此重开程序后可以一键把勾选**恢复到上次提取的那批文件**（"接着上次的活干"）。
///
/// 只保存逻辑相对路径（如 <c>StoryData/S949A.json</c>）；损坏/缺失一律视为"没有清单"，不抛异常。
/// </summary>
public static class ExtractManifest
{
    /// <summary>清单文件名（相对待提取目录）。</summary>
    public const string FileName = ".limbus-extract.manifest.json";

    /// <summary>清单内容。</summary>
    private sealed record Payload(string? CreatedAtUtc, string? Target, IReadOnlyList<string> Files);

    /// <summary>写入清单（失败只记录，不影响提取结果）。</summary>
    public static void Save(string pendingDirectory, IEnumerable<string> logicalFiles, Action<string>? log = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pendingDirectory))
            {
                return;
            }

            Directory.CreateDirectory(pendingDirectory);
            var payload = new Payload(
                DateTime.UtcNow.ToString("O"),
                pendingDirectory,
                logicalFiles.Where(file => !string.IsNullOrWhiteSpace(file)).Distinct(StringComparer.Ordinal).ToList());

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(pendingDirectory, FileName), json);
        }
        catch (Exception ex)
        {
            log?.Invoke($"[调试] 提取清单写入失败（不影响提取结果）：{ex.Message}");
        }
    }

    /// <summary>读取清单；文件不存在 / 损坏 / 无有效条目 ⇒ 返回 null。</summary>
    public static IReadOnlyList<string>? TryLoad(string pendingDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(pendingDirectory))
            {
                return null;
            }

            var path = Path.Combine(pendingDirectory, FileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path));
            var files = payload?.Files?
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return files is { Count: > 0 } ? files : null;
        }
        catch
        {
            return null;   // 损坏清单 = 没有清单（调用方给用户明确提示即可）
        }
    }
}
