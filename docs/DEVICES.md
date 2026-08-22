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

## Matrix

<!-- An HTML table, not a markdown one, so a device can carry a Notes row
     spanning the full width. The Notes row is optional — only add one for
     behaviour the Working settings column cannot express. -->
<table>
  <thead>
    <tr>
      <th>Device</th>
      <th>Firmware</th>
      <th>Panel</th>
      <th>Detected resolution</th>
      <th>Working settings</th>
      <th>Status</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>Tolino epos 2</td>
      <td>16.x</td>
      <td>1440 × 1920</td>
      <td>1442 × 1787 @ dpr 1.875</td>
      <td>retina on, grayscale on, no override; page scale 98 on the beta reader</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino vision 5</td>
      <td>16.2.0</td>
      <td>1264 × 1680</td>
      <td>1266 × 1547 @ dpr 1.875</td>
      <td>no override needed; page scale 98 on the beta reader</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino page 2</td>
      <td>16.2.0</td>
      <td>768 × 1024</td>
      <td>759 × 930 @ dpr 1.325</td>
      <td>no override needed; page scale 98 on the beta reader</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino shine</td>
      <td>10.5.0</td>
      <td>758 × 1024</td>
      <td>751 × 909 @ dpr 1.325</td>
      <td>retina on, grayscale on, <strong>override 1021 × 1236 @ ratio 1.325</strong></td>
      <td>Usable, with caveats</td>
    </tr>
    <tr>
      <td colspan="6">
        <strong>Notes:</strong> Retains no cookies — every browser restart means
        logging in again and re-entering the override, so keep the numbers noted
        off-device. Page scale has no effect: this reader sizes pages from the
        image and ignores the box we declare — its reader is old enough to honour
        nothing we declare. The layout itself is rough here
        rather than broken — its browser predates unprefixed <code>flex</code>
        and <code>box-sizing</code>, so rows stack and full-width fields
        overflow slightly. Everything works; it is not pretty.
      </td>
    </tr>
  </tbody>
</table>

**Panel** figures come from the vendor or the model's Wikipedia page; a dash
means no published pixel count, leaving the detected column as all we have.

**Three different numbers, and they rarely agree.** Reading a device row from
left to right:

- **Panel** is the vendor's hardware resolution.
- **Detected resolution** is what the Settings page shows: the browser's own
  viewport multiplied by its pixel ratio. It falls short of the panel by
  whatever the browser's chrome occupies — on the epos 2, 1787 against a
  1920-px panel, so roughly 133 px of it is browser furniture. The width lines
  up (769 × 1.875 = 1442 for a 1440-px panel), the height cannot. Across the
  four readers above the width lands within 2 px above, or 9 px below, the
  panel, while the height comes up 94–133 px short. So expect the detected
  width to be about right and the detected height to be a tenth low.
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
| The bottom of every page is clipped | The beta reader honours the declared viewport, then keeps ~2% of the page height | Set page scale to 98 |
| Page scale changes nothing | The legacy reader ignores the declared viewport, and scale only ever multiplies that viewport | Use the override dimensions instead |
| Logged out and settings lost whenever the browser is reopened | The device's cookie store does not persist cookies across a browser restart (seen on the shine) | Nothing yet — log in and re-enter the override; keep the numbers noted off-device |

## Browser engines

The browsers on these readers are old, and Inkshelf's markup is written for the
oldest one rather than for a modern engine. What is and is not supported —
no `object-fit`, no flex `gap`, no CSS custom properties, ES5 JavaScript only —
is documented in [`tolino-browser.md`](tolino-browser.md), probed on the epos 2
and treated as the floor for every device here.

The EPUB reader app is a separate engine and a far less documented one. It is
not probeable: our JavaScript runs in the browser, while comic layout happens in
the reader. Everything known about it comes from what pages look like on
hardware, which is what the Notes rows in the matrix record.

Tolino firmware carries **two** of them, and which one opens a book decides how
converted comics look:

- The **beta reader** honours the `viewport` a fixed-layout page declares — then
  keeps roughly 2% of the page height for itself. At page scale 100 that clips
  the bottom of every page, so **set page scale to 98 on the beta reader**. It is
  not the default: the readers that need it are specific ones, and correcting for
  them everywhere would shrink pages on readers that do not.
- The **legacy reader** ignores the declared viewport and lays the page out in
  its own area. Page scale has no effect there at all — it only ever multiplies
  the declared viewport — so the override dimensions are the only knob that
  moves anything.

Firmware predicts this better than the model does. Every 16.x reader tested — an
epos 2, a vision 5 and a page 2 — behaves identically: **the beta reader clips
the bottom of a page and wants scale 98, the legacy reader shows the full page at
100.** 16.x is also the last release those devices receive, so the beta reader
stays permanently "beta" and both engines matter indefinitely.

The shine (10.5.0) has no beta reader, and its old one honours nothing we
declare, which is why it is the only device here needing a hand-measured
override.

## Reporting a device

Open a [GitHub issue](https://github.com/thomaslazar/inkshelf/issues) — whether
your reader works or not, both are worth knowing, and a row here saves the next
owner of the same model the measuring.

Useful to include: the model, the *Detected resolution* line from Settings,
whether retina and grayscale are on, the override and page scale values that
work, and which of the symptoms above you hit.

If the device turns out to need engine-specific CSS, open `/diag.html` on it
(needs `DIAG_ENABLED=true`, the default). It renders the browser's capabilities
on screen *and* reports them to the server — and the server log is how you get
them off the device, because e-reader browsers generally cannot select or copy
text:

```bash
docker logs inkshelf 2>&1 | grep "Browser probe"
```

That prints the whole probe as one line of JSON, ready to paste into the issue.
The page also shows the same block at the bottom, which only helps if you are
probing from a desktop or phone.
