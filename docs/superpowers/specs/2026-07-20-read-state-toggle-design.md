# Read-state toggle (mark items read/unread)

**Status:** design approved, ready for implementation plan
**Date:** 2026-07-20
**Roadmap item:** Browsing & reading — "Read-state toggle"

## Goal

Let the user manually mark a library item **read / unread** from the listing
(and search) rows. Reading happens offline on the device, so ABS never observes
progress; this is a manual flag. State is **synced to Audiobookshelf** so it
reflects in the ABS web/mobile apps too, survives cookie/device loss, and scales
past what a cookie could hold.

## Scope

**In:** a per-row read/unread toggle on the **listing rows and search rows**,
backed by the ABS media-progress API.

**Out (for now):**
- **Detail page** read toggle — the item detail page is a separate, unbuilt
  roadmap item; it gets its own read control when built.
- **Filtering** by read/unread — deferred (YAGNI); easy follow-up later.
- **Partial/“in progress”** state — reading is offline, so the flag is binary
  (finished / not).

## ABS API (verified against the ABS v2.35.1 source)

- **Write:** `PATCH /api/me/progress/{libraryItemId}` with body
  `{"isFinished": true}` to mark read, `{"isFinished": false}` to unmark.
  The URL takes the library-item id (our `item.Id`); ABS resolves it to the
  media and sets `isFinished` + `finishedAt`. (`MeController.createUpdateMediaProgress`
  → `User.createUpdateMediaProgressFromPayload`.)
  - Unmark uses `PATCH isFinished:false` (not `DELETE /api/me/progress/{id}`) —
    symmetric, one endpoint, no need to know the progress-row id. It leaves a
    harmless `isFinished:false` progress row.
- **Read:** `GET /api/me` returns the user with a `mediaProgress[]` array
  (`User.toOldJSONForBrowser`). Each entry (`MediaProgress.getOldMediaProgress`)
  exposes `libraryItemId` (from `extraData`) and `isFinished`. Build a
  `HashSet<string>` of `libraryItemId` where `isFinished == true`.
  - *Verification point:* `libraryItemId` comes from `extraData`. Marks written
    via this endpoint (by us and by the ABS apps, which use the same route) set
    it. The plan must assert matching against a real `/api/me` response; if an
    entry's `libraryItemId` is absent, that item simply reads as unread (safe
    degradation).

## Design

### A. `AbsApiClient` — two new methods + one DTO

Following the "new ABS call = new method on `AbsApiClient`, no `accessToken`
param (the handler injects the Bearer), new DTO rather than widening an
existing one" convention.

- `Task<HashSet<string>> GetFinishedItemIdsAsync(CancellationToken ct)` —
  `GET /api/me`, deserialize into a new `AbsMe` DTO, return the finished
  `libraryItemId` set.
- `Task SetReadAsync(string itemId, bool finished, CancellationToken ct)` —
  `PATCH /api/me/progress/{Uri.EscapeDataString(itemId)}` with a JSON body
  `{"isFinished": finished}`.
- New DTO: `record AbsMe([JsonPropertyName("mediaProgress")] List<AbsMediaProgress> MediaProgress)`
  and `record AbsMediaProgress(string? LibraryItemId, bool IsFinished)` with the
  matching `[JsonPropertyName]` attributes (`libraryItemId`, `isFinished`).

### B. Row UI — `ItemRowModel` + `_ItemRow.cshtml`

- Add `bool Read = false` to the `ItemRowModel` record.
- In `_ItemRow.cshtml`, inside the existing `<div class="actions">` block
  (which already renders only when the item has an ebook format / Download
  link), add a read toggle **below** the Download / convert controls:
  a plain POST `<form method="post" action="/read/{item.Id}">` with an
  antiforgery token, a hidden field carrying the **target** state, and a
  submit button:
  - unread (`Read == false`): button text **"Mark read"**, hidden `read=1`,
    `title="Mark as read"`.
  - read (`Read == true`): button text **"✓ Read"**, hidden `read=0`,
    `title="Mark as unread"`.
