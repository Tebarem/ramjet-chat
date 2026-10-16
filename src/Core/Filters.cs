using System.Linq;

namespace RamjetChat.Core;

// Text checks that run on the typed message before it is sent. Pure: no engine types.
// Each rule has an action: block (the message is dropped and the sender told), mask (the message goes out signed, the filtering
// recipients see the matched characters hidden: the vanilla filter mask, never a rewrite), warn (the sender is told, the message goes out)
// or log (the chat log records it).

/// <summary>What a rule does when it matches.</summary>
public enum FilterAction
{
    /// <summary>Record it in the chat log only.</summary>
    Log,
    /// <summary>Tell the sender and record it; the message goes out.</summary>
    Warn,
    /// <summary>Hide the matched characters from filtering recipients (filter mask); the message goes out signed.</summary>
    Mask,
    /// <summary>Drop the message and tell the sender.</summary>
    Block,
}

/// <summary>Which rule matched.</summary>
public enum FilterRule
{
    /// <summary>A word of the word list.</summary>
    Words,
    /// <summary>A link to a host that is not allowed.</summary>
    Link,
    /// <summary>Too many capitals.</summary>
    Caps,
    /// <summary>The same message again and again.</summary>
    Repeat,
}

/// <summary>Action names as the config spells them.</summary>
public static class FilterActions
{
    /// <summary>Reads "log", "warn", "mask" or "block" (any case); anything else gives the fallback.</summary>
    public static FilterAction Parse(string text, FilterAction fallback) => text.Trim().ToLowerInvariant() switch
    {
        "log" => FilterAction.Log,
        "warn" => FilterAction.Warn,
        "mask" => FilterAction.Mask,
        "block" => FilterAction.Block,
        _ => fallback,
    };
}

/// <summary>A word list with whole-word matching: a word matches a run of letters and digits that equals it, ignoring case.</summary>
public sealed class WordFilter
{
    private readonly HashSet<string> _words;

    /// <summary>Builds the list. Empty entries are skipped.</summary>
    /// <param name="words">The words.</param>
    public WordFilter(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        _words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string word in words)
        {
            string trimmed = word.Trim();
            if (trimmed.Length > 0) _words.Add(trimmed);
        }
    }

    /// <summary>The number of words.</summary>
    public int Count => _words.Count;

    /// <summary>Finds the listed words in the text and appends their spans.</summary>
    /// <param name="text">The typed text.</param>
    /// <param name="into">The list to append to.</param>
    /// <returns>The number of matches found.</returns>
    public int Find(string text, List<TextSpan> into)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_words.Count == 0) return 0;
        var lookup = _words.GetAlternateLookup<ReadOnlySpan<char>>();
        int found = 0;
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }
            int j = i;
            while (j < text.Length && char.IsLetterOrDigit(text[j])) j++;
            if (lookup.Contains(text.AsSpan(i, j - i)))
            {
                into.Add(new TextSpan(i, j - i));
                found++;
            }
            i = j;
        }
        return found;
    }
}

/// <summary>Builds the filter mask of a message: one "0" or "1" per UTF-16 unit of the typed text, "1" hides the unit.</summary>
public static class FilterMask
{
    /// <summary>The mask string for the spans (empty when there are none).</summary>
    public static string Bits(string text, IReadOnlyList<TextSpan> spans)
    {
        if (spans.Count == 0) return "";
        var bits = new char[text.Length];
        Array.Fill(bits, '0');
        foreach (var span in spans)
            for (int i = span.Start; i < span.Start + span.Length && i < bits.Length; i++) bits[i] = '1';
        return new string(bits);
    }
}

/// <summary>Link detection. A link is a token with a scheme, a "www." host, or a host whose last label is a known top-level domain (a
/// plain "end.Thanks" is not a link). A host is allowed when it is an allowed domain or a subdomain of one.</summary>
public static class LinkScan
{
    private static readonly HashSet<string> s_topLevel = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "edu", "gov", "io", "gg", "co", "me", "tv", "ly", "gl", "to", "cc", "ws", "xyz", "info", "biz", "dev", "app",
        "site", "online", "store", "club", "top", "link", "click", "de", "fr", "uk", "us", "ca", "au", "nl", "se", "pl", "ru", "jp", "cn",
        "br", "in", "eu", "es", "it", "ch", "at", "be", "no", "fi", "dk", "kr", "tk",
    };

    /// <summary>Whether the text has a link to a host that is not allowed.</summary>
    /// <param name="text">The typed text.</param>
    /// <param name="allowed">The allowed domains (lower case), for example "example.org".</param>
    public static bool HasDisallowedLink(string text, IReadOnlyCollection<string> allowed)
    {
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (i == start) continue;
            string? host = HostOf(text.Substring(start, i - start));
            if (host is not null && !IsAllowedHost(host, allowed)) return true;
        }
        return false;
    }

    /// <summary>The host a token links to, or null when the token is not a link.</summary>
    public static string? HostOf(string token)
    {
        string t = token.Trim('(', ')', '[', ']', '{', '}', '<', '>', '"', '\'', ',', '!', '?', ';');
        bool scheme = t.Contains("://", StringComparison.Ordinal);
        if (scheme) t = t[(t.IndexOf("://", StringComparison.Ordinal) + 3)..];
        int at = t.LastIndexOf('@');
        if (at >= 0 && !scheme) t = t[(at + 1)..];
        int end = t.IndexOfAny(['/', '?', '#', ':']);
        string host = (end >= 0 ? t[..end] : t).TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return null;
        if (scheme) return host;
        string[] labels = host.Split('.');
        if (labels.Length < 2 || labels.Any(l => l.Length == 0 || !l.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))) return null;
        if (host.StartsWith("www.", StringComparison.Ordinal)) return host;
        if (labels.Length == 4 && labels.All(l => l.All(char.IsAsciiDigit))) return host;
        return s_topLevel.Contains(labels[^1]) ? host : null;
    }

    /// <summary>Whether a host is an allowed domain or a subdomain of one.</summary>
    public static bool IsAllowedHost(string host, IReadOnlyCollection<string> allowed)
    {
        foreach (string domain in allowed)
            if (host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>The capitals check: enough letters, and most of them capitals.</summary>
public static class CapsCheck
{
    /// <summary>Whether the text is shouting.</summary>
    /// <param name="text">The typed text.</param>
    /// <param name="minLetters">The fewest letters a message needs before the check applies.</param>
    /// <param name="ratio">The share of capitals among the letters, 0 to 1.</param>
    public static bool IsShouting(string text, int minLetters, double ratio)
    {
        int letters = 0;
        int upper = 0;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (char.IsUpper(c)) upper++;
        }
        return letters > 0 && letters >= minLetters && upper >= ratio * letters;
    }
}
