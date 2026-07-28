# CLAUDE.md

## Main rule
be brief

## What this is
Inkshelf — thin, server-rendered web client for the Audiobookshelf (ABS) API.
Near-zero JavaScript so old e-reader browsers work. Runs as a sidecar
container next to ABS. See `docs/superpowers/specs/` for the design.

- ASP.NET Core Razor Pages, .NET 10, **no AOT**.
- Plain HTML: `<form>` and `<a>` only. No client JS unless unavoidable.
- Stateless: ABS JWT lives in an encrypted cookie (Data Protection).

**Read `docs/ARCHITECTURE.md` before adding features** — it maps the structure and
the load-bearing conventions (some look like cleanup targets but are deliberate).

### What belongs in ARCHITECTURE.md — and what doesn't

It is a **map, not a diary**. Most features should change it **not at all**: if a
change fits the existing structure, there is nothing new to describe.

The test for any line: **would this still be true if we rewrote the
implementation but kept the design?** If no, it's a code comment, not
architecture.

Belongs:
- The bird's-eye view — what shapes the design, not what it does.
- The code map: where things live, one line each.
- **Invariants, especially the ones phrased as an absence** — "never attach the
  auth handler to `AbsAuthClient`", "don't unify the metadata DTOs", "don't
  restore Server GC". These are the whole point: rules whose violation causes a
  bug that isn't obvious from reading the code.
- Deliberate exceptions and accepted risks.

Does NOT belong:
- **A per-feature entry.** Shipped work goes to `docs/ROADMAP.md`'s `## Done` and
  `CHANGELOG.md`. A commit message of the form `docs: record <feature>` touching
  this file is the smell.
- How a module works step by step — that's an inline comment next to the code.
- Endpoint/query-parameter reference, config tables, response shapes. `README.md`
  owns operator-facing config; duplicating it here has already shipped a wrong
  default.
- Anything that changes when the implementation is refactored.

If it grows every time a feature lands, it's being used as a changelog. Prune it
rather than appending.

## Development environment
- **All .NET development happens inside the devcontainer. No dotnet on the Mac host.**
- Reopen the folder in the container, then work on a feature branch.
- Claude sessions are shared in/out of the container via the project-path
  symlink set up in `.devcontainer/post-create.sh`.

## Testing — use the local stack, never ask for credentials

Everything needed to exercise this app end to end is in the repo. There is no
situation where a verification pass is "blocked on credentials".

- **Unit/integration:** `dotnet test` from the repo root.
- **UI / browser pass:** `tools/uicheck/run.sh` — headless Playwright Chromium.
  It brings up the seeded ABS, starts Inkshelf, drives both anonymous and
  logged-in pages in English and German, asserts key strings, and writes
  full-page screenshots to `tools/uicheck/shots/` (read them; don't just trust
  the exit code). Extend it when a feature adds or changes a page.
- **Seeded ABS backend:** `docker/docker-compose.yml` (project `inkshelf-it`,
  port **13379**, `root`/`root`), populated by `docker/seed.sh` with ~22 items
  including deliberately broken fixtures (corrupt archive, bad page, oversized)
  for the failure paths. `run.sh` brings it up and seeds it automatically.
- **HTTP smoke:** `docker/smoke-test.sh` drives a running Inkshelf against it.
- **Manual run:** `ABS_URL=http://host.docker.internal:13379 dotnet run
  --project src/Inkshelf --no-launch-profile --urls http://localhost:5099`.
  Use port **5099** — `launchSettings.json`'s 5197 is not the bookmarked one.

The headless pass does **not** reproduce the old e-ink engine (no `object-fit`,
no flex `gap`), so a real e-reader pass by the user stays mandatory for
engine-specific rendering. Do the headless pass first, then hand over.

## Git conventions
- **Always ask before committing.** Do not commit automatically.
- **Conventional Commits**: `type: subject` — `feat`, `fix`, `docs`, `test`,
  `ci`, `refactor`, `chore`.
- Subject: imperative, lowercase, no period, max ~72 chars.
- Body (optional): explain *why*, not *what*. Wrap at 72 chars.
- Do NOT add `Co-Authored-By:` or "Generated with Claude Code" lines.
- After `gh pr create`, present the PR URL as a clickable link.

## ABS source reference
- The ABS server source is the authoritative reference for API behavior,
  request/response shapes, routing, and permission checks.
  `https://api.audiobookshelf.org` is stale — do not trust it.
- Expected location: `temp/audiobookshelf/` (gitignored). The devcontainer
  clones it on create. If missing:
  ```bash
  git clone --depth 1 --branch v2.35.1 \
    https://github.com/advplyr/audiobookshelf.git temp/audiobookshelf
  ```
