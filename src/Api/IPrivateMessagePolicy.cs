namespace RamjetChat;

// The chat plugin's private message policy, offered as a service for plugins that deliver private messages themselves and want the same
// rules and formatting. Plugins that only hand a message over use the engine's Mc.Scripting.Services.IPrivateMessages instead.

/// <summary>Whether a private message may go out, as <see cref="IPrivateMessagePolicy.Check"/> decides it.</summary>
public enum PrivateMessageVerdict
{
    /// <summary>It may go out.</summary>
    Allowed,
    /// <summary>The sender does not hold <c>chat.msg</c>.</summary>
    NoPermission,
    /// <summary>The sender is muted (engine chat mute).</summary>
    Muted,
    /// <summary>The target ignores the sender (and the sender cannot bypass ignores).</summary>
    Ignored,
}

/// <summary>Private message policy and formatting. Registered with the service manager by the chat plugin while it is enabled.</summary>
/// <remarks>Delivery stays with the caller: the engine's /msg executor sends signed lines, and a plugin that delivers a message itself sends
/// it as an unsigned chat line. Call <see cref="Check"/> first, <see cref="Decorate"/> for the body, and <see cref="Delivered"/> after the
/// message went out (it records the reply target and the social spy copies). Thread: tick thread.</remarks>
public interface IPrivateMessagePolicy
{
    /// <summary>Whether a message from sender to target may go out. Has no side effects.</summary>
    /// <param name="sender">The player who typed the message.</param>
    /// <param name="target">The recipient.</param>
    PrivateMessageVerdict Check(Mc.Scripting.Player sender, Mc.Scripting.Player target);

    /// <summary>The decorated body of a private message (colours, hover and click the sender may use), or null when the text is shown as typed.</summary>
    /// <param name="sender">The sender.</param>
    /// <param name="text">The typed text.</param>
    Mc.Scripting.Text.Component? Decorate(Mc.Scripting.Player sender, string text);

    /// <summary>Records a delivered message: the reply target of both players and the social spy copies.</summary>
    /// <param name="sender">The sender.</param>
    /// <param name="target">The recipient.</param>
    /// <param name="text">The typed text.</param>
    void Delivered(Mc.Scripting.Player sender, Mc.Scripting.Player target, string text);

    /// <summary>The player the given player last messaged or was messaged by, when that player is online.</summary>
    /// <param name="player">The player.</param>
    Mc.Scripting.Player? LastPartner(Mc.Scripting.Player player);
}
