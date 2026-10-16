using System.Linq;
using Mc.Scripting.Text;
using RamjetChat.Core;

namespace RamjetChat;

// The chat plugin's commands. /msg, /tell and /w are the engine's (vanilla names cannot be registered by a plugin); /r, /ignore, /socialspy,
// the mute family, /chatlog, /mentions and /chat reload are here. A name the engine already owns is refused by the registry: it is logged, the
// rest still register.

internal sealed partial class ChatEngine
{
    private const int kMuteListLimit = 20;
    private const int kDefaultLogLines = 20;
    private const int kMaxLogLines = 200;

    /// <summary>Registers the commands. Called once at enable.</summary>
    public void RegisterCommands(CommandRegistry commands)
    {
        Register(commands, "ch", Command.Literal("ch").Then(Command.Argument("channel", Arg.Word())
            .Executes(ctx => { SwitchChannel(ctx.RequirePlayer(), ctx.GetString("channel")); })), "Switch the channel you talk in: global, local or staff");
        Register(commands, "g", Command.Literal("g").Executes(ctx => { ChangeChannel(ctx.RequirePlayer(), ChannelKind.Global); }), "Talk in global chat");
        Register(commands, "l", Command.Literal("l").Executes(ctx => { ChangeChannel(ctx.RequirePlayer(), ChannelKind.Local); }), "Talk in local chat");
        Register(commands, "r", Command.Literal("r").Permission(Nodes.Reply)
            .Then(Command.Argument("message", Arg.GreedyString()).Executes(ctx => { Reply(ctx.RequirePlayer(), ctx.GetString("message")); })),
            "Reply to your last private message");
        Register(commands, "ignore", Command.Literal("ignore").Permission(Nodes.Ignore)
            .Then(Command.Argument("player", Arg.Word()).Executes(ctx => { Ignore(ctx.RequirePlayer(), ctx.GetString("player"), true); })),
            "Hide a player's chat and private messages from you");
        Register(commands, "unignore", Command.Literal("unignore").Permission(Nodes.Ignore)
            .Then(Command.Argument("player", Arg.Word()).Executes(ctx => { Ignore(ctx.RequirePlayer(), ctx.GetString("player"), false); })),
            "Stop ignoring a player");
        Register(commands, "ignorelist", Command.Literal("ignorelist").Permission(Nodes.Ignore).Executes(ctx => { IgnoreList(ctx); }), "List the players you ignore");
        Register(commands, "socialspy", Command.Literal("socialspy").Permission(Nodes.SocialSpy).Executes(ctx => { ToggleSpy(ctx.RequirePlayer()); }),
            "Toggle copies of private messages");
        Register(commands, "mentions", Command.Literal("mentions").Permission(Nodes.Mention).Executes(ctx => { ToggleMentions(ctx.RequirePlayer()); }),
            "Toggle the sound when someone mentions you");
        Register(commands, "mute", Command.Literal("mute").Permission(Nodes.Mute)
            .Then(Command.Argument("player", Arg.Word())
                .Executes(ctx => { Mute(ctx, ctx.GetString("player"), null, null); })
                .Then(Command.Argument("duration", Arg.Word())
                    .Executes(ctx => { Mute(ctx, ctx.GetString("player"), ctx.GetString("duration"), null); })
                    .Then(Command.Argument("reason", Arg.GreedyString())
                        .Executes(ctx => { Mute(ctx, ctx.GetString("player"), ctx.GetString("duration"), ctx.GetString("reason")); })))),
            "Mute a player's chat: /mute <player> [duration] [reason]");
        Register(commands, "unmute", Command.Literal("unmute").Permission(Nodes.Mute)
            .Then(Command.Argument("player", Arg.Word()).Executes(ctx => { Unmute(ctx, ctx.GetString("player")); })),
            "Lift a chat mute");
        Register(commands, "mutelist", Command.Literal("mutelist").Permission(Nodes.Mute).Executes(ctx => { MuteList(ctx); }), "List the chat mutes");
        Register(commands, "chatlog", Command.Literal("chatlog").Permission(Nodes.ChatLog)
            .Then(Command.Argument("player", Arg.Word())
                .Executes(ctx => { ChatLogLines(ctx, ctx.GetString("player"), kDefaultLogLines); })
                .Then(Command.Argument("count", Arg.Int(1, kMaxLogLines))
                    .Executes(ctx => { ChatLogLines(ctx, ctx.GetString("player"), ctx.GetInt("count")); }))),
            "Show a player's recent chat lines");
        Register(commands, "chat", Command.Literal("chat").Permission(Nodes.Reload)
            .Then(Command.Literal("reload").Executes(ctx => { ReloadConfig(ctx); })), "Reload the chat config");
    }

    private void Register(CommandRegistry commands, string name, CommandNode root, string description)
    {
        try
        {
            commands.Register(root, description: description);
        }
        catch (InvalidOperationException e)
        {
            _host.Log.Warn("command /" + name + " not registered: " + e.Message);
        }
    }

    private void SwitchChannel(Player player, string name)
    {
        ChannelKind channel = name.ToLowerInvariant() switch
        {
            "global" => ChannelKind.Global,
            "local" => ChannelKind.Local,
            "staff" => ChannelKind.Staff,
            _ => throw new CommandFailure("Unknown channel " + name + ". Use global, local or staff."),
        };
        ChangeChannel(player, channel);
    }

