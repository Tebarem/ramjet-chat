using System.Linq;
using System.Text;

namespace RamjetChat.Core;

// Format templates ("{prefix}{display}{suffix} {message}") are parsed once at load. Pure: no engine types, so the console tests compile this file.

/// <summary>What a template token stands for.</summary>
public enum TemplateToken
{
    /// <summary>The profile name.</summary>
    Name,
    /// <summary>The display name (the name the server shows; falls back to the profile name).</summary>
    Display,
    /// <summary>The world the player is in.</summary>
    World,
    /// <summary>The player's prefix (permissions meta).</summary>
    Prefix,
    /// <summary>The player's suffix (permissions meta).</summary>
    Suffix,
    /// <summary>The typed text. Always present once a template is parsed.</summary>
    Message,
}

/// <summary>One piece of a parsed template: a literal (MiniMessage markup from the config) or a token.</summary>
/// <param name="Literal">The literal text; null for a token.</param>
/// <param name="Token">The token, when <paramref name="Literal"/> is null.</param>
public readonly record struct TemplatePart(string? Literal, TemplateToken Token)
{
    /// <summary>Whether this part is a token rather than literal text.</summary>
    public bool IsToken => Literal is null;
}

/// <summary>A parsed format template, split at the message token.</summary>
/// <param name="Before">The parts before the message: they become the sender component (shown inside the client's name brackets).</param>
/// <param name="After">The parts after the message: they are appended to the body (unsigned decoration).</param>
public sealed record SplitTemplate(IReadOnlyList<TemplatePart> Before, IReadOnlyList<TemplatePart> After);

/// <summary>Parses and renders format templates.</summary>
public static class FormatTemplate
{
    /// <summary>Parses a template. Unknown "{word}" stays literal text. A template without "{message}" gets " {message}" appended, so the
    /// typed text is always shown.</summary>
    /// <param name="template">The template text.</param>
    /// <returns>The parts, in order.</returns>
    public static IReadOnlyList<TemplatePart> Parse(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        var parts = new List<TemplatePart>();
        var literal = new StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c == '{')
            {
                int close = template.IndexOf('}', i + 1);
                if (close > i && TryToken(template.AsSpan(i + 1, close - i - 1), out var token))
                {
                    if (literal.Length > 0)
                    {
                        parts.Add(new TemplatePart(literal.ToString(), default));
                        literal.Clear();
                    }
                    parts.Add(new TemplatePart(null, token));
                    i = close + 1;
                    continue;
                }
            }
            literal.Append(c);
            i++;
        }
        if (literal.Length > 0) parts.Add(new TemplatePart(literal.ToString(), default));
        if (!parts.Exists(p => p.IsToken && p.Token == TemplateToken.Message))
        {
            parts.Add(new TemplatePart(" ", default));
            parts.Add(new TemplatePart(null, TemplateToken.Message));
        }
        return parts;
    }

    /// <summary>Splits parsed parts at the first message token.</summary>
    /// <param name="parts">The parts from <see cref="Parse"/>.</param>
    /// <returns>The parts before and after the message token.</returns>
    public static SplitTemplate Split(IReadOnlyList<TemplatePart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        int at = parts.Count;
        for (int i = 0; i < parts.Count; i++)
            if (parts[i].IsToken && parts[i].Token == TemplateToken.Message) { at = i; break; }
        var before = parts.Take(at).ToList();
        // The client draws the separator after the name itself, so trailing spaces of the sender part are dropped.
        if (before.Count > 0 && !before[^1].IsToken)
        {
            string trimmed = before[^1].Literal!.TrimEnd();
            if (trimmed.Length == 0) before.RemoveAt(before.Count - 1);
            else before[^1] = new TemplatePart(trimmed, default);
        }
        var after = at < parts.Count ? parts.Skip(at + 1).ToArray() : Array.Empty<TemplatePart>();
        return new SplitTemplate(before, after);
    }

    /// <summary>Renders the parts to plain text (literals as written, tokens through <paramref name="value"/>). Used by tests and by the
    /// fallbacks that need a string; the chat path builds components instead.</summary>
    /// <param name="parts">The parts.</param>
    /// <param name="value">The text of a token.</param>
    /// <returns>The text.</returns>
    public static string RenderPlain(IReadOnlyList<TemplatePart> parts, Func<TemplateToken, string> value)
    {
        var sb = new StringBuilder();
        foreach (var part in parts)
            sb.Append(part.IsToken ? value(part.Token) : part.Literal);
        return sb.ToString();
    }

    private static bool TryToken(ReadOnlySpan<char> name, out TemplateToken token)
    {
        if (name.Equals("name", StringComparison.OrdinalIgnoreCase)) token = TemplateToken.Name;
        else if (name.Equals("display", StringComparison.OrdinalIgnoreCase)) token = TemplateToken.Display;
        else if (name.Equals("world", StringComparison.OrdinalIgnoreCase)) token = TemplateToken.World;
        else if (name.Equals("prefix", StringComparison.OrdinalIgnoreCase)) token = TemplateToken.Prefix;
        else if (name.Equals("suffix", StringComparison.OrdinalIgnoreCase)) token = TemplateToken.Suffix;
        else if (name.Equals("message", StringComparison.OrdinalIgnoreCase)) token = TemplateToken.Message;
        else { token = default; return false; }
        return true;
    }
}

/// <summary>Picks the format of a player: the first format (in config order) whose node the player holds, the last format is the default and
/// needs no node.</summary>
public static class FormatSelector
{
    /// <summary>Returns the index of the format to use.</summary>
    /// <param name="names">The format names, highest precedence first; the last one is the default.</param>
    /// <param name="holds">Whether the player holds the node <c>chat.format.&lt;name&gt;</c>.</param>
    /// <returns>The index into <paramref name="names"/>; the last index when none matched (or the list has one entry).</returns>
    public static int Select(IReadOnlyList<string> names, Func<string, bool> holds)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(holds);
        if (names.Count == 0) throw new ArgumentException("At least one format (the default) is needed.", nameof(names));
        for (int i = 0; i < names.Count - 1; i++)
            if (holds(names[i])) return i;
        return names.Count - 1;
    }
}
