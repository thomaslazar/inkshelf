# Contributing to Inkshelf

Thanks for your interest in improving Inkshelf! This guide covers the local
setup, the build/test loop, project conventions, and how to get a change merged.

## Development environment

**All .NET work happens inside the devcontainer** — there is no need for a .NET
SDK on your host.

1. Install [VS Code](https://code.visualstudio.com/) + the Dev Containers
   extension, and Docker.
2. Open the repository folder and choose **"Reopen in Container"**.
3. On first create, the container restores dependencies and clones the
   Audiobookshelf server source into `temp/audiobookshelf/` (see below).

The app targets **.NET 10** and is an ASP.NET Core Razor Pages project.

## Build, test, format

Run these from the repository root, inside the container:

```bash
dotnet build src/Inkshelf                 # build the app
dotnet test                               # run the test suite
dotnet format Inkshelf.sln                # apply formatting
dotnet format Inkshelf.sln --verify-no-changes   # check formatting (what CI runs)
```

Run the app locally against your ABS server:

```bash
ABS_URL=http://<abs-host>:13378 dotnet run --project src/Inkshelf
```

See the [README](README.md#configuration) for all configuration variables.

## Project layout

Before making a substantial change, read
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — it maps the overall structure
(`Endpoints/`, `Abs/`, `Convert/`, `Pages/` + `Pages/Support/`, `Auth/`) and the
conventions behind it.

Roadmap items and ideas live in [`docs/ROADMAP.md`](docs/ROADMAP.md).

## Audiobookshelf API reference

The ABS **server source** is the authoritative reference for API behavior,
request/response shapes, routing, and permission checks — the public API docs at
`api.audiobookshelf.org` are stale, so don't trust them. The devcontainer clones
the server source to `temp/audiobookshelf/` (gitignored) on create. If it's
missing:

```bash
git clone --depth 1 --branch v2.35.1 \
  https://github.com/advplyr/audiobookshelf.git temp/audiobookshelf
```

## Local ABS test harness

`docker/` contains a throwaway Audiobookshelf instance for integration testing:

- `docker/docker-compose.yml` — starts a local ABS server.
- `docker/seed.sh` — populates it with fixtures (e.g. a library large enough to
  exercise pagination).
- `docker/smoke-test.sh` — a basic end-to-end check.

Use it when a change touches ABS interaction and you want to exercise it against a
real (if minimal) server rather than only unit tests.

## Coding conventions

- **Near-zero client JavaScript.** The target is old, low-powered e-ink browser
  engines. Build UI from plain `<form>`/`<a>` HTML; add client JS only when truly
  unavoidable (there are two tiny, deliberate inline scripts today).
- **Defensive CSS.** Assume an old rendering engine: no `object-fit`, no flex
  `gap`, and similar modern-only features. Prefer widely-supported properties.
- **Localise user-facing strings.** Wrap new chrome text in the injected
  localizer (`@L["…"]`) instead of hardcoding English — see
  [Localisation](#localisation).
- **Tests stay green.** `dotnet test` must pass, and `dotnet format --verify-no-changes`
  must be clean, before you open a PR. New behavior needs tests.
- **Respect the non-goals** documented in `docs/ARCHITECTURE.md`.

## Localisation

Inkshelf's own UI chrome (nav, buttons, breadcrumbs, empty states) is
localisable. Audiobookshelf content — titles, author names, descriptions — is
left in whatever language ABS holds. **English is the source language: the
English string is itself the lookup key**, so there is no English translation
file to keep in sync. Full design in
[`docs/superpowers/specs/2026-07-23-ui-localisation-design.md`](docs/superpowers/specs/2026-07-23-ui-localisation-design.md).

Translations live in `src/Inkshelf/locales/<lang>.json` — one flat JSON file per
language, mapping the English source string to its translation:

```json
{
  "$name": "Deutsch",
  "Download": "Herunterladen",
  "Mark read": "Gelesen",
  "Page {0} of {1}": "Seite {0} von {1}"
}
```

- `$name` (optional) is the language's own display name shown in the Settings
  picker; omit it and the picker shows the bare code.
- Keep `{0}`, `{1}` placeholders — you may reorder them for grammar.
- Any string you leave out falls back to English, so a partial translation is
  fine.

**Add or update a language:**

1. Create or edit `src/Inkshelf/locales/<lang>.json` (e.g. `de.json`, `fr.json`).
2. Run the app (`ABS_URL=… dotnet run --project src/Inkshelf`), open **Settings**,
   and pick the language — or set your browser's preferred language, since a
   first visit with no saved choice honours `Accept-Language`.

No rebuild is needed in a deployed container: drop a `<lang>.json` into the
directory named by `LOCALES_PATH` (default `<content-root>/locales`) and restart.

**Adding a new UI string in code:** write the English text through the injected
localizer (`@L["New label"]`); the English string becomes the key automatically.
Add its translation to each `<lang>.json` — until you do, that language shows the
English text.

## Commits

We use [Conventional Commits](https://www.conventionalcommits.org/):

```
type: subject
```

- **type**: one of `feat`, `fix`, `docs`, `test`, `ci`, `refactor`, `chore`.
- **subject**: imperative, lowercase, no trailing period, ~72 chars max.
- **body** (optional): explain *why*, not *what*; wrap at 72 columns.

## Pull requests

1. Branch off `main` (e.g. `feat/…`, `fix/…`).
2. Make your change with tests; keep commits conventional.
3. Ensure `dotnet test` and `dotnet format --verify-no-changes` pass.
4. Open a PR against `main`. CI (build + tests) runs on the PR.
5. A maintainer reviews and merges.

## Releases

Releases are cut by maintainers: the version is bumped, the changelog updated, and
a GitHub Release is created. Publishing a release triggers CI to build and push the
multi-arch container image (`:X.Y.Z` and `:latest`) to GitHub Container Registry.
