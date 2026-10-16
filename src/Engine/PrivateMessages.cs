using Mc.Scripting.Text;
using RamjetChat.Core;

namespace RamjetChat;

// Private messages: the policy and formatting behind IPrivateMessagePolicy, the engine's IPrivateMessages service (the Essentials /msg
// override hands its messages here), the PrivateMessage event (the engine's /msg, /tell and /w), the reply command and social spy. The engine
// keeps the signed delivery of its own /msg; messages handed over by a plugin and the reply command go out as unsigned lines.

internal sealed partial class ChatEngine : IPrivateMessagePolicy, IPrivateMessages
{
    /// <inheritdoc/>
    public PrivateMessageResult Send(Player sender, Player recipient, string message)
    {
        if (Server.GetPlayer(recipient.Uuid) is null) return PrivateMessageResult.Offline;
        if (string.IsNullOrWhiteSpace(message)) return PrivateMessageResult.Refused;
        return Check(sender, recipient) switch
        {
            PrivateMessageVerdict.Allowed => Deliver(sender, recipient, message),
            PrivateMessageVerdict.Muted => PrivateMessageResult.Muted,
            PrivateMessageVerdict.Ignored => PrivateMessageResult.Ignored,
            _ => PrivateMessageResult.Refused,
        };
    }

    /// <inheritdoc/>
    public PrivateMessageVerdict Check(Player sender, Player target)
    {
        if (!sender.HasPermission(Nodes.Msg)) return PrivateMessageVerdict.NoPermission;
        if (ChatMute.Get(sender.Uuid) is not null) return PrivateMessageVerdict.Muted;
        if (_ignores.Count > 0 && _ignores.IsIgnoring(target.Uuid, sender.Uuid) && !sender.HasPermission(Nodes.IgnoreBypass))
            return PrivateMessageVerdict.Ignored;
        return PrivateMessageVerdict.Allowed;
    }

    /// <inheritdoc/>
    public Component? Decorate(Player sender, string text)
    {
        Settings s = _s;
        var gate = MiniMessageGate.Build(
            text,
            s.Markup,
            node => sender.HasPermission(node),
            _ => false,
            url => LinkScan.HostOf(url) is { } host && LinkScan.IsAllowedHost(host, s.AllowedDomains));
        return gate.Changed ? Component.Parse(gate.Markup) : null;
    }

    /// <inheritdoc/>
    public void Delivered(Player sender, Player target, string text)
    {
        _lastPartner[target.Uuid] = sender.Uuid;
        _lastPartner[sender.Uuid] = target.Uuid;
        if (_spies.Count == 0) return;
        Component line = Component.Text("[Spy] " + sender.Name + " -> " + target.Name + ": " + text).Color(TextColor.Gray);
        foreach (Uuid spy in _spies)
        {
            if (spy == sender.Uuid || spy == target.Uuid) continue;
            if (Server.GetPlayer(spy) is { } watcher && watcher.HasPermission(Nodes.SocialSpySee)) watcher.SendMessage(line);
        }
    }

    /// <inheritdoc/>
    public Player? LastPartner(Player player) =>
        _lastPartner.TryGetValue(player.Uuid, out Uuid partner) ? Server.GetPlayer(partner) : null;

    /// <summary>The PrivateMessage event, before delivery (awaited): refuses what the policy refuses, and sets the decorated body.</summary>
    public void OnPrivateMessage(ref PrivateMessageEvent e)
    {
        PrivateMessageVerdict verdict = Check(e.Sender, e.Target);
        if (verdict != PrivateMessageVerdict.Allowed)
        {
            e.Cancelled = true;
            e.Sender.SendMessage(Component.Text(Notice(verdict, e.Target.Name)));
            return;
        }
        if (Decorate(e.Sender, e.Message.ToString()) is { } body) e.SetDecoration(body);
    }

    /// <summary>The PrivateMessage event after delivery (monitor: only messages that went out): records the reply target and spy copies.</summary>
    public void OnPrivateMessageDelivered(ref PrivateMessageEvent e) => Delivered(e.Sender, e.Target, e.Message.ToString());

    /// <summary>The reply command: sends to the last partner. Delivered as unsigned chat lines, under the same policy.</summary>
    internal void Reply(Player sender, string text)
    {
        if (LastPartner(sender) is not { } target) throw new CommandFailure("Nobody to reply to.");
        PrivateMessageVerdict verdict = Check(sender, target);
        if (verdict != PrivateMessageVerdict.Allowed) throw new CommandFailure(Notice(verdict, target.Name));
        Deliver(sender, target, text);
    }

    /// <summary>Delivers an allowed message as unsigned lines in the vanilla /msg layout and records it.</summary>
    private PrivateMessageResult Deliver(Player sender, Player target, string text)
    {
        Component body = Decorate(sender, text) ?? Component.Text(text);
        Component senderName = Component.Text(sender.Name);
        Component targetName = Component.Text(target.Name);
        Chat.SendTo([target], ChatKind.MsgCommandIncoming, senderName, targetName, body);
        Chat.SendTo([sender], ChatKind.MsgCommandOutgoing, senderName, targetName, body);
        Delivered(sender, target, text);
        return PrivateMessageResult.Ok;
    }

    private static string Notice(PrivateMessageVerdict verdict, string target) => verdict switch
    {
        PrivateMessageVerdict.NoPermission => "You cannot send private messages.",
        PrivateMessageVerdict.Muted => "You are muted and cannot send private messages.",
        _ => "Your message could not be delivered to " + target + ".",
    };
}
