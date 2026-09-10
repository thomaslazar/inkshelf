namespace Inkshelf.Pages;

// What _Pager.cshtml needs, and nothing more. Implemented by the page models
// themselves rather than by a wrapper model, so neither call site has to
// construct anything and each page keeps ownership of its own href shape (the
// library listing carries filter/author/series, the converted page carries its
// local sort).
public interface IPagedListing
{
    Pager Pager { get; }

    // The URL for a 1-based page number, preserving whatever else the current
    // view is showing.
    string PageHref(int page);
}
