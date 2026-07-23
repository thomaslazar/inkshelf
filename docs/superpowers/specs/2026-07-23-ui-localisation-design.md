# UI localisation — design

## Problem

Inkshelf's own chrome is hardcoded English in the Razor views: nav and
breadcrumbs (Libraries / Settings), row actions (Download / Convert / Mark read),
the pager (Prev / Next / "Page X of Y"), the login and settings forms, and
empty-state text. Non-English readers see an English UI even though the ABS
content itself is in their language. There is no mechanism to translate these
strings.

## Goal

Localise Inkshelf's own UI strings, German first, with English as the default
and the per-string fallback. A translator (or the operator) can add a language
by dropping a JSON file into the container and restarting — no rebuild. Language
is a per-device choice, alongside the existing retina/grayscale settings. ABS
content (titles, descriptions, author names) is untouched — it is already in its
own language.

## Approach (why this, not `.resx` / `.po`)

Settled in brainstorming, backed by measurement:

- The string surface is tiny (~40 short labels), so heavy i18n tooling is
  unjustified.
- `.resx` (`IStringLocalizer`) brings XML editing and a rebuild per language;
  `.po` (OrchardCore) adds a NuGet dependency for ~40 strings. Neither earns its
  keep at this surface.
- Chosen: a **file-backed JSON catalog** keyed by the **source English string**,
  selected by a per-device cookie. No `RequestLocalization` middleware, no new
  NuGet dependency. This is the ponytail option — the minimum that satisfies the
  requirement.

## Scope decisions (settled in brainstorming)

- **English is the code.** The source English string *is* the lookup key. There
  is no `en.json`; a miss in any catalog falls back to the key (English).
- **Source-string-as-key**, gettext-style: no separate key catalog to maintain,
  and English needs no file.
- **Load from the filesystem at startup**, not embedded resources — so a file
  dropped into the container (or a mounted volume) is picked up on restart with
  no rebuild. **No hot reload.**
- **One dependency-free path.** Uses `System.Text.Json` (already in the runtime)
  and the already-registered `IHttpContextAccessor`.
- **Out of scope (YAGNI):** plural/gender forms, translating ABS content, hot
  reload, and any per-language code.

## Components

### 1. Catalog files & format

- One file per language, `<lang>.json`, e.g. `de.json`. `<lang>` is a short code
  (`de`, `pt-BR`); it is a bare identifier, never resolved to a `CultureInfo`.
- Flat JSON object: source English string → translation.
  ```json
  {
    "$name": "Deutsch",
    "Download": "Herunterladen",
    "Mark read": "Gelesen",
    "Libraries": "Bibliotheken",
    "Page {0} of {1}": "Seite {0} von {1}"
  }
  ```
- Reserved key **`$name`** (optional): the language's own display name for the
  Settings dropdown. Absent → the dropdown shows the bare code. This lets a
  dropped-in file name itself with no code change.
- Missing translation, empty string, or missing file → the English key is used.

### 2. Location & shipping

- Directory configured by **`LOCALES_PATH`** (new `AbsOptions` field, mirroring
  `CachePath` / `DataProtectionKeysPath`), default `<ContentRoot>/locales`.
- The repo ships `src/Inkshelf/locales/de.json`; the `.csproj` copies
  `locales/**` to output (`<Content>` with `CopyToOutputDirectory`).
- To add/replace a language in a running deployment: mount or copy a `<lang>.json`
  into `LOCALES_PATH` and restart. A mounted volume over the default dir replaces
  the shipped set; extra files sit alongside.

### 3. Loading

- At startup, scan `LOCALES_PATH` for `*.json`, parse each into a
  `Dictionary<string,string>` keyed by filename stem (`de.json` → `de`).
- Held in a **singleton `LocalizationCatalog`** — immutable after load.
- **Resilience:** a malformed or unreadable file is logged (warning) and skipped;
  it must never crash the sidecar. A missing/empty dir → zero catalogs → the app
  runs fully in English.

### 4. Localizer + call sites

- **`Localizer`** (singleton, injected into every view via `_ViewImports.cshtml`):
  resolves the request's language (see *Language resolution & fallback*), then
  looks the key up in that catalog. Uses the already-registered
  `IHttpContextAccessor`.
- Indexer call site, mirroring the familiar `IStringLocalizer` ergonomics:
  ```cshtml
  @L["Mark read"]
  @L["Page {0} of {1}", page, total]     @* string.Format on the resolved template *@
  ```
- `this[string key]` → translation or `key`. `this[string key, params object[] args]`
  → `string.Format(resolved, args)`. Reading the language inside the Localizer
  (not from a PageModel field) is deliberate: the strings live in `_Layout` and
  shared partials (`_ItemRow`, `_Pager`, `_ConvertAction`) that have no shared
  model, so threading a `Lang` field through every model would be boilerplate.

### 5. Language selection — `DeviceSettings`

Extend the existing record and its cookie, preserving backward compatibility:

- `record DeviceSettings(bool Retina, bool Grayscale, string Lang)`. `Lang == ""`
  means **no explicit choice yet** (→ `Accept-Language` resolution); a non-empty
  code (including `"en"`) is an **explicit** choice that overrides the header.
  `Default = new(true, false, "")`.
