# Showing who is logged in

The deployment is shared with family, and nothing in the UI says which ABS
account a device is signed in as. Handing a reader to someone else, or picking up
a reader that has been in a drawer, there is no way to tell.

## Scope

**In:** the logged-in user's name on the libraries page, appended to the version
line that is already there.

**Out:** everywhere else. The header is already tight on a 751 px-wide reader —
the same row where the logout button once collided with the breadcrumb — and the
libraries page is one tap away from any page via the breadcrumb, which is enough
for a question asked occasionally rather than continuously.

**Out:** changing who can log in, or showing anything else about the account.

## Behaviour

The libraries page renders a labelled name after the version it already shows:

```
Inkshelf v0.6.0 — User: root          (en)
Inkshelf v0.6.0 — Benutzer: root      (de)
```

The label is localised like everything else user-facing, so it needs one key in
each locale file. The login page is untouched: there is no user yet.

When the name is not known the line is exactly what it is today — bare
`Inkshelf v0.6.0`, with no separator and no empty label.

## Where the name comes from

ABS returns the user object on **both** login and refresh, though by different
routes: `/login` (`server/Auth.js:320`) calls `handleLoginSuccess`
(`server/Auth.js:294`), which calls `getUserLoginResponsePayload`
(`server/Auth.js:96`); `/auth/refresh` (`server/Auth.js:329`) calls
`getUserLoginResponsePayload` directly after `handleRefreshToken`, without going
through `handleLoginSuccess`. Both paths include `username` via
`toOldJSONForBrowser` (`server/models/User.js:596`), and
`AbsAuthClient.ReadTokens` already parses that object to pull the tokens out of
it. So the name arrives with the credentials we already read, on either path,
for both password and OIDC login.

It is then stored in the session cookie beside the tokens, which means **no extra
ABS call anywhere**: the libraries page reads a cookie it already decrypts, and
the name still shows when ABS is unreachable.

The alternative — calling `/api/me` when rendering the libraries page — was
rejected. It adds a request per page load for a display string, and shows nothing
in the one situation where you most want to know what you are looking at, namely
ABS being down.

## The cookie must stay backwards compatible

`TokenStore` writes `access \n refresh` and `Read` requires exactly two parts.
Requiring three would make every existing session cookie unparseable and **sign
the whole household out on upgrade**.

So `Read` accepts two *or* three parts: three yields the name, two yields no
name. Nobody is logged out; each device picks up its name on its next login or
token refresh, whichever comes first.

The name goes **last**. ABS usernames are not newline-free by contract, and
putting the name after the tokens means a newline inside it cannot shift the
token fields — the worst case is a display string with a line break in it, which
the view HTML-encodes anyway.

### Rolling back

The reverse direction — a three-part cookie meeting an old build — was also
worked through, because it is not free the way the upgrade is. The old `Read`
does `Split('\n', 2)`, which on `"acc\nref\nalice"` returns two parts: it
*accepts* the cookie, with the username glued onto the refresh token. The access
token is untouched, so the rolled-back device keeps browsing until that access
token expires. On the first 401 the refresh sends the mangled value as an HTTP
header, which throws — a newline is not a legal header value — before anything
leaves the process, so nothing is sent upstream and nothing is logged. The
handler catches that, clears the session, and the device lands on `/login`. Net
cost: every signed-in device is signed out exactly once, deferred to its first
token refresh after the rollback — one hand-typed password on an e-reader
keyboard. Logging back in on the old build writes two fields again, so rolling
forward afterwards just re-learns the name. No wedged state either direction.

This is why the format stays a plain `access \n refresh \n username` rather than
something that would degrade more gracefully on rollback. A version prefix fares
no better — the old parser would read the prefix as the access token and still
fail the same way on refresh — and it adds a field for one direction of a rare
operation. A separate cookie just for the name was rejected for a sharper
reason: it would outlive the session it names — surviving a logout, or a
session that simply expires — so a shared reader could go on showing a name
next to libraries that account can no longer reach: the exact confusion this
feature exists to prevent.

## Components

**`AbsAuthUser`** gains `username`, mirroring how it already carries
`accessToken` and `refreshToken`.

**`Tokens`** gains `Username`. It is the natural carrier: every place that mints
or refreshes a session already passes a `Tokens` around, so nothing else has to
learn about the name, and a mid-request refresh replaces the name with the same
name rather than blanking it.

Empty string, not null, when ABS does not supply one — the record then has no
nullable field to thread through `TokenStore` and the view.

**`TokenStore`** serialises three fields and parses two or three.

**`IndexModel`** exposes the name next to the `Version` it already exposes.

**`Index.cshtml`** renders it, and the locale files gain one key for the label.

## Testing

- **The upgrade case** — a two-part session cookie still authenticates and yields
  no name. This is the test that stops a release from signing the family out.
- **Round trip** — a three-part cookie yields the name.
- **Refresh keeps it** — a mid-request token refresh leaves the name intact
  rather than blanking it.
- **A newline in the name** cannot corrupt the tokens.
- **Render** — the libraries page shows the name when known, and shows exactly
  today's plain version line when not.
- **uicheck** — the name appears on the libraries page. The authed browser pass
  runs in German only, so that covers the German string; the English one is
  covered by the render test.

## Notes

The name is a display string captured at login, not an authorisation input:
nothing branches on it. If an ABS account is renamed, the old name shows until
that device logs in again or its token refreshes.

The session cookie grows by the length of a username. It is already carrying two
JWTs, so this is not a size concern.

`/settings` deliberately makes no ABS calls, which is what lets it work with a
dead session and makes the bookmark-restore flow possible. Storing the name in
the cookie means that page *could* show it later at no cost, but it is out of
scope here — the libraries page is where the version line already lives.
