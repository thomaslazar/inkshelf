# Security #1 — Secure cookies + trusted-proxy config — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the spoofable `Request.IsHttps` from being able to drop the session/favorite cookie `Secure` flag, and let operators optionally scope which proxy may set forwarded headers.

**Architecture:** Add `ForceSecureCookies` + `TrustedProxy` to the typed `AbsOptions`. `TokenStore` and `Favorites` set the cookie `Secure` flag from `ForceSecureCookies || Request.IsHttps`. `Program.cs` configures `ForwardedHeadersOptions.KnownProxies`/`KnownIPNetworks` from a parsed `TrustedProxy` list (default-deny) when set, retaining today's trust-all behavior when unset.

**Tech Stack:** ASP.NET Core, .NET 10, xUnit. `System.Net.IPNetwork`/`IPAddress` for proxy parsing.

## Global Constraints

- New config lives on `AbsOptions` (keys: `FORCE_SECURE_COOKIES`, `TRUSTED_PROXY`); binding stays inline in `Program.cs`. `dotnet test` green after every task.
- **Behavior change is intentional but must not regress legitimate users:** an HTTPS request still gets `Secure` cookies exactly as before; `ForceSecureCookies` only *adds* `Secure` on requests that report non-HTTPS (i.e. behind a TLS-terminating proxy). Default `false` = today's behavior.
- **Back-compat:** when `TRUSTED_PROXY` is unset, keep clearing `KnownProxies`/`KnownIPNetworks` (trust-all) — do not silently start dropping forwarded headers on existing deployments.
- `SmokeTests.MissingAbsUrl_FailsStartup` still relies on the `InvalidOperationException` guard — don't disturb it.
- Conventional Commits; no `Co-Authored-By`. Branch `security/hardening`.

---

## File Structure

**Task 1 (cookie secure flag):**
- Modify: `src/Inkshelf/AbsOptions.cs`, `src/Inkshelf/Program.cs`, `src/Inkshelf/Auth/TokenStore.cs`, `src/Inkshelf/Auth/Favorites.cs`
- Modify (tests): `tests/Inkshelf.Tests/TokenStoreTests.cs`, `tests/Inkshelf.Tests/AbsAuthHandlerTests.cs`

**Task 2 (trusted-proxy wiring):**
- Create: `src/Inkshelf/ForwardedProxies.cs`, `tests/Inkshelf.Tests/ForwardedProxiesTests.cs`
- Modify: `src/Inkshelf/Program.cs`

---

## Task 1: Force-secure cookies

**Files:** as above.

**Interfaces:**
- `AbsOptions` gains `bool ForceSecureCookies` and `string? TrustedProxy` (the latter is consumed in Task 2 but added now — one config class).
- `TokenStore` constructor gains a required `AbsOptions options` parameter.

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (73 tests).

- [ ] **Step 2: Add the options**

In `src/Inkshelf/AbsOptions.cs`, add two properties and update the doc comment's key list:

```csharp
namespace Inkshelf;

// Typed view of the app's configuration, bound once at startup so config reads
// live in one place instead of scattered Configuration["…"] lookups. Config keys:
// ABS_URL (required), CachePath, DataProtectionKeysPath, FORCE_SECURE_COOKIES, TRUSTED_PROXY.
public sealed class AbsOptions
{
    public string AbsUrl { get; set; } = "";
    public string? CachePath { get; set; }
    public string? DataProtectionKeysPath { get; set; }
    // Force the Secure flag on cookies even when Request.IsHttps is false (the app
    // sits behind a TLS-terminating proxy). Defaults false = derive from IsHttps.
    public bool ForceSecureCookies { get; set; }
    // Comma-separated IPs/CIDRs allowed to set forwarded headers. Null = trust all
    // (deploy behind a trusted proxy). Consumed in Program.cs forwarded-headers setup.
    public string? TrustedProxy { get; set; }
}
```

- [ ] **Step 3: Bind them in `Program.cs`**

In the `AbsOptions` initializer (`Program.cs:13-18`), add the two reads:

```csharp
var absOptions = new AbsOptions
{
    AbsUrl = builder.Configuration["ABS_URL"] ?? "",
    CachePath = builder.Configuration["CachePath"],
    DataProtectionKeysPath = builder.Configuration["DataProtectionKeysPath"],
    ForceSecureCookies = bool.TryParse(builder.Configuration["FORCE_SECURE_COOKIES"], out var fsc) && fsc,
    TrustedProxy = builder.Configuration["TRUSTED_PROXY"],
};
```

- [ ] **Step 4: Write the failing TokenStore secure-flag test**

In `tests/Inkshelf.Tests/TokenStoreTests.cs`, change `Make` to take options, then add the test. `DefaultHttpContext.Request.IsHttps` is `false`, so this isolates the force-secure path:

```csharp
    private static TokenStore Make(HttpContext ctx, AbsOptions? options = null)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        return new TokenStore(dp, accessor, options ?? new AbsOptions());
    }

    [Fact]
    public void Save_forces_secure_flag_when_configured()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false
        Make(ctx, new AbsOptions { ForceSecureCookies = true }).Save(new Tokens("acc", "ref"));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_omits_secure_flag_on_http_by_default()
    {
        var ctx = new DefaultHttpContext(); // IsHttps == false, ForceSecureCookies false
        Make(ctx).Save(new Tokens("acc", "ref"));
        var setCookie = ctx.Response.Headers.SetCookie.ToString();
        Assert.DoesNotContain("secure", setCookie, StringComparison.OrdinalIgnoreCase);
    }
```

Add `using Inkshelf;` to the test file's usings (for `AbsOptions`).

- [ ] **Step 5: Run — verify fail to compile**

Run: `dotnet test --filter FullyQualifiedName~TokenStoreTests`
Expected: FAIL to compile — `TokenStore` has no 3-arg constructor yet.

- [ ] **Step 6: Update `TokenStore`**

In `src/Inkshelf/Auth/TokenStore.cs`, add the `AbsOptions` dependency and use it for `Secure`:

```csharp
    private readonly IDataProtector _protector;
    private readonly IHttpContextAccessor _accessor;
    private readonly AbsOptions _options;

    public TokenStore(IDataProtectionProvider dp, IHttpContextAccessor accessor, AbsOptions options)
    {
        _protector = dp.CreateProtector("inkshelf.session.v1");
        _accessor = accessor;
        _options = options;
    }
```

and in `Save`, change the `Secure` line to:

```csharp
            Secure = _options.ForceSecureCookies || Ctx.Request.IsHttps,
```

(`AbsOptions` resolves via the enclosing `Inkshelf` namespace — no `using` needed.)

- [ ] **Step 7: Run TokenStore tests — GREEN**

Run: `dotnet test --filter FullyQualifiedName~TokenStoreTests`
Expected: PASS (roundtrip, absent, tampered, root-path, + the two new secure-flag tests).

- [ ] **Step 8: Fix the AbsAuthHandlerTests TokenStore constructions**

`tests/Inkshelf.Tests/AbsAuthHandlerTests.cs` builds `TokenStore` two ways in its `Make` helper — both need the new arg:
1. The DI registration: add `services.AddSingleton(new AbsOptions());` alongside the existing `services.AddTransient<TokenStore>();`.
2. The cookie-seeding line `new TokenStore(dp, new HttpContextAccessor { HttpContext = w })` → `new TokenStore(dp, new HttpContextAccessor { HttpContext = w }, new AbsOptions())`.

Add `using Inkshelf;` if `AbsOptions` doesn't already resolve in that file.

- [ ] **Step 9: Update `Favorites.Set`**

`Favorites` is static; resolve `AbsOptions` from the request's services for the same flag. In `src/Inkshelf/Auth/Favorites.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Auth;

public static class Favorites
{
    public const string Cookie = "inkshelf_fav_library";

    public static string? Read(HttpRequest req) =>
        req.Cookies.TryGetValue(Cookie, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public static void Set(HttpResponse res, string id)
    {
        var forceSecure = res.HttpContext.RequestServices.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
        res.Cookies.Append(Cookie, id, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = forceSecure || res.HttpContext.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });
    }

    public static void Clear(HttpResponse res) =>
        res.Cookies.Delete(Cookie, new CookieOptions { Path = "/" });
}
```

(`AbsOptions` resolves via the enclosing `Inkshelf` namespace.)

- [ ] **Step 10: Full suite**

Run: `dotnet test`
Expected: PASS (73 + 2 new = 75). The `EndpointTests` favorite/logout tests still pass (default `AbsOptions` → `ForceSecureCookies` false → no behavior change under the test's non-HTTPS `WebApplicationFactory`).

- [ ] **Step 11: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs src/Inkshelf/Auth/TokenStore.cs src/Inkshelf/Auth/Favorites.cs tests/Inkshelf.Tests/TokenStoreTests.cs tests/Inkshelf.Tests/AbsAuthHandlerTests.cs
git commit -m "feat: force-secure cookies via FORCE_SECURE_COOKIES option"
```

---

## Task 2: Trusted-proxy forwarded-headers config

**Files:** as above.

**Interfaces:**
- `ForwardedProxies.Parse(string? trustedProxy) : (List<IPAddress> Proxies, List<IPNetwork> Networks)` in namespace `Inkshelf`.

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (75).

- [ ] **Step 2: Write the failing parse tests**

Create `tests/Inkshelf.Tests/ForwardedProxiesTests.cs`:

```csharp
using System.Net;
using Inkshelf;

namespace Inkshelf.Tests;

public class ForwardedProxiesTests
{
    [Fact]
    public void Parse_null_or_empty_returns_empty()
    {
        var (p, n) = ForwardedProxies.Parse(null);
        Assert.Empty(p); Assert.Empty(n);
        (p, n) = ForwardedProxies.Parse("   ");
        Assert.Empty(p); Assert.Empty(n);
    }

    [Fact]
    public void Parse_splits_ips_and_cidrs()
    {
        var (proxies, networks) = ForwardedProxies.Parse("1.2.3.4, 10.0.0.0/8 , ::1");
        Assert.Contains(IPAddress.Parse("1.2.3.4"), proxies);
        Assert.Contains(IPAddress.Parse("::1"), proxies);
        Assert.Single(networks);
        Assert.Equal(IPNetwork.Parse("10.0.0.0/8"), networks[0]);
    }

    [Fact]
    public void Parse_skips_invalid_entries()
    {
        var (proxies, networks) = ForwardedProxies.Parse("not-an-ip, 1.2.3.4, bad/cidr");
        Assert.Equal(new[] { IPAddress.Parse("1.2.3.4") }, proxies);
        Assert.Empty(networks);
    }
}
```

- [ ] **Step 3: Run — verify fail to compile**

Run: `dotnet test --filter FullyQualifiedName~ForwardedProxiesTests`
Expected: FAIL to compile — `ForwardedProxies` does not exist.

- [ ] **Step 4: Create `ForwardedProxies.cs`**

```csharp
using System.Net;

namespace Inkshelf;

// Parses a comma-separated TRUSTED_PROXY value into bare IPs (→ KnownProxies) and
// CIDR ranges (→ KnownIPNetworks) for ForwardedHeadersOptions. Invalid entries are
// skipped. Empty/null input yields empty lists (caller then trusts all hops).
public static class ForwardedProxies
{
    public static (List<IPAddress> Proxies, List<IPNetwork> Networks) Parse(string? trustedProxy)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        if (string.IsNullOrWhiteSpace(trustedProxy)) return (proxies, networks);
        foreach (var raw in trustedProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Contains('/'))
            {
                if (IPNetwork.TryParse(raw, out var net)) networks.Add(net);
            }
            else if (IPAddress.TryParse(raw, out var ip))
            {
                proxies.Add(ip);
            }
        }
        return (proxies, networks);
    }
}
```

- [ ] **Step 5: Run parse tests — GREEN**

Run: `dotnet test --filter FullyQualifiedName~ForwardedProxiesTests`
Expected: PASS (3).

- [ ] **Step 6: Wire it into `Program.cs`**

Replace the forwarded-headers block (`Program.cs:59-64`) with:

```csharp
var fho = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
fho.KnownIPNetworks.Clear();
fho.KnownProxies.Clear();
var (trustedProxies, trustedNetworks) = ForwardedProxies.Parse(absOptions.TrustedProxy);
// With TRUSTED_PROXY set, only those proxies/networks may set forwarded headers
// (default-deny). With it unset, both lists stay empty → forwarded headers are
// trusted from any hop (deploy behind a trusted proxy; FORCE_SECURE_COOKIES
// protects the cookie Secure flag independently).
foreach (var p in trustedProxies) fho.KnownProxies.Add(p);
foreach (var net in trustedNetworks) fho.KnownIPNetworks.Add(net);
app.UseForwardedHeaders(fho);
```

- [ ] **Step 7: Full suite**

Run: `dotnet test`
Expected: PASS (75 + 3 = 78). Existing endpoint/smoke tests still pass — with no `TRUSTED_PROXY` set the lists stay empty (trust-all), unchanged from before.

- [ ] **Step 8: Commit**

```bash
git add src/Inkshelf/ForwardedProxies.cs tests/Inkshelf.Tests/ForwardedProxiesTests.cs src/Inkshelf/Program.cs
git commit -m "feat: optional TRUSTED_PROXY scoping for forwarded headers"
```

---

## Self-Review

**Spec coverage (#1):**
- `ForceSecureCookies` config + cookie flag on `TokenStore` and `Favorites` → Task 1. ✓
- Optional `TrustedProxy` default-deny wiring, trust-all retained when unset → Task 2. ✓
- Testable parse helper → `ForwardedProxies.Parse` + `ForwardedProxiesTests`. ✓
- Only `IsHttps` consumer relevant to security (cookie `Secure`) is addressed; relative redirects untouched. ✓

**Placeholder scan:** None. Every test asserts a concrete cookie flag or parsed value.

**Type consistency:** `AbsOptions.ForceSecureCookies`/`TrustedProxy` used identically in `Program.cs` binding, `TokenStore`, `Favorites`, and Task 2's wiring. `TokenStore`'s 3-arg constructor is matched at all construction sites (DI auto-resolves; `TokenStoreTests.Make` and `AbsAuthHandlerTests` updated). `ForwardedProxies.Parse`'s tuple return matches the test and the `Program.cs` consumer.

**Scope:** Two tasks — the force-secure cookie fix (primary), then the optional proxy scoping. Both behavior-safe by default (`false`/unset = today's behavior). Findings #2–#5 are separate just-in-time plans.
