# Items per page

Closes #67.

The converted page has no pager: it renders every converted book in one list.
Scrolling a long list is cumbersome on an e-reader, where a screen fits roughly
seven rows and every scroll costs a repaint. The library listing is paged, but at
a fixed ten rows that nobody can change.

This adds a per-device "Items per page" setting, default ten, that drives both
lists, and gives the converted page a pager.

## The fork this settles

`docs/ROADMAP.md` carries a "Screenful pagination (investigation)" item: derive
the page size from the `scr` cookie so one page is exactly one screen. Issue #67
instead asks for a manual setting. They are two answers to the same complaint.

**Decision: the manual setting.** It is what the issue asks for, it is
deterministic and directly testable, and it unblocks the converted page today.
The screenful item stays open as an investigation rather than being closed by
this: it owns open questions this design does not have to answer, namely variable
row heights when authors or series wrap, the first load before `scr` is set, and
how it would interact with search. If it later proves out, it becomes another
value this setting can take, not a replacement for it.

## Why the converted page in particular

Two reasons, and the second is invisible from the UI.

Scrolling is the stated one. The other is that the page mints up to two download
tickets per row, for every row, on every load: an EPUB ticket and a raw ticket.
`DownloadTickets` holds them in a `ConcurrentDictionary` with a fifteen minute
sliding idle window, and a raw ticket carries a copy of the ABS bearer. With two
hundred converted books, one page load leaves roughly four hundred live tickets
in memory for a quarter of an hour. Paging the page cuts that to twice the page
size.

That is why the ordering of work inside the page has to change and not just its
output. See "Converted page" below.

## Behaviour

An `int` on `DeviceSettings`, default 10, cookie key `ipp`, form field
`perpage`, with a number input in Settings beside the other list and reader
options. Label: **"Items per page"**, naming what the user sees.

The accepted range is **5 to 50**. Out of range sanitizes to the default and
shows a note, exactly as the page scale setting already does.

The bounds are chosen, not arbitrary:

- 50 caps the ticket churn described above. Today's converted page is
  effectively unbounded, so even the maximum is a strict improvement on it.
- 5 is low enough for the smallest reader viewport and high enough that the
  pager does not become the majority of the page.

### Copy and constraints

**"Items per page"** and **"Not used: items per page must be between 5 and
50."** are new strings and need German entries in `locales/de.json`. The English
source string IS the lookup key, so the two must match character for character
or German silently falls back to English.

The setting needs no client JavaScript. The number input and the pager links are
plain HTML.

The form field follows the settings form's existing convention for a number,
verified against `SettingsEndpoints.cs:32`: absent or unparseable means the
default, and out of range means the default rather than a clamp. The existing
`SanitizeScale` states the reason and it applies here unchanged: "20 is not a
request for 50."

The warning is surfaced the way the page scale's already is, and this part is
easy to get wrong. `SettingsEndpoints` appends a marker to the redirect query
(`&scalerange=1` today), and `Settings.cshtml.cs` reads it back with
`Request.Query.ContainsKey`. A new marker is needed for this setting, and it
must **NOT** be added to `DeviceSettings.Keys`: that array is "the keys
Serialize writes, and nothing else", and it decides whether a query counts as a
settings payload at all. `range` and `scalerange` are deliberately absent from
it for that reason. Adding the new marker there would make a bare warning
redirect look like a settings payload.

## Library listing

`LibraryModel.PageSize` is a `const int = 10` passed straight to
`AbsApiClient.GetItemsAsync`. Replace the constant with the setting's value.
ABS already pages server-side, so this is the entire change.

`SearchLimit = 25` stays as it is. Search is explicitly **out of scope**: it
renders three groups (book rows, series links, author links) rather than one
browsable list, and it is already capped. "Applicable on all lists" in the issue
is read as one setting governing every list, not as a requirement to page a
capped, grouped result page.

## Converted page

The page gains a pager. Getting the ticket saving requires reordering the work in
`ConvertedModel.OnGetAsync`, which today does:

1. enumerate the cache for this device's render target
2. batch-fetch metadata for every converted id
3. build an `ItemRowModel` per item, **minting tickets**
4. sort the built rows
5. render

