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

## Development environment
- **All .NET development happens inside the devcontainer. No dotnet on the Mac host.**
- Reopen the folder in the container, then work on a feature branch.
- Claude sessions are shared in/out of the container via the project-path
  symlink set up in `.devcontainer/post-create.sh`.

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
