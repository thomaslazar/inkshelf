# Read-state Toggle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the user mark a library item read/unread from listing + search rows, synced to Audiobookshelf via its media-progress API.

**Architecture:** Read state lives in ABS. `AbsApiClient` gains a read method (`GET /api/me` → finished `libraryItemId` set) and a write method (`PATCH /api/me/progress/{id}` `{isFinished}`). `LibraryModel` fetches the finished-set once per render and passes `Read` into each `ItemRowModel`; `_ItemRow` renders a POST toggle form; a new `POST /read/{id}` minimal-API endpoint performs the write.

**Tech Stack:** ASP.NET Core Razor Pages + minimal APIs, .NET 10 (no AOT), xUnit, `WebApplicationFactory<Program>` + `StubHandler` for ABS stubbing.

## Global Constraints

- **No AOT.** .NET 10, ASP.NET Core. Razor Pages for HTML, minimal APIs for actions.
- **Near-zero client JS.** The toggle is a plain POST `<form>` (full reload). No new JS.
- **Defensive CSS only** — no `object-fit`, no flex `gap`.
- **State-changing POSTs are antiforgery-protected**, following the exact `/favorite` + `/logout` convention: `[FromForm]` + manual `antiforgery.ValidateRequestAsync(ctx)` in try/catch → `Results.BadRequest()` on `AntiforgeryValidationException` + `.DisableAntiforgery()` on the route.
- **New ABS call = new method on `AbsApiClient`** (no `accessToken` param; `AbsAuthHandler` injects the Bearer). Introduce a **new DTO** rather than widening an existing one.
- **ABS field names** (verified against ABS v2.35.1): `GET /api/me` → top-level `mediaProgress` (array); each entry has `libraryItemId` and `isFinished`. Write: `PATCH /api/me/progress/{libraryItemId}` body `{"isFinished": <bool>}`.
- **Conventional Commits**, imperative lowercase subject. **No** `Co-Authored-By` / "Generated with" trailer lines.
- Build/test from repo root `/workspaces/inkshelf`: `dotnet test`. Build only: `dotnet build src/Inkshelf/Inkshelf.csproj`.

---

### Task 1: `AbsApiClient` read/write methods + `AbsMe` DTO

**Files:**
- Modify: `src/Inkshelf/Abs/AbsModels.cs` (add DTOs)
- Modify: `src/Inkshelf/Abs/AbsApiClient.cs` (add two methods)
- Test: `tests/Inkshelf.Tests/AbsApiClientTests.cs`

**Interfaces:**
- Produces:
  - `record AbsMe([property: JsonPropertyName("mediaProgress")] List<AbsMediaProgress>? MediaProgress)`
  - `record AbsMediaProgress([property: JsonPropertyName("libraryItemId")] string? LibraryItemId, [property: JsonPropertyName("isFinished")] bool IsFinished)`
  - `AbsApiClient.GetFinishedItemIdsAsync(CancellationToken)` → `Task<HashSet<string>>`
  - `AbsApiClient.SetReadAsync(string itemId, bool finished, CancellationToken)` → `Task`

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/AbsApiClientTests.cs`:

```csharp
[Fact]
public async Task GetFinishedItemIdsAsync_returns_only_finished_with_ids()
{
    var h = new StubHandler(_ => StubHandler.Json(
        """{"mediaProgress":[{"libraryItemId":"i1","isFinished":true},{"libraryItemId":"i2","isFinished":false},{"libraryItemId":null,"isFinished":true}]}"""));
    var set = await Client(h).GetFinishedItemIdsAsync();
    Assert.Equal("/api/me", h.Last!.RequestUri!.AbsolutePath);
    Assert.Contains("i1", set);
    Assert.DoesNotContain("i2", set);   // not finished
    Assert.Single(set);                 // null libraryItemId ignored
}

[Fact]
public async Task GetFinishedItemIdsAsync_tolerates_empty_progress()
{
    var h = new StubHandler(_ => StubHandler.Json("""{"mediaProgress":[]}"""));
    Assert.Empty(await Client(h).GetFinishedItemIdsAsync());
}

