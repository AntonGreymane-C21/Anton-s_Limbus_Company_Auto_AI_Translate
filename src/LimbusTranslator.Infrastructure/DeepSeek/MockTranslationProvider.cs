using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Placeholder;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// 模拟翻译提供者（开发/演示用）。
/// 无 API Key 时使用：把英文文本包一层假译文，便于验证整个翻译流水线。
/// 同样执行 Placeholder 保护，验证保护/恢复流程。
/// 生产环境应使用 DeepSeekTranslationProvider。
/// </summary>
public sealed class MockTranslationProvider : ITranslationProvider
{
    private readonly PlaceholderProtector _protector = new();

    public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
        IReadOnlyList<DiffEntry> entries,
        CancellationToken cancellationToken = default,
        string? stageId = null,
        IReadOnlyDictionary<string, TranslationContext>? contexts = null)
    {
        var results = new Dictionary<string, TranslationResult>();
        foreach (var entry in entries)
        {
            var text = entry.NewSourceText ?? entry.OldSourceText ?? string.Empty;

            // 保护 → 模拟翻译 → 恢复
            var protectedText = _protector.Protect(text);
            var fakeProtected = string.IsNullOrWhiteSpace(protectedText.ProtectedText)
                ? "(空)"
                : $"[译]{protectedText.ProtectedText}";
            var fake = _protector.Restore(fakeProtected, protectedText);

            results[entry.Key.ToString()] = new TranslationResult
            {
                Key = entry.Key,
                Translation = fake,
                // 第1轮：模拟翻译必须标记为 Mock，不得冒充 AI（Provenance 需要可区分）
                Source = TranslationSource.Mock,
                NeedsReview = false,
            };
        }
        return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
    }
}
