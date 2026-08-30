# FAQ

## A converted comic renders too small, with space around it

Your scans are larger than the screen override, so the reader shrinks them to
fit and the space is leftover screen. Raise the screen override in Settings:
raising it raises the cap, so less of the scan is thrown away and pages come
out bigger. Each change converts afresh - the cached file is keyed to the
geometry - so just download the comic again after saving.

If pages are still small after raising the override, your scans are smaller
than the cap to start with; see "Pages are much smaller than the screen"
below.

## The bottom of every page is clipped

The reader keeps a strip of the page for itself. Drop the page scale until the
clipping stops. [`DEVICES.md`](DEVICES.md) lists the value that works on each
tested reader.

## Page scale seems to do nothing

Some readers ignore the page size a book declares, and page scale only ever
changes that size. Use the screen override dimensions instead.

## Pages are much smaller than the screen

Page images are only ever shrunk to fit, never enlarged, so a reader that draws
them at their own size shows a small page. Tick **Enlarge small pages** in
Settings: it resamples the pages up to the screen instead of leaving them small.
Files get bigger, which is the trade. Retina must stay on for it to have room to
work, since the enlargement target is the screen in physical pixels.

## I have to log in again whenever I reopen the browser

Some older readers keep no cookies across a browser restart, and the device
settings go with them. Logging in again is unavoidable, but the settings are not:
save them once and bookmark the settings page you land on. Opening that bookmark
restores everything, including the screen override and the device's download
marks.

## I tapped Download and nothing arrived

The page's download links have gone stale, and the part of the device that
actually fetches the file has nothing to authorise itself with. Two things cause
it: the reader brought the page back from its own cache (the page you land on
first after restarting the browser often is one), or Inkshelf itself has been
restarted or updated since the page was opened.

Either way, go to the book again from the library and download from that page.

## Where do I find my device's numbers?

Settings shows a *Detected resolution* line. [`DEVICES.md`](DEVICES.md) lists the
values known to work on specific readers.
