using System.Text;
using System.Linq;
using RamjetChat.Core;

namespace RamjetChat;

// The chat plugin's runtime: the compiled settings, the per-player state, and the file-backed ignore and mention lists. The message path
// (PlayerChat), private messages and the commands are partial files of this class. Tick thread only, except the file work in ChatLog and
// the Scheduler.RunAsync calls here.

/// <summary>The permission nodes the chat plugin checks or registers.</summary>
internal static class Nodes
{
    public const string Color = "chat.color";
    public const string ColorHex = "chat.color.hex";
    public const string StyleBold = "chat.style.bold";
    public const string StyleItalic = "chat.style.italic";
    public const string StyleUnderlined = "chat.style.underlined";
    public const string StyleStrikethrough = "chat.style.strikethrough";
    public const string StyleObfuscated = "chat.style.obfuscated";
    public const string Hover = "chat.hover";
    public const string Item = "chat.item";
    public const string ClickSuggest = "chat.click.suggest";
    public const string ClickUrl = "chat.click.url";
    public const string ClickRun = "chat.click.run";
    public const string Msg = "chat.msg";
    public const string Reply = "chat.reply";
    public const string Ignore = "chat.ignore";
    public const string IgnoreBypass = "chat.bypass.ignore";
    public const string SocialSpy = "chat.socialspy";
    public const string SocialSpySee = "chat.socialspy.see";
    public const string Mention = "chat.mention";
    public const string StaffChannel = "chat.channel.staff";
    public const string BypassSpam = "chat.bypass.spam";
    public const string BypassRepeat = "chat.bypass.repeat";
    public const string BypassCaps = "chat.bypass.caps";
    public const string BypassLinks = "chat.bypass.links";
    public const string BypassWords = "chat.bypass.words";
    public const string Mute = "chat.mute";
    public const string MuteExempt = "chat.mute.exempt";
    public const string MuteForce = "chat.mute.force";
    public const string ChatLog = "chat.chatlog";
    public const string Reload = "chat.reload";
    public const string FormatPrefix = "chat.format.";
}

/// <summary>One format, compiled: its parts, and the components of its literal parts (parsed once, at load).</summary>
internal sealed class FormatSlot
{
    public required string Name { get; init; }
    public required TemplatePart[] Before { get; init; }
    public required Component?[] BeforeText { get; init; }
    public required TemplatePart[] After { get; init; }
    public required Component?[] AfterText { get; init; }
}

/// <summary>The settings in use, compiled from the config. Replaced as a whole on reload, so a message reads one consistent snapshot.</summary>
internal sealed class Settings
{
    public required FormatSlot[] Formats { get; init; }
    public required string[] FormatNames { get; init; }
    public required ThrottleLimits Limits { get; init; }
    public required int LocalRadius { get; init; }
    public required string MentionSound { get; init; }
    public required MarkupOptions Markup { get; init; }
    public required WordFilter Words { get; init; }
    public required string[] AllowedDomains { get; init; }
    public required FilterAction WordAction { get; init; }
    public required FilterAction LinkAction { get; init; }
    public required FilterAction CapsAction { get; init; }
    public required FilterAction RepeatAction { get; init; }
    public required int CapsMinLetters { get; init; }
    public required double CapsRatio { get; init; }
    public required int RepeatLimit { get; init; }
    public required int RepeatWindowMs { get; init; }
    public required int LogRetentionDays { get; init; }
}

/// <summary>The chat plugin's state and behaviour.</summary>
internal sealed partial class ChatEngine
{
    private readonly Plugin _host;
    private readonly Config<ChatConfig> _config;
    private readonly IgnoreBook<Uuid> _ignores = new();
    private readonly HashSet<Uuid> _mentionsOff = [];
    private readonly HashSet<Uuid> _spies = [];
    private readonly Dictionary<Uuid, ThrottleState> _throttle = new();
    private readonly Dictionary<Uuid, ChannelKind> _channels = new();
    private readonly Dictionary<Uuid, Uuid> _lastPartner = new();
    private readonly List<Player> _recipients = new();
    private readonly List<TextSpan> _spans = new();
    private readonly List<TextSpan> _mentions = new();
    private readonly Dictionary<string, Player> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ChatLog _log;
    private Settings _s = null!;

    public ChatEngine(Plugin host, Config<ChatConfig> config)
    {
        _host = host;
        _config = config;
        _log = new ChatLog(host.DataFolder, host.Scheduler);
    }

