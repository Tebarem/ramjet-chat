using System.Text.Json.Serialization;

namespace RamjetChat;

// config.json in the plugin's data folder (written with defaults on first run; /chat reload re-reads it). Plain data, no engine types.

/// <summary>One chat format: a template, used by players who hold <c>chat.format.&lt;Name&gt;</c>. The last format in the list is the default and
/// needs no node.</summary>
public sealed class FormatEntry
{
    /// <summary>The name; lower case letters, digits and underscores (it forms the node <c>chat.format.&lt;Name&gt;</c>).</summary>
    public string Name { get; set; } = "default";

    /// <summary>The template: "{prefix}", "{name}", "{display}", "{suffix}", "{world}" and "{message}". Literal text may use MiniMessage
    /// markup (it comes from the config, so it is trusted); the text before "{message}" is the sender part, the text after it follows the body.</summary>
    public string Template { get; set; } = "{prefix}{display}{suffix} {message}";
}

/// <summary>The chat plugin's settings.</summary>
public sealed class ChatConfig
{
    /// <summary>Formats, highest precedence first. The last entry is the default.</summary>
    public List<FormatEntry> Formats { get; set; } = [new FormatEntry()];

    /// <summary>The radius of local chat in blocks (the edge counts). 0 means the whole world; never global.</summary>
    public int LocalRadius { get; set; } = 100;

    /// <summary>The colour of a highlighted mention: "#rrggbb" or a named colour.</summary>
    public string MentionColor { get; set; } = "#ffaa00";

    /// <summary>The sound played to a mentioned player (a sound id).</summary>
    public string MentionSound { get; set; } = "minecraft:entity.experience_orb.pickup";

    /// <summary>Whether "&amp;c" style colour and style codes are read in messages (they then need the same nodes as the tags).</summary>
    public bool LegacyCodes { get; set; }

    /// <summary>Milliseconds between two messages in the global channel (0 = none).</summary>
    public int GlobalCooldownMs { get; set; }

    /// <summary>Milliseconds between two messages in the local channel (0 = none).</summary>
    public int LocalCooldownMs { get; set; }

    /// <summary>Milliseconds between two messages in the staff channel (0 = none).</summary>
    public int StaffCooldownMs { get; set; }

    /// <summary>The most messages a player may send in one window (0 = no limit).</summary>
    public int MessagesPerWindow { get; set; } = 6;

    /// <summary>The window of the message count, in milliseconds.</summary>
    public int WindowMs { get; set; } = 10_000;

    /// <summary>How many identical messages in a row trip the repeat rule (0 = off).</summary>
    public int RepeatLimit { get; set; } = 3;

    /// <summary>The window of the repeat rule, in milliseconds.</summary>
    public int RepeatWindowMs { get; set; } = 30_000;

    /// <summary>The fewest letters a message needs before the capitals rule applies.</summary>
    public int CapsMinLetters { get; set; } = 8;

    /// <summary>The share of capitals that trips the capitals rule, 0 to 1.</summary>
    public double CapsRatio { get; set; } = 0.75;

    /// <summary>What a listed word does: "mask" (default), "block", "warn" or "log".</summary>
    public string WordAction { get; set; } = "mask";

    /// <summary>What a link to a host that is not allowed does: "block" (default), "warn" or "log".</summary>
    public string LinkAction { get; set; } = "block";

    /// <summary>What shouting does: "log" (default), "warn" or "block".</summary>
    public string CapsAction { get; set; } = "log";

    /// <summary>What repeating does: "log" (default), "warn" or "block".</summary>
    public string RepeatAction { get; set; } = "log";

    /// <summary>The words of the word list (whole words, ignoring case).</summary>
    public List<string> Words { get; set; } = [];

    /// <summary>Hosts that links may point to (for everybody without the links bypass). Subdomains count. The same list allows open-URL click
    /// actions.</summary>
    public List<string> AllowedDomains { get; set; } = [];

    /// <summary>Shortcode (lower case, without colons) to the text it becomes in the decoration.</summary>
    public Dictionary<string, string> Emoji { get; set; } = new()
    {
        ["smile"] = "☺",
        ["heart"] = "❤",
        ["star"] = "★",
        ["skull"] = "☠",
        ["thumbsup"] = "\U0001F44D",
    };

    /// <summary>Days of chat log kept (older files are removed at start-up).</summary>
    public int LogRetentionDays { get; set; } = 14;
}

/// <summary>The JSON source-generated context for <see cref="ChatConfig"/> (no reflection at run time).</summary>
[JsonSerializable(typeof(ChatConfig))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class ChatJson : JsonSerializerContext
{
}