Sorting depends on metadata (title, author, series), so it cannot move before
step 2. But building rows can move after the sort:

1. enumerate the cache for this device's render target
2. batch-fetch metadata for every converted id
3. resolve convert state for every item (see the exception below)
4. sort
5. **slice to the requested page**
6. build an `ItemRowModel` for the slice only, minting tickets for those rows
7. render

### The deliberate exception

Step 3 resolves `ConvertRowStateResolver.Resolve` for **all** items, not only the
page's. This looks like work the slice should have removed, and it is kept on
purpose: `AnyConverting` drives a 30 second `MetaRefresh` on this page, and
scoping it to the visible page would silently change when the page auto-refreshes
(a conversion running on page three would no longer refresh page one). Resolve is
local file existence and queue lookups with no network call, and running it for
every item is what the page already does.

The batch metadata call in step 2 also still covers every converted id, because
the sort needs it. Paging does not reduce that, and this design does not pretend
to.

### Pager reuse

`_Pager.cshtml` is typed to `LibraryModel` and calls
`Model.Links.ListingHref(Sort, Desc, page)`. The converted page has neither a
`LibraryLinks` for itself nor that href shape.

Give the partial a two-member interface instead, implemented by both page models:

```csharp
public interface IPagedListing
{
    Pager Pager { get; }
    string PageHref(int page);
}
```

`LibraryModel.PageHref` delegates to the existing `Links.ListingHref`.
`ConvertedModel.PageHref` builds `/converted?sort=...&desc=1&page=N`, preserving
the active sort and applied direction the same way its existing `SortHref` does.

An interface on the two page models is preferred over a wrapper model that each
call site constructs, because neither call site then changes shape and the
partial keeps a single typed model.

### Edges

- A `page` beyond the end clamps to the last page. The library listing leaves
  this to ABS, which answers an empty result; the converted page slices locally
  and so must clamp itself.
- A `page` below 1, absent, or unparseable means page 1, matching the library
  listing's existing `int page = 1` binding.
- Changing the setting changes what a bookmarked `?page=N` points at. Accepted
  and not worth correcting: the page number is a position in a list, not an
  identity.

## How it fails

- An out-of-range or garbage `perpage` sanitizes to 10 and says so. It cannot
  produce a zero or negative page size, which would divide by zero in
  `Pager.TotalPages` (that property already guards `Limit <= 0`, but the
  sanitizer must not rely on it).
- A cookie predating this feature has no `ipp` key and reads as the default,
  like every other flag in the keyed wire format.
- The warning marker must not become a settings key, as above. If it did, a
  redirect carrying only the warning would parse as a settings payload and could
  overwrite real settings with defaults.
- Nothing here can prevent a download or leave a control unusable. With the
  setting untouched, both lists behave exactly as they do today.

## Out of scope

- Search results, as above.
- The screenful investigation, which stays open in `ROADMAP.md`.
- Reducing the batch metadata call on the converted page.
- Any change to `SearchLimit`.

## Tests

- The setting round-trips through the cookie wire format and defaults to 10.
- Out of range on either side sanitizes to 10 and raises the warning flag.
- A cookie without `ipp` reads as 10.
- The settings page renders the number input, and saving records the value.
- A query carrying only the new warning marker is NOT treated as a settings
  payload, i.e. the marker stayed out of `Keys`.
- The library listing asks `GetItemsAsync` for the configured limit, not 10.
- The converted page renders at most the configured number of rows, and its
  pager reports the right total.
- The converted page's pager hrefs preserve the active sort and direction.
- A `page` beyond the end of the converted list clamps to the last page and
  renders rows rather than an empty list.
- The converted page mints tickets only for the rows it renders. This is the
  test that pins the reordering; without it the slice could sit after the build
  and nothing would catch it.
- `AnyConverting` still reflects an item that is converting on a page other than
  the one being rendered. This pins the deliberate exception above.
- uicheck, in a real browser: the converted page's pager renders and navigates,
  at both viewports.

## Verification

The headless pass covers this feature fully. Unlike the reader-engine
workarounds, nothing here depends on the device's browser behaving unusually, so
a device pass is useful for judging whether the chosen number feels right on the
reader but is not required to trust the change.
