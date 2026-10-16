using System.Linq;
using System.Text;

namespace RamjetChat.Core;

// Player text becomes unsigned decoration (the signed text is never changed). Markup the player may use is kept, every other tag is
// stripped to its text, and "<" that does not start a tag is escaped, so the engine's MiniMessage parser shows exactly what was typed.
// Pure: no engine types.

/// <summary>A run of text in the typed message: the start index and length in UTF-16 units.</summary>
public readonly record struct TextSpan(int Start, int Length);

/// <summary>Settings the markup gate needs. Built from config once.</summary>
/// <param name="MentionColor">The colour of a highlighted mention, "#rrggbb" or a named colour.</param>
/// <param name="Emoji">Shortcode (lower case, without colons) to the text it becomes.</param>
/// <param name="LegacyCodes">Whether "&amp;c" style codes are read as colour and style tags (they then pass the same gate).</param>
public sealed record MarkupOptions(string MentionColor, IReadOnlyDictionary<string, string> Emoji, bool LegacyCodes);

/// <summary>The result of building a decoration.</summary>
/// <param name="Markup">MiniMessage markup for the engine's parser.</param>
/// <param name="Changed">False when the markup shows exactly the typed text, so no decoration is needed.</param>
public readonly record struct GateResult(string Markup, bool Changed);

/// <summary>Finds "@name" mentions. A name is 1 to 16 ASCII letters, digits or underscores, and must be a whole run; an "@" right after a letter
/// or digit is not a mention (an address, not a call).</summary>
public static class Mentions
{
    /// <summary>The longest player name Minecraft allows.</summary>
    public const int MaxNameLength = 16;

    /// <summary>Appends the spans of the mentions of online players (the span covers the "@" too).</summary>
    /// <param name="text">The text.</param>
    /// <param name="isPlayer">Whether a name (as typed, any case) is an online player.</param>
    /// <param name="into">The list to append to.</param>
    public static void Find(string text, Func<string, bool> isPlayer, List<TextSpan> into)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(isPlayer);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '@') continue;
            if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue;
            int j = i + 1;
            while (j < text.Length && j - i - 1 < MaxNameLength && IsNameChar(text[j])) j++;
            int len = j - i - 1;
            if (len == 0 || (j < text.Length && IsNameChar(text[j]))) continue;
            if (!isPlayer(text.Substring(i + 1, len))) continue;
            into.Add(new TextSpan(i, len + 1));
            i = j - 1;
        }
    }

    private static bool IsNameChar(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';
}

/// <summary>Builds the decoration of a player's message.</summary>
public static class MiniMessageGate
{
    /// <summary>The named colours of the vanilla palette, in colour-code order (0-9, a-f).</summary>
    public static readonly string[] NamedColors =
    [
        "black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray",
        "dark_gray", "blue", "green", "aqua", "red", "light_purple", "yellow", "white",
    ];

    private const string kDefaultMentionColor = "gold";

    /// <summary>Builds the markup for one typed message.</summary>
    /// <param name="raw">The typed text.</param>
    /// <param name="options">The markup settings.</param>
    /// <param name="has">Whether the sender holds a node (<c>chat.color</c>, <c>chat.style.bold</c>, ...).</param>
    /// <param name="isPlayer">Whether a name is an online player (for mentions).</param>
    /// <param name="urlAllowed">Whether an <c>open_url</c> target may be linked for this sender.</param>
    /// <returns>The markup and whether it differs visibly from the typed text.</returns>
    public static GateResult Build(string raw, MarkupOptions options, Func<string, bool> has, Func<string, bool> isPlayer, Func<string, bool> urlAllowed)
    {
        ArgumentNullException.ThrowIfNull(raw);
        ArgumentNullException.ThrowIfNull(options);
        string text = options.LegacyCodes ? Legacy(raw) : raw;
        var mentions = new List<TextSpan>();
        Mentions.Find(text, isPlayer, mentions);
        return Sanitize(text, mentions, options, has, urlAllowed);
    }

