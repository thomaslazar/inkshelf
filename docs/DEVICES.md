# Device support matrix

Which e-readers Inkshelf has been run on, and the settings each one needs. The
numbers here are **measured on hardware**, not derived — a reader's own spec
sheet does not predict them.

Two different engines matter per device, and they disagree more often than you
would expect:

- **The browser** renders Inkshelf itself. It reports the screen size we
  convert against, and it decides whether cookies survive.
- **The EPUB reader app** renders converted comics. It may lay pages out in a
  different pixel space than the browser reports, which is what the screen
  override exists to correct.

See [`tolino-browser.md`](tolino-browser.md) for the browser engine's CSS/JS
limits, which apply to every device below.

## Matrix

| Device | Panel | Detected resolution | Working settings | Status |
|---|---|---|---|---|
| Tolino epos 2 | 1440 × 1920 | 1442 × 1787 @ dpr 1.875 | retina on, grayscale on, page scale 98, no override | Works |
| Tolino shine | 758 × 1024 | 751 × 909 @ dpr 1.325 | retina on, grayscale on, **override 1021 × 1236 @ ratio 1.325**, page scale has no effect | Usable, but see below |

Two further readers have been reported working without special settings; models
to be filled in.

**The shine does not retain cookies.** Reopening its browser loses everything we
store: you are logged out, and every setting goes with it — the language, the
spread mode, and the screen override you just spent time measuring. Cookie
expiry is not the variable; that store simply keeps nothing across a browser
restart. Comics render correctly once it is set up, but setting it up again on
every browser start makes it tiring to live with. Keep your numbers written down
somewhere off-device.

**Three different numbers, and they rarely agree.** Read the row above from left
to right:

- **Panel** is the vendor's hardware resolution.
- **Detected resolution** is what the Settings page shows: the browser's own
  viewport multiplied by its pixel ratio. It falls short of the panel by
  whatever the browser's chrome occupies — on the epos 2, 1787 against a
  1920-px panel, so roughly 133 px of it is browser furniture. The width lines
  up (769 × 1.875 = 1442 for a 1440-px panel), the height cannot.
- **Working settings** is what actually renders correctly, and on a reader that
  needs an override it may match neither of the first two. The shine's 1021 ×
  1236 is nowhere near its 758 × 1024 panel, because its EPUB app lays pages
  out in a wider space than its browser reports.

So treat the first two columns as starting points to measure from, never as the
answer.

## Finding your own numbers

1. Open **Settings** on the device and read the *Detected resolution* line.
2. Convert a comic and look at it. If the pages are right, you are done — most
   devices need nothing beyond page scale.
3. If they are wrong, tick **Override screen resolution** and enter a starting
   point — the detected numbers, or the panel's resolution if you have it — with
   the ratio, save, then use **Regenerate** on the item page. The cache key
   includes these values, so an existing conversion will not change on its own.
4. Adjust in ~2% steps, regenerating each time, until pages fill the screen
   without a blank screen appearing between them. Move both dimensions
   together: the page box keeps the comic's aspect ratio, so trimming only the
   height shrinks the width with it.

   Expect to land somewhere unrelated to either published figure. On the shine
   1120 × 1355 filled the screen but paginated every page into two, and 1021 ×
   1236 was the first step down that cleared it.

## Symptoms and what to change

| Symptom | Cause | What to do |
|---|---|---|
| Comic pages render far too small, complete, with space around them | The reader lays out in a larger pixel space than the browser reports | Raise the override until pages fill the screen |
| A blank screen appears between every page | The page box is slightly taller than the reader's viewport, so one page paginates into two screens | Shrink the override ~2% at a time until it goes away |
| The right or bottom edge of a page is cut off | The page box is larger than the reader's viewport | Shrink the override, or drop page scale a few percent |
| Page scale changes nothing | Some readers size pages from the image's own pixels and ignore the declared box; scale only shrinks the box | Use the override dimensions instead |
| Logged out and settings lost whenever the browser is reopened | The device's cookie store does not persist cookies across a browser restart (seen on the shine) | Nothing yet — log in and re-enter the override; keep the numbers noted off-device |

## Reporting a device

Useful to include: the *Detected resolution* line, whether retina and grayscale
are on, the override and page scale values that work, and which of the symptoms
above you hit. `/diag.html` (with `DIAG_ENABLED=true`) reports the browser's
capabilities if the device turns out to need engine-specific CSS.
