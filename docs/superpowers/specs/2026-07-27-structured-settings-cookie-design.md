# Structured settings cookie (+ fold Favorites in)

**Status:** design approved, ready for implementation plan
**Date:** 2026-07-27
**Roadmap item:** Settings — "Structured settings cookie (refactor)"

## Goal

`DeviceSettings` packs its values into one positional string (`"10de"` =
retina, grayscale, lang). Meaning is by index, only the *last* field may be
variable-length, and every new setting is another hand-rolled parse plus a
legacy shape to keep reading. Two backlog settings are blocked behind this:

- **Resolution override** — a variable-length *number*, which cannot follow the
  variable-length `lang` field. Positional encoding has no room for it.
- **EPUB2 reflowable fallback** — a flag, would fit, but not after `lang`.

Move to a keyed encoding so a new setting is one key and one line, then fold
the sibling `Favorites` cookie into the same value so the app ends with one
preferences cookie instead of two.

Note: the original settings design (`2026-07-17-settings-retina-grayscale-design.md`)
already specified "compact query-string-style, `r=1&g=0`". The positional
packing was drift from that spec, not a decision. This restores it.

## Scope

**In:** the keyed encoding, backward-compat reads for both legacy shapes, the
`Favorites` fold, sanitization of the two free-text fields, updated tests and
docs.

**Out:** the resolution override and EPUB2 settings themselves — they stay on
the roadmap. This change only unblocks them. No UI change: the Settings page
form and its fields are untouched.

## Design

### A. Encoding

```
retina=1&gray=0&lang=de&fav=lib_abc123
```

Full-word keys, not `r`/`g`/`l`/`f`. The point of keying is self-description;
single letters reintroduce a lookup table, which is positional encoding's
problem in spirit. Costs ~12 characters in a cookie that has no size pressure.

**Every key is always written**, including empty ones (`fav=`). Presence of a
key is load-bearing — see the resurrect guard in section C.

**Escaping:** `Response.Cookies.Append` runs the value through
`Uri.EscapeDataString`, and `RequestCookieCollection` reverses it, so `&` and
`=` round-trip as `%26`/`%3D`. This is the same mechanism the roadmap noted for
JSON braces. **Confirmed by probe** (see the table in section C); a
Response→Request round-trip test locks it in so a framework change can't break
it silently.

**Consequence to be aware of:** anything writing this cookie *outside*
`Response.Cookies.Append` — e.g. the inline JS that writes the `scr` probe —
would have to escape `&`/`=` itself. Nothing does today; `scr` is a separate
cookie and stays one.

### B. Why query-string over JSON

The roadmap suggested JSON. Rejected, in order of weight:

1. **JSON needs a nullable DTO purely to avoid a default-flip bug.** With
   `record Dto(int retina, …)`, an absent key deserializes to `0` and silently
   turns retina *off* — retina defaults **on**. Avoiding that needs `int?` on
   every field plus a null check per field. The keyed form gets absent-vs-zero
   from `StringValues.Count == 0`, with no extra type.
2. **Junk cannot throw.** `QueryHelpers.ParseQuery` returns what it can parse;
   `JsonSerializer.Deserialize` needs `try/catch (JsonException)`. The value is
   entirely client-controlled, so fewer failure modes is worth something.
3. **Escaping inflates JSON.** `{`, `}`, `"`, `:` each become three characters.
   ~24 chars stored versus ~60+.
4. **Every value is a scalar.** JSON buys nesting and types; this is three
   (soon five) bools, strings and ints. Nesting would be speculative.

Accepted cost: `retina=1&gray=0` in a cookie is less instantly recognizable
than JSON and could be misread as a stray query string. Mitigated with a
comment on `Serialize()`.

### C. `DeviceSettings`

```csharp
public sealed record DeviceSettings(bool Retina, bool Grayscale, string Lang)
{
    public const string Cookie = "inkshelf_settings";
    public const string LegacyFavCookie = "inkshelf_fav_library";
    public static readonly DeviceSettings Default = new(true, false, "");

    // An init property, not a 4th positional parameter, so the ten existing
    // `new DeviceSettings(a, b, c)` sites in DeviceSettingsTests keep compiling.
    // Those tests are the regression net for this refactor; rewriting them at the
    // same time as the parse logic would weaken exactly what's checking the work.
    // Record equality still covers it, and `with { Fav = … }` still works.
    public string Fav { get; init; } = "";

    // Sanitized on the way OUT here and on the way IN in Read — the two cookie
    // boundaries. See "Why not sanitize in the record" below.
    public string Serialize() =>
        $"retina={(Retina ? 1 : 0)}&gray={(Grayscale ? 1 : 0)}"
        + $"&lang={SanitizeLang(Lang)}&fav={SanitizeId(Fav)}";
}
```

