using Mc.Scripting.Inventories;
using Mc.Scripting.Services;
using Mc.Scripting.Text;
using RamjetChat.Core;

namespace RamjetChat;

// The message path (PlayerChat). Order: channel, cooldown and window, text filters, recipients, formatting. The signed text (Message) is never
// changed: restyling goes into SenderName, Decoration and, for hidden words, the filter mask. Only the chat type stays Chat.

internal sealed partial class ChatEngine
{
    // What the text filters decided about one message.
    private struct FilterScreen
    {
        public bool Block;
        public bool Mask;
        public FilterRule Rule;
        public string Flags;
        public string? Warning;
    }

    /// <summary>The PlayerChat handler (awaited; it must stay cheap).</summary>
    public void OnPlayerChat(ref PlayerChatEvent e)
    {
        Player sender = e.Player;
        string raw = e.Message.ToString();
        if (raw.Length == 0) return;
        Settings s = _s;
        Uuid id = sender.Uuid;
        long now = Environment.TickCount64;

        string typed = raw;
        ChannelKind channel = ChannelOf(id);
        if (raw.Length > 1 && raw[0] == '!')
        {
            typed = raw[1..];
            channel = ChannelKind.Global;
        }
        if (channel == ChannelKind.Staff && !sender.HasPermission(Nodes.StaffChannel)) channel = ChannelKind.Global;

        if (!sender.HasPermission(Nodes.BypassSpam) && !Throttle.Admit(StateOf(id), channel, s.Limits, now, out long waitMs))
        {
            e.Cancelled = true;
            sender.SendMessage(Component.Text("Slow down: you can send again in " + Math.Max(1, (waitMs + 999) / 1000) + " s."));
            return;
        }

        FilterScreen screen = Screen(sender, typed, s, now);
        Log(channel, sender, screen.Flags, raw);
        if (screen.Block)
        {
            e.Cancelled = true;
            sender.SendMessage(Component.Text("Your message was not sent: it matched the chat filter (" + RuleName(screen.Rule) + ")."));
            return;
        }
        if (screen.Warning is { } warning) sender.SendMessage(Component.Text(warning));
        if (screen.Mask)
        {
            // The mask indexes the typed text; a "!" that was taken off the front is kept as an unfiltered unit.
            int offset = raw.Length - typed.Length;
            string bits = FilterMask.Bits(typed, _spans);
            e.FilterKind = ChatFilterKind.PartiallyFiltered;
            e.SetFilterBits(offset > 0 ? "0" + bits : bits);
        }

        BuildRecipients(sender, channel, s);
        if (channel != ChannelKind.Global || _ignores.Count > 0) ChatRoute.Restrict(_recipients);

        bool mentionable = typed.Contains('@') && sender.HasPermission(Nodes.Mention);
        Component? itemBody = ItemBody(typed, sender, s);
        var gate = itemBody is null
            ? MiniMessageGate.Build(
                typed,
                s.Markup,
                node => sender.HasPermission(node),
                name => mentionable && _byName.ContainsKey(name),
                url => LinkScan.HostOf(url) is { } host && LinkScan.IsAllowedHost(host, s.AllowedDomains))
            : default;
        if (mentionable && itemBody is null) PlayMentionSounds(typed, id, s);

        int formatIndex = FormatSelector.Select(s.FormatNames, n => sender.HasPermission(Nodes.FormatPrefix + n));
        FormatSlot slot = s.Formats[formatIndex];
        e.SetSenderName(RenderParts(slot.Before, slot.BeforeText, sender));

        bool shaped = itemBody is not null || gate.Changed || typed.Length != raw.Length || channel != ChannelKind.Global;
        if (!shaped) return;
        Component body = itemBody ?? (gate.Changed ? Component.Parse(gate.Markup) : Component.Text(typed));
        if (channel != ChannelKind.Global) body = Component.Join(ChannelLabel(channel), body);
        if (slot.After.Length > 0) body = Component.Join(body, RenderParts(slot.After, slot.AfterText, sender));
        e.SetDecoration(body);
    }

