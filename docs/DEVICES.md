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
      <th>Panel</th>
      <th>Detected resolution</th>
      <th>Working settings</th>
      <th>Status</th>
    </tr>
  </thead>
  <tbody>
    <tr>
      <td>Tolino epos 2</td>
      <td>1440 × 1920</td>
      <td>1442 × 1787 @ dpr 1.875</td>
      <td>retina on, grayscale on, page scale 98, no override</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino shine</td>
      <td>758 × 1024</td>
      <td>751 × 909 @ dpr 1.325</td>
      <td>retina on, grayscale on, <strong>override 1021 × 1236 @ ratio 1.325</strong></td>
      <td>Usable, with caveats</td>
    </tr>
    <tr>
      <td colspan="5">
        <strong>Notes:</strong> Retains no cookies — every browser restart means
        logging in again and re-entering the override, so keep the numbers noted
        off-device. Page scale has no effect: this reader sizes pages from the
        image and ignores the box we declare.
      </td>
    </tr>
  </tbody>
</table>

Two further readers have been reported working without special settings; models
to be filled in.

**Three different numbers, and they rarely agree.** Reading a device row from
left to right:

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

## Reporting a device

Open a [GitHub issue](https://github.com/thomaslazar/inkshelf/issues) — whether
your reader works or not, both are worth knowing, and a row here saves the next
owner of the same model the measuring.

Useful to include: the model, the *Detected resolution* line from Settings,
whether retina and grayscale are on, the override and page scale values that
work, and which of the symptoms above you hit.

If the device turns out to need engine-specific CSS, open `/diag.html` on it
(needs `DIAG_ENABLED=true`, the default). It shows the browser's capabilities on
screen, with the same data as one block at the bottom — and it reports them to
the server, so you can lift the result from the container log rather than
retyping it off an e-ink screen:

```bash
docker logs inkshelf 2>&1 | grep "Browser probe"
```
