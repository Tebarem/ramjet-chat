using System.Text.Json;
using RamjetChat;
using RamjetChat.Core;

// Console test runner: no framework. Each Check prints its name on failure; the exit code is the failure count (capped at 1).
internal static class Program
{
    private static int s_run;
    private static readonly List<string> s_failed = [];

    private static readonly Func<string, bool> AllNodes = _ => true;
    private static readonly Func<string, bool> NoNodes = _ => false;
    private static readonly Func<string, bool> NoPlayers = _ => false;
    private static readonly Func<string, bool> NoUrls = _ => false;
    private static readonly Func<string, bool> AnyUrl = _ => true;

    private static readonly MarkupOptions Markup = new("#ffaa00", new Dictionary<string, string> { ["smile"] = "\u263A" }, false);
    private static readonly MarkupOptions LegacyMarkup = new("#ffaa00", new Dictionary<string, string>(), true);

    private static void Check(bool ok, string name)
    {
        s_run++;
        if (!ok) s_failed.Add(name);
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        s_run++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            s_failed.Add($"{name}: expected [{expected}] got [{actual}]");
    }

    private static int Main()
    {
        Templates();
        Selection();
        Gate();
        MentionFinder();
        Filters();
        Limits();
        DurationParsing();
        Ignores();
        LogLines();
        ConfigFile();

        foreach (string f in s_failed) Console.Error.WriteLine("FAIL " + f);
        Console.WriteLine($"{s_run - s_failed.Count}/{s_run} checks passed");
        return s_failed.Count == 0 ? 0 : 1;
    }

    private static void Templates()
    {
        var parts = FormatTemplate.Parse("{prefix}{display}{suffix} {message}");
        Equal(5, parts.Count, "template: default parts");
        var split = FormatTemplate.Split(parts);
        Equal(3, split.Before.Count, "template: before the message");
        Equal(0, split.After.Count, "template: nothing after the message");

        var tail = FormatTemplate.Split(FormatTemplate.Parse("{name} says {message}!"));
        Equal("!", FormatTemplate.RenderPlain(tail.After, _ => ""), "template: text after the message");

        string plain = FormatTemplate.RenderPlain(FormatTemplate.Parse("{name}: {message}"), t => t == TemplateToken.Name ? "Steve" : "X");
        Equal("Steve: X", plain, "template: name and message render");
        Equal("{foo} X", FormatTemplate.RenderPlain(FormatTemplate.Parse("{foo} {message}"), _ => "X"), "template: unknown token stays literal");
        Equal("hi X", FormatTemplate.RenderPlain(FormatTemplate.Parse("hi"), _ => "X"), "template: no message gets one appended");
        Equal("A X", FormatTemplate.RenderPlain(FormatTemplate.Parse("A {message}"), _ => "X"), "template: literal text kept");
    }

    private static void Selection()
    {
        var names = new[] { "staff", "vip", "default" };
        Equal(1, FormatSelector.Select(names, n => n == "vip"), "select: first held format wins in order");
        Equal(0, FormatSelector.Select(names, n => n is "staff" or "vip"), "select: highest precedence first");
        Equal(2, FormatSelector.Select(names, NoNodes), "select: default when none held");
        Equal(0, FormatSelector.Select(["default"], AllNodes), "select: a single format is the default");
        Equal(0, FormatSelector.Select(names, _ => true), "select: the first format wins when every node is held");
    }

