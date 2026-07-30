# OIDC / SSO Login Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users log in to Inkshelf through the OIDC provider ABS is configured with, with zero JavaScript and no new secret in Inkshelf's config.

**Architecture:** Inkshelf drives ABS's existing OIDC *mobile* flow, because the web flow requires the callback be same-origin with ABS. Inkshelf performs leg 1 (`GET {ABS}/auth/openid?…`) **server-side** so it can keep the two ABS cookies (`connect.sid`, `auth_method=openid-mobile`) that the final token exchange requires, redirects the browser to the provider, and — when the browser comes back to `/oidc/callback?code&state` — replays those cookies plus the PKCE verifier to `GET {ABS}/auth/openid/callback`, which answers with the same token JSON `AbsAuthClient.ReadTokens` already parses. Flow state lives in a 10-minute encrypted cookie, so the app stays stateless.

**Tech Stack:** .NET 10, ASP.NET Core Razor Pages, xUnit. No new dependencies.

## Global Constraints

- **Spec:** `docs/superpowers/specs/2026-07-30-oidc-login-design.md`. Read it before starting — sections B (the two replayed cookies) and C (the two HTTP-client invariants) are where the traps are.
- **No new dependencies.** PKCE is `SHA256` + base64url from the BCL.
- **All work happens inside the devcontainer.** There is no `dotnet` on the host.
- **Branch:** `feat/oidc-login` (already created, spec already committed).
- **Conventional Commits**, imperative lowercase subject, max ~72 chars. Commit per task.
- **Do NOT add `Co-Authored-By:` or "Generated with Claude Code" lines to commits.**
- **Do NOT edit `CHANGELOG.md`.** Shipped work goes to `docs/ROADMAP.md`'s `## Done`.
- **`docs/ARCHITECTURE.md` is a map, not a diary** — see `CLAUDE.md`. This feature earns the two client invariants and one "why not the web flow" line. Nothing else.
- **Two invariants that silently break things if dropped:**
  - `AllowAutoRedirect = false` on `AbsAuthClient`'s handler — otherwise leg 1 follows the 302 to the provider and the `Location` we need is gone.
  - `UseCookies = false` on the same handler — the handler is shared process-wide, so a `CookieContainer` would pool **every user's ABS session in one jar**. Cookies are passed as headers, per request.
- **Password login must keep working unchanged** at every step.
- Run `dotnet format Inkshelf.sln --verify-no-changes` before the final commit; CI runs it over the whole solution.
- Run the suite with `dotnet test` from `/workspaces/inkshelf`. It reports **288 passed** before you start.

---

### Task 1: Flow state in an encrypted cookie

**Files:**
- Create: `src/Inkshelf/Auth/OidcFlowStore.cs`
- Test: `tests/Inkshelf.Tests/OidcFlowStoreTests.cs`

**Interfaces:**
- Consumes: `IDataProtectionProvider`, `IHttpContextAccessor`, `AbsOptions` — same constructor shape as `TokenStore`.
- Produces: `record OidcFlow(string State, string Verifier, string Cookies)`; `void Save(OidcFlow)`, `OidcFlow? Read()`, `void Clear()`.

Model it on `TokenStore` (same file layout, same protector pattern) — cookie `inkshelf_oidc`, protector `inkshelf.oidc.v1`, payload `State \n Verifier \n Cookies`, `MaxAge = 10 min`, `HttpOnly`, `IsEssential`, `SameSite=Lax`, `Secure = _options.ForceSecureCookies || IsHttps`, `Path = "/oidc"`.

`SameSite=Lax` is load-bearing: the callback is a cross-site top-level navigation, and `Strict` would strip the cookie. Do not "tighten" it.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Save_then_Read_roundtrips()      // mirror TokenStoreTests: move Set-Cookie into a fresh context
    [Fact]
    public void Read_returns_null_when_absent()
    [Fact]
    public void Read_returns_null_when_tampered()   // flip a char in the protected blob
    [Fact]
    public void Save_scopes_the_cookie_to_oidc_and_is_lax()  // assert "path=/oidc" and "samesite=lax" in Set-Cookie
    [Fact]
    public void Save_marks_secure_when_forced()   // AbsOptions { ForceSecureCookies = true } over plain http
