# Mark Read Without A Reload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Marking a book read no longer reloads the page, and with JavaScript off the reload lands back on the row that was tapped.

**Architecture:** Progressive enhancement over the form that already works. The existing `POST /read/{id}` form is untouched in behaviour; it gains a row anchor in its `return` field for the no-JavaScript path. A small ES5 script in `_Layout.cshtml` intercepts the submit, posts the same fields by `XMLHttpRequest` with `xhr=1` appended, and updates the button in place. The endpoint answers `204 No Content` to an `xhr=1` request and keeps redirecting otherwise.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit. Client side is hand-written ES5 with `XMLHttpRequest`, no libraries.

## Global Constraints

- **No em dashes (U+2014) and no en dashes (U+2013) anywhere**: not in code, comments, docs, or commit messages. Use a plain hyphen, a comma, or two sentences. The existing files legitimately contain U+00D7, U+2192, U+00F7 and the ellipsis U+2026; only U+2013 and U+2014 are banned.
- **Client JavaScript must be ES5 and must degrade.** The target browsers are Chrome 30 era and older. No `fetch`, no `Promise`, no arrow functions, no `const`/`let`, no template literals. Use `XMLHttpRequest`. Wrap everything in `try/catch` so an engine that cannot run it leaves the plain form working.
- **No new dependencies, no libraries, no build step for the script.** It is inline in the Razor layout, as the existing convert script is.
- **Localized strings reach the script as JSON**, via the existing `I18N` object at `src/Inkshelf/Pages/Shared/_Layout.cshtml:44`. Never interpolate a localizer lookup into quoted JavaScript: Razor HTML-encodes localizer output, so an ellipsis would arrive as `&#x2026;` and render literally when assigned via `nodeValue`.
- **UI copy names what the user sees**, not the mechanism.
- Conventional Commits: `type: subject`, imperative, lowercase, no period, max ~72 chars. No `Co-Authored-By` and no "Generated with Claude Code" lines.
- Per-task commits on branch `feat/read-without-reload` are pre-authorized by the user for this plan.
- `dotnet test` from the repo root must be green before each commit. `dotnet format --verify-no-changes` must be clean.
- Do NOT start a dev server during implementation; it holds a file lock on `src/Inkshelf/bin` and breaks `dotnet test`.
- Do NOT touch `CHANGELOG.md`; it belongs to the release process.
- Spec: `docs/superpowers/specs/2026-09-08-read-without-reload-design.md`.

## File Structure

- `src/Inkshelf/Pages/Shared/_ReadButton.cshtml` (new): the read form, the single copy. Takes a small model so both call sites can pass their own item id, read state and return URL.
- `src/Inkshelf/Pages/Support/ReadButtonModel.cs` (new): that model.
- `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`: renders the partial instead of its own copy; gains the row `id`.
- `src/Inkshelf/Pages/Item.cshtml`: renders the partial instead of its own copy.
- `src/Inkshelf/Endpoints/ReadEndpoints.cs`: `204` for `xhr=1`.
- `src/Inkshelf/Pages/Shared/_Layout.cshtml`: the interception script and two new `I18N` entries.
- `src/Inkshelf/locales/de.json`: the German string for the working label.

---

### Task 1: Extract the read button into one partial

The read form exists twice, near-verbatim: `_ItemRow.cshtml:60-72` and `Item.cshtml:77-89`. Later tasks add a row anchor and a working label to it, and doing that to two copies is how they drift. This task is a pure refactor: the rendered HTML must not change.

