# uicheck — headless-browser UI pass

A small dev-only Playwright (.NET) harness that starts Inkshelf, drives it in a
headless browser, captures full-page screenshots, and asserts key strings on the
pages that render without an ABS login (`/login`, `/settings`) in English and
German.

## Run

```bash
tools/uicheck/run.sh
```

First run downloads the headless Chromium into `~/.cache/ms-playwright`
(one-time, no root). Screenshots land in `tools/uicheck/shots/` (git-ignored);
the process exits non-zero if any assertion fails.

If Chromium fails to launch for missing system libs (it works out-of-the-box on
the devcontainer's `dotnet:10` image), run once with root:

```bash
sudo pwsh tools/uicheck/bin/Debug/net10.0/playwright.ps1 install-deps chromium
```

## What it does and does not cover

- **Catches:** gross breakage / 500s, layout overflow, and untranslated or
  English-leak strings — the class of issue a normal browser shows.
- **Does not cover:** the old e-ink e-reader engine (no `object-fit`, no flex
  `gap`), so a device pass stays mandatory; and authenticated pages
  (Library/Item/Converted), which need a real ABS session.

## Extending

Add `Check(...)` calls in `Program.cs` as features land — a new page, a new
language cookie (`inkshelf_settings` = `<retina><grayscale><lang>`, e.g. `10de`),
or new expected/forbidden strings.

Not part of `Inkshelf.sln` — it never affects the app build or `dotnet test`.
