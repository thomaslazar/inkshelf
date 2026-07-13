# Changelog

All notable changes to Inkshelf are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/).

## v0.1.0 — 2026-07-13

### Highlights
- Initial release: a thin, server-rendered Audiobookshelf web client for
  e-reader browsers (near-zero JavaScript), deployable as a sidecar container.

### Features
- feat: log in with Audiobookshelf credentials (token stored in an encrypted cookie)
- feat: list libraries and browse a library's items, paginated with cover thumbnails
- feat: transparent access-token refresh on 401
- feat: cover images proxied through the app
