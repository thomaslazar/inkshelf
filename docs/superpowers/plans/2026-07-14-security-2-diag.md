# Security #2 — /diag hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop the unauthenticated `/diag` endpoint from being a log-injection / log-flood vector, and give operators a switch to disable it.

**Architecture:** Bound the body read to 4 KB, sanitize control chars/newlines out of the logged string (via a pure, testable helper), and map the endpoint only when `AbsOptions.DiagEnabled` (config `DIAG_ENABLED`) is true.

**Tech Stack:** ASP.NET Core minimal APIs, .NET 10, xUnit. `InternalsVisibleTo Inkshelf.Tests` is set (so an `internal static` helper is test-visible).

## Global Constraints

- New config on `AbsOptions` (key `DIAG_ENABLED`, **default true** = current behavior). `dotnet test` green after the task.
- `/diag` stays functionally the same when enabled (logs the probe) — it just caps + sanitizes the input and can be turned off.
- Conventional Commits; no `Co-Authored-By`. Branch `security/hardening`.

## File Structure

- Modify: `src/Inkshelf/AbsOptions.cs`, `src/Inkshelf/Program.cs`, `src/Inkshelf/Endpoints/DiagEndpoints.cs`
- Create: `tests/Inkshelf.Tests/DiagEndpointsTests.cs`

---

## Task 1: Cap, sanitize, and gate /diag

**Files:** as above.

**Interfaces:**
- `AbsOptions` gains `bool DiagEnabled` (default `true`).
- `DiagEndpoints.SanitizeProbe(string raw) : string` — `internal static`, pure.

- [ ] **Step 1: Green baseline**

Run: `dotnet test`
Expected: PASS (78 tests).

- [ ] **Step 2: Add the option**

In `src/Inkshelf/AbsOptions.cs`, add (and extend the doc-comment key list with `DIAG_ENABLED`):

```csharp
    // Whether the unauthenticated /diag probe endpoint is mapped. Default true.
    public bool DiagEnabled { get; set; } = true;
```

- [ ] **Step 3: Bind it in `Program.cs`**

In the `AbsOptions` initializer, add (default true unless explicitly `"false"`):

```csharp
    DiagEnabled = !string.Equals(builder.Configuration["DIAG_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
```

- [ ] **Step 4: Write the failing tests**

Create `tests/Inkshelf.Tests/DiagEndpointsTests.cs`. Note: `SanitizeProbe` neutralizes only *control* characters — spaces are printable and are intentionally preserved, so do NOT assert on spaces. The `\r`, `\n`, `\t` below are C# escape sequences in the test source (real control chars at runtime), which is exactly what the helper must strip.

```csharp
using Microsoft.AspNetCore.Mvc.Testing;
using Inkshelf.Endpoints;

namespace Inkshelf.Tests;

public class DiagEndpointsTests
{
    [Fact]
    public void SanitizeProbe_replaces_control_chars_but_keeps_printable()
    {
        var cleaned = DiagEndpoints.SanitizeProbe("line1\r\nFAKE LOG\tend");
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\t', cleaned);
        Assert.Contains("line1", cleaned);
        Assert.Contains("FAKE LOG", cleaned); // printable content (incl. spaces) kept
    }

    [Fact]
    public void SanitizeProbe_truncates_to_cap()
    {
        var cleaned = DiagEndpoints.SanitizeProbe(new string('a', 10_000));
        Assert.True(cleaned.Length <= 4096, $"expected <=4096, got {cleaned.Length}");
    }

    [Fact]
    public async Task Diag_returns_404_when_disabled()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => { b.UseSetting("ABS_URL", "http://localhost:1"); b.UseSetting("DIAG_ENABLED", "false"); });
        using var client = factory.CreateClient();
        var res = await client.PostAsync("/diag", new StringContent("probe"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Diag_accepts_probe_when_enabled()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ABS_URL", "http://localhost:1"));
        using var client = factory.CreateClient();
        var res = await client.PostAsync("/diag", new StringContent("probe"));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
    }
}
```

- [ ] **Step 5: Run — verify fail to compile**

Run: `dotnet test --filter FullyQualifiedName~DiagEndpointsTests`
Expected: FAIL to compile — `DiagEndpoints.SanitizeProbe` does not exist.

- [ ] **Step 6: Harden `DiagEndpoints.cs`**

Replace the file with the bounded-read + sanitize version and the pure helper:

```csharp
using System.Text;

namespace Inkshelf.Endpoints;

public static class DiagEndpoints
{
    private const int MaxBytes = 4096;

    public static void MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        // Receives the /diag.html browser capability probe and logs it, so device
        // limitations can be collected without a screenshot. No auth (pre-login tool)
        // — so the body is bounded and sanitized before logging, and the whole
        // endpoint is only mapped when enabled (see Program.cs / DIAG_ENABLED).
        app.MapPost("/diag", async (HttpContext ctx, ILogger<DiagLog> logger, CancellationToken ct) =>
        {
            // Read at most MaxBytes; never drain an unbounded body into memory/logs.
            var buffer = new byte[MaxBytes];
            var total = 0;
            int read;
            while (total < MaxBytes &&
                   (read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct)) > 0)
                total += read;

            logger.LogInformation("Browser probe: {Probe}", SanitizeProbe(Encoding.UTF8.GetString(buffer, 0, total)));
            return Results.Ok();
        });
    }

    // Neutralize control characters (incl. CR/LF, so a probe body can't forge log
    // lines) and cap the length. Pure — unit-tested directly.
    internal static string SanitizeProbe(string raw)
    {
        if (raw.Length > MaxBytes) raw = raw[..MaxBytes];
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsControl(c) ? '.' : c);
        return sb.ToString();
    }

    // Log-category marker for the /diag endpoint.
    public sealed class DiagLog { }
}
```

- [ ] **Step 7: Gate the mapping in `Program.cs`**

Change `app.MapDiagEndpoints();` to:

```csharp
if (absOptions.DiagEnabled) app.MapDiagEndpoints();
```

- [ ] **Step 8: Full suite**

Run: `dotnet test`
Expected: PASS (78 + 4 new = 82).

- [ ] **Step 9: Commit**

```bash
git add src/Inkshelf/AbsOptions.cs src/Inkshelf/Program.cs src/Inkshelf/Endpoints/DiagEndpoints.cs tests/Inkshelf.Tests/DiagEndpointsTests.cs
git commit -m "feat: bound and sanitize /diag body, add DIAG_ENABLED kill-switch"
```

---

## Self-Review

**Spec coverage (#2):** body capped at 4 KB (bounded read, not `ReadToEndAsync`); control chars + newlines sanitized before logging (pure `SanitizeProbe`); `DIAG_ENABLED` kill-switch gates the mapping.

**Placeholder scan:** None — tests assert concrete sanitization, truncation bound, and 404/200 wiring. The sanitize test intentionally does not assert on spaces (they are printable and preserved).

**Type consistency:** `AbsOptions.DiagEnabled` used in `Program.cs` binding + gate; `SanitizeProbe` signature matches its tests and the endpoint call site; `MaxBytes` bounds both the read and the truncation.

**Note:** `char.IsControl` covers CR, LF, TAB, NUL and other C0/C1 controls — exactly the log-forging surface — while leaving printable content (including spaces) intact so legitimate probes still read clearly.

**Scope:** One task. Findings #3/#5/#4/docs are separate just-in-time plans.