**Files:**
- Create: `src/Inkshelf/Pages/Support/ReadButtonModel.cs`
- Create: `src/Inkshelf/Pages/Shared/_ReadButton.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml:60-72`
- Modify: `src/Inkshelf/Pages/Item.cshtml:77-89`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`, `tests/Inkshelf.Tests/ItemRenderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Inkshelf.Pages.ReadButtonModel(string ItemId, bool Read, string ReturnUrl)`, rendered by the partial `_ReadButton`.

- [ ] **Step 1: Write the failing tests**

Both render tests already assert `action="/read/{ItemId}"` is present (`ListingRenderTests.cs:377` and `ItemRenderTests.cs:105`). Those keep the refactor honest but do not pin the fields. Add one test to each file asserting the full set of fields the no-JavaScript path needs, so a botched extraction fails loudly.

In `tests/Inkshelf.Tests/ListingRenderTests.cs`, following the shape of the neighbouring tests (they use `CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path)` and `LibraryRequest(factory)`):

```csharp
    [Fact]
    public async Task The_listing_read_form_carries_everything_the_no_js_path_needs()
    {
        // With JS off this form IS the feature: method, action, the antiforgery
        // token and the absolute desired state all have to be in the markup.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        var form = Regex.Match(html, "<form class=\"read-form\"[\\s\\S]*?</form>");
        Assert.True(form.Success, "Expected a read form in the rendered listing.");
        Assert.Contains("method=\"post\"", form.Value);
        Assert.Contains($"action=\"/read/{ItemId}\"", form.Value);
        Assert.Contains("__RequestVerificationToken", form.Value);
        Assert.Contains("name=\"read\" value=\"1\"", form.Value);
        Assert.Contains("name=\"return\"", form.Value);
    }
