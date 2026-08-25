# Logged-in User Display Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show which ABS user a device is signed in as, on the libraries page, appended to the version line already there.

**Architecture:** ABS returns the user object on both login and refresh, and `AbsAuthClient` already parses it for the tokens. The username rides along in `Tokens`, gets stored as a third field in the session cookie, and the libraries page reads it from that cookie — no extra ABS call anywhere.

**Tech Stack:** ASP.NET Core Razor Pages, .NET 10, xUnit with `WebApplicationFactory<Program>`. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-08-25-logged-in-user-design.md`

## Global Constraints

- **A two-field session cookie must keep working.** `TokenStore.Read` currently requires exactly two `\n`-separated parts. Requiring three would make every existing cookie unparseable and sign the whole household out on upgrade. Accept two *or* three.
- **The username goes LAST in the cookie.** ABS usernames are not newline-free by contract; after the tokens, a newline inside one can only add a line break to a display string instead of shifting a token field.
- `Tokens.Username` defaults to `""` so the eight existing `new Tokens("acc", "ref")` call sites keep compiling. Empty string, never null.
- The name is a **display string only** — nothing branches on it, nothing authorises with it.
- Non-ASCII glyphs are emitted as HTML entities, matching the codebase (`&#8595;`, `&#8594;`, `›`).
- Localised label: locale keys in this project **are** the English string (`L["Log out"]`), and formatted keys exist (`L["Log in with {0}", …]`).
- **Comments state rules and reasons, not narration.** A comment restating what the next line does will be rejected in review. Keep them short.
- **Do not touch `CHANGELOG.md`** — release process only. `docs/ARCHITECTURE.md` is a map: this feature introduces no new invariant, so **do not add anything to it**.
- Run `dotnet format --verify-no-changes` before each commit; CI fails on formatting.
- Baseline: 461 tests passing at `main`.

---

## File Structure

**Created:**
- `tests/Inkshelf.Tests/IndexRenderTests.cs` — renders `/` and asserts the version line, matching the one-file-per-page-render convention (`ItemRenderTests`, `ListingRenderTests`, `ConvertedRenderTests`).

**Modified:**
- `src/Inkshelf/Abs/AbsModels.cs` — `AbsAuthUser` gains `username`.
- `src/Inkshelf/Auth/Tokens.cs` — gains `Username`.
- `src/Inkshelf/Abs/AbsAuthClient.cs` — pass it through `ReadTokens`.
- `src/Inkshelf/Auth/TokenStore.cs` — write three fields, parse two or three.
- `src/Inkshelf/Pages/Index.cshtml.cs` — expose the name.
- `src/Inkshelf/Pages/Index.cshtml` — render it.
- `src/Inkshelf/locales/de.json` — one key. English needs no file: `LocalizationCatalog.Get` returns the key itself on a miss, and the key is the English string.
- `tests/Inkshelf.Tests/TokenStoreTests.cs`, `AbsAuthClientTests.cs`.
- `tools/uicheck/Program.cs` — assert it on the authed German pass.

---

### Task 1: Carry the username from ABS into the session cookie

**Files:**
- Modify: `src/Inkshelf/Abs/AbsModels.cs` (`AbsAuthUser`, ~line 7)
- Modify: `src/Inkshelf/Auth/Tokens.cs`
- Modify: `src/Inkshelf/Abs/AbsAuthClient.cs` (`ReadTokens`, ~line 95)
- Modify: `src/Inkshelf/Auth/TokenStore.cs` (`Save`, `Read`)
- Modify: `tests/Inkshelf.Tests/TokenStoreTests.cs`
- Modify: `tests/Inkshelf.Tests/AbsAuthClientTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `record Tokens(string Access, string Refresh, string Username = "")`. `TokenStore.Read()` returns it with `Username` populated from a three-field cookie, or `""` from a two-field one.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Inkshelf.Tests/TokenStoreTests.cs`:

```csharp
    [Fact]
    public void Save_then_Read_roundtrips_the_username()
    {
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(new Tokens("acc", "ref", "alice"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_session={value}";

        Assert.Equal(new Tokens("acc", "ref", "alice"), Make(ctx2).Read());
    }

    [Fact]
    public void Read_accepts_a_cookie_written_before_the_username_was_stored()
    {
        // THE upgrade test. A two-field cookie predates this feature; rejecting it
        // would sign every logged-in device out the moment the new build deploys.
        var payload = DataProtectionProvider.Create("inkshelf-tests")
            .CreateProtector("inkshelf.session.v1").Protect("acc\nref");
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers.Cookie = $"inkshelf_session={payload}";

        var read = Make(ctx).Read();

        Assert.NotNull(read);
        Assert.Equal("acc", read!.Access);
        Assert.Equal("ref", read.Refresh);
        Assert.Equal("", read.Username);   // not known yet, and that is fine
    }

    [Fact]
    public void A_username_containing_a_newline_cannot_corrupt_the_tokens()
    {
        // Why the name is stored last: a newline in it must not shift a token field.
        var ctx = new DefaultHttpContext();
        Make(ctx).Save(new Tokens("acc", "ref", "ev\nil"));

        var value = ctx.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
        var ctx2 = new DefaultHttpContext();
        ctx2.Request.Headers.Cookie = $"inkshelf_session={value}";

        var read = Make(ctx2).Read();

        Assert.NotNull(read);
        Assert.Equal("acc", read!.Access);
        Assert.Equal("ref", read.Refresh);
    }
```

