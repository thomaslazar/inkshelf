# Bookmarkable device settings

Issue #45. Some readers keep no cookies across a browser restart, so every
session starts by logging in again *and* re-entering settings — including the
screen override that had to be measured by hand on hardware.

A bookmark is the one thing such a device does keep. This makes the settings page
restorable from one: the URL after saving carries the settings, and opening that
URL applies them.

## Scope

**In:** device settings survive a browser restart via a bookmarked URL.

**Out:** the session. A token in a bookmark sits in browser history and gets
shared by accident, so login still happens on every restart. This fixes the
retyping, not the re-login.

## Behaviour

**Saving.** The settings POST already redirects (POST-redirect-GET). The target
becomes `/settings?` + `DeviceSettings.Serialize()`, which is byte-identical to
the cookie's value. The page you land on after Save is therefore the page that
restores what you just saved, and the browser's own bookmark button captures it.
The existing `range` / `scalerange` warning markers are appended as extra params.

Example target:

```
/settings?retina=1&gray=0&lang=de&fav=&did=9c2f1a4b8e07d631&spread=rotateleft
         &scale=100&ovr=1&ovrw=1120&ovrh=1355&ovrd=1.325
```

**Arriving with settings.** `GET /settings` carrying any recognised settings key
means "these are my settings": values are sanitised, written to the cookie, and
rendered. A plain `GET /settings` behaves exactly as today.

The recognised keys are exactly the ones `Serialize` writes — `retina`, `gray`,
`lang`, `fav`, `did`, `spread`, `scale`, `ovr`, `ovrw`, `ovrh`, `ovrd` — and
nothing else. `range` and `scalerange` are warning markers, not settings, so a URL
carrying only those is not a restore.

**The restart flow.** `/settings` needs no login — it reads cookies and the
locale catalog, never ABS — so opening the bookmark lands straight on the settings
page with the values applied. Logging in happens on the next page that needs the
library, and it does not disturb the settings cookie just written. Either order
works: bookmark then login, or login then bookmark. The restart therefore costs a
login and nothing else.

**Wholesale replacement.** Absent keys fall to their documented defaults rather
than keeping whatever the device had. Merge semantics would make the same URL
mean different things on different devices; replacement makes a bookmark a
complete, predictable statement of a device's configuration.

**Nowhere else.** No other page reads settings from the query, so a link cannot
change settings from a page where changing settings is not the point.
`/library/…?ovrw=1120` does nothing.

**The device id travels too.** Download marks are keyed to it, so the ↓ arrows
showing what this device already has survive the restart as well. It is a random
16-hex tag, not anything personal, but sharing the URL does hand over that
identity — hence the id is in the URL only because the URL is meant to be
bookmarked, not sent.

## Components

Three small changes, no new files.

**`DeviceSettings`** — the cookie path already parses a query string. Extract it
into `FromQuery(IQueryCollection)`, returning `null` when the query carries none
of the recognised keys, and have `Read` call it by wrapping the parsed cookie in a
`QueryCollection`. One parser, two sources, so cookie and URL cannot drift apart.

**`SettingsModel.OnGet`** —

```csharp
var restored = DeviceSettings.FromQuery(Request.Query);
Settings = restored is { } r ? DeviceSettings.Set(Response, r) : DeviceSettings.Read(Request);
```

`Set` mints a device id when one is missing, so a hand-edited URL without `did`
still lands on a valid marks key rather than an empty one.

**`SettingsEndpoints`** — redirect target becomes `"/settings?" +
settings.Serialize()`, warnings appended.

**View** — one localised sentence: "Bookmark this page to restore these settings
later", plus the German string.

## Validation

No new sanitisation. Every field already passes `SanitizeDim`, `SanitizeDpr`,
`SanitizeScale`, `SanitizeId` or `SanitizeLang` on the way in, because the cookie
was already untrusted input. A URL is the same trust boundary and reuses the same
guards: an out-of-range override drops to 0 (override inactive), a hostile `did`
is rejected to empty and re-minted, an unparseable ratio drops to 0.

## Testing

- **Parser parity** — the same string parsed as a cookie and as a query yields
  identical settings. This is the invariant that keeps the two sources honest.
- **`FromQuery` returns null** for a query with no recognised keys, so a plain
  page load cannot be mistaken for a restore.
- **Restore writes the cookie** — `GET /settings?…ovrw=1120…` responds with a
  `Set-Cookie` carrying those values and renders them in the fields.
- **Plain GET leaves the cookie alone** — no `Set-Cookie` for settings.
- **Save round-trips** — `POST /settings` redirects to a location carrying the
  saved values, and following that location reproduces them.
- **Sanitisation holds** — `ovrw=99999` and `did=../../x` do not survive.
- **uicheck** — the bookmark sentence appears in both languages.

## Notes

The URL runs to roughly 200 characters and is visible in the address bar and
browser history. It carries render settings and a device tag; nothing
authorising, and nothing that grants access to a library.

An incidental benefit on a device that cannot copy text: the measured override
numbers are legible in the address bar, so they can be read off the screen
instead of kept on paper. That is the reason for the flat key format over an
opaque blob.

The restore GET is not CSRF-protected while the POST is. A cross-origin image
tag can scramble a device's render settings and re-mint its device id.
Accepted: the only usable defence is a `Sec-Fetch-Dest` header that the old
e-reader engines this feature exists for do not send, so honouring
header-less requests would mean protecting everyone except the vulnerable
client. Impact is annoyance, recoverable with one Save; nothing authorising
is exposed and no response is read.

After a restore you are sitting on the settings URL, so a Back-button return
to an older bookmarked URL re-applies those older settings. That follows
directly from "a URL carrying settings means these are my settings" and is
intended.
