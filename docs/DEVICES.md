# Device support matrix

Which e-readers Inkshelf has been run on, and the settings each one needs. The
values are measured on hardware — a spec sheet does not predict them.

<!-- An HTML table, not a markdown one, so a device can carry a Notes row spanning
     the full width. Only add one for behaviour the columns cannot express. -->
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
      <td>16.2.0</td>
      <td>1440 × 1920</td>
      <td>1442 × 1787 @ dpr 1.875</td>
      <td>no override; page scale 98 on the beta reader</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino vision 5</td>
      <td>16.2.0</td>
      <td>1264 × 1680</td>
      <td>1266 × 1547 @ dpr 1.875</td>
      <td>no override; page scale 98 on the beta reader</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino page 2</td>
      <td>16.2.0</td>
      <td>768 × 1024</td>
      <td>759 × 930 @ dpr 1.325</td>
      <td>no override; page scale 98 on the beta reader</td>
      <td>Works</td>
    </tr>
    <tr>
      <td>Tolino shine</td>
      <td>10.5.0</td>
      <td>758 × 1024</td>
      <td>751 × 909 @ dpr 1.325</td>
      <td><strong>override 1120 × 1355 @ ratio 1.325</strong></td>
      <td>Usable, with caveats</td>
    </tr>
    <tr>
      <td colspan="6">
        <strong>Notes on the shine:</strong> it keeps no cookies across a browser
        restart, so the login and every setting — the override included — are
        re-entered each session. Its reader honours nothing a book declares, so
        page scale has no effect there. Its browser predates unprefixed
        <code>flex</code> and <code>box-sizing</code>, so the layout is rough but
        usable.
      </td>
    </tr>
  </tbody>
</table>

## Tolino reader engines

16.2.0 carries two EPUB readers, and which one opens a book decides how converted
comics look:

- **beta** — honours the viewport a fixed-layout page declares, then keeps ~2% of
  the page height for itself. Set page scale to 98 or the bottom is clipped.
- **standard** — ignores the declared viewport and sizes pages from the image
  itself, never enlarging one. Page scale does nothing here, and **retina must
  stay on**: with it off, images are capped at the panel divided by the pixel
  ratio and pages come out at roughly half size.

A device picks either — a vision 5 used the standard reader while an epos 2 on the
same firmware used the beta one. 16.2.0 is the last release these three receive,
so both engines stay relevant. The shine has neither, only its own older reader.

## Browsers

These four run browsers from 2011–2013 — Chrome 30 on 16.2.0, Android 2.3 /
WebKit 533.1 on the shine — and that is what Inkshelf's markup targets. Their
limits are in [`tolino-browser.md`](tolino-browser.md). None of it generalises to
e-readers at large: a device with a current browser can drive the ABS web UI
directly and has no need of Inkshelf.

## Reporting a device

Open a [GitHub issue](https://github.com/thomaslazar/inkshelf/issues), working or
not — a row here saves the next owner of that model the measuring. Useful to
include: the model and firmware, the *Detected resolution* line from Settings, and
the override and page scale that work.

For a browser capability probe, open `/diag.html` on the device. It reports to the
server, which is how you get the result off a reader that cannot copy text:

```bash
docker logs inkshelf 2>&1 | grep "Browser probe"
```

See [`FAQ.md`](FAQ.md) when comics render wrong.