- Text labels (not glyphs) — e-ink-safe. Styled to match the existing actions
  (a borderless button, like the favorite star / convert links). Defensive CSS
  only.
- Renders identically on listing and search rows (both go through `_ItemRow`).

### C. Write endpoint — `MapReadEndpoints` in `Endpoints/`

Mirrors the `/favorite` + `/logout` convention exactly:

- `POST /read/{id}` — `HttpContext`, `IAntiforgery`, `AbsApiClient`, `[FromForm]`.
  `try { await antiforgery.ValidateRequestAsync(ctx); } catch (AntiforgeryValidationException) { return Results.BadRequest(); }` then read the
  form's `read` field (`"1"` → finished true, else false), call
  `SetReadAsync(id, finished, ct)`, and `Results.Redirect` back to a local-only
  return path (same open-redirect guard style as `ConvertEndpoints.LocalReturn`).
  `.DisableAntiforgery()` on the route.
- Mapped in `Program.cs` next to `app.MapSessionEndpoints();`.
- An expired session surfaces `AbsAuthException` from `SetReadAsync`, which the
  `Program.cs` middleware turns into a `/login` redirect — consistent with the
  rest of the app.

### D. Wiring — `LibraryModel`

- Add `GetFinishedItemIdsAsync` to **both** branches of `OnGetAsync`:
  - Listing branch: after fetching `Items` (alongside `FetchStructuredAsync` /
    `ComputeConvertStates`).
  - Search branch: after fetching the search books.
- Store the result in a private `HashSet<string> _finished`. Wrap the call so a
  failure degrades to an empty set (rows render as unread) rather than throwing
  the page — `GET /api/me` failing shouldn't blank the listing.
- `RowFor(item)` sets `Read = _finished.Contains(item.Id)` on the returned
  `ItemRowModel`.
- One extra `GET /api/me` per listing/search render (accepted — single-user
  sidecar; the listing already issues several ABS calls).

### E. Testing + docs

**Tests:**
- `AbsApiClientTests`: `GetFinishedItemIdsAsync` parses a stub `/api/me`
  (mixed finished/unfinished) into the correct id set; `SetReadAsync` issues
  `PATCH /api/me/progress/{id}` with body `{"isFinished":true}` / `false`
  (assert method, URL, and payload via `StubHandler`).
- `EndpointTests`: `POST /read/{id}` with a valid antiforgery token → 302 +
  the ABS PATCH was issued; without a token → `400`.
- `ListingRenderTests`: stub `/api/me` so the item is finished → row shows
  **"✓ Read"** and the form posts `read=0`; unfinished → **"Mark read"** posting
  `read=1`. Assert on a search row too.

**Docs:**
- `ARCHITECTURE.md`: document the `/read/{id}` endpoint group, the two new
  `AbsApiClient` methods, and the `GET /api/me` read-state path (present-tense,
  structural — no changelog/shipped-status prose).
- `ROADMAP.md`: move **Read-state toggle** out of *Browsing & reading* into
  **Done** (short bullet). The *Item detail page* backlog item keeps its own
  read-state mention for when that page is built.

## Data flow (summary)

```
GET /api/me ─► finished libraryItemId set (HashSet) ─► RowFor(item).Read
                                                          │
row renders "Mark read" (read=1) or "✓ Read" (read=0)     ▼
   POST /read/{id}  (antiforgery)  ─►  AbsApiClient.SetReadAsync(id, finished)
                                          └► PATCH /api/me/progress/{id} {isFinished}
   ─► PRG redirect back to listing (no-store) ─► re-render reflects new state
```

## Non-goals

- No detail-page control (separate roadmap item).
- No read/unread filtering or sorting.
- No partial-progress tracking; binary finished flag only.
- No local/cookie fallback store — state lives in ABS.
