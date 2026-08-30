# Enlarge small pages

A per-device setting, default off, that resamples comic pages UP to the screen
box when the scans are smaller than the screen. For readers that size a page
from the image itself and never enlarge one.

## Why

Inkshelf never enlarges a page image. `PageImageProcessor.FinishAsync` resizes
only when the image exceeds the cap, and the letterbox uses `Pad` specifically
so a small page is centred rather than scaled. `EpubConverter.PageBox` likewise
returns the scan's own size when it already fits.

That is deliberate. The declared CSS viewport IS scaled up to the cap
(`EpubConverter.Viewport`), so a reader that honours the viewport enlarges the
page for free, with no extra bytes and no extra decode memory on the device.

It only works on a reader that honours the viewport. The Tolino standard reader
and the shine's reader size pages from the image and never enlarge one, so on
those a small comic is drawn small with dead margin around it.

Measured on the real library, converted at the epos 2 geometry
(1442 x 1787 @ ratio 1.875, retina on, grayscale, spreads rotated left):

| | |
|---|---|
| Page images | 1125 x 1600, two volumes of 166 and 178 pages |
| Page box | 1125 x 1600, unchanged, already under the cap |
| Declared viewport | 670 x 953, which fills the 953 CSS height exactly |
| Standard reader | draws 1125 on a 1440 panel, 78% of the width |

Fitting 1125 x 1600 into the 1442 x 1787 box gives 1257 x 1787: 1.117x linear,
1.25x the pixels, roughly 54 MB to 67 MB per volume. Height goes from 89.5% to
100%, width from 78% to 87%. Full width is unreachable because the comic's
aspect is 0.703 against the screen's 0.807, so side margin is permanent. The
purpose is easier reading, not smaller files.

## Not folded into retina

Retina also trades bytes for a fuller screen, so folding this in was considered
and rejected. Retina is on by default and doing this there would change every
existing conversion, including for viewport-honouring readers that already get
the enlargement for free and would start paying real bytes for the same picture.
The whole point is a knob that is off unless the reader needs it.

## Behaviour

`bool Upscale` on `DeviceSettings`, default false, an init property so the
positional construction sites keep compiling. Cookie key `up`, added to the
allow-list of recognised keys. A checkbox on the settings page beside grayscale,
following the existing absent-means-off convention for checkboxes. It reaches
the converter as a matching `RenderTarget.Upscale` init property, via
`DeviceSettingsTargetExtensions` and `ScreenTarget.FromCookie`.

Two guards encode "never enlarge" and both flip together:

1. `EpubConverter.PageBox` returns the scan's own size when the fit factor is
   at least 1. With the flag on it always applies the factor, so the box becomes
   the fitted size.
2. `PageImageProcessor.FinishAsync` resizes only when the image exceeds the cap.
   With the flag on it resizes regardless.

Both, or neither. Enlarging the box alone would pad a white border around an
unchanged image, which is worse than the current behaviour.

The cover is excluded: `ProcessCoverAsync` passes upscale false. A 600 x 853
cover blown up to page size costs bytes for a thumbnail nobody reads.

### The viewport is unaffected

Once the pixels fill the box, `Viewport`'s `fit` factor collapses to 1 and the
declared viewport is the box divided by the ratio: 1257/1.875 x 1787/1.875, or
670 x 953. Identical to what the same comic declares today. A reader that
honours the viewport sees the same layout with denser pixels, so the setting
cannot break the reader that already works.

### Cache key

`EpubCache.PathFor` gains a `-u` marker between the grayscale marker and the
spread letter, with the mirror strip in `TryParse` and a `bool Upscale` on
`CachedVariant`. `Converted.cshtml.cs` and `ConvertRowStateResolver` compare it
like the other render knobs. Without this an upscaled EPUB would overwrite the
plain one at the same path.

`u` is safe: it is not one of the spread letters (l, m, a, c, f) and does not
collide with the `-s` scale or `-d` ratio suffixes. Existing cached files carry
no `-u` and parse as off, which is what they are, so nothing is invalidated.

## Tests

- Converter: a below-cap page yields box and image at the fitted size with the
  flag on, and unchanged with it off.
- Cover stays unenlarged with the flag on.
- Cache filename round-trips the flag, and a name without `-u` parses as off.

## Docs

- `FAQ.md`, "Pages are much smaller than the screen", is currently wrong: it
  claims retina and the screen override make small images bigger. Both only
  raise a ceiling, which does nothing to a scan already below it. Rewrite it to
  point at the new setting.
- `tolino.md`: one line on the standard reader bullet.
- `DEVICES.md`: the epos 2 row's working settings.
- `ARCHITECTURE.md`: a clause on the existing viewport invariant noting that
  "the image keeps its own pixels" is now conditional. Not a new entry.
- German strings for the new label in `locales/de.json`.
