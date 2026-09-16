using System.Text;
using System.Text.RegularExpressions;
using LimbusTranslator.Infrastructure.Glossary;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 术语扫描结果。
/// </summary>
public sealed class TermScanCandidate
{
    /// <summary>扫描到的英文术语</summary>
    public required string OriginalText { get; init; }

    /// <summary>出现次数</summary>
    public required int OccurrenceCount { get; init; }

    /// <summary>来源文本数</summary>
    public required int SourceTextCount { get; init; }

    /// <summary>是否已经收录到主术语库</summary>
    public bool IsExisting { get; init; }

    /// <summary>已收录术语的译名；新术语为空</summary>
    public string ExistingTranslation { get; init; } = string.Empty;
}

/// <summary>
/// 术语扫描器。
///
/// 从英文文本中提取候选术语：
///   - 大写开头的专有名词/词组（如 "Blade Lineage"、"Ricardo"）
///   - 多词组合（如 "Mirror Dungeon"）
///   - 与主术语库做规范化比对，未收录术语优先显示
///   - 已收录多词术语的碎片词会被过滤，避免重复候选
/// </summary>
public static class TermScanner
{
    /// <summary>常见英文单词（避免把普通词当成术语）</summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "The", "And", "For", "With", "This", "That", "From", "When", "Your",
        "Their", "After", "Before", "Each", "Turn", "Start", "End", "Max",
        "Base", "Coin", "Power", "Level", "On", "In", "To", "Of", "At", "By",
        "Once", "Then", "Also", "Only", "Can", "Will", "Has", "Have", "Had",
        "But", "Not", "All", "Any", "One", "Two", "Three", "Five", "Ten",
        "You", "They", "She", "He", "It", "We", "I", "Attack", "Defense",
        "Damage", "Next", "Last", "Other", "Some", "Gain", "Lose", "Apply",
        "Inflict", "Take", "Deal", "Increase", "Decrease", "Reduce", "Raise",
        "Win", "Lose", "Win", "Hit", "Use", "Per", "Total", "Current", "Foe",
        "Foes", "Ally", "Allies", "Target", "Effect", "Value", "Count",
        "Potency", "Speed", "Resource", "Specific", "This", "Their", "Below",
        // 句首常见词
        "What", "How", "Why", "Where", "When", "Who", "Let", "Yeah", "Well",
        "Yes", "No", "Hah", "Oh", "Ah", "Okay", "Right", "Wait", "Look",
        "Please", "Maybe", "Ready", "Team", "House", "Research", "Season",
        "Obtained", "Radio", "Corridor", "Pass", "There", "Mommy", "Golden",
        "Corp", "Both", "Each", "Every", "Same", "Different", "New", "Old",
        "Good", "Bad", "Great", "Big", "Small", "High", "Low", "Plus", "Minus",
    };

    /// <summary>
    /// 常见界面词、状态词和描述词。
    /// 这些词即使句首大写，也通常不值得为 DeepSeek 单独生成术语解释。
    /// </summary>
    private static readonly HashSet<string> OrdinaryEnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alliance", "Astute", "August", "Banner", "Because", "Boughs", "Celebratory", "Central",
        "City", "Class", "Classical", "Command", "Compassion", "Completion", "Cradle", "Cultivation",
        "Dad", "Dignified", "Director", "Driver", "Dreams", "Even", "Everything", "Executive", "Free",
        "Greeting", "Guileful", "Hmm", "House", "Intelligent", "Knocking", "Laboratory", "League",
        "Level", "Liberation", "Lighthouse", "Limbus", "Lovely", "Madam", "Manager", "Mister", "Mom",
        "Naive", "Nightmares", "Nine", "Package", "Pass", "Path", "Perhaps", "Pinky", "Pilgrimage",
        "Proactive", "Rational", "Resourceful", "Rooftop", "Ruins", "Select", "Sinners", "Special", "Spiders",
        "Spring", "Stupid", "Subterranean", "Technology", "Team", "Ticket", "Title", "Unbending", "Window", "Rank",
        "Grade", "Chapter", "Episode", "Story", "Stage", "Battle", "Skill", "Identity", "Ego",
        "Reward", "Item", "Items", "Event", "Notice", "Mission", "Daily", "Weekly", "Monthly",
        "Update", "Version", "Normal", "Rare", "Common", "Basic", "Advanced", "Standard",
        "Maximum", "Minimum", "Previous", "Available", "Complete", "Completed", "Required",
        "Selected", "Loading", "Failed", "Success", "Error", "Unknown", "None", "Default",
        "Information", "Warning", "Confirm", "Cancel", "Close", "Open", "Save", "Delete", "Edit",
        "Copy", "Continue", "Retry", "Return", "Back", "First", "Main", "Additional", "Extra",
        "Amount", "Type", "Chance", "Rate", "Percent", "Bonus", "Cost", "Price", "Time", "Day",
        "Month", "Year", "Today", "Tomorrow", "Yesterday", "Morning", "Night", "Character", "Unit",
        "Member", "Player", "World", "Area", "District", "Street", "Room", "Door", "Gate", "Road",
        "Train", "Station", "Building", "Shop", "Store", "Restaurant", "Hospital", "School",
        "University", "Museum", "Park", "Forest", "Mountain", "River", "Sea", "Sky", "Sun", "Moon",
        "Star",
    };

    /// <summary>带在角色名前的普通描述词，例如 "Naive Faust"。</summary>
    private static readonly HashSet<string> DescriptivePrefixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Naive", "Stupid", "Guileful", "Lovely", "Brilliant", "Rational", "Dignified", "Astute",
        "Intelligent", "Proactive", "Golden", "Free", "Special", "New", "Old", "Resourceful",
        "Classical", "Mister", "Madam",
    };

    /// <summary>组织名称常见结尾；非普通前缀的组织词组仍应保留。</summary>
    private static readonly HashSet<string> OrganizationSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alliance", "Association", "Syndicate", "Office", "Company", "Corporation", "Group",
        "Society", "Family", "Church", "Order", "Workshop", "League",
    };

    /// <summary>和组织结尾组合后依旧明显是普通界面文案的前缀。</summary>
    private static readonly HashSet<string> OrdinaryOrganizationPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "New", "Old", "Special", "Free", "Main", "Other", "General", "Local", "National",
        "International",
    };

    /// <summary>
    /// 扫描候选术语并保留其是否已经收录的信息。
    /// 未收录术语始终排在已收录术语之前，再按出现次数降序排列。
    /// </summary>
    public static IReadOnlyList<TermScanCandidate> ScanDetailed(
        IEnumerable<string> texts,
        IReadOnlyDictionary<string, GlossaryEntry>? existingGlossary = null,
        int minOccurrence = 2,
        IEnumerable<string>? excludedTerms = null)
    {
        var glossaryByNormalizedTerm = BuildGlossaryLookup(existingGlossary);
        var normalizedExcludedTerms = BuildNormalizedTermSet(excludedTerms);
        var knownMultiWordTerms = glossaryByNormalizedTerm.Keys
            .Concat(normalizedExcludedTerms)
            .Where(term => SplitWords(term).Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var occurrence = new Dictionary<string, Counter>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var candidates = ExtractCandidates(text);
            foreach (var candidate in candidates)
            {
                var normalized = NormalizeTerm(candidate);
                if (IsExcludedTermOrPluralVariant(normalized, normalizedExcludedTerms)
                    || ShouldSkip(candidate, normalized, knownMultiWordTerms))
                {
                    continue;
                }

                if (!occurrence.TryGetValue(candidate, out var entry))
                {
                    entry = new Counter
                    {
                        OriginalText = candidate,
                        NormalizedText = normalized,
                    };
                    occurrence[candidate] = entry;
                }

                entry.Count++;
                entry.SourceTexts.Add(text);
            }
        }

        var entries = occurrence.Values
            .Where(entry => entry.Count >= minOccurrence)
            .ToList();

        return entries
            .Where(entry =>
            {
                // 术语库已有内容保留在列表末尾作对照；新术语则使用严格规则。
                if (glossaryByNormalizedTerm.ContainsKey(entry.NormalizedText))
                {
                    return true;
                }

                return IsCandidateWorthExplaining(entry, minOccurrence)
                    && !IsProperFragmentOfLongerCandidate(entry, entries);
            })
            .Select(entry =>
            {
                var isExisting = glossaryByNormalizedTerm.TryGetValue(entry.NormalizedText, out var glossaryEntry);
                return new TermScanCandidate
                {
                    OriginalText = entry.OriginalText,
                    OccurrenceCount = entry.Count,
                    SourceTextCount = entry.SourceTexts.Count,
                    IsExisting = isExisting,
                    ExistingTranslation = glossaryEntry?.Translation ?? string.Empty,
                };
            })
            .OrderBy(candidate => candidate.IsExisting)
            .ThenByDescending(candidate => candidate.OccurrenceCount)
            .ThenBy(candidate => candidate.OriginalText, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 兼容旧调用方：只返回未收录的候选术语。
    /// 新界面应使用 <see cref="ScanDetailed"/> 以便展示已收录状态。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, (int Occurrence, int FileCount)>> Scan(
        IEnumerable<string> texts,
        IReadOnlyDictionary<string, GlossaryEntry>? existingGlossary = null,
        int minOccurrence = 2,
        IEnumerable<string>? excludedTerms = null)
    {
        return ScanDetailed(texts, existingGlossary, minOccurrence, excludedTerms)
            .Where(candidate => !candidate.IsExisting)
            .Select(candidate => new KeyValuePair<string, (int, int)>(
                candidate.OriginalText,
                (candidate.OccurrenceCount, candidate.SourceTextCount)))
            .ToList();
    }

    /// <summary>
    /// 将术语转换为可比较的规范形式。
    /// 大小写、连续空白、连字符和不同破折号不会导致同一术语重复出现。
    /// </summary>
    public static string NormalizeTerm(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        normalized = Regex.Replace(normalized, @"[\s\-_‐‑‒–—]+", " ");
        return normalized;
    }

    private static Dictionary<string, GlossaryEntry> BuildGlossaryLookup(
        IReadOnlyDictionary<string, GlossaryEntry>? glossary)
    {
        var result = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        if (glossary is null)
        {
            return result;
        }

        foreach (var item in glossary)
        {
            var normalized = NormalizeTerm(item.Key);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result[normalized] = item.Value;
            }
        }

        return result;
    }

    private static HashSet<string> BuildNormalizedTermSet(IEnumerable<string>? terms)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (terms is null)
        {
            return result;
        }

        foreach (var term in terms)
        {
            var normalized = NormalizeTerm(term);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static bool IsExcludedTermOrPluralVariant(
        string normalizedCandidate,
        IReadOnlySet<string> normalizedExcludedTerms)
    {
        if (normalizedExcludedTerms.Contains(normalizedCandidate))
        {
            return true;
        }

        // 角色配置中的 "Faust" 与文本中的 "Fausts" 都是同一个已知角色，
        // 不应因为英文复数形式重新进入待解释术语。
        if (SplitWords(normalizedCandidate).Length != 1 || normalizedCandidate.Length < 4)
        {
            return false;
        }

        var singularCandidates = new List<string>();
        if (normalizedCandidate.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
        {
            singularCandidates.Add(normalizedCandidate[..^3] + "y");
        }

        if (normalizedCandidate.EndsWith("es", StringComparison.OrdinalIgnoreCase))
        {
            singularCandidates.Add(normalizedCandidate[..^2]);
        }

        if (normalizedCandidate.EndsWith('s'))
        {
            singularCandidates.Add(normalizedCandidate[..^1]);
        }

        return singularCandidates.Any(normalizedExcludedTerms.Contains);
    }

    /// <summary>
    /// 可变计数容器。
    /// </summary>
    private sealed class Counter
    {
        public required string OriginalText { get; init; }
        public required string NormalizedText { get; init; }
        public int Count;
        public readonly HashSet<string> SourceTexts = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从单条文本中提取候选术语。
    /// </summary>
    private static IEnumerable<string> ExtractCandidates(string text)
    {
        // 去掉富文本标签和占位符
        var cleaned = Regex.Replace(text, @"<[^>]+>", " ");
        cleaned = Regex.Replace(cleaned, @"\{[^}]+\}", " ");

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 多词大写词组（如 "Blade Lineage"、"Mirror Dungeon"）
        foreach (Match match in Regex.Matches(cleaned, @"\b[A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,2}\b"))
        {
            results.Add(match.Value.Trim());
        }

        // 2) 单个大写单词
        foreach (Match match in Regex.Matches(cleaned, @"\b[A-Z][a-z]{2,}\b"))
        {
            results.Add(match.Value.Trim());
        }

        return results;
    }

    /// <summary>
    /// 判断候选词是否应跳过。
    /// </summary>
    private static bool ShouldSkip(
        string candidate,
        string normalizedCandidate,
        IReadOnlyCollection<string> knownMultiWordTerms)
    {
        // 常见英文词跳过
        if (CommonWords.Contains(candidate))
        {
            return true;
        }

        // 长度过滤
        if (candidate.Length < 3 || candidate.Length > 40)
        {
            return true;
        }

        // 已收录的多词术语会同时匹配到单词碎片，例如 "Blade Lineage" / "Blade" / "Lineage"。
        // 完整术语仍会作为已收录项显示；碎片项不再污染新术语候选列表。
        return IsProperFragmentOfKnownTerm(normalizedCandidate, knownMultiWordTerms);
    }

    private static bool IsProperFragmentOfKnownTerm(string candidate, IReadOnlyCollection<string> knownMultiWordTerms)
    {
        var candidateWords = SplitWords(candidate);
        if (candidateWords.Length == 0)
        {
            return false;
        }

        foreach (var knownTerm in knownMultiWordTerms)
        {
            var knownWords = SplitWords(knownTerm);
            if (knownWords.Length <= candidateWords.Length)
            {
                continue;
            }

            for (var start = 0; start <= knownWords.Length - candidateWords.Length; start++)
            {
                var isMatch = true;
                for (var index = 0; index < candidateWords.Length; index++)
                {
                    if (!string.Equals(knownWords[start + index], candidateWords[index], StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = false;
                        break;
                    }
                }

                if (isMatch)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 严格模式：只把高频、明显像专名或固定称呼的词交给 DeepSeek 解释。
    /// 普通单词、界面短语和“形容词 + 角色名”不会再挤进候选列表。
    /// </summary>
    private static bool IsCandidateWorthExplaining(Counter entry, int minOccurrence)
    {
        var words = SplitWords(entry.NormalizedText);
        if (words.Length == 0)
        {
            return false;
        }

        if (words.Length == 1)
        {
            // 单词最容易误命中句首普通英文，因此生产扫描时要求至少出现 3 次。
            // minOccurrence=1 时保留兼容性，便于测试和手动小样本检查。
            return entry.Count >= (minOccurrence <= 1 ? 1 : Math.Max(minOccurrence, 3))
                && !OrdinaryEnglishWords.Contains(words[0]);
        }

        if (words.Length == 2 && DescriptivePrefixWords.Contains(words[0]))
        {
            return false;
        }

        if (IsOrdinaryPhrase(words))
        {
            return false;
        }

        return true;
    }

    private static bool IsOrdinaryPhrase(IReadOnlyList<string> words)
    {
        if (!words.All(word => CommonWords.Contains(word) || OrdinaryEnglishWords.Contains(word)))
        {
            return false;
        }

        // “Technology Liberation Alliance”等专属组织名仍有解释价值；
        // “New League”这类普通界面标签则不保留。
        return words.Count < 2
            || !OrganizationSuffixes.Contains(words[^1])
            || OrdinaryOrganizationPrefixes.Contains(words[0]);
    }

    private static bool IsProperFragmentOfLongerCandidate(
        Counter candidate,
        IReadOnlyCollection<Counter> allCandidates)
    {
        foreach (var possibleWholeTerm in allCandidates)
        {
            if (ReferenceEquals(candidate, possibleWholeTerm)
                || possibleWholeTerm.Count < Math.Ceiling(candidate.Count * 0.75d))
            {
                continue;
            }

            if (IsProperFragmentOfKnownTerm(
                    candidate.NormalizedText,
                    new[] { possibleWholeTerm.NormalizedText }))
            {
                return true;
            }
        }

        return false;
    }

    private static string[] SplitWords(string value)
        => value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
