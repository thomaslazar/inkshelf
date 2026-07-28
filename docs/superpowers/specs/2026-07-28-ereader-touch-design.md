# E-reader touch design pass

Date: 2026-07-28
Branch: `feat/touch-design` (stacked on `feat/downloaded-marks`)

## Problem

Tap targets across the site are text links at the base font size: roughly 24px
tall, stacked `.35rem` (5.6px) apart. On a 6" e-ink panel reporting 758 CSS px
across ~90mm of glass, that is ~8.4 px/mm, so adjacent actions sit **~3.6mm
apart centre to centre**. A fingertip contact patch is 8–10mm. Download,
Convert and Mark read all fall under one finger. This is not a tuning problem;
the targets are an order of magnitude too small.

The listing row makes it worse. `.item .actions` is a fixed `8.5rem` column, too
narrow for German — "Als gelesen markieren" and "Konvertieren (erneut)" each
wrap to two lines, so a failed row renders as five near-touching links (see
`tools/uicheck/shots/failed-row-de.png`). The same fixed column forces the
`max-width: calc(100% - 72px - 1rem - 8.5rem - .75rem)` hack on `.item .body`,
which exists only to stop the two columns overlapping on the old engine.

Secondary: the item detail page has the most screen room and uses none of it —
Download and Mark read are bare underlined text. The "Converted on this device"
link on the index is a plain `<p><a>` at body size sitting directly above
`.libraries` entries styled at 1.3rem, so two links to comparable destinations
render as two different kinds of thing. The layout also degrades badly at phone
width, where the 8.5rem sidebar does not fit at all.

## Non-goal: device detection

Investigated and rejected as the mechanism.

- `@media (monochrome)` reports `0` on e-ink devices. Their framebuffer is RGB;
  the panel is the grayscale part.
- `@media (update: slow)` exists precisely for e-ink but shipped around Chrome
  113, years after the target engine. It would work on a new device and not on
  the one we actually target. Harmless to add later as a bonus signal, never as
  the mechanism.
- User-agent sniffing works but only ever knows the readers we enumerate, and
  this project has external users on hardware we have never seen.
- The `scr` cookie (`_Layout.cshtml`, parsed by `ScreenTarget.FromCookie`) is
  the one real signal available — `screen.width × screen.height × DPR`, where
  DPR 1 plus a wide viewport is a decent e-ink fingerprint. But it is absent on
  first paint and would require server-side layout branching.

The reframe: **the e-reader design is the correct design.** Large targets, high
contrast, no hover, no motion — a phone wants all of the same. The phone
experience is poor because a fixed 8.5rem sidebar does not fit in 390px, not
because the layout is e-ink-specific. So the base layout becomes touch-first and
one width breakpoint handles narrow screens. No JS, no detection, correct on
first paint, correct on e-readers we have never heard of. The `scr` cookie keeps
doing the single job it is good at: sizing converted comic pages.

## Design

### 1. Shared button primitive

One `.btn` class usable on both `<a>` and `<button>`, because the row mixes them
(Download is a link, Mark read is a form submit).

```css
.btn { display: inline-block; border: 1px solid #000; padding: .55rem .7rem;
       margin: 0 .5rem .5rem 0; font: inherit; color: #000; background: #fff;
       text-decoration: none; line-height: 1.2; cursor: pointer; }
.btn:active { color: #fff; background: #000; }
```

Padding alone yields ~44px height; no `min-height` needed. The border is
load-bearing, not decoration — it makes the target boundary visible, which
underlined text never did. Spacing via `margin-right`, never flex `gap`.
`font: inherit` is required or `<button>` reverts to the UA sans-serif.

### 2. Listing row (`_ItemRow.cshtml`)

Actions move out of the fixed right column and onto their own line inside
`.body`, below title and author:

```
┌───────┬──────────────────────────────────────────────┐
│       │  Corrupt Archive                             │
│ cover │  Broken Comics · Band 3                      │
│ 72px  │                                              │
│       │  ┌─────────────┐┌──────────────┐┌──────────┐ │
│       │  │Herunterladen││ Konvertieren ││ Gelesen  │ │
│       │  └─────────────┘└──────────────┘└──────────┘ │
└───────┴──────────────────────────────────────────────┘
```

Consequences:

- **Delete** `.item .body`'s `max-width: calc(...)` hack and `.item .actions`'s
  `flex: 0 0 8.5rem` / `width: 8.5rem`. Both existed solely to keep the sidebar
  from colliding with long titles. Titles regain the full row width.
- `.item .actions` becomes a plain block with `margin-top: .5rem`; its children
  are `.btn`.
- `.item .actions .read-form` stays `display: inline` so the form does not
  break the horizontal button run.