    /// <summary>The settings currently in use (read on the tick thread).</summary>
    internal Settings Current => _s;

    /// <summary>Compiles the config, registers the permission nodes and loads the saved lists. Called at enable and on every reload.</summary>
    public void Apply(ChatConfig config)
    {
        _s = Compile(config);
        RegisterNodes();
    }

    /// <summary>Loads the persisted ignore pairs and mention preferences (the files are small; read once at enable).</summary>
    public void LoadPersisted()
    {
        foreach (string line in ReadLines("ignore.txt"))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && ParseId(parts[0]) is { } owner && ParseId(parts[1]) is { } target)
                _ignores.Add(owner, target);
        }
        foreach (string line in ReadLines("mentions-off.txt"))
            if (ParseId(line.Trim()) is { } id) _mentionsOff.Add(id);
    }

    /// <summary>Starts the chat log writer and removes old logs.</summary>
    public void StartLog()
    {
        _log.Start(_s.LogRetentionDays);
        _host.Scheduler.RunEvery(100, _log.Flush, 100);
    }

    /// <summary>Forgets a player's per-session state (on quit).</summary>
    public void Forget(Uuid id)
    {
        _throttle.Remove(id);
        _channels.Remove(id);
        _lastPartner.Remove(id);
        _spies.Remove(id);
    }

    internal ChannelKind ChannelOf(Uuid id) => _channels.TryGetValue(id, out var channel) ? channel : ChannelKind.Global;

    internal void SetChannel(Uuid id, ChannelKind channel) => _channels[id] = channel;

    internal ThrottleState StateOf(Uuid id)
    {
        if (!_throttle.TryGetValue(id, out var state)) _throttle[id] = state = new ThrottleState();
        return state;
    }

    /// <summary>Saves the ignore pairs and mention preferences off the tick.</summary>
    internal void SaveLists()
    {
        var ignores = new StringBuilder();
        foreach (var (owner, target) in _ignores.Pairs())
            ignores.Append(owner.ToString()).Append(' ').Append(target.ToString()).Append('\n');
        var mentions = new StringBuilder();
        foreach (var id in _mentionsOff) mentions.Append(id.ToString()).Append('\n');
        string ignoreText = ignores.ToString();
        string mentionText = mentions.ToString();
        _host.Scheduler.RunAsync(() =>
        {
            _host.Config.WriteText("ignore.txt", ignoreText);
            _host.Config.WriteText("mentions-off.txt", mentionText);
        });
    }

    private IEnumerable<string> ReadLines(string file)
    {
        string? text = _host.Config.ReadText(file);
        if (text is null) return [];
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private void RegisterNodes()
    {
        var permissions = _host.Permissions;
        void Node(string node, PermissionDefault fallback, string description)
        {
            // A reload registers the same nodes again; the first registration stays.
            if (!PermissionManager.IsRegistered(node)) permissions.Register(node, fallback, description);
        }
        foreach (var slot in _s.Formats)
            if (slot.Name != _s.FormatNames[^1]) Node(Nodes.FormatPrefix + slot.Name, PermissionDefault.False, "Use the chat format " + slot.Name);
        Node(Nodes.Color, PermissionDefault.True, "Named colour tags in chat");
        Node(Nodes.ColorHex, PermissionDefault.True, "Hex colour tags in chat");
        Node(Nodes.StyleBold, PermissionDefault.True, "Bold tags in chat");
        Node(Nodes.StyleItalic, PermissionDefault.True, "Italic tags in chat");
        Node(Nodes.StyleUnderlined, PermissionDefault.True, "Underlined tags in chat");
        Node(Nodes.StyleStrikethrough, PermissionDefault.True, "Strikethrough tags in chat");
        Node(Nodes.StyleObfuscated, PermissionDefault.True, "Obfuscated tags in chat");
        Node(Nodes.Hover, PermissionDefault.True, "Hover text tags in chat");
        Node(Nodes.Item, PermissionDefault.True, "Show the held item with [item]");
        Node(Nodes.ClickSuggest, PermissionDefault.True, "Suggest-command click tags in chat");
        Node(Nodes.ClickUrl, PermissionDefault.True, "Open-URL click tags (allowed domains only)");
        Node(Nodes.ClickRun, PermissionDefault.False, "Run-command click tags (staff)");
        Node(Nodes.Msg, PermissionDefault.True, "Send private messages");
        Node(Nodes.Reply, PermissionDefault.True, "Reply to the last private message");
        Node(Nodes.Ignore, PermissionDefault.True, "Ignore other players");
        Node(Nodes.IgnoreBypass, PermissionDefault.False, "Cannot be ignored");
        Node(Nodes.SocialSpy, PermissionDefault.False, "Turn social spy on");
        Node(Nodes.SocialSpySee, PermissionDefault.False, "Receive social spy copies");
        Node(Nodes.Mention, PermissionDefault.True, "Mention players with @name");
        Node(Nodes.StaffChannel, PermissionDefault.False, "Use the staff channel");
        Node(Nodes.BypassSpam, PermissionDefault.False, "Skip the message cooldown and window");
        Node(Nodes.BypassRepeat, PermissionDefault.False, "Skip the repeat rule");
        Node(Nodes.BypassCaps, PermissionDefault.False, "Skip the capitals rule");
        Node(Nodes.BypassLinks, PermissionDefault.False, "Skip the link rule");
        Node(Nodes.BypassWords, PermissionDefault.False, "Skip the word list");
        Node(Nodes.Mute, PermissionDefault.False, "Mute and unmute chat");
        Node(Nodes.MuteExempt, PermissionDefault.False, "Cannot be muted without the force node");
        Node(Nodes.MuteForce, PermissionDefault.False, "Mute players who are exempt");
        Node(Nodes.ChatLog, PermissionDefault.False, "Read the chat log");
        Node(Nodes.Reload, PermissionDefault.False, "Reload the chat config");
    }

    private static Settings Compile(ChatConfig c)
    {
        // The default format is never missing: a config without formats gets the built-in one.
        List<FormatEntry> entries = c.Formats.Count == 0 ? [new FormatEntry()] : c.Formats;
        var formats = new FormatSlot[entries.Count];
        var names = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var split = FormatTemplate.Split(FormatTemplate.Parse(entry.Template));
            names[i] = entry.Name;
            formats[i] = new FormatSlot
            {
                Name = entry.Name,
                Before = [.. split.Before],
                BeforeText = [.. split.Before.Select(LiteralText)],
                After = [.. split.After],
                AfterText = [.. split.After.Select(LiteralText)],
            };
        }
        var limits = new ThrottleLimits(
            Math.Max(0, c.GlobalCooldownMs), Math.Max(0, c.LocalCooldownMs), Math.Max(0, c.StaffCooldownMs),
            Math.Max(0, c.MessagesPerWindow), Math.Max(0, c.WindowMs));
        return new Settings
        {
            Formats = formats,
            FormatNames = names,
            Limits = limits,
            LocalRadius = Math.Max(0, c.LocalRadius),
            MentionSound = c.MentionSound,
            Markup = new MarkupOptions(MiniMessageGate.SafeMentionColor(c.MentionColor), EmojiMap(c.Emoji), c.LegacyCodes),
            Words = new WordFilter(c.Words),
            AllowedDomains = [.. c.AllowedDomains.Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0)],
            WordAction = FilterActions.Parse(c.WordAction, FilterAction.Mask),
            LinkAction = FilterActions.Parse(c.LinkAction, FilterAction.Block),
            CapsAction = FilterActions.Parse(c.CapsAction, FilterAction.Log),
            RepeatAction = FilterActions.Parse(c.RepeatAction, FilterAction.Log),
            CapsMinLetters = Math.Max(1, c.CapsMinLetters),
            CapsRatio = Math.Clamp(c.CapsRatio, 0.01, 1.0),
            RepeatLimit = Math.Max(0, c.RepeatLimit),
            RepeatWindowMs = Math.Max(0, c.RepeatWindowMs),
            LogRetentionDays = Math.Max(1, c.LogRetentionDays),
        };
    }

    internal static Uuid? ParseId(string text)
    {
        if (!Guid.TryParse(text, out Guid guid)) return null;
        string hex = guid.ToString("N");
        return new Uuid(Convert.ToUInt64(hex[..16], 16), Convert.ToUInt64(hex[16..], 16));
    }

    // Shortcodes are matched in lower case; two keys that differ only by case keep the last one.
    private static Dictionary<string, string> EmojiMap(Dictionary<string, string> source)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, glyph) in source)
            if (key.Trim().Length > 0) map[key.Trim().ToLowerInvariant()] = glyph;
        return map;
    }

    private static Component? LiteralText(TemplatePart part) => part.IsToken ? null : Component.Parse(part.Literal!);
}
