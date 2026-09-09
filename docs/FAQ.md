# FAQ

## A converted comic renders too small, with space around it

Your scans are larger than the screen override, so Inkshelf shrinks them to it
during conversion and the reader draws them at that size, leaving the rest of
the screen empty. Raise the screen override in Settings: raising it raises the
cap, so less of the scan is thrown away and pages come out bigger. With no
override set, the cap comes from the detected resolution instead. Each change
converts afresh - the cached file is keyed to the geometry - so just download
the comic again after saving.

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

## The list shows too many or too few books per screen

Set **Items per page** in Settings to how many rows you want per page, from 5
to 50 (default 10). It applies to the library listing and the converted page.
Search results are not affected: they stay capped at 25 and are not paged.

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

## After I download a book, the reader leaves the page I was on

Some readers close and reopen the browser to hand the file to their reader app,
and it comes back on an older page instead of the one you were reading. Tick
**Return to the list after a download** in Settings: it sends you back to the
page each download started from. It costs one extra page load per download.

## Where do I find my device's numbers?

Settings shows a *Detected resolution* line. [`DEVICES.md`](DEVICES.md) lists the
values known to work on specific readers.