```

In `tests/Inkshelf.Tests/ItemRenderTests.cs`, using that file's existing harness (`CreateFactory(MakeStub(), …)` plus its `Request(factory, url)` helper, exactly as `Breadcrumb_shows_the_actual_library_between_libraries_and_title` at line 66 does). The assertions are the same except the return value, which on the detail page is the item's own URL:

```csharp
    [Fact]
    public async Task The_item_read_form_carries_everything_the_no_js_path_needs()
    {
        // Same contract as the listing's form. The two used to be separate copies
        // of this markup, so pin both.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(Request(factory, $"/item/{ItemId}"))).Content.ReadAsStringAsync();

        var form = System.Text.RegularExpressions.Regex.Match(html, "<form class=\"read-form\"[\\s\\S]*?</form>");
        Assert.True(form.Success, "Expected a read form on the rendered item page.");
        Assert.Contains("method=\"post\"", form.Value);
        Assert.Contains($"action=\"/read/{ItemId}\"", form.Value);
        Assert.Contains("__RequestVerificationToken", form.Value);
        Assert.Contains("name=\"read\" value=\"1\"", form.Value);
        Assert.Contains($"name=\"return\" value=\"/item/{ItemId}\"", form.Value);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~read_form_carries_everything"`
Expected: FAIL. Both are new, and they fail on the assertions rather than compiling, since the markup they describe partly exists already. If either PASSES at this point, that is fine and expected: the fields are present today. The point of writing them first is that they must still pass after the extraction. Note in your report which of the two passed before the change.

- [ ] **Step 3: Create the model**

`src/Inkshelf/Pages/Support/ReadButtonModel.cs`:

```csharp
namespace Inkshelf.Pages;

// The read/unread toggle for one item. Used by BOTH the listing row and the item
// detail page, which carried near-identical copies of this form until they
// drifted apart once too often.
//
// ReturnUrl is where the no-JavaScript POST comes back to. The partial appends
// the row anchor to it; do NOT pre-append it here, and do not reuse
// ItemRowModel.ReturnUrl for the anchored value, because that same string feeds
// the convert links.
public record ReadButtonModel(string ItemId, bool Read, string ReturnUrl);
```

- [ ] **Step 4: Create the partial**

`src/Inkshelf/Pages/Shared/_ReadButton.cshtml`, which is the current markup verbatim with the model's names substituted:

```razor
@model Inkshelf.Pages.ReadButtonModel
<form class="read-form" method="post" action="/read/@Model.ItemId">
    @Html.AntiForgeryToken()
    @* The ABSOLUTE desired state, not a toggle. That is what makes a retry after
       a failed request safe: tapping again re-sends the same intent instead of
       flipping the book back. *@
    <input type="hidden" name="read" value="@(Model.Read ? "0" : "1")" />
    <input type="hidden" name="return" value="@Model.ReturnUrl" />
    @if (Model.Read)
    {
        <button type="submit" class="btn read-btn" title="@L["Mark as unread"]">&#10003; @L["Read"]</button>
    }
    else
    {
        <button type="submit" class="btn read-btn" title="@L["Mark as read"]">@L["Mark read"]</button>
    }
</form>
```

If `L` is not in scope in a partial in this project, check how the other shared partials get it (`_ConvertAction.cshtml` uses it) and follow that.

- [ ] **Step 5: Use it from both call sites**

In `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, replace the whole `<form class="read-form">…</form>` block with:

```razor
                <partial name="_ReadButton" model="new Inkshelf.Pages.ReadButtonModel(item.Id, Model.Read, Model.ReturnUrl)" />
```

In `src/Inkshelf/Pages/Item.cshtml`, replace the whole `<form class="read-form">…</form>` block with:

```razor
<partial name="_ReadButton" model="new Inkshelf.Pages.ReadButtonModel(Model.Id, Model.Read, $"/item/{Model.Id}")" />
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green. The rendered HTML should be unchanged by this task, so every pre-existing render assertion must still hold. If one broke, the extraction changed the markup; fix the partial rather than the test.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Pages/Support/ReadButtonModel.cs src/Inkshelf/Pages/Shared/_ReadButton.cshtml src/Inkshelf/Pages/Shared/_ItemRow.cshtml src/Inkshelf/Pages/Item.cshtml tests/Inkshelf.Tests/ListingRenderTests.cs tests/Inkshelf.Tests/ItemRenderTests.cs
git commit -m "refactor: extract the read button into one partial"
```

---

### Task 2: Answer 204 to an xhr request

**Files:**
- Modify: `src/Inkshelf/Endpoints/ReadEndpoints.cs`
- Test: `tests/Inkshelf.Tests/ReadEndpointTests.cs` (new)

**Interfaces:**
- Consumes: nothing.
- Produces: `POST /read/{id}?xhr=1` responds `204 No Content` on success. Without `xhr=1` it still responds `302` to the sanitized `return`.

- [ ] **Step 1: Write the failing tests**

The existing read tests in `EndpointTests.cs` only cover the antiforgery rejection and the no-session redirect, so neither reaches a successful ABS call. This needs a stubbed ABS and a session cookie. `ListingRenderTests.cs` has that harness; copy its shape into a new focused file rather than growing `EndpointTests.cs`.

Create `tests/Inkshelf.Tests/ReadEndpointTests.cs`:

```csharp
using System.Net;
using Inkshelf.Abs;
using Inkshelf.Convert;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Inkshelf.Tests;

// The read toggle's transport contract. A tap with JavaScript posts xhr=1 and
// wants no redirect body to throw away; a tap without it wants the redirect that
// has always been there. Both paths call the same ABS PATCH.
public class ReadEndpointTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "read-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    private const string ItemId = "item1";

    // Records the PATCH so a test can prove the ABS call really happened, which is
    // what separates "204 because it worked" from "204 because nothing ran".
    private sealed class Recorder
    {
        public int Patches;
        public string? LastBody;
    }

    private static StubHandler MakeStub(Recorder rec) => new(req =>
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path == $"/api/me/progress/{ItemId}" && req.Method == HttpMethod.Patch)
        {
            rec.Patches++;
            rec.LastBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    });

    private static WebApplicationFactory<Program> CreateFactory(StubHandler stub, string cachePath, string keysPath) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("CachePath", cachePath);
            b.UseSetting("DataProtectionKeysPath", keysPath);
            b.ConfigureTestServices(services =>
            {
                services.Configure<HttpClientFactoryOptions>(nameof(AbsApiClient), o =>
                    o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub));
                var worker = services.FirstOrDefault(s => s.ImplementationType == typeof(ConvertWorker));
                if (worker is not null) services.Remove(worker);
            });
        });

    private static string SessionCookie(WebApplicationFactory<Program> factory)
    {
        var dp = factory.Services.GetRequiredService<IDataProtectionProvider>();
        var protector = dp.CreateProtector("inkshelf.session.v1");
        return $"inkshelf_session={Uri.EscapeDataString(protector.Protect("access\nrefresh"))}";
    }

    // The endpoint validates antiforgery, so a real token plus its cookie is
    // needed. GET /login issues both.
    private static async Task<HttpResponseMessage> PostReadAsync(
        WebApplicationFactory<Program> factory, HttpClient client, string url, string read)
    {
        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = read,
                ["return"] = "/converted",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        return await client.SendAsync(req);
    }

    [Fact]
    public async Task Xhr_read_returns_204_and_still_patches_abs()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await PostReadAsync(factory, client, $"/read/{ItemId}?xhr=1", "1");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(1, rec.Patches);
        Assert.Contains("\"isFinished\":true", rec.LastBody);
    }

    [Fact]
    public async Task Non_xhr_read_still_redirects_to_the_return_url()
    {
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await PostReadAsync(factory, client, $"/read/{ItemId}", "1");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/converted", res.Headers.Location?.OriginalString);
        Assert.Equal(1, rec.Patches);
    }

    [Fact]
    public async Task Unmarking_sends_isFinished_false()
    {
        // The form posts the ABSOLUTE desired state, so read=0 must reach ABS as
        // false rather than toggling whatever is stored.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var res = await PostReadAsync(factory, client, $"/read/{ItemId}?xhr=1", "0");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Contains("\"isFinished\":false", rec.LastBody);
    }

    [Fact]
    public async Task An_offsite_return_is_still_rejected_by_the_guard()
    {
        // The open-redirect guard predates this change and must survive it.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
                ["return"] = "//evil.example/x",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_return_with_a_row_anchor_survives_the_guard()
    {
        // The no-JavaScript path depends on this: the fragment has to reach the
        // Location header, because a 302 whose Location has none does not inherit
        // one from the POST target.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        var rec = new Recorder();
        using var factory = CreateFactory(MakeStub(rec), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var token = await GetAntiforgeryTokenAsync(client);
        var req = new HttpRequestMessage(HttpMethod.Post, $"/read/{ItemId}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["read"] = "1",
                ["return"] = $"/converted#item-{ItemId}",
            }),
        };
        req.Headers.Add("Cookie", SessionCookie(factory));
        var res = await client.SendAsync(req);

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal($"/converted#item-{ItemId}", res.Headers.Location?.OriginalString);
    }
}
```

`GetAntiforgeryTokenAsync` is `private static` in `EndpointTests.cs:22`, so it is not reachable from a new class. Copy it into this file as a private helper rather than widening its visibility; it is six lines and it GETs `/login`, scrapes the token out of the returned HTML with a regex, and relies on the client keeping the matching cookie:

```csharp
    private static async Task<string> GetAntiforgeryTokenAsync(HttpClient client)
    {
        var html = await (await client.GetAsync("/login")).Content.ReadAsStringAsync();
        var match = System.Text.RegularExpressions.Regex.Match(
            html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, "Expected an antiforgery token in /login response.");
        return match.Groups[1].Value;
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ReadEndpointTests`
Expected: FAIL. `Xhr_read_returns_204_and_still_patches_abs` and `Unmarking_sends_isFinished_false` report `Redirect` where `NoContent` was expected, because the endpoint does not know about `xhr` yet. The other three should pass immediately: they describe behaviour that already exists and are here as regression cover.

- [ ] **Step 3: Implement**

In `src/Inkshelf/Endpoints/ReadEndpoints.cs`, add the `xhr` query parameter and branch the result. The whole handler becomes:

```csharp
        app.MapPost("/read/{id}", async (string id, HttpContext ctx, IAntiforgery antiforgery,
            AbsApiClient api, [FromForm] string? read, [FromForm(Name = "return")] string? @return,
            string? xhr, CancellationToken ct) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            await api.SetReadAsync(id, read == "1", ct);
            // The script updates the button itself and has no use for a listing it
            // would only throw away, so answer with nothing. Signalled by a query
            // parameter rather than a header to match how convert already says what
            // it wants (?warm=1, ?status=1).
            return xhr == "1" ? Results.NoContent() : Results.Redirect(LocalReturn(@return));
        }).DisableAntiforgery();
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 5: Commit**

