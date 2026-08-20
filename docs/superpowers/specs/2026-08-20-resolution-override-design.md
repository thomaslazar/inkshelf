# Resolution override

**Status:** design approved, implemented
**Date:** 2026-08-20
**Roadmap item:** Settings → "Resolution override"

## Goal

Let the user hand-set the screen geometry the converter targets, for the cases
where the `scr` probe cannot be trusted: it is missing (JS off, cookies
blocked), it reports a size that is wrong or odd, or it is right but the user
wants something else.

Deliberately an advanced setting. Most devices will never need it; it exists so
that a device the probe gets wrong is still usable.

## Scope

**In:** an "override detected screen" checkbox plus width, height and pixel
ratio fields on `/settings`; the override taking precedence in
`ScreenTarget.FromCookie`; `Dpr` joining the EPUB cache key; a small inline
script that disables the fields when the override is off and disables the
retina checkbox when it is on.

**Out:** presets or a device list (a spec-sheet lookup is the user's job).
Auto-detecting a wrong probe. Any change to how `scr` itself is written.
Reading direction, spread handling and page scale are untouched.

## Design

### A. What the three numbers mean

Width and height are **physical screen pixels** — the number a vendor spec
sheet gives. Pixel ratio is how many image pixels the reader draws per CSS
layout pixel, which is what turns those pixels into the declared viewport:
`viewport = px × scale ÷ dpr` (scale as a fraction), unchanged from today.

When the override is active it supplies the whole answer:

| | override off (today) | override on |
|---|---|---|
| `MaxW`/`MaxH` | from `scr`, × dpr under retina | the entered pixels |
| `Dpr` | from `scr` (1 without retina) | the entered ratio |
| retina | chooses CSS vs CSS × dpr | **not consulted** |

Retina's only job is choosing between the CSS size and CSS × dpr. With both
numbers stated explicitly there is nothing left for it to decide, so the UI
disables it rather than leaving a control that silently does nothing.

A higher pixel ratio means a smaller declared viewport, so the page lays out
smaller. That is the direction to move if pages come out too large or clipped.

### B. Resolution order

`ScreenTarget.FromCookie` gains the override and consults it **first**. This is
the load-bearing part of the change: today the method returns `(0, 0, 1)` the
moment the cookie is missing and never looks further, so an override that is
merely "preferred over a bad value" would not help the no-probe case at all —
which is one of the three reasons for the feature.

With an override there is always a page box, which also closes the one gap left
in spread handling: `SpreadMode.Fit` needs a box to letterbox a spread onto, and
without a probe it had none.

Invalid input (zero, negative, unparseable, or past `MaxDimension` (4096) /
`MaxDpr` (4)) is dropped to 0 on the way out of the cookie, which makes the
override inactive and falls back to the probe — the same posture as a malformed
`scr` cookie. Deliberately dropped rather than clamped: 4096 is far beyond any
e-reader, so a bigger number is a typo, and converting at a size the user never
asked for is worse than ignoring it. `ScreenTarget` clamps again anyway, because
the value crosses a trust boundary and clamping twice is cheaper than trusting
once.

### C. Storage

Four new fields on `DeviceSettings`, in the one existing settings cookie:
`OverrideScreen` (bool, default false), `OverrideW`, `OverrideH` (int px,
default 0 = nothing stored), `OverrideDpr` (double, default 0 = nothing
stored). Init properties, like `Fav`/`Did`/`Spread`/`Scale`, so the existing
positional construction sites keep compiling.

Zero means "no stored value", which is what makes the fields render blank
rather than as a misleading `0` on a device that has never had an override.

The numbers are stored whether or not the override is on, so they survive being
switched off and can be shown as a starting point.

The ratio is parsed accepting both `1.875` and `1,875` — the UI is translated,
and a German-locale user typing a comma should not silently get the fallback.
Normalise the comma, then parse with `InvariantCulture`.

### D. UI

One setting block, so the separator rule ("one rule per setting, not per
option") keeps the fields attached to their checkbox:

```
[x] Override detected screen
    Width  [1264]  Height [1680]  Pixel ratio [1.875]
```

Fields are prefilled from the stored override when there is one, else from the
probe, else blank. Labels stay bare — no explanatory prose beyond at most one
short line. This is a setting most people should scroll past.

A small inline script (ES5: `getElementById`, `onclick`, no libraries) toggles
`disabled` on the three fields, and on the retina checkbox in the opposite
direction. JS is a guideline rather than a hard rule for something this simple,
and without it the page still works: the server ignores the fields when the
checkbox is off.

### E. The disabled-input trap

**A disabled input is not submitted.** So the POST handler must treat an absent
field as "keep what is stored", or the UI would quietly destroy settings:

- override off → the three numbers are disabled → absent → they must be
  preserved, not zeroed.
- override on → retina is disabled → absent → it must be preserved. The current
  code reads `form.ContainsKey("retina")`, where absent means **off**, so
  without this rule turning the override on would silently switch retina off.

Narrow the rule to exactly that case: retina is absent-means-keep **only when
the override checkbox is on**. Everywhere else, a checkbox absent still means
off, which is what the other toggles rely on.

### F. Cache key

`Dpr` joins the key as `-d1.875`, emitted only when it is not 1 — the same
optional-suffix trick as `-s95`, so nothing already cached is invalidated by the
suffix itself.

This is a correctness fix, not bookkeeping. `Dpr` gets away with being absent
from the key today because under retina the cap is CSS × dpr, so a different
ratio already produces a different `WxH`, and without retina it is always 1.
An explicit override breaks that: `1000×2000 @ 1` and `1000×2000 @ 2` produce
identical filenames and different EPUBs, so the second device would be served
the first one's file.

Cached files for retina devices do get a new name once and re-convert, since
their dpr is not 1.

## Testing

Unit:

- an override beats a present-and-valid probe
- an override works with **no** probe, and the resulting target has a box (so
  `Fit` can letterbox a spread)
- retina is not consulted while the override is on
- invalid or absurd input falls back to the probe; values are clamped to
  `MaxDimension` / `MaxDpr`
- the ratio parses from both `1.875` and `1,875`
- cookie round-trip, and a cookie written before this feature lands on the
  documented defaults
- POST preserves the numbers when the fields are absent, and preserves retina
  when the override is on
- cache key round-trips `Dpr`, and omits the suffix at 1

Browser: extend `tools/uicheck` to assert the new controls in English and
German. A real device pass stays with the user — the headless run cannot
reproduce the e-ink engine.

## Limitations

The right numbers cannot be derived, only guessed and then corrected: the
reader's usable page box is smaller than any screen size we can learn, and it
never scales a page to fit. So the expected workflow is "enter the spec-sheet
numbers, then adjust the pixel ratio and Page scale until nothing is cut" —
three knobs the user fiddles with, not a calculation. This is why the fields are
plain numbers rather than a wizard.