```

- [ ] **Step 2: Implement, run `dotnet test`, commit** — `feat: store the oidc flow in an encrypted cookie`

---

### Task 2: The two ABS calls

**Files:**
- Modify: `src/Inkshelf/Abs/AbsAuthClient.cs`, `src/Inkshelf/Abs/AbsExceptions.cs`
- Test: `tests/Inkshelf.Tests/AbsAuthClientTests.cs`

**Interfaces:**
- Produces:
  - `Task<(string AuthorizeUrl, string Cookies)> StartOidcAsync(string redirectUri, string challenge, string state, CancellationToken ct = default)`
  - `Task<Tokens> CompleteOidcAsync(string code, string state, string verifier, string cookies, CancellationToken ct = default)`
  - `class AbsOidcException(int status, string body) : Exception` in `AbsExceptions.cs`.
- Reuses the existing private `ReadTokens`.

`StartOidcAsync` issues
`GET /auth/openid?response_type=code&redirect_uri={escaped}&code_challenge={challenge}&code_challenge_method=S256&state={state}`,
requires a 3xx with a `Location`, and returns `Location` plus every `Set-Cookie`'s `name=value` part joined with `"; "` (drop the attributes — we are building a request `Cookie` header, not storing cookies). Anything else → `AbsOidcException` carrying the status and the body, because ABS answers a bad whitelist entry with `400 Invalid redirect_uri` and that body is the operator's fix.

`CompleteOidcAsync` issues `GET /auth/openid/callback?code&state&code_verifier` with a hand-set `Cookie` header, throws `AbsOidcException` on non-success, else `ReadTokens`.

Keep this client **handler-free** (no `AbsAuthHandler`): neither call carries a bearer, and neither may recurse through the refresh handler.

- [ ] **Step 1: Write the failing tests** (extend `AbsAuthClientTests`, `StubHandler` is enough)

```csharp
    [Fact]
    public async Task StartOidcAsync_sends_pkce_params_and_returns_location_and_cookies()
    {
        var h = new StubHandler(_ =>
        {
            var res = new HttpResponseMessage(System.Net.HttpStatusCode.Found);
            res.Headers.Location = new Uri("https://idp.example/authorize?x=1");
            res.Headers.Add("Set-Cookie", "connect.sid=s%3Aabc; Path=/; HttpOnly");
            res.Headers.Add("Set-Cookie", "auth_method=openid-mobile; Path=/; HttpOnly");
            return res;
        });

        var (url, cookies) = await Client(h).StartOidcAsync("https://ink.example/oidc/callback", "chal", "st8");

        Assert.Equal("https://idp.example/authorize?x=1", url);
        Assert.Equal("connect.sid=s%3Aabc; auth_method=openid-mobile", cookies);
        var q = h.Last!.RequestUri!.Query;
        Assert.Contains("response_type=code", q);
        Assert.Contains("code_challenge=chal", q);
        Assert.Contains("code_challenge_method=S256", q);
        Assert.Contains("state=st8", q);
        Assert.Contains(Uri.EscapeDataString("https://ink.example/oidc/callback"), q);
    }

    [Fact]
    public async Task StartOidcAsync_throws_with_body_on_400()   // "Invalid redirect_uri" survives on the exception
    [Fact]
    public async Task StartOidcAsync_throws_when_no_location()
    [Fact]
    public async Task CompleteOidcAsync_replays_cookies_and_verifier_and_parses_tokens()
    [Fact]
    public async Task CompleteOidcAsync_throws_on_401()
```

- [ ] **Step 2: Implement, run `dotnet test`, commit** — `feat: drive the abs oidc mobile flow from the auth client`

---

### Task 3: Config and client wiring

**Files:**
- Modify: `src/Inkshelf/AbsOptions.cs`, `src/Inkshelf/Program.cs`
- Test: `tests/Inkshelf.Tests/SmokeTests.cs` (or wherever the options assertions live)

**Interfaces:**
- Produces: `AbsOptions.OidcEnabled` (`bool`, default `false`), `AbsOptions.OidcButtonLabel` (`string?`).
- Bind `OIDC_ENABLED` (`bool.TryParse`, same shape as `FORCE_SECURE_COOKIES`) and `OIDC_BUTTON_LABEL`; extend the config-key comment at the top of `AbsOptions.cs`.

Also in `Program.cs`, on the `AbsAuthClient` registration only:

```csharp
builder.Services.AddHttpClient<AbsAuthClient>(ConfigureAbs)
    // OIDC leg 1 needs the raw 302 (we want its Location), and this handler is
    // shared process-wide — a CookieContainer would pool every user's ABS
    // session in one jar, so cookies are passed as headers instead.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    });