```bash
git add src/Inkshelf/Endpoints/ReadEndpoints.cs tests/Inkshelf.Tests/ReadEndpointTests.cs
git commit -m "feat: answer 204 to an xhr read toggle"
```

---

### Task 3: Anchor the no-JavaScript return

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`
- Modify: `src/Inkshelf/Pages/Shared/_ReadButton.cshtml`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: `ReadButtonModel(string ItemId, bool Read, string ReturnUrl)` from Task 1.
- Produces: listing rows carry `id="item-<id>"`, and the read form's `return` is `<ReturnUrl>#item-<id>`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`:

```csharp
    [Fact]
    public async Task The_listing_row_is_anchorable_and_the_read_form_returns_to_it()
    {
        // With JS off, marking read reloads. The fragment is what puts the reader
        // back on the row they tapped instead of the top of the listing.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        Assert.Contains($"id=\"item-{ItemId}\"", html);
        var form = Regex.Match(html, "<form class=\"read-form\"[\\s\\S]*?</form>");
        Assert.True(form.Success, "Expected a read form in the rendered listing.");
        Assert.Contains($"name=\"return\" value=\"/library/{LibId}#item-{ItemId}\"", form.Value);
    }

    [Fact]
    public async Task The_convert_href_does_not_carry_the_row_anchor()
    {
        // The anchor belongs to the read form alone. ItemRowModel.ReturnUrl feeds
        // the convert links too, so appending it there would put a fragment on
        // every convert href.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        Assert.DoesNotContain("item-", PrimaryConvertAnchor(html));
    }
```

