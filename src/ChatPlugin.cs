using Mc.Scripting.Services;

namespace RamjetChat;

// Chat: formats, gated markup, channels, private messages, mentions, filters, cooldowns, mute and the chat log. The plugin's state is in
// ChatEngine; this class only wires the events, the commands and the service.

/// <summary>The chat plugin (id "chat").</summary>
[PluginInfo("Chat", Version = "0.1.0", Description = "Chat formats, channels, private messages, mentions, filters and the chat log")]
public sealed class ChatPlugin : Plugin
{
    private ChatEngine _engine = null!;

    /// <inheritdoc/>
    protected override void OnEnable()
    {
        var config = Config.Load(ChatJson.Default.ChatConfig);
        _engine = new ChatEngine(this, config);
        _engine.Apply(config.Value);
        config.Changed += value => _engine.Apply(value);
        _engine.LoadPersisted();
        _engine.StartLog();
        _engine.RegisterCommands(Commands);

        Events.On((ref PlayerChatEvent e) => _engine.OnPlayerChat(ref e), EventPriority.Normal);
        Events.On((ref PrivateMessageEvent e) => _engine.OnPrivateMessage(ref e), EventPriority.Normal);
        Events.On((ref PrivateMessageEvent e) => _engine.OnPrivateMessageDelivered(ref e), EventPriority.Monitor);
        Events.On((ref PlayerQuitEvent e) => _engine.Forget(e.Player.Uuid), EventPriority.Monitor);

        Services.Register<IPrivateMessages>(_engine, ServicePriority.Normal);
        Services.Register<IPrivateMessagePolicy>(_engine, ServicePriority.Normal);
        Log.Info("enabled: " + _engine.Current.FormatNames.Length + " format(s), local radius " + _engine.Current.LocalRadius);
    }
}