```

- [ ] **Step 1: Write the failing test** — a `WebApplicationFactory<Program>` boot with `OIDC_ENABLED=true` resolves an `AbsOptions` with `OidcEnabled == true`; default boot has it `false`.
- [ ] **Step 2: Implement, run `dotnet test`, commit** — `feat: add the oidc config flags`

---

### Task 4: The endpoints

**Files:**
- Create: `src/Inkshelf/Endpoints/OidcEndpoints.cs`
- Modify: `src/Inkshelf/Program.cs` (`app.MapOidcEndpoints();` next to `MapSessionEndpoints`)
- Test: `tests/Inkshelf.Tests/OidcEndpointTests.cs`

**Interfaces:**
- Produces: `MapOidcEndpoints(this IEndpointRouteBuilder)`, mapping **only when `OidcEnabled`** (so it is a real 404 when off); `internal static string Challenge(string verifier)` for the PKCE derivation.

`GET /oidc/start`:
1. `verifier` = base64url of 32 `RandomNumberGenerator` bytes; `state` = same over 16 bytes; `challenge = Challenge(verifier)` = base64url of `SHA256(ASCII(verifier))`, **unpadded, `-`/`_` alphabet**.
2. `redirectUri = $"{scheme}://{Request.Host}/oidc/callback"` where `scheme` is `https` when `options.ForceSecureCookies || Request.IsHttps` — reusing the knob the operator already sets, rather than adding a second one that can disagree.
3. `StartOidcAsync` → `flowStore.Save(new OidcFlow(state, verifier, cookies))` → `Results.Redirect(authorizeUrl)`.

`GET /oidc/callback?code&state`:
1. `flowStore.Read()`; missing → fail. `state` mismatch (compare with `CryptographicOperations.FixedTimeEquals` over the UTF-8 bytes, or at minimum `==`) → fail **before any ABS call**. Empty `code` → fail.
2. `CompleteOidcAsync` → `tokenStore.Save(tokens)` → `flowStore.Clear()` → `Results.Redirect("/")`.

Failure in either endpoint: log at **warning** with the status, ABS's body and the `redirectUri` we sent (that line is how an operator fixes a whitelist mismatch), then `Results.Redirect("/login?error=sso")`. Catch `AbsOidcException`, `HttpRequestException` and `InvalidOperationException` — the same set `LoginModel` already catches. Never let ABS's body reach the page.

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void Challenge_is_unpadded_base64url_sha256_of_the_verifier()
    {
        // RFC 7636 Appendix B test vector — the one failure that looks like a
        // provider misconfiguration instead of our bug.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            OidcEndpoints.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public async Task Start_and_callback_are_404_when_disabled()
    [Fact]
    public async Task Start_redirects_to_the_provider_and_sets_the_flow_cookie()
    [Fact]
    public async Task Callback_with_mismatched_state_redirects_to_login_error_without_calling_abs()
    [Fact]
    public async Task Callback_without_the_flow_cookie_redirects_to_login_error()
    [Fact]
    public async Task Callback_exchanges_the_code_and_sets_the_session_cookie()
    [Fact]
    public async Task Callback_on_abs_400_redirects_to_login_error_and_sets_no_session()
    [Fact]
    public async Task Two_interleaved_flows_do_not_share_abs_cookies()  // guards UseCookies = false
```

Stub ABS inside the factory by re-registering the named client's primary handler — the last registration wins:

```csharp
    private static WebApplicationFactory<Program> Factory(StubHandler stub) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("OIDC_ENABLED", "true");
            b.ConfigureServices(s => s.AddHttpClient<AbsAuthClient>()
                .ConfigurePrimaryHttpMessageHandler(() => stub));
        });
```

Drive it with `AllowAutoRedirect = false` clients, as `EndpointTests` does, and carry the `inkshelf_oidc` cookie from the start response into the callback request (the `WebApplicationFactory` client tracks cookies, so a single client across both calls is the simplest form of the happy-path test).