[Fact]
public async Task SetReadAsync_patches_progress_with_isFinished_true()
{
    var h = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
    await Client(h).SetReadAsync("item1", finished: true);
    Assert.Equal(HttpMethod.Patch, h.Last!.Method);
    Assert.Equal("/api/me/progress/item1", h.Last!.RequestUri!.AbsolutePath);
    var body = await h.Last!.Content!.ReadAsStringAsync();
    Assert.Contains("\"isFinished\":true", body);
}

[Fact]
public async Task SetReadAsync_patches_isFinished_false_to_unmark()
{
    var h = new StubHandler(_ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK));
    await Client(h).SetReadAsync("item1", finished: false);
    var body = await h.Last!.Content!.ReadAsStringAsync();
    Assert.Contains("\"isFinished\":false", body);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter AbsApiClientTests`
Expected: FAIL — `GetFinishedItemIdsAsync` / `SetReadAsync` / `AbsMe` don't exist (compile error).

- [ ] **Step 3: Add the DTOs**

In `src/Inkshelf/Abs/AbsModels.cs`, append:

```csharp
// Current user (GET /api/me) — only the media-progress read-state is consumed.
public record AbsMe(
    [property: JsonPropertyName("mediaProgress")] List<AbsMediaProgress>? MediaProgress);
public record AbsMediaProgress(
    [property: JsonPropertyName("libraryItemId")] string? LibraryItemId,
    [property: JsonPropertyName("isFinished")] bool IsFinished);
```

- [ ] **Step 4: Add the methods**

In `src/Inkshelf/Abs/AbsApiClient.cs`, add (after `SearchAsync`, before `GetCoverAsync`):

```csharp
// Read state lives in ABS as per-user media progress. One call returns the
// whole finished-set; we key on libraryItemId (matches AbsItem.Id).
public async Task<HashSet<string>> GetFinishedItemIdsAsync(CancellationToken ct = default)
{
    using var res = await SendAsync(HttpMethod.Get, "/api/me", ct);
    var me = await res.Content.ReadFromJsonAsync<AbsMe>(ct);
    var set = new HashSet<string>();
    foreach (var mp in me?.MediaProgress ?? new())
        if (mp.IsFinished && !string.IsNullOrEmpty(mp.LibraryItemId))
            set.Add(mp.LibraryItemId);
    return set;
}

