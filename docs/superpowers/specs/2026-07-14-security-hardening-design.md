# Inkshelf Security Hardening — Design

**Date:** 2026-07-14
**Status:** Approved for planning
**Source:** Handover §5 security review (`temp/refactor-handover.md`), triaged
against the post-refactor code via a brainstorming session with the owner.
Follow-on to the structural refactor (PR #5, merged to `main` @ 2cf628d).

## Goal & scope

Close the actionable security findings from the review. Unlike the refactor,
this work **intentionally changes runtime behavior** — that is the point.

**In scope** (owner's selection): findings **#1, #2, #3, #4, #5**.
**Documented as accepted tradeoffs, not coded:** #6, #7 (see end).

Ground rules (from `CLAUDE.md`):
- All new config extends the typed `AbsOptions` (one config surface, added in the
  refactor). `dotnet test` green after every step.
- Conventional Commits; ask before committing; no `Co-Authored-By`.
- Verify ABS behavior against `temp/audiobookshelf/`. Near-zero client JS
  unchanged (none of this touches the two inline scripts → no Tolino re-test).
- Sequenced as small, independently-testable steps on one branch
  (`security/hardening`), shipping as a single PR — same model as the refactor.

## New configuration (all on `AbsOptions`)

| Property | Config key | Type | Default | Consumer |
|---|---|---|---|---|
| `ForceSecureCookies` | `FORCE_SECURE_COOKIES` | bool | `false` | cookie `Secure` flag |
| `TrustedProxy` | `TRUSTED_PROXY` | string? | `null` | forwarded-headers trust |
| `DiagEnabled` | `DIAG_ENABLED` | bool | `true` | `/diag` endpoint mapping |
| `MaxArchiveBytes` | `MaxArchiveBytes` | long | `524288000` (500 MB) | convert size ceiling |
| `MaxCacheBytes` | `MaxCacheBytes` | long | `1073741824` (1 GB) | cache LRU cap |

Booleans parse from config strings (`"true"`/`"false"`); longs parse with a
fallback to the default when unset/invalid. `AbsOptions` binding stays inline in
`Program.cs` (as established in the refactor).

---

## #1 — Forwarded-header trust & Secure cookies (High)

**Problem.** `Program.cs` clears `KnownIPNetworks`/`KnownProxies`, so any direct
client can spoof `X-Forwarded-Proto`/`X-Forwarded-For`. The only security-relevant
consumer of the resulting `Request.IsHttps` is the session/favorite cookie
`Secure` flag (redirects in the app are relative, so scheme doesn't matter). A
spoofed `X-Forwarded-Proto: http` makes the app drop `Secure`, exposing the
encrypted-token cookie to plaintext interception.

**Design.**
- **Primary fix — force-secure cookies.** `TokenStore` (already a DI service)
  injects `AbsOptions`; the cookie `Secure` becomes
  `options.ForceSecureCookies || Ctx.Request.IsHttps`. `Favorites` is a static
  helper taking `HttpResponse`, so `Favorites.Set` resolves `AbsOptions` from
  `res.HttpContext.RequestServices` and applies the same rule. Operators set
  `FORCE_SECURE_COOKIES=true` in the (always-HTTPS-behind-proxy) deployment; the
  spoofable `IsHttps` no longer has a security consequence.
- **Optional root-cause tightening.** When `TrustedProxy` is set (a comma-separated
  list of IPs/CIDRs), `Program.cs` populates `ForwardedHeadersOptions.KnownProxies`
  / `KnownIPNetworks` from it (default-deny — only those proxies may set forwarded
  headers) instead of clearing. When unset, current clear-all behavior is retained
  for back-compat (the real risk is already covered by force-secure).

**Tests.** `TokenStore` writes `Secure` when `ForceSecureCookies` is true even on a
non-HTTPS request (and still when `IsHttps` is true). A parser test maps a
`TRUSTED_PROXY` CIDR string to the expected `KnownIPNetworks`/`KnownProxies` (extract
the parse into a small pure helper so it's testable without booting the host).

---

## #2 — `/diag` log-injection / flood (High)

**Problem.** `DiagEndpoints` reads the request body unbounded and logs it verbatim,
pre-auth: newlines forge fake log lines, large bodies flood log storage, no rate
limit.

**Design.**
- **Bound the body.** Read at most **4096 bytes** from `ctx.Request.Body` (read
  4097 and note truncation; do not `ReadToEndAsync` the whole stream).
- **Sanitize before logging.** Replace control characters (including `\r`/`\n`)
  with a placeholder (e.g. `.`) so a probe body can't inject log lines. Cap the
  logged string at the 4 KB bound.
- **Kill-switch.** `Program.cs` calls `MapDiagEndpoints()` only when
  `DiagEnabled` is true, so production can disable the pre-auth endpoint entirely.

**Tests.** A pure `SanitizeProbe(string)` helper: newlines/control chars replaced,
over-length input truncated to the cap. (The enable/disable is a wiring concern
covered by an endpoint smoke test: `/diag` returns 404 when disabled.)

---

## #3 — EPUB cache: `scr` clamp + LRU cap (Medium)

**Problem.** The client-set `scr` cookie (only validated as positive ints) feeds
`maxW × maxH` into the cache filename, and the cache has no eviction — an
authenticated user can mint unlimited size variants per item and exhaust disk.

**Design.**
- **Clamp the cookie.** `ScreenTarget.FromCookie` clamps each parsed dimension to
  `[1, 4096]` (a `const MaxDimension = 4096`), so absurd sizes can't reach the
  cache key (or the converter).
- **Global max-bytes LRU sweep.** `EpubCache` gains `EnforceCap(long maxBytes)`:
  enumerate `*.epub`, sum sizes, and while over the cap delete files by oldest
  `LastWriteTimeUtc` until under. To approximate true LRU (serving a cached file
  doesn't normally update its timestamp), a **cache hit touches** the file
  (`File.SetLastWriteTimeUtc(path, now)`). `ConvertService` touches on a cached
  serve and calls `EnforceCap(options.MaxCacheBytes)` after a successful
  conversion write.

**Tests.** `ScreenTarget.FromCookie` clamps `"9999x9999x1"` → `4096×4096` (and
leaves in-range values untouched). `EpubCache.EnforceCap`: seed a temp dir with
files whose sizes exceed the cap and staggered `LastWriteTimeUtc`; assert the
oldest are deleted until under the cap and newest survive.

---

## #5 — Concurrent-convert race (Low-Med)

**Problem.** Two simultaneous `/convert` requests for the same item both pass the
`File.Exists` check, both convert, and both write `outPath + ".tmp"` → wasted CPU
and a possibly corrupted cache file.

**Design.** A new **singleton** `ConvertLock` holding a
`ConcurrentDictionary<string, SemaphoreSlim>` keyed by the **cache-output path**
(so distinct device sizes/items don't serialize against each other, but identical
targets do). `AcquireAsync(key, ct)` returns an `IAsyncDisposable`/`IDisposable`
that releases and **ref-counts** the semaphore, removing it from the dictionary at
zero waiters so the map can't grow unbounded. Injected into the scoped
`ConvertService`; the convert-on-miss block runs inside the lock with a
**double-checked `File.Exists`** so the second waiter serves the file the first
just wrote instead of re-converting.

**Tests.** `ConvertLock`: two concurrent `AcquireAsync` on the same key serialize
(the second doesn't enter until the first releases); different keys run
concurrently; the dictionary is empty again after all releases (ref-count
cleanup).

---

## #4 — Archive size ceiling (Medium)

**Problem.** `ConvertService` copies the whole ebook stream into a `MemoryStream`
and the converter holds pages as `byte[]`; a decompression-bomb or huge CBZ OOMs
the sidecar.

**Design.** In `ConvertService`, before/while buffering the ebook stream:
- If the response `Content-Length` is present and exceeds `MaxArchiveBytes`, abort
  immediately.
- Enforce the ceiling **during** the copy as well (count bytes; abort past the
  limit) to catch missing/lying `Content-Length`.
- On exceed: log a warning (item id + limit) and return `ConvertOutcome.NotFound`
  (the existing "can't convert" signal the endpoint maps; no new outcome kind
  needed). Default ceiling **500 MB** — conversion downscales to device size
  anyway, so this only blocks pathological/bomb inputs.

No temp-file spooling (the reader already streams per-page post-refactor; only the
initial full-archive buffer remains, and the ceiling bounds it deterministically).

**Tests.** A helper that copies a stream into a buffer with a byte ceiling: a
stream over the limit throws/aborts; an under-limit stream copies fully.
`ConvertService` returns `NotFound` when the ebook stream exceeds the ceiling
(stub `AbsApiClient` returns an oversized stream).

---

## Sequencing

Six steps on `security/hardening`, one PR at the end. Each ends with `dotnet test`
green.

1. `AbsOptions` knobs + **#1** (force-secure cookies + optional trusted-proxy parse).
2. **#2** `/diag` body cap + sanitize + kill-switch.
3. **#3** `scr` clamp + `EpubCache.EnforceCap` LRU + touch-on-hit.
4. **#5** `ConvertLock` singleton + double-checked convert.
5. **#4** archive size ceiling in `ConvertService`.
6. Docs: update `docs/ARCHITECTURE.md` (fold the "not yet done" list into the
   shipped conventions; note the new config), and document the accepted tradeoffs.

Steps are independent except that #4 and #5 both touch `ConvertService`'s
convert-on-miss block — do #5 before #4 so the ceiling check lands inside the
already-locked block.

## Accepted tradeoffs (documented, not coded)

- **#6 — `/login` has no local rate limit.** ABS enforces its own brute-force
  protection, and per-IP limiting is only meaningful once client IP is
  trustworthy (which `TRUSTED_PROXY` enables but the deployment doesn't require).
  Revisit if the sidecar is ever exposed without a trusted proxy.
- **#7 — existing hygiene is sound:** Data-Protection-encrypted, HttpOnly,
  SameSite=Lax token cookie; antiforgery on state changes; URL-escaped ids;
  `/cover` width clamped 1–400; XML-escaped EPUB metadata. Data-Protection keys
  sit unencrypted in the keys volume — standard for this deployment; keep the
  volume private.

## Non-goals

- No auth/authz model changes, no new endpoints, no dependency additions.
- Not behavior-preserving (by design) — but no *intended* change to a legitimate
  user's experience: real archives convert, real devices get cached variants,
  cookies still round-trip. Only abuse/edge paths change.