- [ ] **Step 2: Implement, run `dotnet test`, commit** — `feat: add the oidc login endpoints`

---

### Task 5: The login page

**Files:**
- Modify: `src/Inkshelf/Pages/Login.cshtml`, `src/Inkshelf/Pages/Login.cshtml.cs`, `src/Inkshelf/locales/de.json`
- Test: `tests/Inkshelf.Tests/OidcEndpointTests.cs` (render assertions)

**Interfaces:**
- Produces: `LoginModel.SsoLabel` (`string?` — null when disabled) and `OnGet(string? error)` setting `Error = "SSO login failed. Please try again."` when `error == "sso"`.
- Consumes `AbsOptions` (already a singleton).

In `Login.cshtml`, after the form:

```html
@if (Model.SsoLabel is not null)
{
    <p><a class="button" href="/oidc/start">@Model.SsoLabel</a></p>
}
```

`SsoLabel` is `OidcButtonLabel` when set, else `L["Log in with SSO"]`. Note `locales/` holds **`de.json` only** — English keys *are* the strings — so add just the German entries: `"Log in with SSO"` and `"SSO login failed. Please try again."`.

Check `wwwroot/site.css` for an existing `.button`/link-as-button rule and reuse it; only add CSS if none exists, and if you do, **no flex `gap` and no `object-fit`** — the target e-ink engine supports neither.

- [ ] **Step 1: Write the failing tests** — button absent when disabled, present with the default label when enabled, replaced by `OIDC_BUTTON_LABEL` when set, and `/login?error=sso` renders the failure string.
- [ ] **Step 2: Implement, run `dotnet test`, commit** — `feat: offer sso on the login page`

---

### Task 6: Browser pass

**Files:**
- Modify: `tools/uicheck/run.sh`, `tools/uicheck/Program.cs`

Export `OIDC_ENABLED=true` alongside the other `export`s in `run.sh` (the seeded ABS has no provider, so the button is asserted but never clicked), and add the label to the `mustContain` list of the existing `login-en` and `login-de` checks.

- [ ] **Step 1: Run `tools/uicheck/run.sh`, read `tools/uicheck/shots/login-*.png`** — confirm the button renders and the layout still holds at both viewports. Do not trust the exit code alone.
- [ ] **Step 2: Commit** — `test: assert the sso button in the browser pass`

---

### Task 7: Docs

**Files:**
- Modify: `README.md`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`

- `README.md`: two rows in the config table (`OIDC_ENABLED`, `OIDC_BUTTON_LABEL`) plus a short **SSO / OIDC login** subsection stating the one-time ABS step — add `https://<your-inkshelf-host>/oidc/callback` to ABS → Settings → Authentication → mobile redirect URIs, exact string match — and that Inkshelf logout is local, so the provider session survives.
- `docs/ROADMAP.md`: an entry under `## Done`.
- `docs/ARCHITECTURE.md`: at most three lines, all invariants — `AllowAutoRedirect = false` / `UseCookies = false` on `AbsAuthClient` and *why*, and "ABS's OIDC web callback flow is unusable off-origin; the mobile flow is the only path". No description of the flow's steps.

- [ ] **Step 1: Write the docs, commit** — `docs: document sso login`

---

### Task 8: Verification and hand-off

- [ ] **Step 1:** `dotnet format Inkshelf.sln --verify-no-changes`
- [ ] **Step 2:** `dotnet test` — expect **288 + the new tests**, all passing. Quote the real numbers.
- [ ] **Step 3:** `tools/uicheck/run.sh` and read the shots.
- [ ] **Step 4:** Verify password login still works end to end (it is covered by the browser pass's authenticated leg — confirm that leg passed).
- [ ] **Step 5:** Hand over for the two checks this environment cannot do: the **real e-ink device pass**, and an **end-to-end SSO login against the operator's real provider** (the seeded stack has none). Tell the user the exact redirect URI their ABS needs whitelisted.

## Out of scope — do not build

- Provider-side logout (needs the `openid_id_token` ABS hands the browser, which Inkshelf never sees).
- Auto-detecting OIDC from ABS's `/status` `authMethods` — ABS still needs the callback whitelisted, so detection would render a button that 400s.
- A mock OIDC provider in `docker/docker-compose.yml`.
