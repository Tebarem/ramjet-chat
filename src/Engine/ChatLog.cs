using System.Text;
using RamjetChat.Core;

namespace RamjetChat;

// The chat log: one file per UTC day under the data folder (logs/yyyy-MM-dd.log). Lines are queued on the tick thread and written in
// batches on a thread-pool task, so the tick never waits for the disk. Old files are removed at start-up.

/// <summary>Queues chat lines and writes them in batches; reads a player's recent lines for /chatlog.</summary>
internal sealed class ChatLog
{
    // The most lines one /chatlog answer scans and the most bytes it reads: a bounded answer, whatever the log size.
    private const int kScanLines = 20_000;
    private const long kScanBytes = 4L * 1024 * 1024;
    private const int kLogFiles = 4;

    private readonly string _dir;
    private readonly Scheduler _scheduler;
    private readonly Queue<string> _pending = new();

    public ChatLog(string dataFolder, Scheduler scheduler)
    {
        _dir = Path.Combine(dataFolder, "logs");
        _scheduler = scheduler;
    }

    /// <summary>The folder the logs are in.</summary>
    public string Folder => _dir;

    /// <summary>Queues a line (tick thread).</summary>
    public void Add(DateTimeOffset at, string channel, Uuid id, string name, string flags, string text) =>
        _pending.Enqueue(ChatLogLine.Format(at, channel, id.ToString(), name, flags, text));

    /// <summary>Writes the queued lines to today's file, off the tick (tick thread: it only takes the queue).</summary>
    public void Flush()
    {
        if (_pending.Count == 0) return;
        var sb = new StringBuilder();
        while (_pending.Count > 0) sb.Append(_pending.Dequeue()).Append('\n');
        string path = Path.Combine(_dir, DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".log");
        string text = sb.ToString();
        _scheduler.RunAsync(() => Append(path, text));
    }

    /// <summary>Creates the folder and removes the files older than the retention, off the tick.</summary>
    public void Start(int retentionDays)
    {
        _scheduler.RunAsync(() =>
        {
            System.IO.Directory.CreateDirectory(_dir);
            DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);
            foreach (string file in System.IO.Directory.GetFiles(_dir, "*.log"))
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        });
    }

    /// <summary>Reads the last lines of a player from the newest logs (bounded). Runs off the tick.</summary>
    /// <param name="dir">The log folder.</param>
    /// <param name="name">The player's name.</param>
    /// <param name="count">How many lines to return at most.</param>
    /// <returns>The lines, oldest first.</returns>
    public static string[] ReadFor(string dir, string name, int count)
    {
        if (!System.IO.Directory.Exists(dir)) return [];
        var files = System.IO.Directory.GetFiles(dir, "*.log");
        Array.Sort(files, StringComparer.Ordinal);
        var found = new List<string>();
        long bytes = 0;
        int scanned = 0;
        for (int f = files.Length - 1; f >= 0 && f >= files.Length - kLogFiles && found.Count < count; f--)
        {
            string[] lines = File.ReadAllLines(files[f]);
            for (int i = lines.Length - 1; i >= 0 && found.Count < count; i--)
            {
                bytes += lines[i].Length;
                if (++scanned > kScanLines || bytes > kScanBytes) return Ordered(found);
                if (ChatLogLine.IsFrom(lines[i], name)) found.Add(lines[i]);
            }
        }
        return Ordered(found);
    }

    private static string[] Ordered(List<string> newestFirst)
    {
        newestFirst.Reverse();
        return [.. newestFirst];
    }

    private static void Append(string path, string text)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, text);
        }
        catch (IOException)
        {
            // The next batch tries again; a line lost to a locked file is not worth a crash.
        }
    }
}
