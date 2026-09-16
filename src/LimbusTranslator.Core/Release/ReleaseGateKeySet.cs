using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Release;

/// <summary>
/// 输出结构 Key 集（第9.0B-P4轮）：让 ReleaseGate 使用与 Merge **完全相同**的权威 Key 语义。
///
/// 语义：
///   ExpectedKeys = 权威结构（EN_ONLY → 当前英文；KR_EN / KR_JP / KR_ONLY → 当前韩文）里必须出现在 output 的 UnitKey；
///   OutputKeys   = Merge 实际写入 output 的 UnitKey（<c>OutputMergeResult.WrittenKeys</c>）；
///   Missing      = ExpectedKeys − OutputKeys；
///   Unexpected   = OutputKeys − ExpectedKeys（例如英文残留、当前韩文已删除的旧 Key）。
///
/// 与 <see cref="TranslationModePolicy.UsesCanonicalKoreanOutput"/> 同源：权威结构只由翻译模式决定，
/// 禁止 Merge 与 Gate 各自判断一次。
/// </summary>
public sealed class ReleaseGateKeySet
{
    /// <summary>权威结构里的期望 Key 集（必须写入 output）。</summary>
    public required IReadOnlyCollection<string> ExpectedKeys { get; init; }

    /// <summary>Merge 实际写入 output 的 Key 集。</summary>
    public IReadOnlyCollection<string> OutputKeys { get; init; } = Array.Empty<string>();

    /// <summary>权威来源可读名（"en" / "ko"；仅用于报告与日志）。</summary>
    public string AuthoritativeSourceCode { get; init; } = SourceLanguageHelper.ToCode(SourceLanguage.English);
}