    private void ChangeChannel(Player player, ChannelKind channel)
    {
        if (channel == ChannelKind.Staff && !player.HasPermission(Nodes.StaffChannel)) throw new CommandFailure("You cannot use the staff channel.");
        SetChannel(player.Uuid, channel);
        player.SendMessage(Component.Text("You talk in the " + ChannelName(channel) + " channel now. Write !text to send one global message."));
    }

    private void Ignore(Player me, string name, bool add)
    {
        Uuid target = LookupId(name);
        if (target == me.Uuid) throw new CommandFailure("You cannot ignore yourself.");
        if (add)
        {
            if (Server.GetPlayer(target) is { } other && other.HasPermission(Nodes.IgnoreBypass)) throw new CommandFailure(name + " cannot be ignored.");
            if (!_ignores.Add(me.Uuid, target)) throw new CommandFailure("You already ignore " + name + ".");
            me.SendMessage(Component.Text("You ignore " + name + " now."));
        }
        else
        {
            if (!_ignores.Remove(me.Uuid, target)) throw new CommandFailure("You do not ignore " + name + ".");
            me.SendMessage(Component.Text("You no longer ignore " + name + "."));
        }
        SaveLists();
    }

    private void IgnoreList(CommandContext ctx)
    {
        Player me = ctx.RequirePlayer();
        var names = _ignores.TargetsOf(me.Uuid).Select(NameOf).ToList();
        ctx.Reply(names.Count == 0 ? "You ignore nobody." : "You ignore: " + string.Join(", ", names));
    }

    private void ToggleSpy(Player me)
    {
        if (_spies.Remove(me.Uuid)) me.SendMessage(Component.Text("Social spy is off."));
        else
        {
            _spies.Add(me.Uuid);
            me.SendMessage(Component.Text("Social spy is on."));
        }
    }

    private void ToggleMentions(Player me)
    {
        if (_mentionsOff.Remove(me.Uuid)) me.SendMessage(Component.Text("Mention sounds are on."));
        else
        {
            _mentionsOff.Add(me.Uuid);
            me.SendMessage(Component.Text("Mention sounds are off."));
        }
        SaveLists();
    }

    private void Mute(CommandContext ctx, string name, string? duration, string? reason)
    {
        Uuid target = LookupId(name);
        if (Server.GetPlayer(target) is { } online && online.HasPermission(Nodes.MuteExempt)
            && !(ctx.Source.IsConsole || ctx.Source.Player?.HasPermission(Nodes.MuteForce) == true))
            throw new CommandFailure(name + " cannot be muted.");
        DateTimeOffset? expires = null;
        if (duration is not null)
        {
            if (!Durations.TryParse(duration, out TimeSpan? span)) throw new CommandFailure("Bad duration " + duration + ". Use 30s, 10m, 2h, 3d, 1w or perm.");
            if (span is { } length) expires = DateTimeOffset.UtcNow + length;
        }
        if (reason is not null && reason.Length == 0) reason = null;
        if (!ChatMute.Set(target, expires, reason)) ctx.Reply(name + " was already muted that way.");
        else ctx.Reply("Muted " + name + (expires is { } at ? " until " + at.ToString("u") : " for good") + (reason is null ? "." : ": " + reason));
    }

    private void Unmute(CommandContext ctx, string name)
    {
        Uuid target = LookupId(name);
        if (!ChatMute.Clear(target)) throw new CommandFailure(name + " is not muted.");
        ctx.Reply("Unmuted " + name + ".");
    }

    private void MuteList(CommandContext ctx)
    {
        var mutes = ChatMute.List();
        if (mutes.Count == 0)
        {
            ctx.Reply("No one is muted.");
            return;
        }
        foreach (var mute in mutes.Take(kMuteListLimit))
        {
            string until = mute.Expires is { } at ? "until " + at.ToString("u") : "for good";
            ctx.Reply(NameOf(mute.Player) + " " + until + (mute.Reason is null ? "" : " (" + mute.Reason + ")"));
        }
    }

    private void ChatLogLines(CommandContext ctx, string name, int count)
    {
        CommandSource source = ctx.Source;
        string folder = _log.Folder;
        var read = _host.Scheduler.RunAsync(() => ChatLog.ReadFor(folder, name, count));
        read.ContinueWith(t => _host.Scheduler.Post(() =>
        {
            if (t.IsFaulted)
            {
                source.Failure("The chat log could not be read.");
                return;
            }
            if (t.Result.Length == 0)
            {
                source.Failure("No chat lines from " + name + " in the log.");
                return;
            }
            foreach (string line in t.Result) source.Success(line.Replace('\t', ' '));
        }));
    }

    private void ReloadConfig(CommandContext ctx)
    {
        if (!_config.Reload()) throw new CommandFailure("The chat config was not reloaded: see the log for the error.");
        ctx.Reply("Chat config reloaded.");
    }

    // Online players first, then the profile cache. An unknown name is a failure, not a guess.
    private static Uuid LookupId(string name)
    {
        if (Server.GetPlayer(name) is { } online) return online.Uuid;
        return OfflinePlayers.Find(name) ?? throw new CommandFailure("Unknown player " + name + ".");
    }

    private static string NameOf(Uuid id) =>
        Server.GetPlayer(id)?.Name ?? OfflinePlayers.Get(id)?.Name ?? id.ToString();
}