- `Serialize()` → `$"{r}{g}{Lang}"`. With `Lang == ""` this is the current
  2-char value, so **existing cookies keep working** (parsed as English).
- `Read()` → relax the strict length-2 check to length ≥ 2; `[0]/[1]` are the
  0/1 flags, `Lang = v[2..]` sanitised to `[a-z-]` (lower-case letters/dash,
  capped ~5 chars), else `""`. An unknown code is accepted syntactically and
  simply misses in lookup → English. No catalog dependency in `Read`.
- Only construction sites to update: `SettingsEndpoints` (reads `form["lang"]`)
  and `Default`.

### 6. Settings page

- Add a language `<select name="lang">` to the settings form: an `English`
  option (value `en`) plus one option per loaded catalog language, labelled by
  its `$name` (fallback: the code), with the current selection marked. Saving
  always records an explicit code, so after a first save the header no longer
  applies — an explicit English pick sticks even on a German browser.
- `SettingsEndpoints` reads `form["lang"]` into the new `DeviceSettings` field.
- `SettingsModel` exposes the available languages (from the catalog) for the view.

### 7. `<html lang>` and the convert-status JS labels

- `_Layout.cshtml`: set `<html lang="@(currentLang or "en")">` to the selected
  language.
- **Convert-status labels are set in client-side JS** in `_Layout` (`Converting…`,
  `EPUB ↓`, `Convert`, `Convert (retry)`). To keep them consistent with the
  server-rendered text, `_Layout` emits the four localised labels once as a small
  inline JSON object and the existing convert script reads them instead of the
  hardcoded literals. No new script behaviour, just sourced strings.

## String inventory (server-rendered)

To be wrapped in `@L[...]`. This is the actual bulk of the work and is
mechanism-independent.

- **`_Layout`**: JS convert labels (via §7). (`<title>Inkshelf` is the brand —
  left as-is.)
- **`_Pager`**: `Prev`, `Next`, `Page {0} of {1}`.
- **`_ItemRow`**: `Download`, `Mark read`, `Read`, titles `Mark as read` /
  `Mark as unread`, `(untitled)`.
- **`_ConvertAction`**: `EPUB ✓`, `Converting…`, `Convert`, `Convert (retry)`,
  titles `Already converted — downloads right away`, `Regenerate`.
- **`Login`**: `Username`, `Password`, `Log in`.
- **`Settings`**: `Libraries`, `Settings`, `These settings apply to this device /
  browser only.`, `Detected screen: {0}`, the retina/grayscale label text,
  `Save`, plus the new language-picker label.
- **`Index` / `Library` / `Item` / `Converted`**: breadcrumbs, headings, facet
  labels (`Books`, `Authors`, `Series`, `Files`), `Filtered by {0}`, `Search`,
  sort labels, `Log out`, and empty-state text. The implementation plan
  enumerates every literal in these views exhaustively.

Server-produced error strings (e.g. `LoginModel.Error`) are in scope where
Inkshelf authors them; raw ABS error text passed through is not.

## Language resolution & fallback

Language is resolved per request, in order:

1. **Explicit choice** — `DeviceSettings.Lang` is a non-empty code → use it.
   `"en"` is a valid explicit choice (English = the keys) and overrides the
   browser header.
2. **No choice yet** — `Lang` empty (never chosen, or a legacy 2-char cookie) →
   the best quality-ranked `Accept-Language` entry (`Request.GetTypedHeaders()
   .AcceptLanguage`) that matches a loaded catalog code.
3. **Nothing matches** → English (the keys).

This is a resolution *fallback*, not cookie-seeding: a GET never writes the
cookie — it is written only when the user saves Settings.

Within the chosen language, a missing/empty key falls back to the English key
verbatim; there is never a placeholder, so the worst case is an English word in
an otherwise-translated UI. Old e-reader browsers that send a sparse or absent
`Accept-Language` simply land on English until the reader picks a language in
Settings.

## Testing (runnable checks to leave behind)

- `LocalizationCatalog`: hit returns the translation; miss returns the key;
  malformed file is skipped without throwing; unknown language → English.
- `DeviceSettings`: round-trips `Lang`; a legacy 2-char cookie parses as unset
  (`Lang == ""`); junk `Lang` sanitises to `""`.
- Language resolution: an explicit `Lang` wins over `Accept-Language`; an empty
  `Lang` picks the best `Accept-Language` match among loaded catalogs; no match
  (or sparse/absent header) → English.
- `Localizer`: `["Page {0} of {1}", 2, 5]` formats correctly against a loaded
  catalog and against a miss (English template).

## Files touched

- New: `src/Inkshelf/Localization/LocalizationCatalog.cs`,
  `src/Inkshelf/Localization/Localizer.cs`, `src/Inkshelf/locales/de.json`.
- Edit: `AbsOptions.cs` (+`LocalesPath`), `Program.cs` (register catalog +
  localizer, wire `LOCALES_PATH`), `Auth/DeviceSettings.cs` (+`Lang`),
  `Endpoints/SettingsEndpoints.cs`, `Pages/Settings.cshtml(.cs)`,
  `Pages/_ViewImports.cshtml` (`@inject`), `Inkshelf.csproj` (copy `locales/**`),
  and every view holding chrome strings (per the inventory).