- Row height grows from ~90px to ~130px for rows that have actions. Rows with
  no convertible/downloadable file stay short, so the listing keeps its existing
  raggedness rather than uniformly ballooning. ~7 rows per e-reader screen.
- Horizontally the three targets get ~660px, putting their centres 15–25mm
  apart — well clear of a fingertip, versus 3.6mm today.

**Labels are not shortened.** Measured: `Herunterladen` + `Konvertieren` +
`Als gelesen markieren` is ~375px of text plus padding ≈ 460px, inside the ~660px
available. German only failed because 8.5rem starved it. Keeping the labels
avoids re-translating and keeps the existing `>Mark read</button>` assertion in
`ListingRenderTests` valid.

### 3. Regen moves to the item page only

`ConvertActionModel` gains `bool ShowRegen`. Listing rows pass `false`; the item
page passes `true`. Rationale: a bare `↻` glyph is a ~14px target sitting beside
Convert, and a mistap costs a real conversion run. Regenerating is rare and
deliberate, so it belongs on the page you navigate to on purpose.

On the item page it stops being a bare glyph and becomes a labelled `.btn` using
the `Regenerate` localisation key that already exists as its `title`.

The invariant in `ListingRenderTests.cs:16` still holds and still needs
coverage: the regen anchor must remain a **plain** link with no `data-warm`, or
the status poller overwrites the glyph with status text. That assertion moves to
an item-page render.

### 4. Item detail page (`Item.cshtml`)

`Mark read`, and each file's `Download` / `Convert` / `Regenerate`, become
`.btn`. No structural change — `.file-row` stays block layout for the documented
reason (the target engine mishandles flex-shrink of a long filename node).

### 5. Index — the Converted link (`Index.cshtml`)

Gets the same treatment as `.libraries` entries (1.3rem, block, `.7rem` padding,
`<small>` caption below), and moves **below** the library list. It is a
secondary destination; sitting above the libraries currently makes it read as
the more important one.

### 6. Pager, sortbar, search, header

- `_Pager.cshtml`: `← Prev` / `Next →` become `.btn`; the page count stays plain
  text between them.
- `.sortbar` links become inline-block with `.5rem .6rem` padding. The `·`
  separators are dropped — the boxes now do the separating. Sort arrows stay
  inside the box.
- `.searchbar input`: `padding: .5rem; font: inherit`. Search button → `.btn`.
- `.settings-link`, `.fav-star`, logout button get padding to reach ~44px.
- `body { font-size: 1.1rem }`. Cover slots are px-fixed (72px listing, 160px
  detail) so nothing reflows around them.

### 7. Narrow-screen breakpoint

```css
@media (max-width: 600px) {
  body { margin: .5rem }
  .item .cover { flex-basis: 48px; width: 48px; height: 48px }
  .item .cover img { max-width: 48px; max-height: 48px }
  .item .cover.placeholder { font-size: 1.4rem; line-height: 48px }
  .item .actions .btn { display: block; margin-right: 0 }
  .detail-cover { flex-basis: 96px; width: 96px }
  .searchbar { margin-left: 0; width: 100% }
}
```

Full-width stacked buttons beat wrapped horizontal ones at 390px, and stay 44px
tall with clear separation. `@media (max-width:)` long predates anything the
target engine could be missing, which is exactly why it is the mechanism and
`update: slow` is not.

## Verification

- `dotnet test`. Expected to need changes in `ListingRenderTests`: the two
  `..._regen_stays_plain` tests and the `RegenAnchor` helper (~line 103) move to
  item-page renders. Everything else should pass untouched — if a test outside
  those breaks, the change went further than intended.
- `tools/uicheck/run.sh`. Existing assertions should hold; no page gains or
  loses a string. Add a second pass at `VIEWPORT_W=390` writing `shots/phone-*.png`
  — the breakpoint is new and nothing currently exercises it, and `run.sh`
  already reads that env var.
- Read the screenshots, do not just trust the exit code.
- Then the user's e-ink pass. The headless run cannot judge whether 44px is
  actually 44px under a thumb, and it does not reproduce the old engine's
  missing `object-fit` / flex `gap`.

## Accepted risks

- Rows get taller, so a library page shows fewer items per screen (~7 vs ~10).
  Deliberate: misclicking a conversion is worse than one extra page turn.
- Mark read remains inline on listing rows. If the e-ink pass shows the
  three-across run is still mistap-prone under a real thumb, the fallback is to
  demote Mark read to the item page — but that is a behaviour change and should
  be driven by device evidence, not guessed at now.
- This pass is expected to be the first of several. The device pass will
  produce a second round.
