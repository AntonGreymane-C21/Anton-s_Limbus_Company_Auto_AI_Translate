using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.2轮：**锁定术语强制执行与自动修正**的 Agent 级回归。
///
/// 全部走真实生产链（生产计划 → ApplyToEntries → Coordinator → TranslationAgent →
/// 真实 DeepSeekTranslationProvider + FakeE2EBatchClient → 真实 SQLite TM / request_cache → Trace），
/// 真实网络请求恒为 0。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class LockedTerminologyRepairTests : IDisposable
{
    private const string KoreanA = "핑키 너스파더";
    private const string KoreanB = "핑키 너스파더가 나타났다";
    private const string EnglishSource = "Pinky Nursefather";
    private const string Fixed = "粉红护父";
    private const string Violating = "粉红护士长";

    private readonly FourModeAgentE2EHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static Func<TranslationCacheServices, ITranslationProvider> Factory(
        FakeE2EBatchClient client,
        TranslationMode mode)
        => services => new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-e2e-fake",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "e2e-test-model",
                MaxRetry = 0,
            },
            configDir: null,
            batchOptions: null,
            cacheServices: services,
            client: client,
            translationMode: mode);

    private static ActiveGlossarySnapshot Glossary(params (string Term, string Translation, bool Locked)[] terms)
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, translation, locked) in terms)
        {
            entries[term] = new GlossaryEntry { Translation = translation, Locked = locked };
        }

        return ActiveGlossarySnapshot.FromEntries(entries, sourcePath: null);
    }

    private MultilingualCaptureResult CaptureKrChanged(string english = EnglishSource)
        => _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { english }, null),
            new E2ESources(new[] { KoreanB }, new[] { english }, null));

    private static DiffEntry Single(DiffEntry[] entries, AgentE2ERun run) => Assert.Single(run.Plan.OutputEntries);

    private static bool HasIssue(DiffEntry entry, string code)
        => entry.ValidationIssues.Any(issue => issue.Code == code);

    // ───────── §三十一 Nursefather 真实问题回归 ─────────

    [Fact]
    public async Task 锁定术语_首次违反时自动修正一次并采用修正结果()
    {
        var capture = CaptureKrChanged();
        var client = new FakeE2EBatchClient
        {
            // 第 0 次 = 首次翻译（违反锁定术语）；第 1 次 = 自动修正
            ScriptedTranslation = (index, _) => index == 0 ? Violating : Fixed,
        };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        // ① 一次翻译 + 一次修正 = 2 次请求
        Assert.Equal(2, client.BatchCallCount);

        // ② 最终采用修正后的译文
        Assert.Equal(Fixed, entry.Translation);
        Assert.DoesNotContain("护士长", entry.Translation);

        // ③ 术语违规消失，且不再因为该术语要求人工确认
        Assert.False(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));
        Assert.False(entry.NeedsReview);

        // ④ 修正过程被记录
        Assert.Equal(1, entry.TerminologyRepairAttempts);
        Assert.True(entry.TerminologyRepairSucceeded);
        Assert.Equal("已自动修正锁定术语", entry.TerminologyRepairNote);

        // ⑤ 修正请求携带当前译文与锁定术语，且 Thinking 关闭
        var repairItem = Assert.Single(client.RepairItems);
        Assert.Equal(Violating, repairItem.LockedTerms is null ? null : repairItem.CurrentTranslation);
        Assert.Equal("Nursefather → 护父", repairItem.LockedTerms);
        Assert.False(Assert.Single(client.Thinkings.Skip(1))!.Enabled);
    }

    [Fact]
    public async Task 锁定术语_修正仍失败时不无限重试并转人工审核()
    {
        var capture = CaptureKrChanged();
        var client = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => Violating };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(2, client.BatchCallCount);          // 只允许 1 次自动修正，不得第 3 次
        Assert.Equal(1, entry.TerminologyRepairAttempts);
        Assert.False(entry.TerminologyRepairSucceeded);
        Assert.Equal(Violating, entry.Translation);      // 保留修正后的模型输出（此处仍违规）
        Assert.True(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));
        Assert.True(entry.NeedsReview);
    }

    [Fact]
    public async Task 锁定术语_首次即遵守则不修正()
    {
        var capture = CaptureKrChanged();
        var client = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => Fixed };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(1, client.BatchCallCount);          // 不得无意义追加请求
        Assert.Equal(0, entry.TerminologyRepairAttempts);
        Assert.False(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));
        Assert.False(entry.NeedsReview);
    }

    [Fact]
    public async Task 锁定术语_Preferred不触发强制修正()
    {
        var capture = CaptureKrChanged();
        var client = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => Violating };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            // Locked = false ⇒ 软约束
            glossarySnapshot: Glossary(("Nursefather", "护父", false)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(1, client.BatchCallCount);
        Assert.Equal(0, entry.TerminologyRepairAttempts);
        Assert.False(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));
    }

    // ───────── §三十五 多锁定术语一次修正 / §三十六 占位符安全 / §三十七 结构破坏回滚 ─────────

    [Fact]
    public async Task 锁定术语_多锁定术语一次修完()
    {
        var capture = CaptureKrChanged();
        var client = new FakeE2EBatchClient
        {
            ScriptedTranslation = (index, _) => index == 0 ? Violating : "小指护父",
        };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(
                ("Nursefather", "护父", true),
                ("Pinky", "小指", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(2, client.BatchCallCount);              // 一次修正处理全部违规（不是 2 次）
        Assert.Equal(1, entry.TerminologyRepairAttempts);
        Assert.Equal("小指护父", entry.Translation);
        Assert.False(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));

        var repairItem = Assert.Single(client.RepairItems);
        Assert.Contains("Nursefather → 护父", repairItem.LockedTerms);
        Assert.Contains("Pinky → 小指", repairItem.LockedTerms);
    }

    [Fact]
    public async Task 锁定术语_修正不得破坏占位符与数字()
    {
        const string withPlaceholder = "{0} Nursefather deals +10% damage.";
        var capture = CaptureKrChanged(withPlaceholder);
        var client = new FakeE2EBatchClient
        {
            // 用请求里的（已保护）源文构造响应，确保占位符标记原样带回
            ScriptedTranslation = (index, item) => index == 0 ? item.Source! : item.Source + " 护父",
        };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(2, client.BatchCallCount);
        Assert.Contains("护父", entry.Translation);
        Assert.Contains("+10%", entry.Translation);
        Assert.False(HasIssue(entry, ValidationIssueCodes.PlaceholderMismatch));
        Assert.False(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));
    }

    [Fact]
    public async Task 锁定术语_修正引入结构错误时回滚原译文()
    {
        const string withPlaceholder = "{0} Nursefather deals +10% damage.";
        var capture = CaptureKrChanged(withPlaceholder);
        var client = new FakeE2EBatchClient
        {
            // 修正结果丢失占位符 ⇒ 必须被 PlaceholderValidator 拦住并回滚
            ScriptedTranslation = (index, item) => index == 0 ? item.Source! : "护父造成伤害",
        };

        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(client, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(2, client.BatchCallCount);
        Assert.Equal(1, entry.TerminologyRepairAttempts);              // 确实尝试过 1 次修正
        Assert.False(entry.TerminologyRepairSucceeded);
        Assert.Contains("回滚", entry.TerminologyRepairNote);
        Assert.Equal(withPlaceholder, entry.Translation);              // 回滚为原译文
        Assert.True(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));
        Assert.Contains(entry.ValidationIssues, issue => issue.Validator == nameof(LockedTerminologyRepairService));
        Assert.True(entry.NeedsReview);
    }

    // ───────── §三十八 TM 命中仍受最新锁定术语约束 ─────────

    [Fact]
    public async Task 锁定术语_TM命中仍按最新术语修正()
    {
        var capture = CaptureKrChanged();

        // 第一次：修正也失败 ⇒ TM 存入违反锁定术语的译文（模拟历史遗留数据），
        //        同时把「翻译请求」「修正请求」的响应一起写进 request_cache。
        var firstClient = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => Violating };
        var first = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(firstClient, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));
        Assert.Equal(Violating, Assert.Single(first.Plan.OutputEntries).Translation);

        // 第二次 · 同术语：TM 命中（不重新翻译），但仍按**当前**术语规则走校验 + 修正闭环；
        //              该修正请求与第一次完全相同 ⇒ 修正响应由 request_cache 复用（0 次网络请求）。
        var sameGlossaryClient = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => Fixed };
        var sameGlossary = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(sameGlossaryClient, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护父", true)));

        var cachedEntry = Assert.Single(sameGlossary.Plan.OutputEntries);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, cachedEntry.TmMatchType);  // 确实是 TM 命中
        Assert.Equal(0, sameGlossaryClient.BatchCallCount);                           // 翻译与修正都未再出网
        Assert.Empty(sameGlossaryClient.TranslateItems);                              // 没有重新翻译
        Assert.Equal(1, cachedEntry.TerminologyRepairAttempts);                       // 仍然执行了修正闭环

        // 第三次 · 术语变更（护父 → 护工之父）：TM 仍命中，但修正请求的指纹必须变化 ⇒ 缓存 Miss ⇒ 真实请求
        var changedClient = new FakeE2EBatchClient { ScriptedTranslation = (_, _) => "粉红护工之父" };
        var changed = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            Factory(changedClient, TranslationMode.KoreanEnglish),
            glossarySnapshot: Glossary(("Nursefather", "护工之父", true)));

        var changedEntry = Assert.Single(changed.Plan.OutputEntries);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, changedEntry.TmMatchType);
        Assert.Equal(1, changedClient.BatchCallCount);                                 // 旧修正缓存没有误命中
        Assert.Empty(changedClient.TranslateItems);
        Assert.Equal("粉红护工之父", changedEntry.Translation);
        Assert.Equal("Nursefather → 护工之父", Assert.Single(changedClient.RepairItems).LockedTerms);
        Assert.False(HasIssue(changedEntry, ValidationIssueCodes.TerminologyMismatch));
    }

    // ───────── §十八 / §十九 修正请求独立指纹（不串翻译缓存、术语变化即失效） ─────────

    [Fact]
    public void 修正请求指纹_与翻译请求不同且对术语内容敏感()
    {
        var key = new UnitKey { RelativeFilePath = "Items.json", RecordId = "1", FieldPath = "dataList[0].name" };
        var prompt = new PromptOptions { SystemPrompt = "系统提示", OutputFormat = "输出格式" };

        RequestFingerprint BuildRepair(string snapshotHash, string lockedTermsText)
            => RequestFingerprintBuilder.Build(new RequestFingerprintPayload
            {
                Provider = DeepSeekTranslationProvider.ProviderName,
                ProviderIdentity = "https://api.deepseek.com",
                Model = "m",
                SystemPrompt = DeepSeekRequestComposer.BuildSystemPrompt(
                    prompt, string.Empty, string.Empty, includeRepairRule: true),
                UserContent = DeepSeekRequestComposer.BuildUserContent("Repair001", new[]
                {
                    new DeepSeekTranslateRequestItem
                    {
                        Id = key.ToString(),
                        Source = "Pinky Nursefather",
                        CurrentTranslation = Violating,
                        LockedTerms = lockedTermsText,
                    },
                }),
                RequestKind = LockedTerminologyCheck.RepairReasonKind,
                RepairCurrentTranslation = Violating,
                RepairLockedTerms = lockedTermsText,
                GlossarySnapshotHash = snapshotHash,
                Items = new[]
                {
                    new RequestFingerprintItem
                    {
                        Id = "x",
                        UnitKey = key.ToString(),
                        TranslationMode = TranslationAction.TranslateModified.ToString(),
                        Source = "Pinky Nursefather",
                    },
                },
            });

        var translate = RequestFingerprintBuilder.Build(new RequestFingerprintPayload
        {
            Provider = DeepSeekTranslationProvider.ProviderName,
            ProviderIdentity = "https://api.deepseek.com",
            Model = "m",
            SystemPrompt = DeepSeekRequestComposer.BuildSystemPrompt(prompt, string.Empty, string.Empty),
            UserContent = DeepSeekRequestComposer.BuildUserContent("Batch001", new[]
            {
                new DeepSeekTranslateRequestItem { Id = key.ToString(), Source = "Pinky Nursefather" },
            }),
            Items = new[]
            {
                new RequestFingerprintItem
                {
                    Id = "x",
                    UnitKey = key.ToString(),
                    TranslationMode = TranslationAction.TranslateModified.ToString(),
                    Source = "Pinky Nursefather",
                },
            },
        });

        var repairA = BuildRepair("snapshot-a", "Nursefather → 护父");
        var repairB = BuildRepair("snapshot-b", "Nursefather → 护工之父");

        Assert.NotEqual(translate.Value, repairA.Value);        // 翻译请求与修正请求不互相命中
        Assert.NotEqual(repairA.Value, repairB.Value);          // 术语变化 ⇒ 旧修正缓存失效
        Assert.Contains("requestKind", repairA.CanonicalJson);
        Assert.Contains("repairLockedTerms", repairA.CanonicalJson);
        Assert.Contains("glossarySnapshotHash", repairA.CanonicalJson);
        // 常规翻译请求的 canonical JSON 必须保持历史形态（否则用户已有 v3 缓存会全部失效）
        Assert.DoesNotContain("requestKind", translate.CanonicalJson);
        Assert.DoesNotContain("repairLockedTerms", translate.CanonicalJson);
    }

    // ───────── §二十四 / §三十二 四模式均支持锁定术语修正 ─────────

    [Theory]
    [InlineData(TranslationMode.EnglishOnly)]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task 锁定术语_四模式均支持自动修正且模式不被改写(TranslationMode mode)
    {
        // EN_ONLY / KR_EN 的选择源是英文 ⇒ 用英文锁定术语（真实 Nursefather 场景）；
        // KR_JP / KR_ONLY 的选择源是韩文 ⇒ 用韩文锁定术语（锁定术语来源不一定是英文）。
        var (term, sourceEnglish, sourceJapanese) = mode switch
        {
            TranslationMode.KoreanJapanese => ("너스파더", EnglishSource, KoreanB),
            TranslationMode.KoreanOnly => ("너스파더", EnglishSource, null),
            _ => ("Nursefather", EnglishSource, null),
        };

        var client = new FakeE2EBatchClient
        {
            ScriptedTranslation = (index, _) => index == 0 ? Violating : Fixed,
        };

        AgentE2ERun run;
        if (mode == TranslationMode.EnglishOnly)
        {
            _harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { sourceEnglish }, null));
            var capture = _harness.Capture();
            run = await _harness.RunProductionChainAsync(
                mode, capture, oldChinese: null, Factory(client, mode),
                oldEnglish: new[] { sourceEnglish }, newEnglish: new[] { sourceEnglish },
                glossarySnapshot: Glossary((term, "护父", true)));
        }
        else
        {
            var japanese = sourceJapanese is null ? null : new[] { sourceJapanese };
            var capture = _harness.CaptureAfterBaseline(
                new E2ESources(new[] { KoreanA }, new[] { sourceEnglish }, japanese),
                new E2ESources(new[] { KoreanB }, new[] { sourceEnglish }, japanese));
            run = await _harness.RunProductionChainAsync(
                mode, capture, oldChinese: null, Factory(client, mode),
                glossarySnapshot: Glossary((term, "护父", true)));
        }

        var entry = Assert.Single(run.Plan.OutputEntries);

        Assert.Equal(2, client.BatchCallCount);
        Assert.Equal(1, entry.TerminologyRepairAttempts);
        Assert.Equal(Fixed, entry.Translation);
        Assert.False(HasIssue(entry, ValidationIssueCodes.TerminologyMismatch));

        // 模式不被改写：两次请求都使用同一个模式
        Assert.All(client.Modes, used => Assert.Equal(mode, used));

        // 修正请求的 Canonical 语义与模式一致（EN_ONLY 绝不携带韩文）
        var repairItem = Assert.Single(client.RepairItems);
        if (TranslationModePolicy.SendsKorean(mode))
        {
            Assert.False(string.IsNullOrWhiteSpace(repairItem.CanonicalKorean));
        }
        else
        {
            Assert.Null(repairItem.CanonicalKorean);
        }
    }
}

