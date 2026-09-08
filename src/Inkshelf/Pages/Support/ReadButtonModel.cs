namespace Inkshelf.Pages;

// The read/unread toggle for one item. Used by BOTH the listing row and the item
// detail page, which carried near-identical copies of this form until they
// drifted apart once too often.
//
// ReturnUrl is where the no-JavaScript POST comes back to. The partial appends
// the row anchor to it; do NOT pre-append it here, and do not reuse
// ItemRowModel.ReturnUrl for the anchored value, because that same string feeds
// the convert links.
public record ReadButtonModel(string ItemId, bool Read, string ReturnUrl);
