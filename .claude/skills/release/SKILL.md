---
name: release
description: Cut a new Inkshelf release with human review gates — bumps version, writes the changelog, opens a PR for CI, then tags and lets CI publish the container image. Use when the user asks to cut/publish a release.
disable-model-invocation: true
allowed-tools:
  - Bash
  - Read
  - Write
  - Glob
  - Grep
  - Edit
  - AskUserQuestion
---

# Release Inkshelf

Multi-step release workflow with human gates. You drive each step and pause at
every GATE for human approval before proceeding. Never skip a gate.

Inkshelf ships as a container image published to
`ghcr.io/thomaslazar/inkshelf`. Cutting a release means: bump the version,
record the changelog, validate on CI via a PR, then create a GitHub Release —
which triggers CI to build and push the `:X.Y.Z` and `:latest` image tags.

## Step 1: Preflight

```bash
# Must be on main, clean tree, up to date
BRANCH=$(git branch --show-current)
[ "$BRANCH" = "main" ] || { echo "ERROR: must be on main, on $BRANCH"; exit 1; }
git diff --quiet && git diff --cached --quiet || { echo "ERROR: working tree not clean"; git status --short; exit 1; }
git pull

# Gates the release must satisfy
dotnet format Inkshelf.sln --verify-no-changes
dotnet test tests/Inkshelf.Tests/Inkshelf.Tests.csproj
```

If any check fails, stop and report. Do not proceed.

(Note: there is no live smoke test against a seeded ABS yet — that is a future
preflight gate once the seed harness exists.)

Determine the version:
- Last tag: `git describe --tags --abbrev=0 2>/dev/null || echo "none"`
- Read commits since the last tag.
- Propose the next version from conventional commits:
  - any `feat:` since last tag → bump MINOR
  - only `fix:`/`docs:`/`test:`/`ci:`/`chore:`/`refactor:` → bump PATCH
  - project is pre-1.0; keep the leading `0.` until a deliberate 1.0.

**GATE: show the human the proposed version + commit summary; wait for confirmation.**

## Step 2: Create release branch and bump version

```bash
VERSION="v{version}"        # e.g. v0.2.0
VERSION_NUM="${VERSION#v}"   # 0.2.0 — the csproj wants no leading v
git checkout -b "release/${VERSION}"
```

Use `Edit` to set `<Version>OLD</Version>` → `<Version>${VERSION_NUM}</Version>`
in `src/Inkshelf/Inkshelf.csproj`. Confirm:

```bash
grep "<Version>" src/Inkshelf/Inkshelf.csproj   # must show ${VERSION_NUM}
dotnet build src/Inkshelf                        # must build clean
```

The app's ABS `User-Agent` is derived from this version (`Inkshelf/${VERSION_NUM}`).

Commit:

```bash
git add src/Inkshelf/Inkshelf.csproj
git commit -m "chore: bump version to ${VERSION_NUM}"
```

## Step 3: Changelog

Generate `release-notes.md` with a **Highlights** section (3–5 plain-language
bullets) and grouped conventional commits since the last tag:

```bash
LAST_TAG=$(git describe --tags --abbrev=0 2>/dev/null || echo "")
RANGE=$([ -n "$LAST_TAG" ] && echo "${LAST_TAG}..HEAD" || echo "HEAD")
git log --oneline $RANGE --pretty="- %s" | grep -E "^- (feat|fix|refactor|docs|test|ci|chore):" | sort
```

Format:

```markdown
## v{version} — YYYY-MM-DD

### Highlights
- ...

### Features
- feat: ...

### Fixes
- fix: ...
```

**GATE: open `release-notes.md` for the human to review and approve.** Make any
requested edits and show again.

Prepend the notes to `CHANGELOG.md` (keep its top header block intact) and commit:

```bash
git add CHANGELOG.md
git commit -m "docs: add ${VERSION} changelog entry"
```

## Step 4: Open PR for CI validation

```bash
git push -u origin "release/${VERSION}"
gh pr create --title "release: ${VERSION}" \
  --body "Release ${VERSION}. See CHANGELOG.md." --base main
```

Wait for CI, then report structured results (do not paste streaming output):

```bash
for i in $(seq 1 10); do
  RUN_ID=$(gh run list --branch "release/${VERSION}" --limit 1 --json databaseId -q '.[0].databaseId')
  [ -n "$RUN_ID" ] && break; sleep 3
done
gh run watch "$RUN_ID" --exit-status
gh run view "$RUN_ID" --json jobs --jq '.jobs[] | "\(.name)\t\(.conclusion)"'
```

If CI fails: `gh run view "$RUN_ID" --log-failed`, fix on the branch, push, re-check.

**GATE: tell the human CI passed; show the PR URL; ask them to review and merge.**
Wait for confirmation the merge is done.

## Step 5: Tag and create the GitHub Release

```bash
git checkout main && git pull
gh release create "${VERSION}" --title "${VERSION}" --notes-file release-notes.md
rm release-notes.md
```

Creating the release fires the `release` CI trigger, which builds the
multi-arch image and pushes `:${VERSION_NUM}` + `:latest`.

**GATE: confirm the release was created; show the URL.**

## Step 6: Wait for release CI

```bash
for i in $(seq 1 10); do
  RUN_ID=$(gh run list --limit 5 --json databaseId,event -q '[.[] | select(.event=="release")] | .[0].databaseId')
  [ -n "$RUN_ID" ] && break; sleep 3
done
gh run watch "$RUN_ID" --exit-status
gh run view "$RUN_ID" --json jobs --jq '.jobs[] | "\(.name)\t\(.conclusion)"'
```

If CI fails, show failure details and stop.

## Step 7: Verify

```bash
docker buildx imagetools inspect ghcr.io/thomaslazar/inkshelf:${VERSION_NUM}
```

Confirm both `linux/amd64` and `linux/arm64` are present, and that `:latest`
resolves to the same digest.

**GATE: ask the human to check the GitHub Release page and the GHCR package
page** (release notes render correctly; `:${VERSION_NUM}` and `:latest` tags
present).

## Step 8: Done

Report: release URL, version, image tags published, and that the changelog is
committed to `main`.

## Rules

- NEVER skip a human gate.
- NEVER proceed past a failed check.
- If anything unexpected happens, stop and ask.
- Clean up `release-notes.md` at the end (it is gitignored, never committed).
- `CHANGELOG.md` is the source of truth; the GitHub Release notes mirror it.
- No private/self-hosted deployment details anywhere.
- This skill may commit as part of its defined steps (overrides the
  ask-before-commit rule in CLAUDE.md).
