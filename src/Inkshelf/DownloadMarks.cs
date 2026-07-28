namespace Inkshelf;

// Which files each device has already downloaded, so a row can say "you already
// pulled this one onto this reader". One append-only file per device id, keys one
// per line. Deliberately NOT in the EPUB cache's own directory listing: this
// lives in a `marks/` subdirectory, and every cache operation globs
// non-recursively for *.epub / *.tmp, so eviction can never delete marks.
//
// A singleton over a directory path, mirroring EpubCache.
public sealed class DownloadMarks
{
    // Reading marks refreshes the file's timestamp so pruning tracks "this device
    // still uses the app" rather than "still downloads". Rate-limited so rendering
    // doesn't turn into a write per request.
    private static readonly TimeSpan TouchAfter = TimeSpan.FromDays(1);

    private readonly string _dir;
    public DownloadMarks(string dir) { _dir = dir; Directory.CreateDirectory(_dir); }

    // The `d:`/`e:` prefix is load-bearing: an item's raw ebook and its converted
    // EPUB are different files reachable from the same row, so one shared key
    // would make fetching either light up both.
    public static string RawKey(string itemId, string? ino) => Key("d", itemId, ino);
    public static string EpubKey(string itemId, string? ino) => Key("e", itemId, ino);
    private static string Key(string kind, string itemId, string? ino) =>
        string.IsNullOrEmpty(ino) ? $"{kind}:{itemId}" : $"{kind}:{itemId}:{ino}";

    public HashSet<string> Read(string did)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (PathFor(did) is not { } path || !File.Exists(path)) return set;
        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (line.Length > 0) set.Add(line);
            var last = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - last > TouchAfter) File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException) { }
        return set;
    }

    public void Add(string did, string key)
    {
        if (PathFor(did) is not { } path) return;
        try
        {
            if (Read(did).Contains(key)) { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); return; }
            File.AppendAllText(path, key + Environment.NewLine);
        }
        catch (IOException) { }
    }

    public void Prune(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var f in new DirectoryInfo(_dir).GetFiles())
        {
            if (f.LastWriteTimeUtc >= cutoff) continue;
            try { f.Delete(); } catch (IOException) { }
        }
    }

    // The device id comes from a client cookie and becomes a FILE NAME, so it is a
    // trust boundary. A blank or invalid id means "no marks" — never a fallback
    // name, which would pool every malformed device into one shared bucket.
    private string? PathFor(string did) =>
        Auth.DeviceSettings.IsValidDid(did) ? Path.Combine(_dir, did) : null;
}