`PrimaryConvertAnchor` is an existing helper in that file (`ListingRenderTests.cs:113`) which isolates the row's convert anchor, so a whole-page assertion cannot false-fail on the layout script.

If the expected return value in the first test does not match what the page actually renders (the listing URL depends on how `ItemRowModel.ReturnUrl` is built for this harness), print the rendered form once, use the real value, and say so in your report. Do not weaken the assertion to a `Contains("#item-")` that would pass with the fragment on the wrong URL.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~anchorable|FullyQualifiedName~does_not_carry_the_row_anchor"`
Expected: `The_listing_row_is_anchorable_and_the_read_form_returns_to_it` FAILS on the missing `id="item-item1"`. `The_convert_href_does_not_carry_the_row_anchor` PASSES already and is regression cover for the mistake this task must not make.

- [ ] **Step 3: Add the row anchor**

In `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, the row's opening tag is currently `<div class="item">`. Give it an id:

```razor
<div class="item" id="item-@item.Id">
```

- [ ] **Step 4: Append the anchor in the read form only**

In `src/Inkshelf/Pages/Shared/_ReadButton.cshtml`, change the return field to append the anchor, and record why it is done here:

```razor
    @* Anchored HERE, not in the caller's ReturnUrl: that same string feeds the
       convert links, which must not start carrying a fragment. The fragment has
       to be in the Location header the POST redirects to, because a 302 whose
       Location has no fragment does not inherit one from the POST target. *@
    <input type="hidden" name="return" value="@(Model.ReturnUrl)#item-@Model.ItemId" />
```

The item detail page renders this too, where the anchor is meaningless (one item, top of page) and simply unused. That is fine and needs no branch.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green. Note that Task 1's `The_item_read_form_carries_everything_the_no_js_path_needs` asserted `name="return" value="/item/{ItemId}"`, which this task changes to carry a fragment. Update that assertion to the new value; it is the same contract, one field wider.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_ItemRow.cshtml src/Inkshelf/Pages/Shared/_ReadButton.cshtml tests/Inkshelf.Tests/ListingRenderTests.cs tests/Inkshelf.Tests/ItemRenderTests.cs
git commit -m "feat: return to the tapped row when marking read"
```

---

### Task 4: Intercept the submit

**Files:**
- Modify: `src/Inkshelf/Pages/Shared/_Layout.cshtml:44` (the `I18N` object) and the script block below it
- Modify: `src/Inkshelf/locales/de.json`
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: `POST /read/{id}?xhr=1` returning 204 from Task 2; `form.read-form` and `button.read-btn` from Tasks 1 and 3.
- Produces: nothing later tasks depend on.

