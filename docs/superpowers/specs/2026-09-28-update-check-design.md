# Update check

The version string on the libraries page identifies the running build, but
nothing tells the operator that a newer one exists. Inkshelf ships as a
container that someone pulls by hand; without a nudge, a deploy silently stays
on an old image until its owner happens to look at the repo.

This adds a daily check against the GitHub releases API and renders the result
next to the version already on the page:

```
Inkshelf v1.0.0 (v1.0.1 available) - User: thomas
```

The parenthetical appears only when a newer release exists.

## What this deliberately is not

- Not an updater. It reports; pulling the image stays manual.
- Not dismissable, and not remembered across restarts. A restart re-checks. A
  snooze would need persistence, and the hint is one parenthetical on one page.
- Not on the login page. Telling anonymous visitors that this deployment is
  behind is information they cannot act on and the operator did not offer.
- Not linked to the release notes. An `<a>` to github.com opens a heavy
  JavaScript site on an e-reader; the version number is enough to search for.

## Behaviour

`UpdateCheck`, a singleton, holds the newer release tag or null. Null covers
every uninteresting case at once: disabled, not yet checked, up to date, the
fetch failed, either version unparseable. The page renders the parenthetical
only when it is non-null, so no failure path needs its own UI.

**The check never runs on the request path.** Rendering the libraries page
pokes the singleton, which returns immediately; if the cached result is stale it
starts a fetch in the background and the current render uses whatever it already
had. The first ever page load therefore shows no hint and the next one does.
This is the whole reason for the design: a synchronous call to github.com would
put a third-party host in the critical path of a page load on a device whose
browser is already slow, and one unlucky load a day would look broken.

Intervals: 24 hours after a successful check, 1 hour after a failed one, so a
deployment with no outbound network does not retry on every render.

### Comparison

Both sides are normalised the same way: strip a leading `v`, strip anything from
the first `+`, then `Version.TryParse`. The `+` strip matters because the Docker
build stamps non-release images as `<version>+pr-34.a1b2c3d`; a PR build of
1.0.0 is treated as 1.0.0 and so does see 1.0.1 as newer, which is correct.

If either side fails to parse, the result is null. `/releases/latest` excludes
prereleases, so a `-rc1` tag is not expected; if one arrives anyway,
`Version.TryParse` rejects it and the hint stays hidden. That is the right
failure: a check that cannot compare confidently shows nothing.

### Request

`GET https://api.github.com/repos/thomaslazar/inkshelf/releases/latest`, reading
`tag_name`. It uses its own typed client, not one of the ABS ones: those carry
`AbsAuthHandler` or an ABS `BaseAddress`, and neither belongs on a call to a
third party. It does reuse the same `Inkshelf/x.y.z` User-Agent string, because
GitHub rejects requests that send none.

Unauthenticated GitHub allows 60 requests per hour per IP. One per day is not
near it.

## Configuration

`UPDATE_CHECK`, default on, `false` disables it. On by default because the
operator who benefits is the one least likely to find an opt-in flag; the
opt-out exists because a deployment that makes no outbound connections is a
legitimate choice and should not have to block a host to keep it.

Disabled means the fetch never starts, so there is no traffic to github.com at
all.

## Testing

The comparison is the only logic worth a test. A table over the normalise and
compare helper:

| local | remote | result |
| --- | --- | --- |
| 1.0.0 | v1.0.1 | 1.0.1 |
| 1.0.0 | v1.0.0 | null |
| 1.1.0 | v1.0.1 | null |
| 1.0.0+pr-34.a1b2c3d | v1.0.1 | 1.0.1 |
| 1.0.0 | nightly | null |

No test drives the HTTP call: it would assert that `HttpClient` works.

The UI pass covers the absence case only, since the seeded stack has no newer
release to report. That is fine - the null path is the one every user sees for
most of the life of a deployment.
