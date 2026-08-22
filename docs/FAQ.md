# FAQ

## A converted comic renders too small, with space around it

The reader is drawing the page image at its own size and will not enlarge it.
Raise the screen override in Settings until pages fill the screen, then use
**Regenerate** on the item page — the conversion is cached per geometry.

## The bottom of every page is clipped

The reader keeps a strip of the page for itself. Drop the page scale a couple of
percent; 98 is usually enough.

## Page scale seems to do nothing

Some readers ignore the page size a book declares, and page scale only ever
changes that size. Use the screen override dimensions instead.

## Pages are about half the size they should be

Retina is off, so page images are capped at the screen size divided by the pixel
ratio. Turn it back on — on a reader that will not enlarge an image, the image has
to arrive big enough.

## I have to log in again whenever I reopen the browser

Some older readers keep no cookies across a browser restart, and the device
settings go with them. Nothing on the server can prevent that; keep a note of your
override values.

## A downloaded file will not open

If the file is a few kilobytes and named with random digits, the reader saved an
error page instead of the book. Check that you are still logged in and try again.

## Where do I find my device's numbers?

Settings shows a *Detected resolution* line. See
[`DEVICES.md`](DEVICES.md) for the values known to work on specific readers.