Add to `tests/Inkshelf.Tests/AbsAuthClientTests.cs`, following that file's existing stub style — one for login and one for refresh, since ABS returns the user object on both and a refresh that dropped the name would blank it mid-session:

```csharp
    [Fact]
    public async Task LoginAsync_parses_the_username()
    {
        var stub = new StubHandler(_ => StubHandler.Json(
            """{"user":{"username":"alice","accessToken":"acc","refreshToken":"ref"}}"""));

        var tokens = await Client(stub).LoginAsync("alice", "pw");

        Assert.Equal("alice", tokens.Username);
    }

    [Fact]
    public async Task RefreshAsync_parses_the_username_so_a_refresh_does_not_blank_it()
    {
        var stub = new StubHandler(_ => StubHandler.Json(
            """{"user":{"username":"alice","accessToken":"acc2","refreshToken":"ref2"}}"""));

        var tokens = await Client(stub).RefreshAsync("ref");

        Assert.Equal("alice", tokens.Username);
    }
```

Use whatever the file's existing helper for building the client is called; do not invent a second one.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Inkshelf.Tests --filter "TokenStoreTests|AbsAuthClientTests"`
Expected: build failure — `Tokens` has no `Username`, and no third constructor argument.

- [ ] **Step 3: Add the field to the model and the record**

`src/Inkshelf/Abs/AbsModels.cs`:

```csharp
public record AbsAuthUser(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("username")] string? Username = null);
```

`src/Inkshelf/Auth/Tokens.cs`:

```csharp
namespace Inkshelf.Auth;

// Username is a display string captured at login, never an authorisation input:
// nothing branches on it. Defaulted so the tokens-only construction sites stay
// valid, and empty rather than null so nothing downstream has to null-check it.
public record Tokens(string Access, string Refresh, string Username = "");
```

- [ ] **Step 4: Pass it through `ReadTokens`**

In `AbsAuthClient.ReadTokens`, return `new Tokens(u.AccessToken, u.RefreshToken!, u.Username ?? "")`. Leave the existing token-presence guard exactly as it is — a missing username is not an error.

- [ ] **Step 5: Store and parse it**

In `TokenStore.Save`, replace the payload line and amend the comment:

```csharp
        // access \n refresh \n username. Neither token contains a newline (JWTs are
        // base64url.compact); the username might, so it goes LAST, where a newline
        // can only add a line break to a display string instead of shifting a token.
        var payload = _protector.Protect($"{tokens.Access}\n{tokens.Refresh}\n{tokens.Username}");
```

In `TokenStore.Read`:

```csharp
            // Two fields is a cookie written before the username was stored. Parse it
            // rather than rejecting it, or an upgrade signs every device out.
            var parts = _protector.Unprotect(raw).Split('\n', 3);
            return parts.Length switch
            {
                3 => new Tokens(parts[0], parts[1], parts[2]),
                2 => new Tokens(parts[0], parts[1]),
                _ => null,
            };
```

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Inkshelf.Tests --filter "TokenStoreTests|AbsAuthClientTests"`
Expected: PASS, including the pre-existing tests in both files.

- [ ] **Step 7: Full suite, format, commit**

```bash
dotnet test
dotnet format --verify-no-changes
git add src/Inkshelf/Abs/AbsModels.cs src/Inkshelf/Abs/AbsAuthClient.cs \
        src/Inkshelf/Auth/Tokens.cs src/Inkshelf/Auth/TokenStore.cs \
        tests/Inkshelf.Tests/TokenStoreTests.cs tests/Inkshelf.Tests/AbsAuthClientTests.cs