- [ ] **Step 1: Write the failing test**

The script cannot be unit-tested in this project. What CAN be pinned is that the layout ships the strings it needs, correctly encoded, which is the trap the existing comment at `_Layout.cshtml:41-43` warns about. Add to `tests/Inkshelf.Tests/ListingRenderTests.cs`:

```csharp
    [Fact]
    public async Task The_layout_ships_the_read_labels_as_json_not_entities()
    {
        // Razor HTML-encodes localizer output, so a label assigned via nodeValue
        // would show "&#x2026;" literally. The I18N object exists to dodge that;
        // these two strings have to travel through it like the convert ones.
        using var cacheDir = new TempDir();
        using var keysDir = new TempDir();
        using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

        var i18n = Regex.Match(html, "var I18N = \\{.*\\};");
        Assert.True(i18n.Success, "Expected the I18N object in the layout.");
        Assert.Contains("\"marking\":", i18n.Value);
        Assert.Contains("\"read\":", i18n.Value);
        Assert.Contains("\"markRead\":", i18n.Value);
        Assert.DoesNotContain("&#x", i18n.Value);
        Assert.DoesNotContain("&amp;", i18n.Value);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter FullyQualifiedName~ships_the_read_labels`
Expected: FAIL, the `I18N` object has no `marking` key.

- [ ] **Step 3: Add the strings**

In `src/Inkshelf/Pages/Shared/_Layout.cshtml`, extend the `I18N` object on line 44:

```razor
    var I18N = @Html.Raw(System.Text.Json.JsonSerializer.Serialize(new { convert = L["Convert"], converting = L["Converting…"], retry = L["Convert (retry)"], marking = L["Marking…"], read = L["Read"], markRead = L["Mark read"] }));
```

In `src/Inkshelf/locales/de.json`, add the one new German entry beside the existing `"Converting…"` one at line 25, matching the file's formatting:

```json
  "Marking…": "Markiere…",
```

`Read` and `Mark read` are existing strings and already have German entries; do not duplicate them.

- [ ] **Step 4: Add the script**

In `src/Inkshelf/Pages/Shared/_Layout.cshtml`, after the closing `})();` of the convert script and before `</script>`, add:

```javascript
    /* Mark read without a reload. The form below stays a real form: with JS off it
       posts and comes back to the row via the #item- anchor in its return field.
       With JS we post the same fields ourselves and repaint the button.
       ES5 + try/catch for old e-readers, same as the poller above. */
    (function () {
        try {
            var forms = document.querySelectorAll('form.read-form');
            for (var i = 0; i < forms.length; i++) {
                (function (f) {
                    f.addEventListener('submit', function (e) {
                        var btn = f.querySelector('button.read-btn');
                        var hidden = f.querySelector('input[name="read"]');
                        if (!btn || !hidden) { return; } // let the form post normally
                        e.preventDefault();
                        // Remember the label so a failure can put it back. The check
                        // mark is part of the read label, so rebuild it rather than
                        // trying to preserve a glyph node.
                        var wasRead = hidden.value === '0';
                        btn.firstChild.nodeValue = I18N.marking;
                        var xhr = new XMLHttpRequest();
                        var url = f.getAttribute('action') + '?xhr=1';
                        xhr.open('POST', url);
                        xhr.setRequestHeader('Content-Type', 'application/x-www-form-urlencoded');
                        xhr.onreadystatechange = function () {
                            if (xhr.readyState !== 4) { return; }
                            if (xhr.status === 204 || xhr.status === 200) {
                                // Flip to the state we just asked for, and flip the
                                // hidden field so the next tap asks for the opposite.
                                var nowRead = !wasRead;
                                btn.firstChild.nodeValue = nowRead ? '✓ ' + I18N.read : I18N.markRead;
                                hidden.value = nowRead ? '0' : '1';
                                btn.setAttribute('title', nowRead ? btn.getAttribute('data-untitle') || '' : btn.getAttribute('data-title') || '');
                            } else {
                                // No error UI on purpose: a button that did not change
                                // IS the signal, and the form posts an absolute state
                                // so tapping again is safe.
                                btn.firstChild.nodeValue = wasRead ? '✓ ' + I18N.read : I18N.markRead;
                            }
                        };
                        xhr.send(serialize(f));
                    });
                })(forms[i]);
            }
            function serialize(f) {
                var parts = [];
                var els = f.elements;
                for (var j = 0; j < els.length; j++) {
                    var el = els[j];
                    if (!el.name || el.disabled) { continue; }
                    parts.push(encodeURIComponent(el.name) + '=' + encodeURIComponent(el.value));
                }
                return parts.join('&');
            }
        } catch (e) {}
    })();
```

