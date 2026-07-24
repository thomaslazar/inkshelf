# uicheck — headless-browser UI pass

A small dev-only Playwright (.NET) harness that starts Inkshelf, drives it in a
headless browser, captures full-page screenshots, and asserts key strings — in
English and German — across both the no-auth pages (`/login`, `/settings`) and,
against the seeded local ABS, the authenticated pages (index, a library listing,
item detail, converted) plus a live Convert-button click.

## Run

```bash
tools/uicheck/run.sh
```

The script brings up the seeded local ABS (`docker/docker-compose.yml`, port
13379, `root`/`root`), seeds it on first use, starts Inkshelf against it (with a
throwaway cache), then runs the checks. First run also downloads the headless
Chromium into `~/.cache/ms-playwright` (one-time, no root). Screenshots land in
`tools/uicheck/shots/` (git-ignored); the process exits non-zero if any
assertion fails.

If Chromium fails to launch for missing system libs (it works out-of-the-box on
the devcontainer's `dotnet:10` image), run once with root:

```bash
sudo pwsh tools/uicheck/bin/Debug/net10.0/playwright.ps1 install-deps chromium
```

## What it does and does not cover

- **Catches:** gross breakage / 500s, layout overflow, untranslated or
  English-leak strings, and JS-updated label bugs (the Convert click asserts the
  label flips to German with no leaked HTML entity) — the class of issue a normal
  browser shows.
- **Does not cover:** the old e-ink e-reader engine (no `object-fit`, no flex
  `gap`), so a device pass stays mandatory.

## Extending

- No-auth pages: add `Check(...)` calls near the top of `Program.cs`.
- Authenticated pages: extend the `UICHECK_AUTHED` block (login is `root`/`root`
  against the seeded ABS). The German context uses cookie `inkshelf_settings` =
  `<retina><grayscale><lang>` (e.g. `10de`). To exercise the item-detail term
  labels, `docker/seed.sh` gives one epub genres/tags/narrators.

Not part of `Inkshelf.sln` — it never affects the app build or `dotnet test`.