    private static void Gate()
    {
        var plain = MiniMessageGate.Build("hello", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("hello", plain.Markup, "gate: plain text");
        Check(!plain.Changed, "gate: plain text needs no decoration");

        var lone = MiniMessageGate.Build("a<b", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("a\\<b", lone.Markup, "gate: a lone < is escaped");
        Check(!lone.Changed, "gate: escaping alone is not a visible change");

        var red = MiniMessageGate.Build("<red>hi</red>", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("<red>hi</red>", red.Markup, "gate: named colour kept with its close");
        Check(red.Changed, "gate: a kept colour is a change");

        var denied = MiniMessageGate.Build("<red>hi", Markup, NoNodes, NoPlayers, NoUrls);
        Equal("hi", denied.Markup, "gate: denied colour stripped to its text");
        Check(!denied.Changed, "gate: stripped tag is not a change");

        var open = MiniMessageGate.Build("<bold>x", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("<bold>x</bold>", open.Markup, "gate: open style is closed at the end");

        var hex = MiniMessageGate.Build("<#ff0000>x", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("<#ff0000>x</#ff0000>", hex.Markup, "gate: hex colour");
        var hexDenied = MiniMessageGate.Build("<#ff0000>x", Markup, n => n != "chat.color.hex", NoPlayers, NoUrls);
        Equal("x", hexDenied.Markup, "gate: hex needs its own node");

        var hover = MiniMessageGate.Build("<hover:show_text:Hi>x</hover>", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("<hover:show_text:Hi>x</hover>", hover.Markup, "gate: hover with text");
        Equal("x", MiniMessageGate.Build("<hover:show_text:Hi>x</hover>", Markup, NoNodes, NoPlayers, NoUrls).Markup, "gate: hover denied");

        var badUrl = MiniMessageGate.Build("<click:open_url:https://evil.com>x", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("x", badUrl.Markup, "gate: open_url to a host that is not allowed is stripped");
        var goodUrl = MiniMessageGate.Build("<click:open_url:https://ex.com/a>x", Markup, AllNodes, NoPlayers, AnyUrl);
        Equal("<click:open_url:https://ex.com/a>x</click>", goodUrl.Markup, "gate: allowed open_url");
        var run = MiniMessageGate.Build("<click:run_command:/op>x", Markup, n => n != "chat.click.run", NoPlayers, NoUrls);
        Equal("x", run.Markup, "gate: run_command needs the staff node");
        var suggest = MiniMessageGate.Build("<click:suggest_command:/msg>x", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("<click:suggest_command:/msg>x</click>", suggest.Markup, "gate: suggest_command");

        var unknown = MiniMessageGate.Build("<foo>x", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("\\<foo>x", unknown.Markup, "gate: unknown tag shows as typed");
        Equal("\\</foo>", MiniMessageGate.Build("</foo>", Markup, AllNodes, NoPlayers, NoUrls).Markup, "gate: unknown closing tag shows as typed");

        var reset = MiniMessageGate.Build("<red>a<reset>b", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("<red>a<reset>b", reset.Markup, "gate: reset clears the open frames");

        var escaped = MiniMessageGate.Build("\\<red>", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("\\<red>", escaped.Markup, "gate: an escaped < stays text");
        Check(!escaped.Changed, "gate: escaped text is not a change");

        var emoji = MiniMessageGate.Build("hi :smile:", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("hi \u263A", emoji.Markup, "gate: known shortcode becomes its glyph");
        Check(emoji.Changed, "gate: glyph is a change");
        Equal(":nope:", MiniMessageGate.Build(":nope:", Markup, AllNodes, NoPlayers, NoUrls).Markup, "gate: unknown shortcode stays");
        Equal("12:30:45", MiniMessageGate.Build("12:30:45", Markup, AllNodes, NoPlayers, NoUrls).Markup, "gate: times are not emoji");

        var legacy = MiniMessageGate.Build("&cred", LegacyMarkup, AllNodes, NoPlayers, NoUrls);
        Equal("<red>red</red>", legacy.Markup, "gate: legacy colour code goes through the gate");
        Equal("&cred", MiniMessageGate.Build("&cred", Markup, AllNodes, NoPlayers, NoUrls).Markup, "gate: legacy codes off by default");
        Equal("x", MiniMessageGate.Build("&cx", LegacyMarkup, NoNodes, NoPlayers, NoUrls).Markup, "gate: legacy colour needs the node");
        Equal("fish & chips", MiniMessageGate.Build("fish & chips", LegacyMarkup, AllNodes, NoPlayers, NoUrls).Markup, "gate: a lone ampersand is text");

        Equal("#ffaa00", MiniMessageGate.SafeMentionColor("#FFAA00"), "gate: mention colour lower case");
        Equal("gold", MiniMessageGate.SafeMentionColor("nope"), "gate: bad mention colour falls back");
    }

    private static void MentionFinder()
    {
        var spans = new List<TextSpan>();
        Mentions.Find("hi @Steve and @steve2 @Bob", n => n.Equals("steve", StringComparison.OrdinalIgnoreCase), spans);
        Equal(1, spans.Count, "mentions: one match");
        Equal(new TextSpan(3, 6), spans[0], "mentions: span covers the @ and the name, case ignored");

        var address = new List<TextSpan>();
        Mentions.Find("mail a@steve", n => true, address);
        Equal(0, address.Count, "mentions: an @ after a letter is not a mention");

        var highlighted = MiniMessageGate.Build("hi @Steve", Markup, AllNodes, n => n == "Steve", NoUrls);
        Equal("hi <#ffaa00><bold>@Steve</bold></#ffaa00>", highlighted.Markup, "mentions: highlight markup");
        Check(highlighted.Changed, "mentions: highlight is a change");

        var noPlayer = MiniMessageGate.Build("hi @Bob", Markup, AllNodes, NoPlayers, NoUrls);
        Equal("hi @Bob", noPlayer.Markup, "mentions: non-player not highlighted");
    }

    private static void Filters()
    {
        var words = new WordFilter(["badword"]);
        var spans = new List<TextSpan>();
        Equal(1, words.Find("this is BADWORD!", spans), "words: whole word, any case");
        Equal("0000000011111110", FilterMask.Bits("this is BADWORD!", spans), "words: mask bits per UTF-16 unit");
        Equal(0, words.Find("badwords", new List<TextSpan>()), "words: part of a word does not match");
        Equal(0, new WordFilter([]).Find("badword", new List<TextSpan>()), "words: empty list matches nothing");

        Equal("evil.com", LinkScan.HostOf("https://evil.com/x") ?? "", "links: scheme host");
        Equal("example.com", LinkScan.HostOf("example.com") ?? "", "links: bare domain");
        Check(LinkScan.HostOf("hello.there") is null, "links: a word with a dot is not a link");
        Check(LinkScan.HostOf("end.Thanks") is null, "links: capitalised word is not a link");
        Equal("www.foo.bar", LinkScan.HostOf("www.foo.bar") ?? "", "links: www host");
        Equal("1.2.3.4", LinkScan.HostOf("1.2.3.4") ?? "", "links: IPv4 host");
        Check(LinkScan.HostOf("3.5") is null, "links: a decimal number is not a link");
        Check(!LinkScan.HasDisallowedLink("visit example.org now", ["example.org"]), "links: allowed domain passes");
        Check(!LinkScan.HasDisallowedLink("see sub.example.org", ["example.org"]), "links: subdomain of an allowed domain passes");
        Check(LinkScan.HasDisallowedLink("see evil.net", ["example.org"]), "links: other domain is refused");
        Check(LinkScan.HasDisallowedLink("example.org.evil.net", ["example.org"]), "links: allowed name as a prefix is refused");

        Check(CapsCheck.IsShouting("HELLO WORLD", 8, 0.75), "caps: shouting");
        Check(!CapsCheck.IsShouting("HELLO", 8, 0.75), "caps: too short to judge");
        Check(!CapsCheck.IsShouting("hello WORLD", 8, 0.75), "caps: mostly lower case");
        Equal(FilterAction.Block, FilterActions.Parse(" BLOCK ", FilterAction.Log), "actions: parse ignores case and spaces");
        Equal(FilterAction.Log, FilterActions.Parse("nope", FilterAction.Log), "actions: unknown falls back");
    }

    private static void Limits()
    {
        var limits = new ThrottleLimits(1000, 0, 0, 3, 10_000);
        var s = new ThrottleState();
        Check(Throttle.Admit(s, ChannelKind.Global, limits, 0, out _), "cooldown: first message");
        Check(!Throttle.Admit(s, ChannelKind.Global, limits, 500, out long wait) && wait == 500, "cooldown: refused with the wait");
        Check(Throttle.Admit(s, ChannelKind.Global, limits, 1200, out _), "cooldown: after the cooldown");

        var s2 = new ThrottleState();
        var noCooldown = new ThrottleLimits(0, 0, 0, 3, 10_000);
        Check(Throttle.Admit(s2, ChannelKind.Local, noCooldown, 0, out _), "window: first");
        Check(Throttle.Admit(s2, ChannelKind.Local, noCooldown, 1, out _), "window: second");
        Check(Throttle.Admit(s2, ChannelKind.Local, noCooldown, 2, out _), "window: third");
        Check(!Throttle.Admit(s2, ChannelKind.Local, noCooldown, 3, out long windowWait) && windowWait == 9997, "window: fourth refused until the window ends");
        Check(Throttle.Admit(s2, ChannelKind.Local, noCooldown, 10_000, out _), "window: new window admits again");

        var s3 = new ThrottleState();
        var perChannel = new ThrottleLimits(5000, 0, 0, 0, 0);
        Check(Throttle.Admit(s3, ChannelKind.Global, perChannel, 0, out _), "channels: global");
        Check(Throttle.Admit(s3, ChannelKind.Local, perChannel, 1, out _), "channels: local has its own cooldown");

        var r = new ThrottleState();
        Equal(1, Throttle.ObserveRepeat(r, "hi", 0, 30_000), "repeat: first");
        Equal(2, Throttle.ObserveRepeat(r, "hi", 100, 30_000), "repeat: second");
        Equal(3, Throttle.ObserveRepeat(r, "hi", 200, 30_000), "repeat: third");
        Equal(1, Throttle.ObserveRepeat(r, "yo", 300, 30_000), "repeat: different text starts again");
        Equal(1, Throttle.ObserveRepeat(r, "yo", 40_000, 30_000), "repeat: outside the window starts again");

        Equal("hello world", Throttle.Normalise("  Hello   WORLD  "), "normalise: case and spaces");
        Check(Radius.Within(3, 4, 0, 5), "radius: the edge counts");
        Check(!Radius.Within(3, 4, 1, 5), "radius: outside");
        Check(Radius.Within(1000, 0, 0, 0), "radius: zero means the whole world");
    }

    private static void DurationParsing()
    {
        Check(Durations.TryParse("30s", out var s) && s == TimeSpan.FromSeconds(30), "durations: seconds");
        Check(Durations.TryParse("10m", out var m) && m == TimeSpan.FromMinutes(10), "durations: minutes");
        Check(Durations.TryParse("1h30m", out var hm) && hm == TimeSpan.FromMinutes(90), "durations: a sum");
        Check(Durations.TryParse("1w", out var w) && w == TimeSpan.FromDays(7), "durations: weeks");
        Check(Durations.TryParse("perm", out var perm) && perm is null, "durations: permanent");
        Check(Durations.TryParse("forever", out var forever) && forever is null, "durations: forever");
        Check(!Durations.TryParse("", out _), "durations: empty is invalid");
        Check(!Durations.TryParse("5", out _), "durations: a number without a unit");
        Check(!Durations.TryParse("0m", out _), "durations: zero is invalid");
        Check(!Durations.TryParse("1x", out _), "durations: unknown unit");
        Check(!Durations.TryParse("400000d", out _), "durations: longer than the maximum");
    }

    private static void Ignores()
    {
        var book = new IgnoreBook<int>();
        Check(book.Add(1, 2), "ignore: add");
        Check(!book.Add(1, 2), "ignore: adding twice changes nothing");
        Check(book.IsIgnoring(1, 2), "ignore: one way");
        Check(!book.IsIgnoring(2, 1), "ignore: not the other way");
        Check(!book.Add(3, 3), "ignore: nobody ignores themselves");
        Equal(1, book.Count, "ignore: count");
        Check(book.Remove(1, 2) && book.Count == 0, "ignore: remove");
        Check(!book.Remove(1, 2), "ignore: removing again");
    }

    private static void LogLines()
    {
        var at = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        string line = ChatLogLine.Format(at, "global", "id", "Steve", "-", "a\tb\nc");
        Equal("2026-10-16T12:00:00.000Z\tglobal\tid\tSteve\t-\ta b c", line, "log: line format, tabs and breaks flattened");
        Check(ChatLogLine.IsFrom(line, "steve"), "log: name matches ignoring case");
        Check(!ChatLogLine.IsFrom(line, "ste"), "log: a part of the name does not match");
        Check(!ChatLogLine.IsFrom(line, "id"), "log: the id is not the name");
    }

    private static void ConfigFile()
    {
        var defaults = new ChatConfig();
        Equal(1, defaults.Formats.Count, "config: one default format");
        Equal(100, defaults.LocalRadius, "config: local radius default");
        Equal("block", defaults.LinkAction, "config: links blocked by default");
        Equal(5, defaults.Emoji.Count, "config: default emoji");

        string json = JsonSerializer.Serialize(defaults, ChatJson.Default.ChatConfig);
        var back = JsonSerializer.Deserialize(json, ChatJson.Default.ChatConfig);
        Check(back is not null && back.LocalRadius == 100 && back.Emoji.Count == 5, "config: round trip");

        var edited = JsonSerializer.Deserialize("{\"WordAction\":\"block\",\"Words\":[\"x\"],\"LocalRadius\":0}", ChatJson.Default.ChatConfig);
        Check(edited is not null && edited.WordAction == "block" && edited.Words.Count == 1 && edited.LocalRadius == 0, "config: partial file keeps defaults");
    }
}
