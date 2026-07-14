<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/Inkshelf/wwwroot/img/logo-inverted.png">
    <img src="src/Inkshelf/wwwroot/img/logo-black.png" alt="Inkshelf" width="360">
  </picture>
</p>

<p align="center">
  A lightweight, server-rendered web client for <a href="https://www.audiobookshelf.org/">Audiobookshelf</a>, built for e-reader browsers.
</p>

<p align="center">
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue.svg"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512BD4.svg">
  <img alt="Container image" src="https://img.shields.io/badge/ghcr.io-thomaslazar%2Finkshelf-2496ED.svg">
</p>

---

Audiobookshelf's own web UI leans heavily on JavaScript, which older e-ink
reader browsers can't run — you log in and then nothing really happens. **Inkshelf**
is a thin companion that renders plain HTML on the server with near-zero client
JavaScript, so browsing your library works on those low-powered browsers.

It runs as a **sidecar container** next to your Audiobookshelf instance, talks to
the ABS API on your behalf, and keeps no database of its own — your ABS session
token lives in an encrypted cookie. Pages are built from `<form>` and `<a>`
elements only.

> **Note:** Inkshelf was built using agentic software engineering (AI-assisted
> coding) and reviewed by a human. See the git history for details.

<!-- SCREENSHOT: drop a library-listing screenshot here, e.g.
<p align="center"><img src="docs/img/screenshot.png" alt="Inkshelf library view" width="720"></p>
-->

## Features

- **Browse & find** — libraries list, paginated item view with covers, full-text
  search, author/series filters, cycling sort links (title / author / added /
  sequence), and a one-tap favorite library that you land on by default.
- **Read** — download the original ebook, or convert CBZ/CBR comics on demand to
  a **device-sized, fixed-layout EPUB** (epubcheck-clean). Conversions are cached
  on disk and the listing shows which items are already converted.
- **Stateless & private** — your ABS token is held in a Data-Protection-encrypted,
  HttpOnly cookie and refreshed transparently when it expires. No accounts, no
  database.
- **Built for weak browsers** — near-zero JavaScript, defensive CSS, plain HTML
  forms and links so it works on older e-ink browser engines.
- **Hardened & bounded** — force-secure cookies behind a proxy, optional
  trusted-proxy scoping, a bounded/sanitized/gateable diagnostics endpoint, and
  resource-exhaustion guards on conversion and the cache.
- **Easy to run** — a single multi-arch (`linux/amd64` + `linux/arm64`) container
  image; no external services beyond your ABS server.

## Deployment

Inkshelf is designed to run as a **sidecar next to Audiobookshelf**, on the same
private network, behind whatever **reverse proxy** already terminates TLS for your
setup. It speaks plain HTTP on port **8080**; your proxy handles HTTPS.

### Quick start (Docker)

```bash
docker run -d --name inkshelf -p 8080:8080 \
  -e ABS_URL=http://your-abs-host:13378 \
  -v inkshelf-keys:/keys -e DataProtectionKeysPath=/keys \
  -v inkshelf-cache:/cache -e CachePath=/cache \
  ghcr.io/thomaslazar/inkshelf:latest
```

Open `http://localhost:8080` and log in with your Audiobookshelf credentials.

### Docker Compose (recommended)

Copy [`docker-compose.example.yml`](docker-compose.example.yml), point `ABS_URL`
at your ABS service, and bring it up:

```bash
docker compose -f docker-compose.example.yml up -d
```

It pulls the published image, exposes port 8080, and persists the Data-Protection
keys and the EPUB cache in named volumes so logins and conversions survive
restarts. To build from source instead, replace the `image:` line with `build: .`.

### Behind a reverse proxy

Because TLS is terminated at your proxy, Inkshelf sees plain HTTP and by default
won't mark cookies `Secure`. In production, set:

- **`FORCE_SECURE_COOKIES=true`** — always mark the session cookie `Secure`
  (the reverse proxy is serving the site over HTTPS).
- **`TRUSTED_PROXY`** *(optional)* — a comma-separated list of proxy IPs/CIDRs
  allowed to set `X-Forwarded-*` headers. Leave unset to trust the immediate hop.

Run Inkshelf on a trusted network and expose it only through your proxy.

### Persistence

Mount a volume for each of these so state survives restarts:

- `DataProtectionKeysPath` (e.g. `/keys`) — encryption keys for the session
  cookie; without persistence everyone is logged out on restart.
- `CachePath` (e.g. `/cache`) — converted EPUBs; without persistence they're
  rebuilt on demand.

### Image tags

`ghcr.io/thomaslazar/inkshelf`

| Tag           | Meaning                                                     |
|---------------|-------------------------------------------------------------|
| `:latest`     | The most recent tagged release                              |
| `:X.Y.Z`      | A specific tagged release — pin this for reproducible deploys |
| `:main`       | Bleeding-edge build from `main` (moves on every merge)      |
| `:main-<sha>` | A specific `main` build, pinnable                           |

## Configuration

All configuration is via environment variables.

| Variable                  | Default              | Description |
|---------------------------|----------------------|-------------|
| `ABS_URL`                 | — (**required**)     | Base URL of your Audiobookshelf server. |
| `DataProtectionKeysPath`  | `<ContentRoot>/.keys`  | Where session-cookie encryption keys are persisted. Mount a volume to keep users logged in across restarts. |
| `CachePath`               | `<ContentRoot>/.cache/epub` | Where converted EPUBs are cached. Mount a volume to keep conversions across restarts. |
| `FORCE_SECURE_COOKIES`    | `false`              | Mark cookies `Secure` regardless of the request scheme. Set `true` when behind a TLS-terminating reverse proxy. |
| `TRUSTED_PROXY`           | *(unset)*            | Comma-separated IPs/CIDRs permitted to set forwarded headers. Unset = trust the immediate hop. |
| `DIAG_ENABLED`            | `true`               | Whether the unauthenticated `/diag` browser-probe endpoint is exposed. Set `false` to disable it. |
| `MaxArchiveBytes`         | `524288000` (500 MB) | Reject ebook archives larger than this before conversion (memory guard). |
| `MaxCacheBytes`           | `1073741824` (1 GB)  | Soft cap on total EPUB cache size; oldest entries are evicted past it. |

## How it works

Inkshelf is an ASP.NET Core Razor Pages app (.NET 10): Razor Pages render the
HTML, minimal-API endpoints serve streams and actions, and a typed HTTP client
talks to the ABS API with transparent token refresh. There is no database — state
is the encrypted cookie plus the on-disk EPUB cache.

For the full picture — structure, the load-bearing conventions, and the
configuration contract — see [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## Contributing

Contributions are welcome. Development happens inside a devcontainer; see
[`CONTRIBUTING.md`](CONTRIBUTING.md) for setup, build/test commands, conventions,
and the PR flow.

## License

[MIT](LICENSE) © 2026 Thomas Lazar.

## Acknowledgements

Built on top of [Audiobookshelf](https://www.audiobookshelf.org/) — a wonderful
self-hosted audiobook and ebook server. Inkshelf is an independent client and is
not affiliated with the Audiobookshelf project.
