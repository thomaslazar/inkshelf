# UI polish + convenience features — Design

**Date:** 2026-07-13
**Status:** Approved — proceeding to plan/implementation on `feat/ui-convenience`

## Purpose

Make Inkshelf a bit prettier and more convenient for day-to-day e-reader use:
integrate the logo/favicon assets, let a user favorite a library (jump straight
into it), tighten the item listing for small e-ink screens, and add search so
you don't have to page through everything to find a title.

All additions keep the near-zero-JavaScript rule: plain `<form>` and `<a>` only.

## Assets

Source assets live in `temp/logo/` (gitignored). Copy into the repo:

- **Web root `wwwroot/`** (favicon set — absolute paths in the manifest require
  root placement): `favicon.ico`, `favicon-16x16.png`, `favicon-32x32.png`,
  `apple-touch-icon.png`, `android-chrome-192x192.png`,
  `android-chrome-512x512.png`, `site.webmanifest`.
- **`wwwroot/img/`** (renamed, no spaces): `logo-black.png`, `logo-inverted.png`
  (wordmark), `icon-black.png`, `icon-inverted.png` (square).

`site.webmanifest`: set `name` and `short_name` to `Inkshelf` (generator left
them blank); keep the icons, `theme_color`/`background_color` `#ffffff`,
`display: standalone`.

`<head>` (in `_Layout.cshtml`) gains: `<link rel="icon">` (16/32 + ico),
`<link rel="apple-touch-icon">`, `<link rel="manifest" href="/site.webmanifest">`,
and `<meta name="theme-color" content="#ffffff">`.

E-ink is grayscale on a white background, so the **black** logo/icon variants
are used in the app. The **inverted** variants are used only for GitHub dark
mode in the README.

## README

Center the wordmark at the top using `<picture>` so it renders correctly in
both GitHub themes:

```html
<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/Inkshelf/wwwroot/img/logo-inverted.png">
    <img src="src/Inkshelf/wwwroot/img/logo-black.png" alt="Inkshelf" width="360">
  </picture>
</p>
```

## Layout / header

`_Layout.cshtml` gains a compact header shown on the authenticated pages:

`[icon] Libraries · ★`

- `[icon]` = `icon-black.png` (~24px tall), links to the full library list
  (`/?all=1`).
- `Libraries` = same target (text link).
- `★` = favorite toggle for the current library (only on the library page).

The **login page** shows the centered wordmark (`logo-black.png`) above the
form. Keep it small (constrained width) for e-ink.

## Favorite library

- Plain (unencrypted) cookie `inkshelf_fav_library=<libraryId>` — a UI
  preference, not a secret. HttpOnly, SameSite=Lax, long max-age.
- **Toggle:** a small `<form method="post">` on the library page posting to a
  `POST /favorite` endpoint with the library id and an antiforgery token (same
  pattern as `/logout`). It sets the cookie to that id, or clears it if the id
  is already the favorite. Redirects back to `/library/{id}`.
- **Auto-redirect:** `GET /` (Index) redirects to `/library/{favId}` when the
  favorite cookie is set **and** the request has no `?all=1`.
- **Escape hatch:** the header `Libraries` link points at `/?all=1`, which
  always renders the full list regardless of the favorite.
- The ★ shows filled when the current library is the favorite, hollow
  otherwise (plain text `★` / `☆`).

## Item listing changes

- Page size **10** (was 24) — `LibraryModel.PageSize = 10`.
- Move the Prev/Next pager to the **top** of the list (above the rows). No
  bottom pager (save e-ink space).
- **Clickable author/series.** Each author links to
  `/library/{id}?filter=authors.<base64(authorId)>` and each series to
  `/library/{id}?filter=series.<base64(seriesId)>` (a book can have several of
  each; render one link per entry, series with its sequence when present).
- To get the author/series **ids** the list must request full item JSON
  (**drop `minified=1`**). The extra fields (`audioFiles`/`chapters`/
  `ebookFile`) travel only on the Inkshelf↔ABS hop; the e-reader still receives
  the same small HTML. Title still falls back to `metadata.title`; if the
  author/series arrays are empty the row shows nothing clickable there.