    /// <summary>Who may see a message: every online player for global chat (minus those who ignore the sender), the players in range for local
    /// chat, the staff for the staff channel. The sender is always included. Also fills the name lookup used for mentions.</summary>
    private void BuildRecipients(Player sender, ChannelKind channel, Settings s)
    {
        _recipients.Clear();
        _byName.Clear();
        Uuid id = sender.Uuid;
        bool ignorable = _ignores.Count > 0 && !sender.HasPermission(Nodes.IgnoreBypass);
        foreach (Player p in Server.Players)
        {
            bool self = p.Uuid == id;
            if (!self)
            {
                if (ignorable && _ignores.IsIgnoring(p.Uuid, id)) continue;
                if (channel == ChannelKind.Local && !InRange(sender, p, s.LocalRadius)) continue;
                if (channel == ChannelKind.Staff && !p.HasPermission(Nodes.StaffChannel)) continue;
            }
            _recipients.Add(p);
            _byName[p.Name] = p;
        }
    }

    private static bool InRange(Player from, Player to, int radius)
    {
        if (!from.World.Equals(to.World)) return false;
        if (radius <= 0) return true;
        Vec3d a = from.Position;
        Vec3d b = to.Position;
        return Radius.Within(b.X - a.X, b.Y - a.Y, b.Z - a.Z, radius);
    }

    private void PlayMentionSounds(string typed, Uuid sender, Settings s)
    {
        _mentions.Clear();
        Mentions.Find(typed, name => _byName.ContainsKey(name), _mentions);
        foreach (var span in _mentions)
        {
            if (!_byName.TryGetValue(typed.Substring(span.Start + 1, span.Length - 1), out Player mentioned)) continue;
            if (mentioned.Uuid == sender || _mentionsOff.Contains(mentioned.Uuid)) continue;
            mentioned.PlaySound(s.MentionSound);
        }
    }

