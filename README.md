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

| Env var                 | Required | Description                        |
|-------------------------|----------|-------------------------------------|
| `ABS_URL`               | yes      | Base URL of the ABS server         |
| `DataProtectionKeysPath`| no       | Filesystem path for persisted Data Protection keys. Default `<ContentRoot>/.keys`. Mount a volume here to keep users logged in across restarts. |

## Running

Locally, inside the devcontainer:

```bash
ABS_URL=http://<abs-host>:13378 dotnet run --project src/Inkshelf
```

As a sidecar container next to ABS, using the example compose file (copy it
and adjust `ABS_URL` to point at your ABS service first):

```bash
docker compose -f docker-compose.example.yml up
```

This builds the image from the repo `Dockerfile`, exposes Inkshelf on port
8080, and persists Data Protection keys (so login cookies survive restarts)
in a named volume mounted at `/keys`.

## Container image

Instead of building locally, you can pull a prebuilt multi-arch image
(`linux/amd64` and `linux/arm64`) from GitHub Container Registry:

    ghcr.io/thomaslazar/inkshelf

Tags:

| Tag           | Meaning                                             |
|---------------|-----------------------------------------------------|
| `:main`       | Latest build from the `main` branch (moves on every merge) |
| `:main-<sha>` | A specific `main` build, pinnable                   |
| `:X.Y.Z`      | A tagged release                                    |
| `:latest`     | The most recent tagged release                      |

Run it directly (or point the example compose file at this image instead of
`build: .`):

```bash
docker run -e ABS_URL=https://your-abs.example -p 8080:8080 \
  ghcr.io/thomaslazar/inkshelf:latest
```
