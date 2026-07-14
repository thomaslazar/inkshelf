using System.Runtime.CompilerServices;
using SharpCompress.Archives;

namespace Inkshelf.Convert;

// Reads image entries from a comic archive (CBZ/CBR) in archive (ordinal) order,
// one at a time, skipping directories and non-image files.
public static class ComicArchiveReader
{
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".webp" };

    // A raw page: the original entry key (its extension selects the codec path) and its bytes.
    public sealed record RawPage(string Key, byte[] Bytes);

    public static async IAsyncEnumerable<RawPage> ReadAsync(Stream archive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var arc = ArchiveFactory.OpenArchive(archive);
        var entries = arc.Entries
            .Where(e => !e.IsDirectory && ImageExts.Contains(Path.GetExtension(e.Key ?? "").ToLowerInvariant()))
            .OrderBy(e => e.Key, StringComparer.Ordinal);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            using var es = e.OpenEntryStream();
            using var mem = new MemoryStream();
            await es.CopyToAsync(mem, ct);
            yield return new RawPage(e.Key ?? "", mem.ToArray());
        }
    }
}