**Sanitization is a trust boundary, not tidiness.** `/favorite` takes
`libraryId` straight from a form POST. A `libraryId` of `x&retina=0` would
write a cookie that parses back with retina off. It is only the caller's own
cookie, so this is a correctness hole rather than a meaningful security one —
but it does have to be closed somewhere no call site can forget.

**Why not sanitize in the record (corrected).** An earlier draft of this spec
put the sanitizers in property initializers so "no invalid instance can
exist". That does not work in C#, confirmed by probe:

| Form | Result |
|---|---|
| `public string Fav { get; } = SanitizeId(Fav);` | ctor sanitizes, but `with { Fav = … }` does not compile — no `init` accessor |
| `public string Fav { get; init; } = SanitizeId(Fav);` | ctor sanitizes; **`with` silently bypasses it** — initializers only run in the primary constructor |
| `private readonly string _fav; public string Fav { get => _fav; init => _fav = SanitizeId(value); }` | `with` sanitizes, but the compiler warns **CS8907 "Parameter 'Fav' is unread"** and `new DeviceSettings(…, "x")` yields `null` |

The only airtight version means dropping the positional constructor entirely
and converting every construction site to object-initializer syntax. Not worth
it. Instead both cookie boundaries sanitize — `Serialize` on the way out,
`Read` on the way in. The in-memory record can transiently hold an odd `Fav`
after a `with`, which is harmless: the one consumer that builds a URL from it
(`Index`'s `Redirect($"/library/{fav}")`) gets its value from `Read`.

- `SanitizeLang` — unchanged from today: short lowercase code, letters and
  dash, else `""`.
- `SanitizeId` — `[A-Za-z0-9_-]`, max 64 chars, else `""`. Covers ABS's
  `lib_…` ids and uuids. Rejecting `%` also rules out double-decoding
  surprises, since `ParseQuery` URL-decodes a value the cookie layer already
  unescaped once.

**`Read`:**

- No cookie, or empty → `Default`, with `Fav` from the legacy cookie.
- Value contains no `=` → legacy positional shape (`"10"`, `"10de"`); parse as
  today, with `Fav` from the legacy cookie.
- Otherwise `QueryHelpers.ParseQuery`, then per field:
  - `Flag(q, "retina", Default.Retina)` — an absent key lands on the
    *documented default*, not `false`. This is the whole reason keyed encoding
    is worth doing, and the easiest thing to get wrong.
  - `Fav`: **presence, not emptiness.** `q.TryGetValue("fav", out var f)`
    succeeding with `""` means deliberately un-favorited and must NOT fall back
    to the legacy cookie. Falling back on empty-string would resurrect a
    favorite the user just cleared.

```csharp
private static bool Flag(Dictionary<string, StringValues> q, string key, bool fallback) =>
    q.TryGetValue(key, out var v) && v.Count > 0 ? v[0] == "1" : fallback;
```

**`ParseQuery` returns a plain `Dictionary<string, StringValues>`, not an
`IQueryCollection`** — so its indexer *throws* `KeyNotFoundException` on a
missing key rather than yielding `StringValues.Empty`. Every access must go
through `TryGetValue`. Confirmed by probe, along with the rest of the
encoding's behavior:

| Input | Result |
|---|---|
| `retina=1&gray=0&lang=de&fav=` | `fav` key **present**, value `""` — the resurrect guard works |
| `q["nope"]` | throws `KeyNotFoundException` |
| `"totally-not-a-query"` | 1 key, no exception, no `retina` key → every field defaults |
| `""` | 0 keys |
| round-trip via `Response.Cookies.Append` | stored `retina%3D1%26gray%3D0%26lang%3Dde%26fav%3Dlib_x`, read back `retina=1&gray=0&lang=de&fav=lib_x` |

**`Set`** keeps today's cookie flags (`HttpOnly`, `SameSite=Lax`,
`Secure = ForceSecureCookies || Request.IsHttps`, `IsEssential`, `Path=/`,
365 days) and additionally **deletes `inkshelf_fav_library` unconditionally**,
so the legacy cookie cannot linger and shadow later reads. Both this and the
presence check above are needed: the presence check closes the window before
the delete lands, the delete stops the stale value existing at all.