    /// <summary>Turns "&amp;c" style codes into tags (the tags are then gated like any other).</summary>
    public static string Legacy(string raw)
    {
        if (raw.IndexOf('&') < 0) return raw;
        var sb = new StringBuilder(raw.Length + 16);
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '&' && i + 1 < raw.Length && LegacyTag(char.ToLowerInvariant(raw[i + 1])) is { } tag)
            {
                sb.Append(tag);
                i++;
                continue;
            }
            sb.Append(raw[i]);
        }
        return sb.ToString();
    }

    /// <summary>Whether a colour text is "#rrggbb".</summary>
    public static bool IsHexColor(string text) =>
        text.Length == 7 && text[0] == '#' && text.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    /// <summary>The mention colour to use: the configured one when valid, otherwise gold.</summary>
    public static string SafeMentionColor(string configured)
    {
        if (IsHexColor(configured)) return configured.ToLowerInvariant();
        return NamedColors.Contains(configured, StringComparer.Ordinal) ? configured : kDefaultMentionColor;
    }

    private static string? LegacyTag(char code)
    {
        if (code >= '0' && code <= '9') return "<" + NamedColors[code - '0'] + ">";
        if (code >= 'a' && code <= 'f') return "<" + NamedColors[code - 'a' + 10] + ">";
        return code switch
        {
            'l' => "<bold>",
            'o' => "<italic>",
            'n' => "<underlined>",
            'm' => "<strikethrough>",
            'k' => "<obfuscated>",
            'r' => "<reset>",
            _ => null,
        };
    }

    private enum TagKind { Keep, Drop, Literal }

    // What a tag does: the markup to emit, the frame it opens or closes (canonical name), and whether it resets the frame list.
    private readonly record struct TagDecision(TagKind Kind, string Output, string Frame, bool Closing, bool Reset);

    private static GateResult Sanitize(string text, List<TextSpan> mentions, MarkupOptions options, Func<string, bool> has, Func<string, bool> urlAllowed)
    {
        var sb = new StringBuilder(text.Length + 16);
        var open = new List<string>();
        string mentionColor = SafeMentionColor(options.MentionColor);
        bool changed = false;
        int m = 0;
        int i = 0;
        while (i < text.Length)
        {
            while (m < mentions.Count && mentions[m].Start < i) m++;
            if (m < mentions.Count && mentions[m].Start == i)
            {
                var span = mentions[m++];
                sb.Append('<').Append(mentionColor).Append("><bold>")
                  .Append(text, span.Start, span.Length)
                  .Append("</bold></").Append(mentionColor).Append('>');
                i += span.Length;
                changed = true;
                continue;
            }

            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '<')
            {
                sb.Append("\\<");
                i += 2;
                continue;
            }
            if (c == ':' && TryEmoji(text, i, options.Emoji, out int emojiEnd, out string glyph))
            {
                sb.Append(glyph);
                i = emojiEnd;
                changed = true;
                continue;
            }
            if (c != '<')
            {
                sb.Append(c);
                i++;
                continue;
            }

            int end = TagEnd(text, i);
            if (end < 0)
            {
                sb.Append("\\<");
                i++;
                continue;
            }
            var decision = Decide(text.Substring(i + 1, end - i - 1), open, has, urlAllowed);
            switch (decision.Kind)
            {
                case TagKind.Keep:
                    sb.Append(decision.Output);
                    if (decision.Reset) open.Clear();
                    else if (decision.Closing) open.RemoveAt(open.LastIndexOf(decision.Frame));
                    else
                    {
                        open.Add(decision.Frame);
                        changed = true;
                    }
                    i = end + 1;
                    break;
                case TagKind.Drop:
                    i = end + 1;
                    break;
                default:
                    // Not a tag we know: the "<" is shown as typed.
                    sb.Append("\\<");
                    i++;
                    break;
            }
        }

        // Close what the player left open, so the markup cannot leak its style past the message.
        for (int k = open.Count - 1; k >= 0; k--) sb.Append("</").Append(open[k]).Append('>');
        return new GateResult(sb.ToString(), changed);
    }

    private static TagDecision Decide(string body, List<string> open, Func<string, bool> has, Func<string, bool> urlAllowed)
    {
        if (body.StartsWith('/'))
        {
            string name = Canonical(body[1..].Trim().ToLowerInvariant());
            if (open.Contains(name)) return new TagDecision(TagKind.Keep, "</" + name + ">", name, true, false);
            return IsKnownName(name) ? new TagDecision(TagKind.Drop, "", name, true, false) : new TagDecision(TagKind.Literal, "", name, true, false);
        }

        var args = SplitArgs(body);
        string head = args[0].Trim().ToLowerInvariant();
        if (head == "reset") return new TagDecision(TagKind.Keep, "<reset>", "reset", false, true);

        if (Array.IndexOf(NamedColors, head) >= 0)
            return Gate(has("chat.color"), head, "<" + head + ">");
        if (IsHexColor(head))
            return Gate(has("chat.color.hex"), head, "<" + head + ">");

        switch (Canonical(head))
        {
            case "bold": return Gate(has("chat.style.bold"), "bold", "<bold>");
            case "italic": return Gate(has("chat.style.italic"), "italic", "<italic>");
            case "underlined": return Gate(has("chat.style.underlined"), "underlined", "<underlined>");
            case "strikethrough": return Gate(has("chat.style.strikethrough"), "strikethrough", "<strikethrough>");
            case "obfuscated": return Gate(has("chat.style.obfuscated"), "obfuscated", "<obfuscated>");
        }

        if (head == "hover")
        {
            if (args.Count < 3 || args[1].Trim().ToLowerInvariant() != "show_text") return Dropped(head);
            if (!has("chat.hover")) return Dropped(head);
            string shown = Plain(string.Join(':', args.Skip(2)));
            return new TagDecision(TagKind.Keep, "<hover:show_text:" + shown + ">", "hover", false, false);
        }

        if (head == "click")
        {
            if (args.Count < 3) return Dropped(head);
            string action = args[1].Trim().ToLowerInvariant();
            string value = Plain(string.Join(':', args.Skip(2)));
            bool allowed = action switch
            {
                "suggest_command" => has("chat.click.suggest"),
                "open_url" => has("chat.click.url") && urlAllowed(value),
                "run_command" => has("chat.click.run"),
                _ => false,
            };
            if (!allowed) return Dropped(head);
            return new TagDecision(TagKind.Keep, "<click:" + action + ":" + value + ">", "click", false, false);
        }

        return IsKnownName(head) ? Dropped(head) : new TagDecision(TagKind.Literal, "", head, false, false);
    }

    private static TagDecision Gate(bool allowed, string frame, string tag) =>
        allowed ? new TagDecision(TagKind.Keep, tag, frame, false, false) : new TagDecision(TagKind.Drop, "", frame, false, false);

    private static TagDecision Dropped(string head) => new(TagKind.Drop, "", head, false, false);

    private static bool IsKnownName(string name) =>
        Array.IndexOf(NamedColors, name) >= 0 || IsHexColor(name) || Canonical(name) is "bold" or "italic" or "underlined"
            or "strikethrough" or "obfuscated" or "hover" or "click" or "reset";

    private static string Canonical(string name) => name switch
    {
        "b" => "bold",
        "i" or "em" => "italic",
        "u" => "underlined",
        "st" => "strikethrough",
        "obf" => "obfuscated",
        _ => name,
    };

    // Tag arguments and values cannot carry markup characters or quotes: they are plain text inside the emitted tag.
    private static string Plain(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
            if (c is not ('<' or '>' or '"' or '\'' or '\\')) sb.Append(c);
        return sb.ToString();
    }

    // The tag ends at the first ">" outside quotes; another "<" first means it is not a tag.
    private static int TagEnd(string s, int start)
    {
        char quote = '\0';
        for (int i = start + 1; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == '>') return i;
            if (c == '<') return -1;
        }
        return -1;
    }

    // Arguments are separated by ":" outside quotes; the quotes themselves are not part of the value.
    private static List<string> SplitArgs(string body)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        foreach (char c in body)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else sb.Append(c);
                continue;
            }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c == ':')
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static bool TryEmoji(string text, int i, IReadOnlyDictionary<string, string> emoji, out int end, out string glyph)
    {
        end = 0;
        glyph = "";
        int j = i + 1;
        while (j < text.Length && j - i - 1 <= 32 && (char.IsAsciiLetterOrDigit(text[j]) || text[j] == '_')) j++;
        int len = j - i - 1;
        if (len < 2 || j >= text.Length || text[j] != ':') return false;
        if (!emoji.TryGetValue(text.Substring(i + 1, len).ToLowerInvariant(), out glyph!)) return false;
        end = j + 1;
        return true;
    }
}
