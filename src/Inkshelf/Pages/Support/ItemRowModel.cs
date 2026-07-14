using Inkshelf.Abs;

namespace Inkshelf.Pages;

// One item row. Links is the shared URL builder for the current library/facet.
// Listing rows get structured authors/series (with ids) from the batch call;
// search rows leave those null and fall back to the comma-joined name strings.
// Cached = a converted EPUB for this item already exists for the requesting device.
public record ItemRowModel(
    AbsItem Item,
    LibraryLinks Links,
    IReadOnlyList<AbsRef>? Authors = null,
    IReadOnlyList<AbsSeriesRef>? Series = null,
    bool Cached = false);
