using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EdgeKit.App.Commands;
using EdgeKit.Core.Tools;

namespace EdgeKit.App.Search;

/// <summary>
/// 统一搜索引擎：合并软件内置工具与本机已安装应用，支持
/// 不区分大小写子串匹配，以及中文标题的全拼 / 拼音首字母匹配
/// （如输入 "wx" 命中 "微信"，输入 "weixin" 同样命中）。
/// 结果按匹配质量打分排序，工具优先，限制返回条数。
/// </summary>
public sealed class SearchEngine
{
    private const int MaxResults = 12;
    private const int MaxSearchQueryLength = 256;
    private const int MaxDirectActionTextLength = 2048;

    // 不参与搜索的工具（如首页本身）。
    private static readonly HashSet<string> ExcludedToolIds =
        new(StringComparer.OrdinalIgnoreCase) { "home.dashboard" };

    private readonly IToolCatalog _catalog;
    private readonly InstalledAppIndex _appIndex;
    private readonly CommandRegistry _commandRegistry;

    // 工具候选预计算缓存（标题不会变，构造时算一次）。
    private readonly List<Candidate> _toolCandidates;

    public SearchEngine(IToolCatalog catalog, InstalledAppIndex appIndex, CommandRegistry commandRegistry)
    {
        _catalog = catalog;
        _appIndex = appIndex;
        _commandRegistry = commandRegistry;

        _toolCandidates = _catalog.GetAll()
            .Where(t => !ExcludedToolIds.Contains(t.Id) && t.Id != "command.palette")
            .Select(t => Candidate.ForTool(t))
            .ToList();
    }

    /// <summary>
    /// 按查询词检索。空查询返回空列表。结果工具优先、按分值降序，限制条数。
    /// </summary>
    public IReadOnlyList<SearchItem> Search(string query)
    {
        var normalized = NormalizeQuery(query);
        if (normalized.Length == 0)
        {
            return Array.Empty<SearchItem>();
        }

        var directAction = TryCreateDirectAction(normalized);
        if (directAction is not null)
        {
            return new[] { directAction };
        }

        if (normalized.Length > MaxSearchQueryLength)
        {
            return Array.Empty<SearchItem>();
        }

        var q = normalized.ToLowerInvariant();
        var scored = new List<(Candidate Candidate, int Score)>();

        foreach (var c in _toolCandidates)
        {
            var score = c.Match(q);
            if (score > 0)
            {
                scored.Add((c, score));
            }
        }

        // 应用候选每次按当前索引快照实时构建（索引可能尚未加载完）。
        foreach (var app in _appIndex.Apps)
        {
            var c = Candidate.ForApp(app);
            var score = c.Match(q);
            if (score > 0)
            {
                scored.Add((c, score));
            }
        }

        foreach (var command in _commandRegistry.GetAll())
        {
            var c = Candidate.ForCommand(command);
            var score = c.Match(q);
            if (score > 0)
            {
                scored.Add((c, score));
            }
        }

        return scored
            // 应用优先，其次命令，再工具；每组内按匹配质量排序。
            .OrderBy(s => s.Candidate.Kind switch
            {
                SearchItemKind.App => 0,
                SearchItemKind.Command => 1,
                SearchItemKind.Tool => 2,
                _ => 3
            })
            .ThenByDescending(s => s.Score)
            .ThenBy(s => s.Candidate.Title.Length)
            .Take(MaxResults)
            .Select(s => s.Candidate.ToSearchItem())
            .ToList();
    }