The `title` swap above reads `data-title` and `data-untitle` off the button. Add those in `src/Inkshelf/Pages/Shared/_ReadButton.cshtml` on both buttons, so the script has the localized strings without a second `I18N` round trip:

```razor
    @if (Model.Read)
    {
        <button type="submit" class="btn read-btn" title="@L["Mark as unread"]"
                data-title="@L["Mark as read"]" data-untitle="@L["Mark as unread"]">&#10003; @L["Read"]</button>
    }
    else
    {
        <button type="submit" class="btn read-btn" title="@L["Mark as read"]"
                data-title="@L["Mark as read"]" data-untitle="@L["Mark as unread"]">@L["Mark read"]</button>
    }
```

Note `btn.firstChild.nodeValue` is how the convert script mutates a label (`_Layout.cshtml:56`), and it works because the button's first child is a text node. The read button in the read state starts with `&#10003;` followed by text, which is a single text node once rendered, so this holds. Verify it by reading the rendered HTML if anything looks off.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 6: Verify the script parses and the page still works**

Run: `tools/uicheck/run.sh`
Expected: exit 0 at both viewport widths. This matters more than usual: the headless Chromium actually EXECUTES the script, so a syntax error that would silently break the page on a device shows up as a failed assertion or a blank region here. Open the listing screenshots in `tools/uicheck/shots/` and confirm the read button still renders with its normal label in both languages, not "Marking…" and not empty.

- [ ] **Step 7: Commit**

```bash
git add src/Inkshelf/Pages/Shared/_Layout.cshtml src/Inkshelf/Pages/Shared/_ReadButton.cshtml src/Inkshelf/locales/de.json tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "feat: mark read without reloading the page"
```

---

### Task 5: Documentation

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/ROADMAP.md`

**Interfaces:**
- Consumes: the finished feature.
- Produces: nothing code-facing.

- [ ] **Step 1: Record the invariant, not the feature**

`docs/ARCHITECTURE.md` is a map, not a diary. `CLAUDE.md` is explicit that a per-feature entry there is a smell. Do NOT add a "read without reload" bullet.

There IS a rule worth recording, because violating it causes a bug that is not obvious from reading the code: the read form posts an ABSOLUTE desired state rather than a toggle, and the no-reload script depends on that for retry safety. Turning it into a toggle would make a retry after a lost response flip the book back.

Find the existing bullet nearest to the near-zero-JavaScript design or the download-ticket invariants and add this as a clause or a single short bullet in the same register as its neighbours. Keep it to two sentences. If, on reading the file, no natural home exists and a new bullet is genuinely warranted, add one, but state in your report why an existing bullet would not carry it.

- [ ] **Step 2: Move the issue to Done**

Add an entry to `docs/ROADMAP.md`'s `## Done` section matching the format of the entries already there, noting it closes #66. Do NOT touch `CHANGELOG.md`.

- [ ] **Step 3: Verify no banned dashes across the branch**

Run: `git diff main | grep -nP '^\+.*[\x{2013}\x{2014}]' ; echo "exit $?"`
Expected: no matching lines printed.

- [ ] **Step 4: Commit**

```bash
git add docs/
git commit -m "docs: record the absolute read-state invariant"
```

---

## After the plan

The headless pass executes the script but is not the e-reader engine, so a device pass by the user stays mandatory: mark several books in a row and confirm the page does not move and each label changes; then disable JavaScript and confirm the form still posts and lands back on the tapped row. Then use `superpowers:finishing-a-development-branch`.
