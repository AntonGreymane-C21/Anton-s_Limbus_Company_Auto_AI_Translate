using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Cli;

/// <summary>
/// 第9.0B-P6轮：真实 DeepSeek 8 条端到端 Smoke（**显式命令**，绝不在 dotnet test 中运行）。
///
/// 安全边界（全部代码层强制）：
///   1. 真实翻译单元 &lt;= 8、真实网络请求 &lt;= 8（<see cref="SmokeBudget"/>：越界**先抛后发**）；
///   2. 全链路 TEMP 隔离（TM / request_cache / Snapshot / Trace / output 全部写 TEMP 根）；
///   3. 绝不调用 DeployService、绝不写游戏目录、绝不触碰生产 data/；
///   4. 绝不打印 / 记录 API Key 与 Authorization（凭据只以 true/false 记录）。
///
/// 三个阶段：
///   Stage1 真实网络（8 单元）；Stage2 TM Replay（新增网络 0）；Stage3 RequestCache Replay（新增网络 0）。
/// </summary>
internal static partial class P6RealSmokeCommand
{
    // ── 人工 TEMP 测试文本（短句、可安全发送、可清楚区分来源语言） ──
    private const string KoreanOld = "안녕하세요";
    private const string KoreanNew = "반갑습니다";
    private const string KoreanStable = "감사합니다";
    private const string KoreanStableChanged = "고맙습니다";
    private const string EnglishOld = "Helloz";
    private const string EnglishNew = "Hello, friend";
    private const string EnglishNewUnit = "Hello {0}";
    private const string EnglishUnit3 = "Hi {0}";
    private const string EnglishUnit4 = "Thank you";
    private const string JapaneseUnit5 = "こんにちは、友よ";
    private const string OldChinese3 = "旧译文甲";
    private const string OldChinese5 = "旧译文乙";
    private const string OldChinese7 = "旧译文丙";

    /// <summary>真实 DeepSeek 8 条 Smoke 入口（仅由 CLI 显式 flag 调用）。</summary>
    public static async Task<int> RunAsync(string projectRoot, string? runId)
    {
        var configDir = Path.Combine(projectRoot, "config");
        Console.WriteLine("[调试] ===== 第9.0B-P6 真实 DeepSeek 8 条 E2E Smoke =====");
        Console.WriteLine($"[调试] 硬上限：单元 <= {SmokeBudget.MaxTranslationUnits}，真实网络请求 <= {SmokeBudget.MaxNetworkRequests}");

        // ① 凭据检测（fail-closed；只输出布尔，永不打印 Key）
        var settings = AppSettingsLoader.LoadProviderSettings(configDir);
        var credentialDetected = settings.Success
                                 && settings.Mode == TranslationProviderMode.DeepSeek
                                 && !string.IsNullOrWhiteSpace(settings.Options.ApiKey);
        Console.WriteLine($"[调试] API credential detected = {credentialDetected}（provider={settings.Mode}）");
        if (!credentialDetected)
        {
            Console.WriteLine("[错误] BLOCKED_NO_API_CREDENTIAL：未检测到有效 DeepSeek 凭据（或 provider 不是 deepseek）。已停止，未调用任何 API。");
            foreach (var error in settings.Errors)
            {
                Console.WriteLine($"[错误]   {error}");
            }

            return 3;
        }

        // Smoke 强制 MaxRetry = 0：保证「1 次逻辑调用 = 1 次 HTTP 尝试」，硬上限才可精确计数（生产值打印对比）
        var productionMaxRetry = settings.Options.MaxRetry;
        var options = new DeepSeekOptions
        {
            ApiUrl = settings.Options.ApiUrl,
            ApiKey = settings.Options.ApiKey,
            Model = settings.Options.Model,
            Thinking = settings.Options.Thinking,
            ThinkingMode = settings.Options.ThinkingMode,
            ReasoningEffort = settings.Options.ReasoningEffort,
            Temperature = settings.Options.Temperature,
            MaxTokens = settings.Options.MaxTokens,
            TimeoutSeconds = settings.Options.TimeoutSeconds,
            MaxConcurrentRequests = 1,
            MaxRetry = 0,
        };

        Console.WriteLine($"[调试] Provider={DeepSeekTranslationProvider.ProviderName}｜Model={options.Model}｜Host={new Uri(options.ApiUrl).Host}");
        Console.WriteLine($"[调试] MaxRetry：生产配置 {productionMaxRetry} → 本次 Smoke 强制 0（重试同样计入 8 次上限，故不允许自动重试风暴）");

        var glossarySnapshot = ActiveGlossarySnapshot.LoadWithParatranz(
            configDir, AppSettingsLoader.LoadParatranz(configDir), out var glossaryMerge);
        Console.WriteLine($"[调试] 术语快照 {glossarySnapshot.Count} 条（hash={glossarySnapshot.SnapshotHash}）｜{glossaryMerge.Describe()}");

        var budget = new SmokeBudget();
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"limbus_real_smoke_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}"
            + (string.IsNullOrWhiteSpace(runId) ? string.Empty : "_" + runId));
        Directory.CreateDirectory(tempRoot);
        Console.WriteLine($"[调试] TEMP Root: {tempRoot}");

        var session = new SmokeSession(configDir, tempRoot, options, settings.Batch, glossarySnapshot, budget);
        try
        {
            // ② 构造 8 条矩阵（4 模式 × 2 单元）+ 建立 KR 基线（不调用 API）
            var phases = BuildPhases(session);
            Console.WriteLine($"[调试] 已准备 {phases.Count} 个模式子根，共 {phases.Sum(phase => phase.Units.Count)} 条单元");

            // ③ Stage 1：真实网络
            var stage1 = await RunStageAsync(session, phases, "stage1_real_network", StageExpectation.Network);

            // ④ Stage 2：TM Replay（复用同一 capture，保证指纹与 Stage1 完全一致）
            var stage2 = await RunStageAsync(session, phases, "stage2_tm_replay", StageExpectation.TmReplay);

            // ⑤ Stage 3：RequestCache Replay（只清空 TEMP TM 的 translations 行；request_cache 保留）
            var cleared = session.ClearTemporaryTranslations();
            Console.WriteLine($"[调试] Stage3 前清空 TEMP TM translations 行数: {cleared}（request_cache 保留）");
            var stage3 = await RunStageAsync(session, phases, "stage3_cache_replay", StageExpectation.CacheReplay);

            return session.Finish(phases, stage1, stage2, stage3);
        }
        catch (SmokeBudgetExceededException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("[错误] 真实 API Smoke 已因硬上限保护终止。");
            return 4;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 真实 API Smoke 失败: {ex.Message}");
            return 2;
        }
    }
}
