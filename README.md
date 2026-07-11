# Inkshelf

A thin, server-rendered web client for the [Audiobookshelf](https://www.audiobookshelf.org/)
(ABS) API, built for e-reader browsers.

The ABS web UI is too JavaScript-heavy for e-ink devices like the Tolino —
after login, nothing really happens. Inkshelf renders plain HTML on the server
with near-zero JavaScript, so it works on those browsers. It runs as a sidecar
container next to your ABS instance and talks to the ABS API on your behalf.

## Status

Early. v1 scope: log in with your ABS credentials, pick a library, browse its
items (paginated, with covers). See `docs/superpowers/specs/` for the design.

## Stack

- ASP.NET Core Razor Pages, .NET 10 (no AOT)
- Stateless: the ABS token is kept in an encrypted cookie
- No database, no client-side framework

## Development

All .NET development happens inside the devcontainer — no dotnet on the host.
Open the folder in VS Code and "Reopen in Container". Configuration and API
reference live in `CLAUDE.md`.

### Configuration

| Env var   | Required | Description                        |
|-----------|----------|------------------------------------|
| `ABS_URL` | yes      | Base URL of the ABS server         |
