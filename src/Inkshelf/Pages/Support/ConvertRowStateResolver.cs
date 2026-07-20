using Inkshelf.Abs;
using Inkshelf.Convert;

namespace Inkshelf.Pages;

// Shared per-item convert-state computation, used by the library listing AND the
// /converted view so the two never diverge. Keyed on the resolved RenderTarget
// (scr probe + settings), matching what a real conversion writes to the cache.
public static class ConvertRowStateResolver
{
    public static ConvertRowState Resolve(AbsItem item, AbsBatchMedia? media,
        RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        // Listing items (minified) carry media.ebookFormat; search/batch items
        // (expanded) carry the format only on the ebookFile. Check all.
        var fmt = item.Media?.EbookFormat ?? item.Media?.EbookFile?.EbookFormat ?? media?.EbookFile?.EbookFormat;
        var efm = media?.EbookFile?.Metadata;
        if (efm is null) return ConvertRowState.NotConvertible; // can't key the cache
        return ResolveFor(item.Id, efm.Size, efm.MtimeMs, fmt, target, cache, queue);
    }

    // Lower-level: state for one specific (itemId, file size+mtime, format).
    public static ConvertRowState ResolveFor(string itemId, long size, long mtimeMs,
        string? fmt, RenderTarget target, EpubCache cache, ConvertQueue queue)
    {
        if (fmt != "cbz" && fmt != "cbr") return ConvertRowState.NotConvertible;
        var path = cache.PathFor(itemId, size, mtimeMs, target.MaxW, target.MaxH, target.Grayscale);
        return queue.Status(path) switch
        {
            ConvertStatus.Done => ConvertRowState.Cached,
            ConvertStatus.Queued or ConvertStatus.Running => ConvertRowState.Converting,
            ConvertStatus.Failed => ConvertRowState.Failed,
            _ => ConvertRowState.Convert,
        };
    }
}
