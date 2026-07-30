# OIDC / SSO login

**Status:** design, awaiting approval
**Date:** 2026-07-30
**Roadmap item:** none yet — new

## Goal

ABS can be configured with an OIDC provider. Inkshelf can only do username +
password, so on a server where SSO is the real identity, Inkshelf users need a
second, ABS-local password. Let them log in to Inkshelf through the same
provider, with no JavaScript and no new secret to configure.

## Scope

**In:** an `/oidc/start` + `/oidc/callback` endpoint pair driving ABS's existing
OIDC "mobile" flow, a short-lived encrypted cookie carrying the flow state, an
SSO button on the login page, one env flag, README/ARCHITECTURE notes.

**Out:** provider-side logout (see Limitations). Auto-detecting whether ABS has
OIDC enabled. Inkshelf as its own OIDC client. Password login is untouched and
stays the default.

## Design

### A. Why the ABS mobile flow is the only viable path

ABS exposes two OIDC entry modes (`server/auth/OidcAuthStrategy.js`):

- **Web flow** (`/auth/openid?callback=…`) is unusable: `isValidWebCallbackUrl`
  requires the callback be **same-origin with ABS**. Inkshelf runs on its own
  hostname, so ABS rejects it with 400.
- **Mobile flow** (`/auth/openid?response_type=code&redirect_uri=…&code_challenge=…`)
  is what third-party clients use. The client supplies its own `redirect_uri`
  and a PKCE challenge; ABS bounces the user through the provider, then
  `/auth/openid/mobile-redirect` forwards `?code&state` to that `redirect_uri`;
  `GET /auth/openid/callback?code&state&code_verifier` answers with **JSON in the
  same shape `AbsAuthClient.ReadTokens` already parses** (`user.accessToken`,
  `user.refreshToken`).

No client secret is involved — PKCE covers it, and the provider keeps seeing a
single client (ABS). Inkshelf never talks to the provider's token endpoint.

### B. The wrinkle: two ABS cookies must be replayed server-side

`GET /auth/openid/callback` refuses without ABS's express session
(`Auth.js` → `No session`), and it only returns tokens as JSON when the
`auth_method=openid-mobile` cookie set on leg 1 comes back with it
(`isAuthMethodAPIBased`). Both cookies live on ABS's origin, so the browser
cannot hand them to Inkshelf.

So **Inkshelf performs leg 1 itself** and keeps those cookies:

1. `GET /oidc/start` — mint `verifier` (32 RNG bytes, base64url), `challenge =
   base64url(SHA256(verifier))`, `state` (16 RNG bytes). Server-side
   `GET {ABS}/auth/openid?response_type=code&redirect_uri={self}/oidc/callback&code_challenge=…&code_challenge_method=S256&state=…`,
   **redirects disabled**. Keep the `Location` (the provider's authorize URL) and
   every `Set-Cookie` name=value pair. Store `state`, `verifier` and that cookie
   header in an encrypted cookie, then 302 the browser to `Location`.
2. Provider authenticates the user → back to ABS `/auth/openid/mobile-redirect`
   (needs no session; ABS keys it off `state` in an in-memory map) → browser
   lands on `{self}/oidc/callback?code&state`.
3. `/oidc/callback` — compare `state` against the cookie, then server-side
   `GET {ABS}/auth/openid/callback?code&state&code_verifier` with the stored
   `Cookie` header. Parse tokens, `TokenStore.Save`, delete the flow cookie,
   redirect to `/`.

Inkshelf stays stateless: the only per-flow state is the cookie in the user's
browser.

### C. Code shape

- **`Endpoints/OidcEndpoints.cs`** — the two `MapGet`s. Both return
  `Results.Redirect`; both no-op to `/login` when the feature is off.
- **`AbsAuthClient`** gains `StartOidcAsync(redirectUri, challenge, state, ct)`
  → `(string AuthorizeUrl, string Cookies)` and `CompleteOidcAsync(code, state,
  verifier, cookies, ct)` → `Tokens` (reusing `ReadTokens`). It stays the
  handler-free client, which is still correct here: neither call carries a
  bearer, and neither may recurse through `AbsAuthHandler`.
- **`OidcFlowStore`** (next to `TokenStore`, same `IDataProtector` pattern):
  cookie `inkshelf_oidc`, protector `inkshelf.oidc.v1`, payload
  `state \n verifier \n cookies` (none of the three can contain a newline),
  `MaxAge` **10 minutes**, `HttpOnly`, `SameSite=Lax`, `Secure` from
  `ForceSecureCookies || IsHttps`, `Path=/oidc`.
  `Lax` is load-bearing — the callback arrives as a cross-site top-level
  navigation, which `Strict` would strip.

**Two invariants for the HTTP client**, both required for correctness:

- `AllowAutoRedirect = false` — otherwise leg 1 follows the 302 to the provider
  and the `Location` we need is gone.
- `UseCookies = false` — a typed client's handler is shared process-wide, so a
  `CookieContainer` would pool **every user's ABS session in one jar**. Cookies
  are read from and written to headers by hand, per request.

Since both are set on `AbsAuthClient`'s handler, they apply to login/refresh too
— harmless there (no redirects, no cookies used).

### D. Config

| Key | Default | Meaning |
|---|---|---|
| `OIDC_ENABLED` | `false` | Map the endpoints and show the button. |
| `OIDC_PROVIDER_NAME` | `SSO` | Provider name substituted into the localized `Log in with {0}`. Deliberately not a whole-label override, which would drop the translation; `LOCALES_OVERRIDE_PATH` already covers rewording. |

Off by default, and when off the endpoints are not mapped at all.

**Not auto-detected**, though ABS's unauthenticated `/status` does report
`authMethods`: ABS also requires the operator to whitelist our callback URL
(below), so detection would happily render a button that 400s.

**The redirect URI is derived, not configured:**
`{scheme}://{Request.Host}/oidc/callback`, where scheme is `https` when
`ForceSecureCookies || Request.IsHttps` — reusing the knob the operator already
sets for exactly this "proxy terminates TLS" case, instead of adding a second
one that can disagree with it.

**One-time ABS-side step, documented in the README:** add that exact URL to ABS →
Settings → Authentication → mobile redirect URIs
(`authOpenIDMobileRedirectURIs`). It is an exact string match, so when ABS
answers `Invalid redirect_uri` we **log the URI we sent** — that log line is the
operator's fix.

**ABS derives its own redirect URL from the host Inkshelf presents on leg 1**
(`OidcAuthStrategy.getAuthorizationUrl` reads `req.get('host')` and
`x-forwarded-proto`), and that URL — `/auth/openid/mobile-redirect` — is both
where the provider returns the user and what the provider matches against its
registered redirect URIs. Since leg 1 is a server-side call to `ABS_URL`, which
may be an internal address, leg 1 sets `Host` (and `x-forwarded-proto: https`)
from **`ABS_PUBLIC_URL`**, defaulting to `ABS_URL`. The connection still goes to
`ABS_URL`. Two consequences for the operator, both README material: set
`ABS_PUBLIC_URL` when the two differ, and **register
`https://<abs-host>/auth/openid/mobile-redirect` with the provider** — the mobile
flow's path differs from the web flow's `/auth/openid/callback`, so the client
registration ABS already has is not sufficient. This is the same prerequisite
ABS's own mobile apps carry.

**Inkshelf must be reachable on a port-free URL for SSO to work at all.** ABS
validates every whitelist entry — in the admin UI *and* in
`MiscController.updateAuthSettings` — against
`^\w+://[\w\.-]+(/[\w\./-]*)*$`, whose host charset has no `:`. So
`https://inkshelf.example/oidc/callback` is accepted and
`http://inkshelf.example:5099/oidc/callback` is not, at any port. Behind a
reverse proxy on 443 this is invisible; a bare `dotnet run` on 5099 cannot be
whitelisted at all. The only escape is setting the list to `*`, which disables
redirect validation server-wide — fine for a short test window, not for a
deployment. This is a README note, not something Inkshelf can work around.

### E. Login page

One `<a class="button" href="/oidc/start">`, rendered under the password form
when enabled. A link, not a form: it is a GET that starts a redirect chain, so
there is nothing to protect with antiforgery, and it stays zero-JS.

New locale keys (German only — the English keys *are* the strings): `Log in with {0}`,
`SSO login failed. Please try again.`

Failures (`state` mismatch, expired/absent flow cookie, ABS 4xx/5xx,
unreachable ABS) redirect to `/login?error=sso` which renders that second
string. Details — status, ABS's message, the redirect URI — go to the log at
warning level. Users on a shared deployment see that it failed; the operator
gets the reason.

## Alternatives rejected

- **Inkshelf as its own OIDC client** (own client registration in the provider,
  own client secret). Clean OIDC, but the identity it yields is worthless here:
  Inkshelf still needs an *ABS* token for every API call, and ABS will not mint
  one for a user it did not authenticate. Dead end unless we impersonate with an
  admin API key, which is worse than a password.
- **Let the browser do leg 3.** The browser holds ABS's session cookie, so it
  could call `/auth/openid/callback` itself — and would be shown the JSON
  containing the tokens, with no way to get them back to Inkshelf without
  JavaScript.
- **Run Inkshelf on an ABS subpath** so the same-origin web flow applies. Turns a
  100-line feature into a reverse-proxy topology every operator must reproduce.
- **Store the flow state server-side** (a dictionary keyed by `state`). Breaks
  the app's statelessness for something a 10-minute cookie already carries.

## Known limitations, accepted

- **Logout is local.** Clearing Inkshelf's cookie leaves the provider session
  alive, so clicking the SSO button logs you straight back in without a prompt.
  ABS's end-session URL needs the `openid_id_token` cookie ABS hands the browser,
  which we never see. Real logout means logging out at the provider.
- **We depend on an ABS-internal flow** (session-cookie replay through the mobile
  path). An upstream refactor breaks it loudly at login, not silently.
- **Abandoned flows leak one map entry in ABS.** `openIdAuthSession` has no TTL
  upstream; entries are only removed on mobile-redirect. Bounded in practice by
  ABS's own auth rate limiter.
- **Legs 1 and 3 come from Inkshelf's IP**, so all users share ABS's auth rate
  limit (40 requests / 10 min per IP, 2 per login). Already true of password
  login today, so nothing new — but SSO doubles the requests per login.

## Tests

Unit, against a stubbed ABS handler (no provider needed):

- `challenge` is `base64url(SHA256(verifier))`, unpadded — the one thing whose
  failure looks like a provider misconfiguration rather than our bug.
- Happy path: leg 1's `Set-Cookie`s and the verifier reach leg 3, tokens land in
  `TokenStore`, browser is redirected to `Location` then `/`.
- `state` mismatch is rejected **before** any ABS call.
- Missing/tampered/expired flow cookie → `/login?error=sso`, no session cookie.
- ABS 400 (`Invalid redirect_uri`) and 401 → `/login?error=sso`, no session
  cookie written; the redirect URI appears in the log.
- Two interleaved flows do not see each other's ABS cookies — the guard on
  `UseCookies = false`.
- Endpoints unmapped (404) and button absent when `OIDC_ENABLED` is unset.

`tools/uicheck/run.sh`: assert the button renders with `OIDC_ENABLED=true`
against the seeded ABS (which has no provider, so only the button is checked).

**End-to-end is manual**, against a real provider — the seeded stack has none,
and adding one is out of proportion to a 100-line feature.

## Risks

| Risk | Mitigation |
|---|---|
| Derived redirect URI does not match the ABS whitelist entry | Log the exact URI on ABS's `Invalid redirect_uri`; document the ABS step in the README |
| Deployment exposes Inkshelf on a non-standard port, so no whitelist entry is possible | Documented as a prerequisite: SSO needs a port-free URL (proxy on 80/443) |
| ABS builds an unreachable redirect URL from an internal `ABS_URL` | Leg 1 presents `ABS_PUBLIC_URL` as `Host`; covered by a test asserting the header |
| Wrong scheme behind a proxy that omits `X-Forwarded-Proto` | Scheme follows `FORCE_SECURE_COOKIES`, which such a deployment already sets |
| A shared `CookieContainer` pools ABS sessions across users | `UseCookies = false`, header-only cookies, plus the interleaved-flows test |
| Leg 1 follows the redirect and loses `Location` | `AllowAutoRedirect = false`, covered by the happy-path test |
| Flow cookie stripped on the cross-site callback | `SameSite=Lax`, verified in the browser pass |
| Upstream ABS changes the mobile flow | Fails at login with a logged reason; password login still works |
| Silent 10-minute expiry confuses a slow login | Failure renders a visible message on the login page |
