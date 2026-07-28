# Mark files already downloaded on this device

**Status:** design approved, ready for implementation plan
**Date:** 2026-07-28
**Roadmap item:** Browsing & reading — "Mark files as already downloaded (per device)"

## Goal

Working through a batch — ten comics just queued for conversion, or ten items
that are already EPUB — there is no way to tell which ones you have already
pulled onto the reader you are holding. So you re-download, or you skip one you
needed.

Show, on each download action, whether **this device** has already fetched
**that file**.

The question is deliberately narrow: *"did I download this, here?"* — not "does
this exist somewhere". That rules out several designs that would otherwise look
tidier (see Alternatives).

## Scope

**In:** retiring the `EPUB ✓` checkmark, a per-device identity, a server-side mark store, marks written by both
download endpoints, an indicator on each download action wherever rows render
(library listing, `/converted`, item detail file list), and pruning.

**Out:** a way to clear a mark by hand (the row is already crowded; a stale mark
is self-evident once you look at the reader). No permanent ledger — see Pruning.
No sync between devices, by definition.

## Design

### A. Identity — a minted device id, not a derived one

`Set` returns the effective settings (with the id it minted), so the download
endpoints can write a mark for a device seen for the first time without
duplicating the minting logic. Existing callers ignore the return.

`did=<random>` becomes one more key in the `inkshelf_settings` cookie. It is
minted **inside `DeviceSettings.Set` when absent**, so any write of that cookie
guarantees an id and no call site can forget. The cookie is `HttpOnly`, so
scripts cannot read the id.

**Generated** from a cryptographic RNG (`RandomNumberGenerator`), rendered as 16
lowercase hex characters — comfortably unique for a household, and inside the
allowlist below. It is an opaque handle, not a secret: knowing another device's
id reveals only which files that device downloaded, and it is not readable by
script or by another origin.

