using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 翻译任务启动结果（第7轮）。
/// </summary>
public sealed class TranslationRunBootstrapResult
{
    /// <summary>是否允许启动翻译（false 时 <see cref="Provider"/> 为 null）。</summary>
    public required bool CanStart { get; init; }

    /// <summary>创建好的 Provider（CanStart = false 时为 null）。</summary>
    public required ITranslationProvider? Provider { get; init; }

    /// <summary>实际使用的 Provider 类型。</summary>
    public required TranslationProviderMode Mode { get; init; }

    /// <summary>阻止启动的错误。</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>配置警告（不阻止启动）。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// 第8.875轮：本次运行固定的**术语快照**（Prompt 与 Validator 共用；Mock 模式为 null）。
    /// 运行中修改 config/glossary.json 不影响本快照，下一次运行才会读取新版本。
    /// </summary>
    public ActiveGlossarySnapshot? GlossarySnapshot { get; init; }

    /// <summary>第8.88轮：术语合并摘要（本地 + Paratranz）。</summary>
    public GlossaryMergeSummary? GlossaryMerge { get; init; }

    /// <summary>
    /// 第9.0B.3A轮：本次 Run 锁定的翻译模式（Run 开始时读取一次，中途配置变化不影响本 Run）。
    /// </summary>
    public TranslationMode TranslationMode { get; init; } = TranslationMode.EnglishOnly;

    /// <summary>是否为显式选择的模拟翻译。</summary>
    public bool IsMock => Mode == TranslationProviderMode.Mock;
}

/// <summary>
/// 翻译任务启动助手（第7轮）。
///
/// 唯一职责：把 <see cref="SettingsLoadResult"/> 变成「可启动的 Provider」或「明确的拒绝」。
/// 之所以共享一份实现，是为了让 WPF / CLI / 集成测试走同一条 fail-closed 路径：
///   配置无效 → 不创建任何 Provider（尤其不创建 Mock）→ 不创建 TM / request_cache → 直接终止。
///
/// 这不是 ProviderFactory：没有插件发现、没有工厂注册、没有多 Provider 抽象，
/// 只有「配置有效才启动」这一条安全规则。
/// </summary>
public static class TranslationRunBootstrap
{
    /// <summary>
    /// 根据配置结果创建 Provider。
    /// </summary>
    /// <param name="settings">严格配置结果（AppSettingsLoader.LoadProviderSettings）</param>
    /// <param name="configDir">配置目录（用于提示词 / 术语 / 角色风格）</param>
    /// <param name="cacheServices">request_cache / Trace 服务（null = 关闭）</param>
    /// <param name="log">调试日志回调</param>
    public static TranslationRunBootstrapResult CreateProvider(
        SettingsLoadResult settings,
        string? configDir = null,
        TranslationCacheServices? cacheServices = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var logFn = log ?? (_ => { });

        if (!settings.Success)
        {
            logFn("[错误] 无法启动翻译：翻译配置无效。");
            foreach (var error in settings.Errors)
            {
                logFn($"[错误]   配置错误：{error}");
            }

            logFn("[错误] 已停止翻译：不会自动改用模拟翻译（Mock）。");
            return new TranslationRunBootstrapResult
            {
                CanStart = false,
                Provider = null,
                Mode = settings.Mode,
                Errors = settings.Errors,
                Warnings = settings.Warnings,
            };
        }

        foreach (var warning in settings.Warnings)
        {
            logFn($"[调试] 配置提示：{warning}");
        }

        if (settings.Mode == TranslationProviderMode.Mock)
        {
            // 模拟翻译必须一眼可见，避免与真实 DeepSeek 结果混淆
            logFn("[调试] 当前翻译Provider：Mock（测试模式）");
            return new TranslationRunBootstrapResult
            {
                CanStart = true,
                Provider = new MockTranslationProvider(),
                Mode = TranslationProviderMode.Mock,
                Errors = settings.Errors,
                Warnings = settings.Warnings,
            };
        }

        logFn($"[调试] 当前翻译Provider：DeepSeek（模型 {settings.Options.Model}：{settings.Options.ApiUrl}）");

        // 第9.0B.3A轮：Run 开始时读取一次 translationMode（Fail-closed；一次 Run 固定）
        if (!AppSettingsLoader.TryLoadTranslationMode(configDir, out var translationMode, out var modeError))
        {
            logFn("[错误] 无法启动翻译：翻译模式配置无效。");
            logFn($"[错误]   {modeError}");
            logFn("[错误] 已停止翻译：不会自动回退默认模式。");
            return new TranslationRunBootstrapResult
            {
                CanStart = false,
                Provider = null,
                Mode = settings.Mode,
                Errors = new[] { modeError ?? "translationMode 配置无效。" },
                Warnings = settings.Warnings,
            };
        }

        logFn($"[调试] 翻译模式：{TranslationModeCodes.GetDisplayName(translationMode)}（{TranslationModeCodes.ToCode(translationMode)}）"
              + (TranslationModePolicy.UsesCanonicalKoreanDiff(translationMode) ? "｜使用韩文 Canonical Diff" : "｜使用旧英文 Diff"));

        // 第8.875轮：本次运行只创建一次术语快照，Prompt 与 Validator 共用同一术语版本
        // 第8.88轮：可选合并已同步的 Paratranz 远程术语（本地永远优先）
        var paratranzOptions = AppSettingsLoader.LoadParatranz(configDir);
        var glossarySnapshot = ActiveGlossarySnapshot.LoadWithParatranz(configDir, paratranzOptions, out var glossaryMerge);
        logFn(
            $"[调试] 术语快照：{glossarySnapshot.Count} 条，glossarySnapshotHash={glossarySnapshot.SnapshotHash}" +
            "（本次运行固定；运行中修改术语库只影响下一次运行）");
        logFn($"[调试] {glossaryMerge.Describe()}");
        foreach (var conflict in glossaryMerge.Conflicts.Take(3))
        {
            logFn($"[调试]   术语冲突：{conflict.Term} 远程={conflict.RemoteTarget} / 本地={conflict.LocalTarget} → 采用本地（{conflict.WinnerReason}）");
        }
        logFn(
            $"[调试] Provider 请求分批：{BatchOptions.MaxItemsFieldName}={settings.Batch.MaxItemsPerBatch}，" +
            $"{BatchOptions.MaxCharactersFieldName}={settings.Batch.MaxCharactersPerBatch}" +
            $"（{BatchOptions.TargetInputTokensFieldName}={settings.Batch.TargetInputTokens} 为保留位，当前不参与切分）");
        return new TranslationRunBootstrapResult
        {
            CanStart = true,
            Provider = new DeepSeekTranslationProvider(
                settings.Options, configDir, settings.Batch, cacheServices: cacheServices, log: logFn,
                glossarySnapshot: glossarySnapshot, translationMode: translationMode),
            Mode = TranslationProviderMode.DeepSeek,
            Errors = settings.Errors,
            Warnings = settings.Warnings,
            GlossarySnapshot = glossarySnapshot,
            TranslationMode = translationMode,
            GlossaryMerge = glossaryMerge,
        };
    }
}