- Cover + download link (when the item has an ebook) unchanged.

## Search + filtered listing

The library page (`/library/{id}`) supports three modes, chosen by query params:

1. **Default** (no `q`, no `filter`): paged listing (10/page).
2. **Search** (`?q=<text>`): grouped results from
   `GET /api/libraries/{id}/search?q=&limit=`:
   - **Books** — matched items rendered like list rows (cover/title/author/
     series + download link when the item has an ebook). The book match's item
     is the expanded form, so its author/series are clickable filter links too,
     exactly like the normal listing.
   - **Series** — each links to `/library/{id}?filter=series.<base64(seriesId)>`.
   - **Authors** — each links to `/library/{id}?filter=authors.<base64(authorId)>`.
   Search results are not paged (ABS caps them; use `limit=25`).
3. **Filtered** (`?filter=<group>.<b64>`): paged listing passing `filter`
   through to `GET /api/libraries/{id}/items?...&filter=...`.

A search `<form method="get" action="/library/{id}">` with a single `q` text
input sits at the top of the page in all modes. Filter mode shows a small
"Filtered by … · clear" line linking back to the unfiltered listing.

### Filter encoding

ABS decodes `filterBy` as `<group>.<value>` where `value` is
`base64(decodeURIComponent(...))`. Build it in C# as
`group + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(id))`, then
URL-encode when placing in the link (`+`/`/`/`=` are not URL-safe).

## Client / data additions (`AbsClient`, `AbsModels`)

- **Items list drops `minified=1`** to obtain author/series ids. This moves
  ebook detection from `media.ebookFormat` (minified only) to
  `media.ebookFile.ebookFormat` (full). `AbsMedia` gains an `EbookFile`
  (`{ EbookFormat, Metadata { Filename } }`, reusing/extending the existing
  `AbsEbookFile`); the download button shows when `media.ebookFile?.ebookFormat`
  is set. `AbsMetadata` gains `Authors: [{ Id, Name }]` and
  `Series: [{ Id, Name, Sequence? }]` (the name strings stay for fallback).
- `GetItemsAsync(..., string? filter = null)` — appends `&filter=<value>` when
  set (value already `group.<b64>`, URL-encoded).
- `SearchAsync(accessToken, libraryId, q, limit)` →
  `Task<AbsSearchResults>`; `GET /api/libraries/{id}/search?q=&limit=`.
- New DTOs:
  - `AbsSearchResults { List<AbsBookMatch> Book; List<AbsSeriesMatch> Series; List<AbsAuthorMatch> Authors; }`
  - `AbsBookMatch { AbsItem LibraryItem; }` (item is expanded → has
    `media.metadata` and `media.ebookFormat`)
  - `AbsSeriesMatch { AbsSeries Series; }` where `AbsSeries { Id, Name }`
  - `AbsAuthorMatch { Id, Name }`
  - Exact JSON keys confirmed against the ABS source during implementation.
- A small `AbsFilter.Encode(string group, string id)` helper (testable).

## Error handling

- Empty/whitespace `q` → treat as no search (render the default listing).
- Search or filter against a library the user can't access → the existing
  `AbsSession` auth flow / 404 handling applies; a bad `filter` value yields an
  empty listing.

## Testing

- `AbsClient.SearchAsync` parses book/series/authors from a fixture (StubHandler).
- `GetItemsAsync` includes `filter=` when supplied; omits it otherwise.
- `AbsFilter.Encode` produces `group.<base64>` for a known id.
- `GetItemsAsync` parses `metadata.authors[]`/`series[]` ids and
  `media.ebookFile.ebookFormat` from a full (non-minified) item fixture, and
  no longer sends `minified=1`.
- Favorite toggle: setting/clearing the cookie and the `/` auto-redirect vs
  `?all=1` bypass (WebApplicationFactory, no live ABS).
- `Pager` math unchanged (existing tests stay).
- Existing 23 tests remain green.

## Out of scope

- Item detail / reading pages (still rows + download only).
- Sorting controls, multi-facet filtering, saved searches.
- Global (cross-library) search.
- Image resizing of the provided PNGs (used as-is; revisit if e-ink load is slow).
