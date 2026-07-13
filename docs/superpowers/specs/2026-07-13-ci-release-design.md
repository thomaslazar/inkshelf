# CI pipeline + container release — Design

**Date:** 2026-07-13
**Status:** Approved — proceeding to plan/implementation on `feat/ci-release`

## Purpose

Add a GitHub Actions CI pipeline that tests every change and publishes a
multi-arch container image to GitHub Container Registry (GHCR), plus a
human-gated release procedure (mirroring abs-cli's `release` skill) that cuts
versioned releases. This lets the image be deployed and updated as features
land.

## Image & tags

Image: `ghcr.io/thomaslazar/inkshelf`. Package visibility: **public** (no auth
to pull; the image carries no secrets — all config is via env). Platforms:
`linux/amd64,linux/arm64`.

Tag convention:
- Push to `main` → `:main` and `:main-<short-sha>` (bleeding edge; every merge).
- GitHub Release `vX.Y.Z` → `:X.Y.Z` and `:latest` (`:latest` tracks releases,
  not `main`).

## Workflow: `.github/workflows/build.yml`

Triggers: `pull_request` → `main`; `push` → `main`; `release: [created]`.

Permissions: `contents: read`, `packages: write`.

**Job `test`** (runs on all triggers):
- `actions/checkout`, `actions/setup-dotnet` (`10.0.x`).
- `dotnet format Inkshelf.sln --verify-no-changes`.
- `dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj`.

**Job `image`** (`needs: test`; skipped on `pull_request`):
- `docker/setup-qemu-action`, `docker/setup-buildx-action`.
- `docker/login-action` to `ghcr.io` using `${{ github.actor }}` / `GITHUB_TOKEN`.
- `docker/metadata-action` to compute tags:
  - `type=raw,value=main,enable={{is_default_branch}}` (on push to main)
  - `type=sha,prefix=main-,enable={{is_default_branch}}`
  - `type=semver,pattern={{version}}` (on release tag → `X.Y.Z`)
  - `type=raw,value=latest,enable=${{ github.event_name == 'release' }}`
- `docker/build-push-action` with `platforms: linux/amd64,linux/arm64`,
  `push: ${{ github.event_name != 'pull_request' }}`, tags/labels from
  metadata, and GHA layer cache (`cache-from/to: type=gha`).

**One-time manual step (documented, not automated):** after the first publish,
set the GHCR package visibility to **public** in GitHub (packages are created
private by default).

## Release procedure: `.claude/skills/release/SKILL.md`

Mirrors abs-cli's gated `release` skill, adapted from CLI binaries to a
container image. Human gates are never skipped. The skill may commit as part of
its defined steps (overrides CLAUDE.md's ask-before-commit rule, as in abs-cli).

1. **Preflight:** on `main`, clean tree, `git pull`,
   `dotnet format Inkshelf.sln --verify-no-changes`, `dotnet test`. Determine
   version from conventional commits since last tag (any `feat:` → minor; else
   patch). **GATE:** confirm version.
   *(Deferred vs abs-cli: no AOT self-test, no live smoke — no seeded-ABS
   harness yet. Noted as a future preflight gate.)*
2. **Branch + bump:** `release/vX.Y.Z`; set `<Version>` in `Inkshelf.csproj` to
   `X.Y.Z`; verify with `grep`. Commit `chore: bump version to X.Y.Z`.
3. **Changelog:** generate `release-notes.md` (Highlights + grouped
   conventional commits). **GATE:** human reviews. Prepend to `CHANGELOG.md`;
   commit `docs: add vX.Y.Z changelog entry`.
4. **PR → CI:** push branch, `gh pr create`, watch CI to green. **GATE:** human
   merges the PR.
5. **Tag + Release:** `git checkout main && git pull`;
   `gh release create vX.Y.Z --notes-file release-notes.md`. This fires the
   `release` trigger → `image` job publishes `:X.Y.Z` + `:latest`. Remove
   `release-notes.md` (gitignored). **GATE:** confirm release created.
6. **Wait for release CI:** watch the run; confirm the `image` job pushed the
   tags.
7. **Verify:** `docker pull ghcr.io/thomaslazar/inkshelf:X.Y.Z` (or inspect the
   tag via `docker buildx imagetools inspect`) and confirm both arches present.
   **GATE:** human checks the GitHub Release + the GHCR package page.

Dropped from abs-cli (CLI-only): deb packaging, Homebrew tap update, per-binary
artifact attach.

## Supporting changes

- **`Inkshelf.csproj`:** add `<Version>0.1.0</Version>` (seed; project is
  pre-1.0). The `AbsClient` User-Agent becomes `Inkshelf/<version>` read from
  the assembly informational/version at startup instead of the hardcoded
  `Inkshelf/1.0`.
- **`CHANGELOG.md`:** created, seeded with a `v0.1.0` entry summarizing the
  shipped initial release (login, libraries, paginated items, covers).
- **`.editorconfig`:** minimal C# rules so `dotnet format --verify-no-changes`
  is deterministic in CI (avoid churn from default heuristics).
- **`Program.cs`:** generalize the existing reverse-proxy comment (currently
  names a specific proxy) to "some reverse proxies / WAFs reject requests with
  no User-Agent".
- **`README.md`:** add a short "Container image" section — image name, tag
  meanings (`:main`, `:X.Y.Z`, `:latest`), and a `docker pull` / compose
  example.
- **`.gitignore`:** ignore `release-notes.md` (generated, per abs-cli).

## Testing / verification

- Open the implementation PR: the `test` job must pass; the `image` job builds
  (both arches) but does not push on `pull_request`.
- `dotnet format --verify-no-changes` and `dotnet test` pass locally before
  pushing (with the new `.editorconfig` applied).
- After merge to `main`: confirm the `image` job publishes `:main` +
  `:main-<sha>`, then set the package public.
- First real release via the skill validates the `:X.Y.Z` + `:latest` path.

## Out of scope

- Live smoke test against a seeded ABS in CI (needs the deferred seed harness).
- Signing/SBOM/provenance attestation.
