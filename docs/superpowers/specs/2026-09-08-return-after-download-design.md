# Return to the list after a download

Closes #68.

Tapping a download on an e-reader leaves the listing. The file downloads, the
reader app opens it, and coming back to the browser lands on a page the reader
passed through earlier rather than the one they were reading. Downloading a batch
of converted books therefore means navigating back to the list after every single
one.

This adds a per-device setting, default off, that returns the browser to the page
the download started from.

## What the spike established

All of this was measured on a Tolino epos 2, firmware 16.2.0, against a real
library. It is recorded here because every one of these findings eliminated a
plausible fix, and without them the design below looks arbitrary.

- **It happens on every download link from every page.** Raw Download and EPUB,
  library listing, item page and converted listing alike. It is not specific to
  one endpoint or one page.
- **It is not history navigation.** `window.history.length` GROWS across the
  event rather than shrinking. The browser is being killed when the reader app
  takes the foreground, then restored from a stale snapshot. What looks like
  "went back two pages" is a restore to an older point.
- **Nothing in the markup can steer it.** `target="_blank"`, a named hidden
  iframe target, and `history.pushState` padding were each tested on device and
  changed nothing. Padding is impossible in principle once the mechanism is
  known: a `pushState` entry is the same document, and the restore skips it.
- **The `download` attribute is actively worse.** The browser tries to handle
  the file itself, fails, and nothing is saved.
- **Correcting after the fact works.** `sessionStorage` survives the reader
  holding the foreground, verified at 2.5 and 4 minutes with the wifi off for
  part of it. The corrected landing is stable and does not loop.
- **A time window is wrong.** Declining the reader's open prompt already took
  28.9 seconds; a real reading session took 154. An age limit only ever blocks a
  correction that should have happened.
- **One frame is unobservable.** The correct page briefly appears first as a
  script-less cached restore. No script runs there, so nothing can hook it and
  nothing needs to.

## Behaviour

A `bool` on `DeviceSettings`, default false, cookie key `ret`, with a checkbox in
Settings beside the other reader workarounds. Label: **"Return to the list after
a download"**, naming what the user gets rather than the mechanism.

When the setting is on:

1. Tapping an armed download link records the current `pathname + search` in
   `sessionStorage`.
2. On any page load, if such a record exists it is cleared, and if it names a
   different page than the one now loaded, the browser is sent there with
   `location.replace`.

Clearing the record BEFORE deciding whether to act is what makes a redirect loop
impossible even if the comparison is wrong. The worst case is one unnecessary
navigation, never a device stuck reloading.

No time window. The record is spent by the first script-running page load after
the download either way, so an age limit can only suppress a correction that
should have happened.

### Copy and constraints

The label **"Return to the list after a download"** is a new string and needs a
German entry in `locales/de.json`. The English source string IS the lookup key,
so the two must match character for character or German silently falls back to
English.

The script needs no localized strings of its own: it changes no visible text, so
nothing has to travel through the `I18N` JSON channel the way the read and
convert scripts do.

The script must be ES5, since it runs on the engine the bug lives on: no `fetch`,
no `Promise`, no arrow functions, no `const`/`let`, no template literals. The
whole block is wrapped in `try/catch` so an engine that cannot run it leaves the
plain anchors working.

The checkbox follows the settings form's existing convention: an unchecked box
submits nothing, so absent means off.

### Gated server-side, not in the script

With the setting off, the script block is not rendered at all. The marker
attributes on the anchors ARE always rendered, and are completely inert without
the script.

This matters beyond tidiness. `CLAUDE.md` allows client JavaScript only where
unavoidable, and this is a workaround for one reader engine's behaviour. Shipping
the script to every device and gating it in JavaScript would put dead code on
every page for every user of every deployment. Gating in Razor means a device
that does not need this never receives it.

The markers are deliberately NOT gated. `_Layout.cshtml` can read the setting
directly off `Context.Request`, which is one read per page and no plumbing, but
the partials cannot: gating the markers too would mean threading the flag through
`ItemRowModel`, `ConvertActionModel` and `Item.cshtml.cs`'s `FileRow` plus every
construction site, to remove a few bytes per row that no user can observe. The
intent of this section is that no unnecessary JavaScript reaches a device, and
gating the script alone achieves it.

### Which links are armed

Every download link, plus the `data-warm` convert anchors, since they become a
download link too once conversion finishes in-page:

- `_ItemRow.cshtml`, the raw Download anchor.
- `Item.cshtml`, the per-file Download anchors.
- `_ConvertAction.cshtml`, every state except Regenerate: the Cached EPUB
  anchor, and the Convert / Converting / Convert-retry anchors (`data-warm`).

The convert URL answers three different ways depending on state:

- The `data-warm` states (Convert, Converting, Convert-retry) are intercepted by
  the background-convert script only while not yet ready: it calls
  `preventDefault`, so a click before the conversion completes does not
  navigate. Once the poller marks the anchor `data-ready="1"` and repaints the
  label to EPUB, the same anchor is a live download link with nothing left to
  intercept it. `preventDefault` does not stop a second listener on the same
  element, so the arming script stays on these anchors and only skips writing
  the record while not yet ready - otherwise a click before completion would
  store a record that nothing spends, and the user's next deliberate
  navigation would get bounced by it.
- Regenerate navigates but only redirects back to the same listing. Arming it
  would be harmless, since the record would match the page it lands on and be
  discarded, but it would also be pointless.

### How it fails

Every failure mode degrades to today's behaviour, never to something worse:

- `sessionStorage` unavailable, or wiped by the browser restart, means no record
  and no correction. The shine is a plausible instance, since it already loses
  cookies across a browser restart.
- A throw is swallowed by the surrounding `try/catch`, and the anchors are plain
  links that still work.
- With JavaScript off nothing is armed and nothing corrects.

Nothing in this feature can prevent a download or leave a control unusable.

## Out of scope

The reader opening each downloaded book is device behaviour and is not addressed.
The extra page load the correction costs, roughly a second of e-ink repaint, is
accepted: in the batch workflow this exists for it replaces a manual navigation
per book.

## Tests

- The setting round-trips through the cookie wire format and defaults to off.
- With the setting off, no script block is rendered.
- The marker appears on every download anchor regardless of the setting: the raw
  Download anchor, the Cached EPUB anchor, and the `data-warm` convert anchors.
  The skip for a not-yet-ready `data-warm` anchor lives in the layout script,
  not in the markup.
- uicheck: clicking a `data-warm` anchor that is not yet `data-ready` stores no
  record, since that click is intercepted and never navigates.
- uicheck, in a real browser: with the setting on, seed `sessionStorage` with a
  record naming page A, load page B, and assert the browser ends up on page A.
  A download cannot be made to misbehave in headless Chromium, but the
  correcting logic is where the bugs live and it is testable directly.
- uicheck: with a record naming the page that is already loaded, assert no
  navigation occurs and the record is cleared.

## Verification

A device pass is required and cannot be substituted: the misbehaviour does not
exist in any browser CI can run.

The EPUB-on-`/converted` path is the motivating workflow and is the one path the
spike never exercised, because the probes only ever armed the raw Download link.
It is to be tested on hardware once the PR is up. If the Cached EPUB anchor turns
out to behave differently from the raw Download anchor, the design holds but that
anchor may need separate treatment.