// Mark an item read (isFinished:true) or unread (false). PATCH is symmetric —
// unmarking leaves a harmless isFinished:false progress row, so no DELETE / no
// need to know the progress-row id.
public async Task SetReadAsync(string itemId, bool finished, CancellationToken ct = default)
{
    using var content = JsonContent.Create(new { isFinished = finished });
    using var res = await SendAsync(HttpMethod.Patch,
        $"/api/me/progress/{Uri.EscapeDataString(itemId)}", ct, content);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter AbsApiClientTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Abs/AbsModels.cs src/Inkshelf/Abs/AbsApiClient.cs tests/Inkshelf.Tests/AbsApiClientTests.cs
git commit -m "feat: add ABS read-state read/write to AbsApiClient"
```

---

### Task 2: `POST /read/{id}` endpoint

**Files:**
- Create: `src/Inkshelf/Endpoints/ReadEndpoints.cs`
- Modify: `src/Inkshelf/Program.cs` (map it)
- Test: `tests/Inkshelf.Tests/EndpointTests.cs`

**Interfaces:**
- Consumes: `AbsApiClient.SetReadAsync` (Task 1).
- Produces: `IEndpointRouteBuilder.MapReadEndpoints()` mapping `POST /read/{id}`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/EndpointTests.cs`, using the existing `CreateFactory()` + `GetAntiforgeryTokenAsync` helpers (no ABS stub / session plumbing — the PATCH URL/body is already covered by `AbsApiClientTests`):

```csharp
[Fact]
public async Task Read_post_without_antiforgery_returns_bad_request()
{
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var response = await client.PostAsync("/read/item1", content: null);

    Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
}

[Fact]
public async Task Read_post_with_token_but_no_session_redirects_to_login()
{
    // Valid antiforgery token (the client stores the matching cookie from /login),
    // but no session. Antiforgery passes → handler calls SetReadAsync → AbsAuthHandler
    // finds no token → AbsAuthException → the auth middleware redirects to /login.
    // A 302→/login (not a 400) proves the endpoint is mapped, antiforgery validated,
    // and the handler reached the ABS call path. Mirrors Cover_WithoutSession.
    using var factory = CreateFactory();
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var token = await GetAntiforgeryTokenAsync(client);
    var content = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = token,
        ["read"] = "1",
    });

    var response = await client.PostAsync("/read/item1", content);

    Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
    Assert.Equal("/login", response.Headers.Location?.OriginalString);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter EndpointTests`
Expected: FAIL — `/read/item1` returns 404 (route not mapped).

- [ ] **Step 3: Implement the endpoint**

Create `src/Inkshelf/Endpoints/ReadEndpoints.cs`:

```csharp
using Inkshelf.Abs;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Inkshelf.Endpoints;

public static class ReadEndpoints
{
    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/read/{id}", async (string id, HttpContext ctx, IAntiforgery antiforgery,
            AbsApiClient api, [FromForm] string? read, [FromForm(Name = "return")] string? @return,
            CancellationToken ct) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            await api.SetReadAsync(id, read == "1", ct);
            return Results.Redirect(LocalReturn(@return));
        }).DisableAntiforgery();
    }

    // Open-redirect guard: only same-site absolute paths are honored.
    private static string LocalReturn(string? r) =>
        !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
```

- [ ] **Step 4: Map it in `Program.cs`**

In `src/Inkshelf/Program.cs`, next to the other endpoint maps:

```csharp
app.MapSessionEndpoints();
app.MapSettingsEndpoints();
app.MapReadEndpoints();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter EndpointTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Inkshelf/Endpoints/ReadEndpoints.cs src/Inkshelf/Program.cs tests/Inkshelf.Tests/EndpointTests.cs
git commit -m "feat: add POST /read endpoint to toggle read state"
```

---

### Task 3: Row toggle UI + `LibraryModel` wiring

**Files:**
- Modify: `src/Inkshelf/Pages/Support/ItemRowModel.cs` (add `Read`)
- Modify: `src/Inkshelf/Pages/Library.cshtml.cs` (fetch finished-set, set `Read` in `RowFor`)
- Modify: `src/Inkshelf/Pages/Shared/_ItemRow.cshtml` (render the toggle form)
- Modify: `src/Inkshelf/wwwroot/app.css` (read-button style)
- Test: `tests/Inkshelf.Tests/ListingRenderTests.cs`

**Interfaces:**
- Consumes: `AbsApiClient.GetFinishedItemIdsAsync` (Task 1); the `/read/{id}` endpoint (Task 2).
- Produces: `ItemRowModel` gains `bool Read = false`.

- [ ] **Step 1: Write the failing tests**

In `tests/Inkshelf.Tests/ListingRenderTests.cs`, first make the shared stub answer `/api/me` (the model now calls it every render). Change `MakeStub` to take an optional `meJson`:

```csharp
private static StubHandler MakeStub(string? meJson = null) => new(req =>
{
    var path = req.RequestUri!.AbsolutePath;
    if (path == "/api/libraries") return StubHandler.Json(LibrariesJson);
    if (path == $"/api/libraries/{LibId}/items") return StubHandler.Json(ItemsJson());
    if (path == "/api/items/batch/get" && req.Method == HttpMethod.Post) return StubHandler.Json(BatchJson());
    if (path == $"/api/libraries/{LibId}/search") return StubHandler.Json(SearchJson());
    if (path == "/api/me") return StubHandler.Json(meJson ?? """{"mediaProgress":[]}""");
    return new HttpResponseMessage(HttpStatusCode.NotFound);
});
```

Add tests:

```csharp
[Fact]
public async Task Unread_row_shows_mark_read_toggle()
{
    using var cacheDir = new TempDir();
    using var keysDir = new TempDir();
    using var factory = CreateFactory(MakeStub(), cacheDir.Path, keysDir.Path);
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

    Assert.Contains($"action=\"/read/{ItemId}\"", html);
    Assert.Contains(">Mark read</button>", html);
    Assert.Contains("name=\"read\" value=\"1\"", html);
}

[Fact]
public async Task Read_row_shows_checked_toggle_that_unmarks()
{
    using var cacheDir = new TempDir();
    using var keysDir = new TempDir();
    var me = $$"""{"mediaProgress":[{"libraryItemId":"{{ItemId}}","isFinished":true} ]}""";
    using var factory = CreateFactory(MakeStub(me), cacheDir.Path, keysDir.Path);
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var html = await (await client.SendAsync(LibraryRequest(factory))).Content.ReadAsStringAsync();

    Assert.Contains("&#10003; Read</button>", html);        // "✓ Read" (entity-encoded in markup)
    Assert.Contains("name=\"read\" value=\"0\"", html);     // tapping unmarks
}

[Fact]
public async Task Search_row_shows_read_toggle_too()
{
    using var cacheDir = new TempDir();
    using var keysDir = new TempDir();
    var me = $$"""{"mediaProgress":[{"libraryItemId":"{{ItemId}}","isFinished":true} ]}""";
    using var factory = CreateFactory(MakeStub(me), cacheDir.Path, keysDir.Path);
    using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    var req = LibraryRequest(factory);
    req.RequestUri = new Uri($"/library/{LibId}?q=comic", UriKind.Relative);
    var html = await (await client.SendAsync(req)).Content.ReadAsStringAsync();

    Assert.Contains("Results for", html);
    Assert.Contains($"action=\"/read/{ItemId}\"", html);
    Assert.Contains("&#10003; Read</button>", html);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter ListingRenderTests`
Expected: FAIL — no `/read/` form / `Mark read` in the rendered rows.

- [ ] **Step 3: Add `Read` to `ItemRowModel`**

In `src/Inkshelf/Pages/Support/ItemRowModel.cs`, extend the record:

```csharp
public record ItemRowModel(
    AbsItem Item,
    LibraryLinks Links,
    IReadOnlyList<AbsRef>? Authors = null,
    IReadOnlyList<AbsSeriesRef>? Series = null,
    ConvertRowState State = ConvertRowState.NotConvertible,
    string ReturnUrl = "/",
    bool Read = false);
```

- [ ] **Step 4: Wire the finished-set into `LibraryModel`**

In `src/Inkshelf/Pages/Library.cshtml.cs`:

Add a field near `_structured`:
```csharp
private HashSet<string> _finished = new();
```

Add a helper near `FetchStructuredAsync`:
```csharp
// Read-state is a single GET /api/me. A transient failure degrades to "all
// unread" rather than blanking the page; an expired session still propagates
// AbsAuthException → /login (only HttpRequestException is swallowed).
private async Task<HashSet<string>> FetchFinishedAsync(CancellationToken ct)
{
    try { return await _api.GetFinishedItemIdsAsync(ct); }
    catch (HttpRequestException) { return new(); }
}
```

In `OnGetAsync`, set `_finished` in **both** branches:
- Search branch — after `_structured = await FetchStructuredAsync(books, ct);` and before `return Page();`:
  ```csharp
  _finished = await FetchFinishedAsync(ct);
  ```
- Listing branch — after `ComputeConvertStates(Items);`:
  ```csharp
  _finished = await FetchFinishedAsync(ct);
  ```

In `RowFor`, pass `Read` on the returned model:
```csharp
return new ItemRowModel(item, Links, media?.Metadata?.Authors, media?.Metadata?.Series, state, ret, _finished.Contains(item.Id));
```

- [ ] **Step 5: Render the toggle in `_ItemRow.cshtml`**

In `src/Inkshelf/Pages/Shared/_ItemRow.cshtml`, inside the `<div class="actions">` block, add the read form as the **last** child (after the `@if (Model.State != ConvertRowState.NotConvertible)` convert block, still inside the `actions` div):

```cshtml
            <form class="read-form" method="post" action="/read/@item.Id">
                @Html.AntiForgeryToken()
                <input type="hidden" name="read" value="@(Model.Read ? "0" : "1")" />
                <input type="hidden" name="return" value="@Model.ReturnUrl" />
                @if (Model.Read)
                {
                    <button type="submit" class="read-btn" title="Mark as unread">&#10003; Read</button>
                }
                else
                {
                    <button type="submit" class="read-btn" title="Mark as read">Mark read</button>
                }
            </form>
```

The `&#10003;` (✓) is emitted as literal markup — like the existing `EPUB &#10003;` — rather than a C# string, so Razor doesn't entity-re-encode it and the rendered output is a stable `&#10003; Read` the test asserts on.

- [ ] **Step 6: Style the read button**

Append to `src/Inkshelf/wwwroot/app.css`:

```css
.item .actions .read-form { margin: 0; }
.item .actions .read-btn { display: block; border: none; background: none; padding: 0; margin-bottom: .35rem; font: inherit; color: #000; text-align: left; cursor: pointer; }
```

- [ ] **Step 7: Run the full suite**

Run: `dotnet test`
Expected: PASS. (Existing `ListingRenderTests` still pass — every row now also renders a "Mark read" button, which their assertions don't conflict with.)

- [ ] **Step 8: Verify in the running app**

Use the `run` skill (or `dotnet run --project src/Inkshelf` on port 5099, `ABS_URL` set). On a library listing and a search: confirm each row shows **Mark read**, tapping it reloads to **✓ Read**, tapping again reverts, and the state persists across reloads (it's in ABS). Confirm the toggle sits cleanly in the actions column and stays inside it.

- [ ] **Step 9: Commit**

```bash
git add src/Inkshelf/Pages/Support/ItemRowModel.cs src/Inkshelf/Pages/Library.cshtml.cs \
  src/Inkshelf/Pages/Shared/_ItemRow.cshtml src/Inkshelf/wwwroot/app.css \
  tests/Inkshelf.Tests/ListingRenderTests.cs
git commit -m "feat: add read/unread toggle to listing and search rows"
```

---

### Task 4: Documentation — ARCHITECTURE + ROADMAP

**Files:**
- Modify: `docs/ARCHITECTURE.md`
- Modify: `docs/ROADMAP.md`

- [ ] **Step 1: Update `ARCHITECTURE.md`** (present-tense, structural — no changelog/shipped-status prose)

- In the layout map, add `Read` to the `Endpoints/` group list.
- Under "Adding a new X" / the ABS section, the two new `AbsApiClient` methods fit the existing "7 data methods" note — update the count/wording if it names a number, and mention read state is per-user ABS media progress (`GET /api/me` for the finished-set, `PATCH /api/me/progress/{id}` to toggle).
- Add one load-bearing bullet:

```markdown
- **Read state is ABS media progress, not local.** The listing/search reads the
  finished-set from `GET /api/me` once per render (keyed by `libraryItemId`),
  and the per-row toggle POSTs to `/read/{id}` → `PATCH /api/me/progress/{id}`
  `{isFinished}`. A failed read-state fetch degrades to "all unread" rather than
  failing the page.
```

- [ ] **Step 2: Update `ROADMAP.md`**

- Remove the **Read-state toggle** bullet from *Browsing & reading*. (Leave the *Item detail page* bullet's own read-state mention — it's built when the detail page is.)
- Add to **Done**:

```markdown
- **Read-state toggle** — per-row Mark read / ✓ Read on listing + search rows,
  synced to ABS media progress (`GET /api/me` finished-set; `PATCH
  /api/me/progress/{id}` `{isFinished}`).
```

- [ ] **Step 3: Commit**

```bash
git add docs/ARCHITECTURE.md docs/ROADMAP.md
git commit -m "docs: record read-state toggle, trim roadmap"
```

---

## Self-Review Notes

- **Spec coverage:** ABS read/write methods + DTO (Task 1); `/read/{id}` endpoint (Task 2); row toggle on listing **and** search + `LibraryModel` wiring with safe degradation (Task 3); docs incl. moving the item to Done (Task 4). PATCH-for-unmark, once-per-render `GET /api/me`, and "every downloadable row" all reflected. All covered.
- **Type consistency:** `AbsMe.MediaProgress` (`List<AbsMediaProgress>?`), `AbsMediaProgress.LibraryItemId`/`IsFinished`; `GetFinishedItemIdsAsync` → `HashSet<string>`; `SetReadAsync(string, bool, CancellationToken)`; `ItemRowModel.Read`; form fields `read` (`"1"`/`"0"`) + `return`; endpoint `read == "1"`. Consistent across tasks.
- **Incrementality:** each task ends green. Task 1 (ABS methods) and Task 2 (endpoint) don't change rendering; Task 3 adds the UI and the once-per-render `/api/me` call, and updates the shared stub so existing render tests keep passing.
- **Placeholder scan:** none — every step carries concrete code/commands.
