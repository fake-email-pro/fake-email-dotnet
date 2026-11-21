# FakeEmailPro

[![NuGet](https://img.shields.io/nuget/v/FakeEmailPro.svg)](https://www.nuget.org/packages/FakeEmailPro)
[![CI](https://github.com/fake-email-pro/fake-email-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/fake-email-pro/fake-email-dotnet/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](https://github.com/fake-email-pro/fake-email-dotnet/blob/main/LICENSE)

The official .NET client for the [fake-email.pro API](https://fake-email.pro/api): create a disposable mailbox, wait
for the email, get the one-time code or the magic link. Built for end-to-end tests of signup, OTP and password-reset
flows with xUnit, NUnit, MSTest and Playwright.

```csharp
var mailbox = await mail.Mailboxes.CreateAsync();
// ...sign up with mailbox.Address in your app...
var code = await mail.WaitForCodeAsync(mailbox.Address); // "048213"
```

- .NET 8+, async all the way, `CancellationToken` on every call
- Zero dependencies: `HttpClient` and `System.Text.Json`, trimming and Native AOT friendly
- Long-poll waiting: the message is returned within about a second of delivery, no polling loops in your tests
- Codes and confirm links are extracted on the server
- Retries on rate limits and transient errors, honoring `Retry-After`

API reference: [api.fake-email.pro/docs](https://api.fake-email.pro/docs) · Guides:
[fake-email.pro/api/docs/sdks](https://fake-email.pro/api/docs/sdks)

## Install

```sh
dotnet add package FakeEmailPro
```

## Get an API key

1. Sign in at [fake-email.pro](https://fake-email.pro). Any account can use the API, the free plan included.
2. Open **Settings → API keys** ([fake-email.pro/settings/api-keys](https://fake-email.pro/settings/api-keys)) and
   create a key. It starts with `fep_` and is shown only once.
3. Put it in the environment of your test runner (and in your CI secrets):

```sh
export FAKE_EMAIL_API_KEY=fep_...
```

The client reads `FAKE_EMAIL_API_KEY` by default. A bigger monthly quota and higher limits come with
[PRO](https://fake-email.pro/pricing).

> **Server-side only.** Never ship the key to a browser or a mobile app: anyone could read it there. Test code,
> CI jobs and backends are the place for it.

## Quickstart

```csharp
using System.Net.Http.Json;
using FakeEmailPro;

using var mail = new FakeEmailClient(); // or new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "fep_..." })

// 1. A fresh address, valid for 24 hours
var mailbox = await mail.Mailboxes.CreateAsync();

// 2. Make your app send an email to it
using var http = new HttpClient();
await http.PostAsJsonAsync("https://your-app.example/api/signup", new { email = mailbox.Address });

// 3. Wait for the email and take the code
var code = await mail.WaitForCodeAsync(mailbox.Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(60) });

// Or the confirm / magic link
// var link = await mail.WaitForLinkAsync(mailbox.Address);

await mail.Mailboxes.DeleteAsync(mailbox.Address);
```

`WaitForCodeAsync` returns the code found in the email when it was delivered. If none was found then, it reads the
full message and takes its first code; an email with no code at all is skipped and the wait goes on.
`WaitForLinkAsync` does the same with the confirm link.

`FakeEmailClient` is thread-safe: create one per test run (a static field or a fixture) and share it.

## xUnit + Playwright

```csharp
// SignupTests.cs — packages: FakeEmailPro, Microsoft.Playwright.Xunit
using FakeEmailPro;
using Microsoft.Playwright.Xunit;

public class SignupTests : PageTest
{
    private static readonly FakeEmailClient Mail = new(); // FAKE_EMAIL_API_KEY from the environment
    private string _inbox = "";

    // A fresh mailbox per test, deleted afterwards
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        _inbox = (await Mail.Mailboxes.CreateAsync()).Address;
    }

    public override async Task DisposeAsync()
    {
        await Mail.Mailboxes.DeleteAsync(_inbox);
        await base.DisposeAsync();
    }

    [Fact]
    public async Task Signs_up_with_an_email_code()
    {
        await Page.GotoAsync("https://your-app.example/signup");
        await Page.GetByLabel("Email").FillAsync(_inbox);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Send code" }).ClickAsync();

        var code = await Mail.WaitForCodeAsync(_inbox, new WaitOptions { Subject = "verification" });

        await Page.GetByLabel("Code").FillAsync(code);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();
        await Expect(Page.GetByText("Welcome")).ToBeVisibleAsync();
    }
}
```

## NUnit + Playwright

```csharp
// SignupTests.cs — packages: FakeEmailPro, Microsoft.Playwright.NUnit
using FakeEmailPro;
using Microsoft.Playwright.NUnit;

public class SignupTests : PageTest
{
    private static readonly FakeEmailClient Mail = new();
    private string _inbox = "";

    [SetUp]
    public async Task CreateInbox() => _inbox = (await Mail.Mailboxes.CreateAsync()).Address;

    [TearDown]
    public async Task DeleteInbox() => await Mail.Mailboxes.DeleteAsync(_inbox);

    [Test]
    public async Task SignsUpWithAMagicLink()
    {
        await Page.GotoAsync("https://your-app.example/login");
        await Page.GetByLabel("Email").FillAsync(_inbox);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Email me a link" }).ClickAsync();

        var link = await Mail.WaitForLinkAsync(_inbox, new WaitOptions { From = "your-app.example" });

        await Page.GotoAsync(link);
        await Expect(Page.GetByText("Welcome")).ToBeVisibleAsync();
    }
}
```

Keep your test timeout above the wait's `Timeout` (`WaitForCodeAsync` defaults to 60 s).

## Waiting for messages

```csharp
var message = await mail.WaitForMessageAsync(address, new WaitOptions
{
    Timeout = TimeSpan.FromSeconds(60), // client-side deadline, then WaitTimeoutException
    From = "acme.example",              // case-insensitive substring of the sender address
    Subject = "verify",                 // case-insensitive substring of the subject
    Match = m => m.AttachmentsCount > 0, // any extra check
});
Console.WriteLine(message.Code);        // "048213" or null
Console.WriteLine(message.ConfirmLink); // "https://..." or null
```

`From` and `Subject` are filtered on the server. A message that fails `Match` is skipped and the wait continues with
the next one. Under the hood the client repeats the server's long poll (up to 60 s each) until the deadline.

### The next message

Without `After`, the wait returns the **earliest** message in the mailbox, even one that arrived before you started
waiting, so nothing is lost. To wait for a newer one, for example after "Resend code", pass the previous message's id:

```csharp
var first = await mail.WaitForMessageAsync(address);
await page.GetByRole(AriaRole.Button, new() { Name = "Resend code" }).ClickAsync();
var second = await mail.WaitForMessageAsync(address, new WaitOptions { After = first.Id });
```

`After` also takes a time (`DateTimeOffset` or `DateTime`): "messages that arrived after this moment" by the server's
clock. Prefer a message id: it is exact, while a time depends on clock skew. Do not pass a message's `CreatedAt` as
`After`: a second message in the same millisecond would be skipped.

An `After` id that is no longer in the mailbox (you deleted that message) is a `NotFoundException`: if your tests
delete processed messages, wait without `After`.

### Cancelling

Every method takes a `CancellationToken` as its last argument:

```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var code = await mail.WaitForCodeAsync(address, cancellationToken: cts.Token);
await mail.Mailboxes.ListAsync(cancellationToken: cts.Token);
```

A cancelled call throws `OperationCanceledException`, as in the rest of .NET.

## Messages, attachments and raw EML

```csharp
var page = await mail.Messages.ListAsync(address, page: 0, size: 20); // newest first
await foreach (var m in mail.Messages.ListAllAsync(address)) Console.WriteLine(m.Subject);

var full = await mail.Messages.GetAsync(address, message.Id);
// full.Html, full.Text, full.Codes, full.Links, full.Attachments

var attachment = await mail.Messages.GetAttachmentAsync(address, message.Id, full.Attachments[0].Id);
await File.WriteAllBytesAsync(attachment.FileName ?? "attachment.bin", attachment.Data); // + attachment.ContentType

byte[] eml = await mail.Messages.GetRawAsync(address, message.Id); // the message as received

await mail.Messages.DeleteAsync(address, message.Id);
```

One long poll without the client-side loop: `mail.Messages.WaitAsync(address, new MessageWaitOptions { Timeout =
TimeSpan.FromSeconds(30), After = ..., From = ..., Subject = ... })` returns the message or `null` when the server
timeout passed.

## Mailboxes, domains and limits

```csharp
await mail.Mailboxes.CreateAsync(); // random address on a random domain
await mail.Mailboxes.CreateAsync(new CreateMailboxOptions { Domain = "example.org" }); // random address on that domain
await mail.Mailboxes.CreateAsync(new CreateMailboxOptions { LocalPart = "signup-test", Domain = "example.org" }); // exactly that

await mail.Mailboxes.GetAsync(address);
await mail.Mailboxes.ListAsync(page: 0, size: 50);
await foreach (var mb in mail.Mailboxes.ListAllAsync()) Console.WriteLine(mb.Address);
await mail.Mailboxes.DeleteAsync(address); // messages are erased; the address is never issued again

var domains = await mail.Domains.ListAsync(); // [{ Name = "example.org", Type = PUBLIC }, ...]
var limits = await mail.Limits.GetAsync(); // Quota, RequestsPerMinute, ConcurrentWaits, Storage, MailboxTtlSeconds
```

A mailbox accepts mail for 24 hours and cannot be renewed; after that it answers `404`. Each created mailbox spends one
unit of your monthly quota (deleting it does not give it back). Custom local parts depend on the plan.

## Errors

All exceptions derive from `FakeEmailException`. Errors from the server are `ApiException` with `Status`, `Code` (the
API's `error` field, see `ErrorCodes`), `Message`, `Body`, `BodyJson`, `Headers` and `RateLimit`:

| Exception                      | When                                                                                     |
| ------------------------------ | ---------------------------------------------------------------------------------------- |
| `BadRequestException`          | 400 `bad_request`: an invalid parameter or body                                          |
| `AuthenticationException`      | 401 `unauthorized`: missing, unknown, revoked or expired key                             |
| `QuotaExhaustedException`      | 402 `quota_exhausted`: monthly mailbox quota spent (`Limit`, `Used`, `ResetsAt`, `UpgradeUrl`) |
| `PermissionDeniedException`    | 403 `plan_forbidden`: the plan does not allow this                                       |
| `NotFoundException`            | 404 `not_found`: mailbox expired, deleted or not yours; no such message                  |
| `ConflictException`            | 409 `address_taken`: the address was issued before                                       |
| `RateLimitException`           | 429: `Code` is `rate_limit_exceeded` or `too_many_waits`; `RetryAfter`                   |
| `InternalServerErrorException` | 5xx                                                                                      |
| `ApiConnectionException`       | no response (DNS, refused, reset)                                                        |
| `ApiTimeoutException`          | an HTTP attempt took longer than `Timeout`                                               |
| `WaitTimeoutException`         | `WaitForMessageAsync` / `WaitForCodeAsync` / `WaitForLinkAsync` reached their deadline   |

```csharp
try
{
    var mailbox = await mail.Mailboxes.CreateAsync();
}
catch (QuotaExhaustedException e)
{
    Console.Error.WriteLine($"Quota {e.Used}/{e.Limit}, renews {e.ResetsAt:u}. More: {e.UpgradeUrl}");
    throw;
}
catch (RateLimitException e) when (e.Code == ErrorCodes.TooManyWaits)
{
    // too many concurrent waits on this key
}
```

`AuthenticationException` shares its name with `System.Security.Authentication.AuthenticationException`; if both
namespaces are imported, write `FakeEmailPro.AuthenticationException`.

### Retries and rate limits

The client retries up to `MaxRetries` times (default 2):

- `429` and `503`, for every method: the server refuses these before doing anything. The wait is `Retry-After` (at
  most 60 s).
- `502`, `504`, timeouts and network errors, for `GET` and `DELETE` only, never for `POST` (a mailbox could be created
  twice). Exponential backoff with jitter: 0.5 s, 1 s, 2 s, ... up to 8 s.

Inside `WaitForMessageAsync` and friends, a `429` keeps being waited out while the deadline allows, and no retry is
started that would end past the deadline.

Your plan's numbers are in `mail.Limits.GetAsync()`. Every `ApiException` carries the window state from the
`X-RateLimit-*` headers as `RateLimit` (`Limit`, `Remaining`, `Reset`, or `null` when the server did not send them).
Each `wait` request counts once, however long it is held open.

## Configuration

```csharp
var mail = new FakeEmailClient(new FakeEmailClientOptions
{
    ApiKey = "fep_...",                      // default: the FAKE_EMAIL_API_KEY environment variable
    BaseUrl = "https://api.fake-email.pro",  // default
    Timeout = TimeSpan.FromSeconds(30),      // per HTTP attempt; a wait request gets its server timeout + 10 s
    MaxRetries = 2,
    UserAgent = "my-e2e-suite/1.0",          // appended to "fake-email-pro-dotnet/<version>"
    HttpClient = httpClient,                 // optional; stays yours to dispose
});
```

Other settings for some calls: `mail.WithOptions(new RequestOptions { Timeout = ..., MaxRetries = 0 })` returns a view
of the client that shares its connections.

### HttpClient and dependency injection

Without `HttpClient`, the client creates and owns one (dispose the `FakeEmailClient` to release it). To use
`IHttpClientFactory`, pass a client from it; set its `Timeout` to `Timeout.InfiniteTimeSpan` (or at least 70 s), since
the SDK times each attempt itself and a wait is held up to 60 s:

```csharp
services.AddHttpClient("fake-email", c => c.Timeout = Timeout.InfiniteTimeSpan);
services.AddSingleton(sp => new FakeEmailClient(new FakeEmailClientOptions
{
    HttpClient = sp.GetRequiredService<IHttpClientFactory>().CreateClient("fake-email"),
}));
```

Models are immutable records: `Mailbox`, `SimpleMessage`, `DetailedMessage`, `Attachment`, `Link`, `Sender`,
`Domain`, `Limits`, `Quota`, `Storage`, `Page<T>`, `DownloadedAttachment`. Timestamps are `DateTimeOffset`.
`BodyState` and `DomainType` are string-backed: a value added by a newer server is kept in `.Value` instead of
failing the response.

## Links

- Product: [fake-email.pro/api](https://fake-email.pro/api)
- API reference: [api.fake-email.pro/docs](https://api.fake-email.pro/docs) (OpenAPI:
  [public.yaml](https://api.fake-email.pro/docs/public.yaml))
- SDK docs: [fake-email.pro/api/docs/sdks](https://fake-email.pro/api/docs/sdks)
- Issues: [github.com/fake-email-pro/fake-email-dotnet/issues](https://github.com/fake-email-pro/fake-email-dotnet/issues)
- Changelog: [CHANGELOG.md](https://github.com/fake-email-pro/fake-email-dotnet/blob/main/CHANGELOG.md)

## License

[MIT](https://github.com/fake-email-pro/fake-email-dotnet/blob/main/LICENSE)