    // [item] shows the item in the main hand as a hover. The text around it is gated like any other text (without mention highlights).
    private Component? ItemBody(string typed, Player sender, Settings s)
    {
        if (typed.IndexOf("[item]", StringComparison.OrdinalIgnoreCase) < 0 || !sender.HasPermission(Nodes.Item)) return null;
        ItemStack held = sender.HeldItem;
        if (held.Type == "minecraft:air") return null;
        var pieces = new List<Component>();
        int from = 0;
        int at;
        while ((at = typed.IndexOf("[item]", from, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            AddText(pieces, typed.Substring(from, at - from), sender, s);
            pieces.Add(ItemComponent(held));
            from = at + "[item]".Length;
        }
        AddText(pieces, typed.Substring(from), sender, s);
        return Component.Join(pieces.ToArray());
    }

    private static void AddText(List<Component> pieces, string text, Player sender, Settings s)
    {
        if (text.Length == 0) return;
        var gate = MiniMessageGate.Build(text, s.Markup, node => sender.HasPermission(node), _ => false,
            url => LinkScan.HostOf(url) is { } host && LinkScan.IsAllowedHost(host, s.AllowedDomains));
        pieces.Add(gate.Changed ? Component.Parse(gate.Markup) : Component.Text(text));
    }

    private static Component ItemComponent(ItemStack held)
    {
        string key = held.Type.StartsWith("minecraft:", StringComparison.Ordinal) ? held.Type["minecraft:".Length..] : held.Type;
        string count = held.Count > 1 ? " x" + held.Count : "";
        return Component.Text("[" + key.Replace('_', ' ') + count + "]").Color(TextColor.Aqua).OnHover(HoverEvent.ShowItem(held.Type, held.Count));
    }

    // Runs the rules in their order. A block stops nothing else (the log still gets the line); masks, warnings and flags accumulate.
    private FilterScreen Screen(Player sender, string typed, Settings s, long now)
    {
        var r = new FilterScreen { Flags = "-" };
        Uuid id = sender.Uuid;
        _spans.Clear();

        if (!sender.HasPermission(Nodes.BypassWords) && s.Words.Count > 0 && s.Words.Find(typed, _spans) > 0)
            Hit(ref r, FilterRule.Words, s.WordAction);
        if (!sender.HasPermission(Nodes.BypassLinks) && LinkScan.HasDisallowedLink(typed, s.AllowedDomains))
            Hit(ref r, FilterRule.Link, s.LinkAction);
        if (!sender.HasPermission(Nodes.BypassCaps) && CapsCheck.IsShouting(typed, s.CapsMinLetters, s.CapsRatio))
            Hit(ref r, FilterRule.Caps, s.CapsAction);
        int repeats = Throttle.ObserveRepeat(StateOf(id), Throttle.Normalise(typed), now, s.RepeatWindowMs);
        if (s.RepeatLimit > 0 && repeats >= s.RepeatLimit && !sender.HasPermission(Nodes.BypassRepeat))
            Hit(ref r, FilterRule.Repeat, s.RepeatAction);
        return r;
    }

    private static void Hit(ref FilterScreen r, FilterRule rule, FilterAction action)
    {
        // Only the word rule can hide characters; for the others a mask means a log entry.
        if (action == FilterAction.Mask && rule != FilterRule.Words) action = FilterAction.Log;
        string flag = RuleName(rule) + ":" + action.ToString().ToLowerInvariant();
        r.Flags = r.Flags == "-" ? flag : r.Flags + "," + flag;
        switch (action)
        {
            case FilterAction.Block:
                if (!r.Block) r.Rule = rule;
                r.Block = true;
                break;
            case FilterAction.Warn:
                r.Warning ??= "Warning: your message matched the " + RuleName(rule) + " rule.";
                break;
            case FilterAction.Mask:
                r.Mask = true;
                break;
        }
    }

    private void Log(ChannelKind channel, Player sender, string flags, string text) =>
        _log.Add(DateTimeOffset.UtcNow, ChannelName(channel), sender.Uuid, sender.Name, flags, text);

    private Component RenderParts(TemplatePart[] parts, Component?[] literal, Player sender)
    {
        if (parts.Length == 0) return Component.Empty;
        var pieces = new Component[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            pieces[i] = parts[i].IsToken ? TokenComponent(parts[i].Token, sender) : literal[i]!;
        return Component.Join(pieces);
    }

    private Component TokenComponent(TemplateToken token, Player sender) => token switch
    {
        TemplateToken.Name => PlayerName(Component.Text(sender.Name), sender.Name),
        TemplateToken.Display => PlayerName(sender.DisplayName ?? Component.Text(sender.Name), sender.Name),
        TemplateToken.World => Component.Text(sender.World.Key),
        TemplateToken.Prefix => Affix(sender.Uuid, AffixKind.Prefix),
        TemplateToken.Suffix => Affix(sender.Uuid, AffixKind.Suffix),
        _ => Component.Empty,
    };

    // Hover shows the name, click starts a private message to the player, insertion (shift-click) is the plain name.
    private static Component PlayerName(Component shown, string name) => shown
        .OnHover(HoverEvent.ShowText(Component.Text(name)))
        .OnClick(ClickEvent.SuggestCommand("/msg " + name + " "))
        .Insertion(name);

    private Component Affix(Uuid id, AffixKind kind)
    {
        if (_host.Services.TryGet<IPlayerMeta>() is not { } meta) return Component.Empty;
        string text = meta.AffixText(id, kind);
        return text.Length == 0 ? Component.Empty : Component.Parse(text);
    }

    private static Component ChannelLabel(ChannelKind channel) =>
        Component.Text(channel == ChannelKind.Staff ? "[Staff] " : "[Local] ").Color(TextColor.Gray);

    internal static string ChannelName(ChannelKind channel) => channel switch
    {
        ChannelKind.Local => "local",
        ChannelKind.Staff => "staff",
        _ => "global",
    };

    private static string RuleName(FilterRule rule) => rule switch
    {
        FilterRule.Words => "word list",
        FilterRule.Link => "link",
        FilterRule.Caps => "capitals",
        _ => "repetition",
    };
}