    private static string NormalizeQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return string.Empty;
        }

        var trimmed = query.Trim().Trim('"');
        if (trimmed.Length > MaxDirectActionTextLength)
        {
            return trimmed[..MaxDirectActionTextLength];
        }

        return trimmed;
    }

    public static SearchItem? TryCreateDirectAction(string query)
    {
        if (TryNormalizeFileUri(query, out var fileUriPath))
        {
            return CreatePathAction(fileUriPath);
        }

        if (TryNormalizeWebUri(query, out var uri))
        {
            return new SearchItem(SearchItemKind.Web, "打开网页", uri, "\uE774", iconImage: null, payload: uri);
        }

        if (LooksLikeFileSystemPath(query))
        {
            return CreatePathAction(query);
        }

        return null;
    }

    private static SearchItem? CreatePathAction(string path)
    {
        path = path.Trim().Trim('"');
        if (File.Exists(path))
        {
            return new SearchItem(SearchItemKind.File, "打开文件位置", path, "\uE8A5", iconImage: null, payload: path);
        }

        if (Directory.Exists(path))
        {
            return new SearchItem(SearchItemKind.Folder, "打开文件夹", path, "\uE8B7", iconImage: null, payload: path);
        }

        return null;
    }

    private static bool TryNormalizeFileUri(string query, out string path)
    {
        path = string.Empty;
        if (!Uri.TryCreate(query, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return false;
        }

        path = uri.LocalPath;
        return !string.IsNullOrWhiteSpace(path);
    }

    private static bool TryNormalizeWebUri(string query, out string uri)
    {
        uri = string.Empty;
        var candidate = query;
        if (!candidate.Contains("://", StringComparison.Ordinal) && LooksLikeBareDomain(candidate))
        {
            candidate = "https://" + candidate;
        }

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        uri = parsed.AbsoluteUri;
        return true;
    }

    private static bool LooksLikeBareDomain(string value)
    {
        if (value.Contains(' ') || value.Contains('\\') || value.Contains('/'))
        {
            return false;
        }

        var dotIndex = value.LastIndexOf('.');
        return dotIndex > 0
            && dotIndex < value.Length - 2
            && value[(dotIndex + 1)..].All(char.IsLetter);
    }

    private static bool LooksLikeFileSystemPath(string value)
    {
        if (value.Length > 2 && char.IsLetter(value[0]) && value[1] == ':' &&
            (value[2] == '\\' || value[2] == '/'))
        {
            return true;
        }

        return value.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>
    /// 单个搜索候选。预计算标题的小写形式、全拼与拼音首字母，匹配时复用。
    /// </summary>
    private sealed class Candidate
    {
        private readonly SearchTextIndex _index;

        public SearchItemKind Kind { get; }
        public string Title { get; }
        private string SubTitle { get; }
        private string Glyph { get; }
        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? Icon { get; }
        private string Payload { get; }
        private string LocationPath { get; }

        private Candidate(
            SearchItemKind kind,
            string title,
            string subTitle,
            string glyph,
            Microsoft.UI.Xaml.Media.Imaging.BitmapImage? icon,
            string payload,
            string locationPath = "",
            IEnumerable<string>? keywords = null)
        {
            Kind = kind;
            Title = title;
            SubTitle = subTitle;
            Glyph = glyph;
            Icon = icon;
            Payload = payload;
            LocationPath = locationPath;
            _index = new SearchTextIndex(title, subTitle, keywords);
        }

        public static Candidate ForTool(ToolDescriptor t) =>
            new(SearchItemKind.Tool, t.Title, t.Description, t.Glyph, icon: null, payload: t.Id);

        public static Candidate ForApp(InstalledApp a) =>
            new(SearchItemKind.App, a.DisplayName, "应用", glyph: "\uECAA", a.IconImage, a.AppUserModelId, a.LocationPath);

        public static Candidate ForCommand(EdgeKit.Core.Commands.CommandDescriptor c) =>
            new(SearchItemKind.Command, c.Title, c.Description, c.Glyph, icon: null, payload: c.Id, keywords: c.Keywords);

        public SearchItem ToSearchItem() =>
            new(Kind, Title, SubTitle, Glyph, Icon, Payload, LocationPath);

        /// <summary>
        /// 计算与查询的匹配分。0 表示不匹配，分值越高越靠前。
        /// 优先级：标题前缀 > 标题子串 > 拼音首字母 > 全拼 > 副标题子串。
        /// </summary>
        public int Match(string q) => _index.Match(q);
    }
}
