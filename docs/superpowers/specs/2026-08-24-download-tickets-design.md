# Download tickets

Issue #40. Reproduced on hardware: a tolino shine downloading a ~54 MB converted
comic makes **two** requests per attempt. The browser's own request gets 13-20 MB
and drops the connection; a second request for the identical URL arrives
milliseconds later **with no session cookie** and is rejected in under a
millisecond. The second request is the device's download manager taking over the
transfer without the browser's cookie jar - the mechanism from
janeczku/calibre-web#1527.

Both reported symptoms follow from it: "nothing downloads" (the manager saves a
26-byte 401) and the original "damaged file" report (before #42 that same request
got the login page and the manager saved it as the book).

A cookie cannot fix this - the manager never had one. The download URL has to
carry its own authorisation.

## Scope

**In:** every download link. Converted comics (`/convert/{id}`) and raw ebook
downloads (`/download/{id}`) of any format - epub, pdf, mobi, cbz. The raw path
is the one the original report was about.

**Out:** the in-browser reader (`/read`). Its requests come from the page itself,
which has the cookie.

**Out:** range/resume for raw ABS streams. See Notes.

## Behaviour

**A ticket is a handle, not a credential.** The URL carries a 22-character
random id; everything it stands for lives in server memory. Nothing authorising
is in the URL, so a ticket is revocable, cheap to log, and short enough to read
off a reader's address bar.

**Pre-minted into the link.** Every download link carries `t=<id>`, minted when
the page renders. This is deliberate over minting on the fly and redirecting: the
manager takes over *some* URL, and pre-minting means whatever it takes over
already works. A redirect would rest on an assumption about the device we have
not observed.

**Sliding 15-minute window.** Any request presenting a valid ticket re-stamps it.
The convert poll runs every 5 seconds against the same href, so a ticket stays
alive for as long as its conversion is running and dies 15 minutes after the page
goes quiet. Coming back a day later needs nothing: navigating re-renders the page
and mints fresh tickets.

**Additive, never a gate.** A missing, unknown or expired ticket falls through to
today's cookie path unchanged. A stale ticket therefore cannot break a request
that works now - worst case is exactly the current behaviour.

**Serving bytes only.** A ticket authorises streaming one file. It never
authorises `fresh=1`, a conversion kick, or a status poll; those still require
the cookie. A download manager has no business doing anything but fetching bytes.

**Precedence for a plain download.** When a plain GET carries a valid ticket, the
ticket path serves it - even if a cookie is also present. Both requests then take
the identical path, so what the user tests in the browser is exactly what the
manager gets. For `/convert` the ticket path applies only when the cache file
exists; a plain GET on a not-yet-converted link (no JS, so no `warm=1`) falls
through to the normal kick.

**Marks come from the ticket.** The ticket carries the device id, so a
cookie-less request marks the download against the right device instead of
minting a new one. That also ends the trail of four device ids in 90 minutes seen
on the shine.

## Components

**`DownloadTickets`** (new, singleton). `MintEpub(path, downloadName, itemId,
did)`, `MintRaw(itemId, fileIno, filename, access, did)`, and `Redeem(id)` which re-stamps
on success and returns null when unknown or idle beyond 15 minutes. Expired
entries are pruned on mint. One record with nullable fields backs both kinds; the
convert endpoint requires `FilePath`, the download endpoint requires `Access`.
Both check the ticket's item id against the route id, so a ticket cannot be
replayed on another item.

**`ConvertRowStateResolver.ResolveFor`** returns `(ConvertRowState, string Path)`
instead of discarding the cache path it already computes. That path is what an
EPUB ticket holds.

**`AbsDownloadClient.DownloadEbookAsync`** grows its return to `(Stream,
ContentType, Length)`, so a ticket-served download keeps the `Content-Length` and
content type the cookie path sets. `ConvertWorker`'s call site ignores the extras.
No new method and no item-detail call: the raw ticket carries the filename, which
every mint site already has - `AbsBatchMedia.EbookFile.Metadata.Filename` on a
row, the library file's own metadata on the item page.

**The download filename** moves out of `ConvertService` into a shared helper, so
a ticket-minted name and a cookie-served name cannot drift.

**Mint sites.** Minting happens in the page models, never in a partial.
`ItemRowModel` gains a raw ticket (for its Download href) and an EPUB ticket
(passed into the `ConvertActionModel` it constructs). `ConvertActionModel` gains a
ticket used on `baseHref` only - not `freshHref`, which a ticket must not
authorise, and not `whyHref`, which is a page. The item page's `FileRow` carries
both for each ebook file it lists. That covers the library listing, search,
`/converted` and the item page; the three page models that build rows take
`TokenStore` and `DownloadTickets`.

