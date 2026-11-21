# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-11-22

First release.

### Added

- `FakeEmailClient` for the fake-email.pro Public API v1: `Mailboxes` (CreateAsync, ListAsync, ListAllAsync, GetAsync,
  DeleteAsync), `Messages` (ListAsync, ListAllAsync, WaitAsync, GetAsync, DeleteAsync, GetRawAsync, GetAttachmentAsync),
  `Domains.ListAsync`, `Limits.GetAsync`.
- Test helpers `WaitForMessageAsync`, `WaitForCodeAsync` and `WaitForLinkAsync` with a client-side deadline,
  server-side `From` / `Subject` filters, a `Match` predicate and `CancellationToken` support.
- An exception per error status (`QuotaExhaustedException`, `RateLimitException`, `NotFoundException`, ...) with the
  server's error code and the `X-RateLimit-*` state.
- Retries on `429`/`503` honoring `Retry-After`, and on `502`/`504`/network errors for `GET` and `DELETE`.
- Immutable record models with `DateTimeOffset` timestamps; string-backed `BodyState` and `DomainType` that keep values
  a newer server adds. .NET 8+, zero dependencies, source-generated JSON (trimming and Native AOT friendly), Source Link.

[0.1.0]: https://github.com/fake-email-pro/fake-email-dotnet/releases/tag/v0.1.0
