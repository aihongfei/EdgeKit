using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TinyPinyin;

namespace EdgeKit.App.Search;

/// <summary>搜索文本索引：支持标题、描述、关键词以及中文拼音/首字母匹配。</summary>
public sealed class SearchTextIndex
{
    private readonly string _titleLower;
    private readonly string _subTitleLower;
    private readonly string[] _keywordsLower;
    private readonly string _pinyin;
    private readonly string _initials;

    public SearchTextIndex(string title, string subTitle = "", IEnumerable<string>? keywords = null)
    {
        _titleLower = (title ?? string.Empty).ToLowerInvariant();
        _subTitleLower = (subTitle ?? string.Empty).ToLowerInvariant();
        _keywordsLower = (keywords ?? Array.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim().ToLowerInvariant())
            .ToArray();
        (_pinyin, _initials) = ComputePinyin(title ?? string.Empty);
    }

    /// <summary>计算匹配分。0 表示不匹配，分值越高越靠前。</summary>
    public int Match(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0)
        {
            return 1;
        }

        if (_titleLower.StartsWith(q, StringComparison.Ordinal))
        {
            return 100;
        }

        if (_titleLower.Contains(q, StringComparison.Ordinal))
        {
            return 80;
        }

        if (_initials.Length > 0 && _initials.StartsWith(q, StringComparison.Ordinal))
        {
            return 70;
        }

        if (_initials.Length > 0 && _initials.Contains(q, StringComparison.Ordinal))
        {
            return 60;
        }

        if (_pinyin.Length > 0 && _pinyin.StartsWith(q, StringComparison.Ordinal))
        {
            return 55;
        }

        if (_pinyin.Length > 0 && _pinyin.Contains(q, StringComparison.Ordinal))
        {
            return 50;
        }

        foreach (var keyword in _keywordsLower)
        {
            if (keyword.StartsWith(q, StringComparison.Ordinal))
            {
                return 45;
            }

            if (keyword.Contains(q, StringComparison.Ordinal))
            {
                return 40;
            }
        }

        if (_subTitleLower.Length > 0 && _subTitleLower.Contains(q, StringComparison.Ordinal))
        {
            return 30;
        }

        return 0;
    }

    private static (string Pinyin, string Initials) ComputePinyin(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return (string.Empty, string.Empty);
        }

        var pinyin = new StringBuilder();
        var initials = new StringBuilder();

        foreach (var ch in title)
        {
            if (PinyinHelper.IsChinese(ch))
            {
                var py = PinyinHelper.GetPinyin(ch);
                if (!string.IsNullOrEmpty(py))
                {
                    pinyin.Append(py.ToLowerInvariant());
                    initials.Append(char.ToLowerInvariant(py[0]));
                }
            }
            else if (!char.IsWhiteSpace(ch))
            {
                var lower = char.ToLowerInvariant(ch);
                pinyin.Append(lower);
                if (char.IsLetterOrDigit(ch))
                {
                    initials.Append(lower);
                }
            }
        }

        return (pinyin.ToString(), initials.ToString());
    }
}