### D. Call sites

All five have `HttpContext` available; verified.

| File | Change |
|---|---|
| `Endpoints/SettingsEndpoints.cs` | fresh `new DeviceSettings(…)` → `Read(ctx.Request) with { Retina = …, Grayscale = …, Lang = … }` |
| `Endpoints/SessionEndpoints.cs` | `/favorite` toggle → `s with { Fav = s.Fav == libraryId ? "" : libraryId }`, replacing Read/Clear/Set |
| `Pages/Index.cshtml.cs` | `Read(Request).Fav`; `is not null` → `!string.IsNullOrEmpty(…)`; stale clear → `Set(Response, s with { Fav = "" })` |
| `Pages/Library.cshtml.cs` | `Read(Request).Fav == Id` |
| `Auth/Favorites.cs` | deleted |

**The `SettingsEndpoints` line is the hazard this fold introduces.** With two
cookies, a settings save and a favorite toggle could not interfere. With one,
constructing a fresh `DeviceSettings` on save wipes the favorite — and the
symptom (a favorite that vanishes when you touch Settings) points nowhere near
the cause. `with` is what makes read-modify-write safe; the rule is that no
call site constructs a fresh instance.

The existing `DeviceSettings.Read` call sites that only read rendering fields
(`ConvertEndpoints`, `ConvertWhy`, `Item`, `Converted`, `Settings`, `Localizer`,
and `Library.cshtml.cs:134`) need no change. `Library.cshtml.cs` appears in the
table above for its *other* line — the `IsFavorite` check at line 48.

### E. Tests

`DeviceSettingsTests`, extended:

- **Response→Request round-trip** — write via `Set`, move the `Set-Cookie`
  value into a fresh context's request cookies, `Read` it back. Verifies the
  `&`/`=` escaping premise rather than assuming it.
- Absent key → documented default, **including retina staying `true`**.
- Junk value → `Default`, no exception.
- Both legacy shapes (`"10"`, `"10de"`) parse, with `Fav` picked up from
  `inkshelf_fav_library`.
- Un-favorite does not resurrect from the legacy cookie.
- `Set` deletes the legacy cookie.
- `fav=x&retina=0` injection attempt via the form value is sanitized away.
- Existing forced/default `Secure` pair stays.

`FavoritesTests.cs` is deleted — its `Secure` pair is already covered by the
`DeviceSettingsTests` equivalents, which now govern the only preferences
cookie.

### F. Docs

- `ARCHITECTURE.md` lines ~30–31 (the `Auth/` map), ~114–116 (the cookie
  `Secure` rule, which names `Favorites`) and ~140–144 ("Two device cookies,
  two purposes") all describe two preferences cookies. All three need
  rewriting: `scr` versus `inkshelf_settings` remains the meaningful split
  (device *truth* versus user *choice*), and the favorite is now a field.
- `ROADMAP.md` — item moves to Done.

## Migration

One format change, not two. Each cookie migration is a chance to reset a real
person's settings on a real device, and this deployment is shared with family
members, so folding `Favorites` in now rather than later halves that exposure.

Existing devices keep retina, grayscale, lang and their favorite. The old
`inkshelf_fav_library` cookie is deleted on the first `Set` after the deploy.
The legacy read paths can be dropped once every device has re-saved, but they
are ~6 lines and cost nothing to keep.

## Risks

| Risk | Mitigation |
|---|---|
| A settings save wipes the favorite | `with` at every write site; no fresh construction. Covered by a test. |
| Un-favorite resurrects from the legacy cookie | Presence check on the `fav` key + unconditional legacy delete in `Set`. Covered by a test. |
| Absent key silently flips retina off | `Flag(…, Default.Retina)`. Covered by a test. |
| `&` in a library id corrupts the cookie | `SanitizeId` in both `Serialize` and `Read`. Covered by a test. |
| An absent-key read throws `KeyNotFoundException` | All dictionary access via `TryGetValue`; the junk-input test exercises it. |
| Escaping does not round-trip as believed | Confirmed by probe before planning; a round-trip test keeps it that way. |
