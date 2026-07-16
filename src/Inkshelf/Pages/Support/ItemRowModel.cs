using Inkshelf.Abs;

namespace Inkshelf.Pages;

// Per-row convert state for the listing. NotConvertible = not a cbz/cbr (or no
// size/mtime to key the cache). Convert = convertible, nothing cached/pending.
public enum ConvertRowState { NotConvertible, Convert, Converting, Failed, Cached }

// One item row. Links is the shared URL builder for the current library/facet.
// State drives the convert action; ReturnUrl is the listing URL a no-JS convert
// navigation returns to.
public record ItemRowModel(
    AbsItem Item,
    LibraryLinks Links,
    IReadOnlyList<AbsRef>? Authors = null,
    IReadOnlyList<AbsSeriesRef>? Series = null,
    ConvertRowState State = ConvertRowState.NotConvertible,
    string ReturnUrl = "/");