**The id is a client-supplied value that ends up in a file path, so it is a
trust boundary.** It is validated by the existing `SanitizeId`, whose
`[A-Za-z0-9_-]`, ≤64 allowlist rejects `/`, `\`, `.` and `%` outright — so
`../../etc/passwd` and every traversal variant collapse to `""`. The rule the
implementation must hold: **a blank or invalid id means "no marks", never a
fallback filename.** Never build a path from an unsanitized cookie value, and
never substitute a default id when validation fails — that would pool every
malformed device into one shared file.

Two mint paths, both needed:

- **Any settings-cookie write** — `POST /settings`, `POST /favorite`, and Index's
  stale-favorite clear. Covers the normal case.
- **First download** — a fresh device that downloads before ever opening
  Settings. The endpoint calls `Set` to mint, reusing one code path rather than
  duplicating the minting logic.

Deliberately **not** minted on ordinary page views. Minting on a GET means a
read-modify-write of the settings cookie, and concurrent requests (page, covers,
status polls) could each mint a different id with the last write winning. A
device that has never downloaded has no marks to show, so it needs no identity
yet. The stale-favorite clear is a GET, but it fires only when a favorite points
at a library ABS no longer has — rare enough not to reintroduce the race.

### B. Alternatives rejected, and why

- **Marks in the cookie itself.** The original roadmap idea. ~4 KB ceiling, a
  rolling window that silently forgets the oldest, and it re-uploads on every
  request over a slow link.
- **Key on the render target** (`maxW×maxH×grayscale`, already the EPUB cache's
  device discriminator). It is a *variant* key, not a device identity. It is not
  unique — two identical readers with identical settings collide, which matters
  on a shared family deployment. It is not stable — toggling grayscale or
  rotating the device changes it and the marks vanish, whereas toggling grayscale
  plainly does not un-download anything. And it does not exist for raw
  downloads, which involve no conversion at all.
- **Key on the ABS user.** The user id sits unused in the `/api/me` response the
  listing already fetches, and in the stored JWT payload
  (`jwt.sign({ userId, username })`), so it would cost nothing to obtain and
  would survive cookie clearing. Rejected on semantics: it answers "did I
  download this at all", and the actual question is per-reader.
- **Storing marks in ABS.** No per-device concept exists there, media progress is
  already spoken for by read state, and anything else would be co-opting a field
  for the wrong meaning — and would sync across devices, which is backwards.
- **Reusing `Touch`-on-serve**, which this project removed. It only ever covered
  converted EPUBs, never raw downloads, and it collided with cache eviction.

Note that the privacy concern about device identifiers does not apply here: this
is a random value **we** mint and set from the server, not a browser-exposed
identifier. Nothing blocks a server-set first-party cookie; Safari's 7-day cap on
first-party cookies applies to ones written via `document.cookie`. (The
fingerprintable thing in this app is the existing `scr` probe — `screen.width`,
`screen.height`, `devicePixelRatio`, script-written — which Firefox's
`resistFingerprinting` spoofs and Safari's cap does reach. That affects the
conversion cache and is out of scope here.)

### C. Storage

One append-only file per device: `{CachePath}/marks/{did}`, newline-delimited
keys.

| Key form | Meaning |
|---|---|
| `d:{itemId}` | the item's primary **raw** ebook (`/download/{id}`) |
| `d:{itemId}:{ino}` | one specific raw file on a multi-file item |
| `e:{itemId}` | the item's primary converted **EPUB** (`/convert/{id}`) |
| `e:{itemId}:{ino}` | the converted EPUB of one specific file |

The `d:`/`e:` discriminator is load-bearing: an item's raw ebook and its
converted EPUB are two different files reachable from the same row, so a single
`{itemId}` key would make downloading the raw file light up the EPUB action as
already fetched. Keys are opaque strings — built identically on write and on
read, never parsed back.

Reading loads the file into a `HashSet<string>` once per render; the page model
then tests the handful of visible ids against it. No subset query and no API
endpoint: the page is server-rendered, so the check happens during render, which
is also what keeps this at zero added JavaScript.

A `marks/` **subdirectory** is safe inside the cache dir because every existing
cache operation globs non-recursively for a specific extension —
`ListVariants` and `EnforceCap` on `*.epub`, `SweepTemp` on `*.tmp`. So eviction
can never delete marks, and no new configuration key is needed.

The store is a singleton holding only the directory path, mirroring `EpubCache`.

### D. Writing

`/download/{id}` (raw, optional `?file={ino}`) and `/convert/{id}` (the cached
EPUB) append their key **on request, before streaming**.

This over-marks a transfer that fails or is cancelled, which is the worse
direction — you could skip a file you do not have. Accepted because it is rare
and self-evident on the device. Note the accurate-looking alternative is not
actually accurate: `HttpResponse.OnCompleted` also fires on client abort, and a
mid-stream failure from ABS arrives after the response is already a 200 with a
partial body, so neither approach can detect that.

### E. Reading — and retiring the `EPUB ✓` checkmark

The downloaded indicator is **` ↓` appended to the action's existing label**, and
**the existing `✓` on the cached-EPUB action is removed** as part of this change.

```
Herunterladen            raw ebook not yet pulled
Herunterladen ↓          raw ebook already pulled to this device
Konvertieren             not converted (clicking costs 60–90 s)
EPUB                     converted, not yet pulled
EPUB ↓                   converted and pulled
```

**Why the `✓` goes.** It was introduced (`f8829d2`) to mark "a row whose
converted EPUB is already cached for this device" — i.e. clicking is instant
rather than a conversion. But the *label already says that*: the states render as
different words (`Konvertieren` vs `EPUB`, 12 characters against 4), so the
glyph decorates a signal that is already unmissable. Keeping it would also force
`EPUB ✓ ↓` — two glyphs of different meaning side by side, in the app's most
crowded column. Removing it makes `↓` the only glyph in that column, with exactly
one meaning.

`↓` is chosen because it is already proven on the target e-ink engine: the sort
bar shipped it and it renders correctly on the device.

The `title="Already converted — downloads right away"` stays, so the explanation
survives for any client that can show it.

**Test consequence.** Six assertions across `ConvertedRenderTests`,
`ItemRenderTests` and `ListingRenderTests` currently use `"EPUB &#10003;"` as the
discriminator for "this row is cached", including a negative one
(`ListingRenderTests:198`) proving a grayscale variant is not cached for a colour
device. They need a new discriminator. Use **the absence of `data-warm`** — a
cached row links straight to the file, while every other state carries
`data-warm` for the JS kick. Matching bare `"EPUB"` is not sufficient: it is a
substring that can occur in other page text.

Marks render wherever a download action does: the library listing, `/converted`,
and the item detail file list.

### F. Pruning

Reading the marks file also **touches its mtime**, rate-limited to at most once
per day so rendering does not amplify writes. So "untouched" means *this device
has not used the app*, rather than *has not downloaded* — otherwise a device that
browsed for a month without downloading would be pruned mid-use.

Files untouched for **30 days** are deleted. Swept at `ConvertWorker` startup
(which already sweeps orphan `.tmp` files, so the hook exists) and
opportunistically when a mark is written — the directory holds one small file per
device, so a scan is cheap.

### G. Known limitations, accepted

- **A regenerated EPUB keeps its mark.** Marks key on item (and ino), not on the
  cache variant, so `?fresh=1` produces a new file while the mark still says
  downloaded. Keying on the cache variant would drag render-target identity back
  in for exactly the reasons rejected in section B.
- **Clearing cookies loses the marks.** The file is orphaned rather than deleted —
  unreachable, and cleaned up by the 30-day sweep.
- **A cancelled download still marks.** See section D.

### H. Tests

- A mint happens on a settings write, and the id round-trips through the cookie.
- A download on a device with no id mints one and records the mark.
- All four key forms are distinct: a primary mark must not mark a specific file
  and vice versa, and — the one that matters — **downloading the raw ebook must
  not mark the converted EPUB as fetched**, nor the reverse.
- A marked item renders the indicator; an unmarked one does not.
- A cached-but-not-downloaded action renders `EPUB` with no glyph; a
  cached-and-downloaded one renders `EPUB ↓`.
- The retired `✓` is gone: no rendered page contains `EPUB &#10003;`, and the
  cached-vs-uncached tests discriminate on `data-warm` instead.
- Two device ids do not see each other's marks.
- A marks file older than 30 days is pruned; a fresh one is not.
- A traversal-shaped `did` (`../../etc/passwd`, `..%2f..`) yields no marks and
  writes no file outside the marks directory.
- **Cache eviction does not touch the marks directory** — seed marks plus
  oversized EPUBs, run `EnforceCap`, assert the marks survive. This is the one
  that would catch someone "simplifying" a cache glob to be recursive.

## Risks

| Risk | Mitigation |
|---|---|
| The actions column overflows on a narrow screen | Retiring the `✓` shortens it rather than lengthening it, so this change is net-neutral at worst; still verify on device before merge, and don't silently adjust CSS |
| A cache glob becomes recursive and eats `marks/` | Explicit test asserting `EnforceCap` leaves marks alone |
| Minting on a GET races and churns ids | Mint only on settings writes and downloads, never on page views |
| A crafted `did` escapes the marks directory | `SanitizeId` allowlist; blank/invalid means no marks, never a fallback filename; covered by a test |
| Marks file grows unbounded | 30-day prune; the working set is a batch, not a ledger |
| Over-marking hides a file you don't have | Accepted; indicator is advisory, and the reader shows the truth |