**`RequestLog`** redacts the ticket to `t=…`. It logs the full query today, and
its comment claims no URL in this app carries anything authorising - that claim
is about to stop being true, and a 15-minute capability in a log file is not
worth the diagnostic value of a random id.

**`ConvertEndpoints`** adds `enableRangeProcessing: true` on the cached-file
result. One argument, and it stops advertising less than a physical file can do.

## Validation

A ticket id is 16 random bytes, base64url. `Redeem` is a dictionary lookup - an
unknown id is indistinguishable from an expired one and both return null. The
payload is server-side, so nothing in it is attacker-controlled: paths come from
`EpubCache.PathFor`, never from a request. The item-id check is the only
cross-field guard needed.

Keying on the client's IP or User-Agent instead of a handle was rejected: this
deployment is shared with family, and two devices behind one household NAT would
be indistinguishable, making it an authorisation decision based on a guess.

## Testing

- **Search rows carry tickets** - a search-result row's download links carry `t=`.
- **Round-trip and expiry** - a minted ticket redeems; one past its window does
  not; redeeming re-stamps, so repeated use keeps it alive.
- **The cookie-less case** - `GET /convert/{id}?t=…` with no cookies streams the
  file; the same request without `t=` still gets a plain-text 401. This is the
  regression test for the reported bug.
- **Raw downloads** - a cookie-less `/download/{id}?t=…` streams from ABS on the
  ticket's bearer, with `Content-Length` set.
- **Serve-only** - a ticket does not authorise `fresh=1`, `warm=1` or `status=1`.
- **No replay across items** - a ticket for item A on item B's route is rejected.
- **Marks** - a ticket-served download marks against the ticket's device id and
  mints no new one.
- **Log hygiene** - the request-log line for a ticketed URL contains no ticket.
- **uicheck** - download links on the listing, the item page and `/converted`
  carry `t=`.
- **Device pass** - a 54 MB comic and a large raw file on the shine, reading the
  request log for the manager's second request.

## Notes

What a leaked URL grants: one file, until 15 minutes of quiet. Not an ABS
session, not another item, and nothing extractable - the ABS bearer for a raw
download never leaves server memory.

A ticket's captured bearer is never refreshed, matching `AbsDownloadClient`'s
existing contract. ABS access tokens live an hour, and a ticket re-stamped by
polls could outlive one; ABS then rejects it and the ticket path falls through to
the cookie path, so a browser click (which carries the session cookie, and
refreshes it) still downloads. Only a cookie-less manager request is left to fail,
and reloading the page fixes that. Adding a refresh to the ticket path would mean
holding a refresh token in the table, which buys an edge case and costs the
table's blast radius - no refresh token ever enters the table.

A restart empties the table, so the download links on any page already open stop
resolving until that page is re-rendered - in a deployment that means every
update. Where cookies persist the browser's own request still succeeds and only
the manager's fails; on a reader whose manager sends none, the download fails
until the page is reloaded. Observed on the shine on 2026-08-25: two attempts
answered 401 on pre-restart tickets, then the same comic downloaded in full once
the listing had been reloaded. Navigating is the whole remedy, which is why it is
an accepted cost rather than a reason to persist the table.

In-memory state is right here: we are one sidecar container, tickets are minted
per page render, and losing the table on restart costs nothing. Growth is bounded
by authenticated page renders and by the 15-minute window. Accepted risk: the
table has no hard ceiling - a few thousand live tickets is single-digit MB, and a
cap would trade that for a silent minting failure, so the bound is traffic, not
code.

**Search rows are covered.** The search branch already fetches the same batch
metadata as the listing and computes convert states from it
(`Library.cshtml.cs:78-82`), and that payload carries the ebook file's size,
mtime and filename - everything both ticket kinds need. The comment in
`RowFor` claiming `_states` is empty for search rows predates that call and is
wrong; correct it while touching the method.

The one case with no ticket is a failed batch-metadata call, which already
degrades a row to the plain "Convert" state. Such a row falls through to today's
cookie path.

**Range/resume stays out, and the device pass settled it.** ABS serves the ebook
endpoint with `res.sendFile`, so Express already honours `Range` and pass-through
would work. It would buy nothing. Across a full pass on the shine (10.5.0) and
the vision 5 and epos 2 (16.2.0) the download manager never sent a `Range`
header - not one `206` in the request log, including on `/convert`, which does
advertise `Accept-Ranges: bytes`. It restarts from zero even when told it need
not.

The waste that costs is real and out of reach from here: the browser transfers a
prefix and discards it at the handoff, about 92 MB thrown away across 281 MB
delivered, worst on a 24.3 MB epub of which 15.9 MB went twice. The browser's
decision to start is not ours to change.
