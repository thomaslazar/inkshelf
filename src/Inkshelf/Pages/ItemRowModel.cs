using Inkshelf.Abs;

namespace Inkshelf.Pages;

// One item row, optionally carrying structured author/series data. Listing rows
// get it from the batch call (accurate per-author/per-series links); search rows
// leave it null and fall back to the comma-joined name strings on the item.
// Cached = a converted EPUB for this item already exists for the requesting
// device (so tapping Convert downloads instantly).
public record ItemRowModel(
    AbsItem Item,
    IReadOnlyList<AbsRef>? Authors = null,
    IReadOnlyList<AbsSeriesRef>? Series = null,
    bool Cached = false);
