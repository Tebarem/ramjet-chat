using System.Text;

namespace RamjetChat.Core;

// Per-player message limits: a cooldown per channel, a message count per window, and the repeat count. Pure: the caller passes the time, so
// the tests do not need a clock. Times are milliseconds.

/// <summary>The channels a message can go to.</summary>
public enum ChannelKind : byte
{
    /// <summary>Everybody.</summary>
    Global = 0,
    /// <summary>Players within the radius of the sender.</summary>
    Local = 1,
    /// <summary>Players with the staff channel node.</summary>
    Staff = 2,
}

/// <summary>The limits from config.</summary>
public sealed record ThrottleLimits(int GlobalCooldownMs, int LocalCooldownMs, int StaffCooldownMs, int MessagesPerWindow, int WindowMs)
{
    /// <summary>The cooldown of a channel.</summary>
    public int CooldownOf(ChannelKind channel) => channel switch
    {
        ChannelKind.Local => LocalCooldownMs,
        ChannelKind.Staff => StaffCooldownMs,
        _ => GlobalCooldownMs,
    };
}

/// <summary>What the limits remember about one player.</summary>
public sealed class ThrottleState
{
    private const long kNever = long.MinValue;

    internal readonly long[] LastByChannel = [kNever, kNever, kNever];
    internal long WindowStart = kNever;
    internal int WindowCount;
    internal string RepeatText = "";
    internal long RepeatAt = kNever;
    internal int RepeatCount;

    /// <summary>When the player last sent a message to the channel (ms), or long.MinValue when never.</summary>
    public long LastSentOn(ChannelKind channel) => LastByChannel[(int)channel];
}

/// <summary>Admits or refuses a message by the cooldown and the window, and counts repeats.</summary>
public static class Throttle
{
    /// <summary>Decides whether a message may go out now. When it may, it is recorded (the cooldown and the window count it).</summary>
    /// <param name="state">The player's state.</param>
    /// <param name="channel">The channel the message goes to.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="nowMs">The time now, in milliseconds.</param>
    /// <param name="waitMs">When refused: how long to wait (ms).</param>
    /// <returns>True when the message is admitted.</returns>
    public static bool Admit(ThrottleState state, ChannelKind channel, ThrottleLimits limits, long nowMs, out long waitMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(limits);
        waitMs = 0;
        int cooldown = limits.CooldownOf(channel);
        long last = state.LastByChannel[(int)channel];
        if (cooldown > 0 && last != long.MinValue && nowMs - last < cooldown)
        {
            waitMs = cooldown - (nowMs - last);
            return false;
        }
        if (limits.MessagesPerWindow > 0 && limits.WindowMs > 0)
        {
            if (state.WindowStart == long.MinValue || nowMs - state.WindowStart >= limits.WindowMs)
            {
                state.WindowStart = nowMs;
                state.WindowCount = 0;
            }
            if (state.WindowCount >= limits.MessagesPerWindow)
            {
                waitMs = state.WindowStart + limits.WindowMs - nowMs;
                return false;
            }
            state.WindowCount++;
        }
        state.LastByChannel[(int)channel] = nowMs;
        return true;
    }

    /// <summary>Records a message for the repeat check. Identical messages (after <see cref="Normalise"/>) that follow each other within the
    /// window count up; anything else starts again at one.</summary>
    /// <param name="state">The player's state.</param>
    /// <param name="normalised">The message, normalised.</param>
    /// <param name="nowMs">The time now, in milliseconds.</param>
    /// <param name="windowMs">The window (ms).</param>
    /// <returns>How many identical messages in a row there are now, this one included.</returns>
    public static int ObserveRepeat(ThrottleState state, string normalised, long nowMs, int windowMs)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (normalised.Length > 0 && normalised == state.RepeatText && state.RepeatAt != long.MinValue && nowMs - state.RepeatAt <= windowMs)
            state.RepeatCount++;
        else
            state.RepeatCount = 1;
        state.RepeatText = normalised;
        state.RepeatAt = nowMs;
        return state.RepeatCount;
    }

    /// <summary>Lower case, single spaces, no leading or trailing space: the form the repeat check compares.</summary>
    public static string Normalise(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool space = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}

/// <summary>The radius of local chat.</summary>
public static class Radius
{
    /// <summary>Whether a point is within the radius of the sender (distance in blocks, the edge counts). A radius of 0 or less means the whole
    /// world, so the caller only checks the world then.</summary>
    /// <param name="dx">X distance.</param>
    /// <param name="dy">Y distance.</param>
    /// <param name="dz">Z distance.</param>
    /// <param name="radius">The radius in blocks.</param>
    public static bool Within(double dx, double dy, double dz, int radius) =>
        radius <= 0 || dx * dx + dy * dy + dz * dz <= (double)radius * radius;
}
