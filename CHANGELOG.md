# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package uses [Semantic Versioning](https://semver.org/).

## [0.2.0] - 2026-11-22

### Changed

- The wait helpers own their retries: the wait request goes out without transport retries, each attempt asks the
  server for `ceil(time left)` seconds (1..60), and `429`, `502`, `503`, `504`, `520`–`524`, network errors and HTTP
  timeouts are waited out (`Retry-After` if sent, else 1 s) until the deadline, then `WaitTimeoutException` (the last
  transient error as its `InnerException`; new constructor `WaitTimeoutException(address, timeout, innerException)`).
  No pause or request runs past the deadline, the extra `GetAsync` of `WaitForCodeAsync` / `WaitForLinkAsync`
  included.
- A wait request's HTTP attempt gets the server timeout + 10 s; a `RequestOptions.Timeout` can only make it longer.
- A `503` with `Retry-After` over 5 s (e.g. no domain to create a mailbox on, `Retry-After: 60`) is thrown at once
  instead of stalling `CreateAsync` for up to two minutes.
- `GET` and `DELETE` are also retried on Cloudflare `520`–`524`, like `502` and `504`.
- `default(WaitAfter)`, a `null` and an empty string mean "no `after`": nothing is sent (converting a `null` string
  no longer throws). `WaitAfter.Message("")` throws `ArgumentException`.

### Fixed

- A `DELETE` retried after a lost response (timeout, network error, `502`/`504`/`52x`) that gets `404` counts as
  done instead of throwing `NotFoundException`.
- When a message the wait loop skipped is deleted meanwhile (`404 message in after not found`), the wait continues
  after the message's time instead of failing.
- A `Retry-After` too large for a `TimeSpan` no longer throws `OverflowException`: it is read as at most a year, so
  the response is a `RateLimitException` and the retry pause stays 60 s.

### Documentation

- `DownloadedAttachment.FileName` is never `null` from the API (the attachment id when the email gave no name) and
  is sender-controlled: the README keeps only `Path.GetFileName` of it. `NotFoundException` also covers an unknown
  `domain` on create and a missing `after` message; the server lowercases a custom local part.

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

[0.2.0]: https://github.com/fake-email-pro/fake-email-dotnet/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/fake-email-pro/fake-email-dotnet/releases/tag/v0.1.0
