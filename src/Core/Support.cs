using System.Globalization;
using System.Text;

namespace RamjetChat.Core;

// Small pure helpers: durations, the ignore relation and the chat log line format. Pure: no engine types.

/// <summary>Durations as the mute command takes them: "30s", "10m", "2h", "3d", "1w", and sums such as "1h30m". "perm", "permanent" and
/// "forever" mean no end.</summary>
public static class Durations
{
    /// <summary>The longest duration accepted (ten years is more than any mute needs).</summary>
    public const long MaxMilliseconds = 3650L * 86_400_000L;

    /// <summary>Parses a duration.</summary>
    /// <param name="text">The text.</param>
    /// <param name="duration">The duration; null for a permanent one. Only meaningful when the result is true.</param>
    /// <returns>True when the text is a valid duration or a permanent word.</returns>
    public static bool TryParse(string text, out TimeSpan? duration)
    {
        duration = null;
        string t = text.Trim().ToLowerInvariant();
        if (t is "perm" or "permanent" or "forever") return true;
        if (t.Length == 0) return false;

        long total = 0;
        int i = 0;
        while (i < t.Length)
        {
            int start = i;
            while (i < t.Length && char.IsAsciiDigit(t[i])) i++;
            if (i == start || i >= t.Length) return false;
            if (!long.TryParse(t.AsSpan(start, i - start), NumberStyles.None, CultureInfo.InvariantCulture, out long amount) || amount <= 0)
                return false;
            long factor = t[i] switch
            {
                's' => 1_000L,
                'm' => 60_000L,
                'h' => 3_600_000L,
                'd' => 86_400_000L,
                'w' => 604_800_000L,
                _ => 0L,
            };
            i++;
            if (factor == 0 || amount > MaxMilliseconds / factor) return false;
            total += amount * factor;
            if (total > MaxMilliseconds) return false;
        }
        if (total <= 0) return false;
        duration = TimeSpan.FromMilliseconds(total);
        return true;
    }
}

/// <summary>Who ignores whom. Ignoring is one-way: A ignoring B hides B's messages from A and drops B's private messages to A.</summary>
/// <typeparam name="T">The player key.</typeparam>
public sealed class IgnoreBook<T> where T : notnull
{
    private readonly Dictionary<T, HashSet<T>> _byOwner = new();

    /// <summary>The number of ignore pairs.</summary>
    public int Count { get; private set; }

    /// <summary>Adds a pair. Nobody ignores themselves.</summary>
    /// <returns>True when the pair was new.</returns>
    public bool Add(T owner, T target)
    {
        if (owner.Equals(target)) return false;
        if (!_byOwner.TryGetValue(owner, out var set)) _byOwner[owner] = set = new HashSet<T>();
        if (!set.Add(target)) return false;
        Count++;
        return true;
    }

    /// <summary>Removes a pair.</summary>
    /// <returns>True when it was there.</returns>
    public bool Remove(T owner, T target)
    {
        if (!_byOwner.TryGetValue(owner, out var set) || !set.Remove(target)) return false;
        if (set.Count == 0) _byOwner.Remove(owner);
        Count--;
        return true;
    }

    /// <summary>Whether <paramref name="owner"/> ignores <paramref name="target"/>.</summary>
    public bool IsIgnoring(T owner, T target) => _byOwner.TryGetValue(owner, out var set) && set.Contains(target);

    /// <summary>The players an owner ignores (empty when none).</summary>
    public IEnumerable<T> TargetsOf(T owner) => _byOwner.TryGetValue(owner, out var set) ? set : Enumerable.Empty<T>();

    /// <summary>Every pair, for saving.</summary>
    public IEnumerable<(T Owner, T Target)> Pairs()
    {
        foreach (var (owner, set) in _byOwner)
            foreach (var target in set)
                yield return (owner, target);
    }
}

/// <summary>The chat log: one line per message, tab-separated: time (UTC), channel, player id, name, flags, text.</summary>
public static class ChatLogLine
{
    /// <summary>Formats a line (no line break inside it: tabs and line breaks in the text become spaces).</summary>
    /// <param name="at">When the message was sent.</param>
    /// <param name="channel">The channel.</param>
    /// <param name="playerId">The player's id.</param>
    /// <param name="name">The player's name.</param>
    /// <param name="flags">The flags (for example "mask,warn:link"); "-" when none.</param>
    /// <param name="text">The text as sent.</param>
    public static string Format(DateTimeOffset at, string channel, string playerId, string name, string flags, string text)
    {
        var sb = new StringBuilder(text.Length + 96);
        sb.Append(at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append('\t');
        sb.Append(channel).Append('\t').Append(playerId).Append('\t').Append(name).Append('\t').Append(flags).Append('\t');
        foreach (char c in text) sb.Append(c is '\t' or '\r' or '\n' ? ' ' : c);
        return sb.ToString();
    }

    /// <summary>Whether the line is from the named player (the name field, ignoring case).</summary>
    public static bool IsFrom(string line, string name)
    {
        int first = line.IndexOf('\t');
        if (first < 0) return false;
        int second = line.IndexOf('\t', first + 1);
        if (second < 0) return false;
        int third = line.IndexOf('\t', second + 1);
        if (third < 0) return false;
        int fourth = line.IndexOf('\t', third + 1);
        if (fourth < 0) return false;
        return line.AsSpan(third + 1, fourth - third - 1).Equals(name, StringComparison.OrdinalIgnoreCase);
    }
}
