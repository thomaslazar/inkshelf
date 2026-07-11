# Inkshelf v1 — Design

**Date:** 2026-07-11
**Status:** Approved (design), pending implementation

## Purpose

A thin, server-rendered web client for the Audiobookshelf (ABS) API with
near-zero JavaScript, so e-reader browsers (Tolino) work. The ABS web UI is too
JS-heavy for e-ink devices — after login, nothing happens. Inkshelf runs as a
sidecar container next to ABS and talks to the ABS API on the user's behalf.

**v1 scope:** log in with ABS credentials → pick a library → browse its items,
paginated, with cover thumbnails.

## Architecture

- **ASP.NET Core Razor Pages**, .NET 10, **no AOT**. Server renders full HTML
  each request. Plain `<form>` and `<a>` only. No client JS.
- **Stateless.** The ABS JWT (access + refresh) lives in an encrypted cookie
  via ASP.NET Data Protection. No database, no server-side session store.
- One typed `HttpClient` (`AbsClient`) wrapping the ABS endpoints we use.
- Base URL from env `ABS_URL` (required; app fails fast at startup if unset).

### Data Protection keys

Persisted to a mounted volume (e.g. `/keys`) via `PersistKeysToFileSystem`, so
cookies survive container restarts/redeploys. Requires one volume mount in the
compose file.

## Routes

| Route            | Method | Behavior                                                          |
|------------------|--------|-------------------------------------------------------------------|
| `/login`         | GET    | username/password `<form>`                                        |
| `/login`         | POST   | ABS `login`, encrypt JWT into cookie, redirect `/`                |
| `/`              | GET    | list libraries as `<a>` links; redirect `/login` if no cookie     |
| `/library/{id}`  | GET    | `?page=` — paginated item list; Prev/Next `<a>`; cover `<img>`    |
| `/cover/{id}`    | GET    | `?w=` — fetch ABS cover with token, stream bytes back             |
| `/logout`        | POST   | clear cookie → `/login`                                           |

## ABS client surface (`AbsClient`)

- `LoginAsync(user, pass)` → `{ accessToken, refreshToken }`
- `RefreshAsync(refreshToken)` → new tokens
- `GetLibrariesAsync(token)` → `[{ id, name, mediaType }]`
- `GetItemsAsync(token, libId, page, limit)` → `{ results[], total, page, limit }`
- `GetCoverAsync(token, itemId, width)` → stream + content-type

### ABS endpoints used

- `POST login` → `user.accessToken`, `user.refreshToken`
- `POST auth/refresh`
- `GET api/libraries`
- `GET api/libraries/{id}/items?limit=&page=`
- `GET api/items/{id}/cover?width=` (ABS resizes server-side)

Authoritative reference is the ABS server source at `temp/audiobookshelf/`, not
`api.audiobookshelf.org` (stale).

## Covers

Delivered **proxied through Inkshelf**: `<img src="/cover/{id}?w=120">` hits
Inkshelf, which fetches from ABS (`?width=120`, ABS resizes) with the token and
streams the bytes back. Works even when ABS is internal-only behind the sidecar.

## Auth / error handling

- No valid cookie → redirect `/login`.
- On ABS `401` mid-session → POST `auth/refresh` once, update cookie, retry the
  request. If refresh also fails → clear cookie, redirect `/login`.
- Login failure → re-render `/login` with an error message.

## Defaults

- Page size: **24** items/page.
- Cover width: **120px** requested from ABS.
- Multi-user: each visitor logs in with their own ABS credentials.

## Out of scope for v1

- Item detail pages, reading/streaming, download.
- Search, filtering, sorting.
- Series/authors/collections browsing.

## Project layout

```
inkshelf/
  .devcontainer/       devcontainer.json, Dockerfile, post-create.sh, statusline.sh
  .claude/             settings.json (settings.local.json gitignored)
  .gitignore
  CLAUDE.md
  README.md
  Inkshelf.sln
  src/Inkshelf/        Inkshelf.csproj, Program.cs, Pages/, Abs/ (client + models), appsettings.json
  temp/audiobookshelf/ gitignored ABS source checkout (API reference)
```

## Development workflow

- `main`: scaffolding only (devcontainer, docs, CLAUDE.md, this spec). No C#.
- All .NET development happens **inside the devcontainer** — no dotnet on the
  Mac host. Implementation lands on a dedicated feature branch created inside
  the container.
