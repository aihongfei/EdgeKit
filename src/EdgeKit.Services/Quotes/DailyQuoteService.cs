using System.Text.Json;

namespace EdgeKit.Services.Quotes;

/// <summary>
/// 每日一言服务。从本地内嵌的 JSON 库中按分类随机抽取诗词/格言。
/// 本身无 UI 依赖、无网络请求，可安全注入到 ViewModel 中。
/// </summary>
public sealed class DailyQuoteService
{
    // 避免短期内重复抽取的最近记录上限。
    private const int MaxRecentCount = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyList<QuoteEntry> _entries;
    private readonly Random _random = new();
    private readonly HashSet<int> _recentHashes = new();
    private readonly Queue<int> _recentQueue = new();

    public DailyQuoteService()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Quotes", "quotes.json");
        _entries = LoadEntries(path);
    }

    /// <summary>可用分类列表，第一个是 "all"（全部）。</summary>
    public IReadOnlyList<string> Categories
    {
        get
        {
            var categories = _entries
                .Select(e => e.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            categories.Insert(0, "all");
            return categories;
        }
    }

    /// <summary>
    /// 随机抽取一条。category 为空或 "all" 时不限分类。
    /// 若对应分类无条目，返回 null。
    /// </summary>
    public DailyQuote? GetQuote(string? category = null)
    {
        var candidates = string.IsNullOrWhiteSpace(category)
            || category.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? _entries
            : _entries.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // 优先从最近未展示过的条目中抽取，提升多样性。
        var available = candidates.Where(e => !_recentHashes.Contains(GetHash(e))).ToList();
        var pool = available.Count > 0 ? available : candidates;

        var selected = pool[_random.Next(pool.Count)];
        TrackRecent(selected);

        return new DailyQuote(selected.Content, selected.Author, selected.Source, selected.Category);
    }

    private static IReadOnlyList<QuoteEntry> LoadEntries(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Array.Empty<QuoteEntry>();
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<QuoteEntry>>(json, JsonOptions) ?? new List<QuoteEntry>();
        }
        catch
        {
            return Array.Empty<QuoteEntry>();
        }
    }

    private void TrackRecent(QuoteEntry entry)
    {
        var hash = GetHash(entry);
        if (_recentHashes.Add(hash))
        {
            _recentQueue.Enqueue(hash);
            while (_recentQueue.Count > MaxRecentCount)
            {
                _recentHashes.Remove(_recentQueue.Dequeue());
            }
        }
    }

    private static int GetHash(QuoteEntry entry)
        => StringComparer.Ordinal.GetHashCode(entry.Content);
}

/// <summary>本地库中的原始条目。</summary>
public sealed record QuoteEntry(
    string Content,
    string? Author,
    string? Source,
    string Category);

/// <summary>对外返回的每日一言。</summary>
public sealed record DailyQuote(
    string Content,
    string? Author,
    string? Source,
    string Category)
{
    /// <summary>作者与出处文案，可能为空。</summary>
    public string AttributionText
    {
        get
        {
            var parts = new List<string? >(2);
            if (!string.IsNullOrWhiteSpace(Author))
            {
                parts.Add(Author);
            }

            if (!string.IsNullOrWhiteSpace(Source))
            {
                parts.Add(Source);
            }

            return parts.Count == 0 ? string.Empty : string.Join(" · ", parts);
        }
    }

    /// <summary>分类的友好中文名。</summary>
    public string CategoryDisplayName => Category.ToLowerInvariant() switch
    {
        "poem" => "诗词",
        "quote" => "格言",
        "dev" => "程序员",
        "all" => "全部",
        _ => Category
    };
}