git commit -m "feat: keep the logged-in username in the session cookie"
```

---

### Task 2: Show it on the libraries page

**Files:**
- Modify: `src/Inkshelf/Pages/Index.cshtml.cs`
- Modify: `src/Inkshelf/Pages/Index.cshtml` (the last line)
- Modify: `src/Inkshelf/locales/de.json` (English needs no file — the key is the English string)
- Create: `tests/Inkshelf.Tests/IndexRenderTests.cs`
- Modify: `tests/Inkshelf.Tests/FavoriteLibraryRoutingTests.cs` (three `new IndexModel(...)` sites)
- Modify: `tools/uicheck/Program.cs` (~line 125)

**Interfaces:**
- Consumes: `Tokens.Username` and `TokenStore.Read()` from Task 1.
- Produces: `IndexModel.Username` (empty string when unknown).

- [ ] **Step 1: Write the failing test**

Create `tests/Inkshelf.Tests/IndexRenderTests.cs`. Follow `ItemRenderTests`' factory pattern — `WebApplicationFactory<Program>` with `ABS_URL`, a `TempDir` cache and keys path, the `ConvertWorker` removed, and a `StubHandler` on `AbsApiClient` answering `GET /api/libraries`. Build the session cookie the way that file does, with the Data Protection protector from `factory.Services`, and protect **three** fields to carry a username:

```csharp
    [Fact]
    public async Task The_version_line_names_the_logged_in_user()
    {
        // Shared deployment: which account a reader is signed in as is otherwise
        // invisible. Reads from the session cookie, so no ABS call is involved.
        var html = await GetIndexHtml(session: "acc\nref\nalice", lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current}", html);
        Assert.Contains("User: alice", html);
    }

    [Fact]
    public async Task The_version_line_stays_bare_when_no_username_is_stored()
    {
        // A cookie from before the username was stored must render exactly today's
        // line — no separator, no empty label.
        var html = await GetIndexHtml(session: "acc\nref", lang: "");

        Assert.Contains($"Inkshelf v{AppVersion.Current}", html);
        Assert.DoesNotContain("User:", html);
        Assert.DoesNotContain("&#8212;", html);
    }

    [Fact]
    public async Task The_label_is_localised()
    {
        var html = await GetIndexHtml(session: "acc\nref\nalice", lang: "de");

        Assert.Contains("Benutzer: alice", html);
        Assert.DoesNotContain("User: alice", html);
    }
```

Write the `GetIndexHtml(session, lang)` helper in that file: it protects `session` with the `inkshelf.session.v1` protector, sends `Cookie: inkshelf_session=<protected>; inkshelf_settings=retina=1&gray=0&lang=<lang>&fav=`, and returns the response body. Keep it to one helper; do not duplicate the request-building per test.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Inkshelf.Tests --filter IndexRenderTests`
Expected: the first and third tests FAIL (no username rendered); the second passes already, which is correct — it pins the unchanged behaviour.

- [ ] **Step 3: Expose the name on the page model**

`Index.cshtml.cs`: inject `TokenStore` alongside `AbsApiClient` and add:

```csharp
    // From the session cookie, not ABS: the libraries page already decrypts it, so
    // this costs no request and still shows when ABS is unreachable.
    public string Username => _tokens.Read()?.Username ?? "";
```

- [ ] **Step 3b: Fix the three construction sites the new parameter breaks**

`FavoriteLibraryRoutingTests` builds `new IndexModel(LibrariesClient(...))` at lines
46, 55 and 68 and will no longer compile. This is a forced mechanical consequence,
not a regression — **do not work around it by avoiding constructor injection**, and
do not change what any of those tests assert.

The wrinkle: `TokenStore` needs an `IHttpContextAccessor`, and that file's
`WithContext` helper creates the `HttpContext` *after* the model. Restructure so the
context is built first, then a `TokenStore` over it, then the model — one helper that
returns the wired-up model. Those three tests never read `Username` (only the view
does), so the store just has to exist and be constructible.

- [ ] **Step 4: Render it**

`Index.cshtml`, replacing the last line:

```html
<p class="app-version"><small>Inkshelf v@(Model.Version)@if (Model.Username.Length > 0) {<text> &#8212; @L["User: {0}", Model.Username]</text>}</small></p>
```

- [ ] **Step 5: Add the locale key**

`de.json`: `"User: {0}": "Benutzer: {0}"`. Insert it in the file's existing key order and keep the JSON valid. No `en.json` exists or is needed — `LocalizationCatalog.Get` returns the key itself on a miss, and the key `"User: {0}"` already reads as English.

- [ ] **Step 6: Run the tests**

Run: `dotnet test tests/Inkshelf.Tests --filter IndexRenderTests` then `dotnet test`
Expected: PASS, whole suite green.

- [ ] **Step 7: Assert it in the browser pass**

`tools/uicheck/Program.cs`, at the `index-de` expectation (~line 125): add `"Benutzer: root"` to the needles — the seeded ABS logs in as `root`, and the authed pass runs in German only, so this is the German half; the English string is covered by `IndexRenderTests`.

Run: `tools/uicheck/run.sh`
Expected: exit 0. Then **look at** `tools/uicheck/shots/index-de.png` and confirm the line reads `Inkshelf v… — Benutzer: root` and has not wrapped awkwardly.

- [ ] **Step 8: Format and commit**

```bash
dotnet format --verify-no-changes
git add src/Inkshelf/Pages/Index.cshtml src/Inkshelf/Pages/Index.cshtml.cs \
        src/Inkshelf/locales/de.json \
        tests/Inkshelf.Tests/IndexRenderTests.cs tools/uicheck/Program.cs
git commit -m "feat: name the logged-in user on the libraries page"
```

---

## Notes for the implementer

- Do not add an `/api/me` call anywhere. The spec rejects it: it costs a request per page load for a display string and shows nothing when ABS is down.
- Do not put the name in the header, on `/settings`, or on the login page. The libraries page is the whole scope.
- Two test files are expected to change mechanically: `FavoriteLibraryRoutingTests` (the new constructor parameter, Step 3b) and the two files Task 1 names. If any *other* pre-existing test fails, stop and report it rather than adapting it — the session cookie format is shared, and a break elsewhere means the two-or-three-field compatibility rule was violated.
