using System.Reflection;

namespace FakeEmailPro;

/// <summary>
/// Client for the fake-email.pro Public API. Thread-safe; create one and reuse it. Server-side only: the key must
/// never reach browser code (the API has no CORS).
/// </summary>
public sealed class FakeEmailClient : IDisposable
{
    /// <summary>The production API.</summary>
    public const string DefaultBaseUrl = "https://api.fake-email.pro";

    private static readonly TimeSpan DefaultWaitTimeout = TimeSpan.FromSeconds(60);
    private const int MaxServerWaitSeconds = 60;

    // A 204 that comes back far earlier than asked (a server restart) is not retried in a tight loop.
    private static readonly TimeSpan EarlyEmptyPause = TimeSpan.FromSeconds(1);

    private readonly ApiCaller _api;
    private readonly bool _ownsHttpClient;

    /// <summary>A client with the key from the <c>FAKE_EMAIL_API_KEY</c> environment variable and default settings.</summary>
    /// <exception cref="FakeEmailException">No API key.</exception>
    public FakeEmailClient()
        : this(new FakeEmailClientOptions())
    {
    }

    /// <summary>A client with the given settings.</summary>
    /// <param name="options">The settings; unset ones take their defaults.</param>
    /// <exception cref="FakeEmailException">No API key in the options or the environment.</exception>
    public FakeEmailClient(FakeEmailClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var apiKey = options.ApiKey;
        if (string.IsNullOrEmpty(apiKey)) apiKey = Environment.GetEnvironmentVariable("FAKE_EMAIL_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            throw new FakeEmailException(
                "missing API key: set FakeEmailClientOptions.ApiKey or the FAKE_EMAIL_API_KEY environment variable " +
                "(create one at https://fake-email.pro/settings/api-keys)");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl)) throw new ArgumentException("BaseUrl must not be empty", nameof(options));
        if (options.Timeout <= TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Timeout, "Timeout must be positive");
        }

        if (options.MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(options), options.MaxRetries, "MaxRetries must not be negative");

        var http = options.HttpClient;
        _ownsHttpClient = http is null;
        // Our own client has no overall timeout: each attempt gets its own timer, which maps to ApiTimeoutException.
        http ??= new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var sdkToken = $"fake-email-pro-dotnet/{Version}";
        var transport = new ApiTransport(
            http,
            apiKey,
            options.BaseUrl.TrimEnd('/'),
            string.IsNullOrEmpty(options.UserAgent) ? sdkToken : $"{sdkToken} {options.UserAgent}",
            options.Timeout,
            options.MaxRetries,
            options.Hooks ?? new ClientHooks());
        _api = new ApiCaller(transport, new RequestOptions());
        Mailboxes = new MailboxesResource(_api);
        Messages = new MessagesResource(_api);
        Domains = new DomainsResource(_api);
        Limits = new LimitsResource(_api);
    }

    private FakeEmailClient(ApiCaller api)
    {
        _api = api;
        _ownsHttpClient = false;
        Mailboxes = new MailboxesResource(api);
        Messages = new MessagesResource(api);
        Domains = new DomainsResource(api);
        Limits = new LimitsResource(api);
    }

    /// <summary>The version of this SDK, as sent in the <c>User-Agent</c> header.</summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>Mailboxes: create, list, get, delete.</summary>
    public MailboxesResource Mailboxes { get; }

    /// <summary>Messages: list, wait, get, delete, raw EML, attachments.</summary>
    public MessagesResource Messages { get; }

    /// <summary>Domains you can create a mailbox on.</summary>
    public DomainsResource Domains { get; }

    /// <summary>Your quota and limits.</summary>
    public LimitsResource Limits { get; }

    /// <summary>The API base URL, without a trailing slash.</summary>
    public string BaseUrl => _api.Transport.BaseUrl;

    /// <summary>
    /// A view of this client whose calls use other per-attempt timeout or retries. It shares the connection pool and
    /// needs no disposal; disposing it does nothing.
    /// </summary>
    /// <param name="options">The overrides; unset ones keep this client's values.</param>
    /// <returns>The view.</returns>
    public FakeEmailClient WithOptions(RequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(options), options.MaxRetries, "MaxRetries must not be negative");
        if (options.Timeout is { } t && t <= TimeSpan.Zero && t != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(options), t, "Timeout must be positive");
        }

        var merged = new RequestOptions
        {
            Timeout = options.Timeout ?? _api.Overrides.Timeout,
            MaxRetries = options.MaxRetries ?? _api.Overrides.MaxRetries,
        };
        return new FakeEmailClient(new ApiCaller(_api.Transport, merged));
    }

    /// <summary>
    /// Waits until a matching message arrives, repeating server long polls until <see cref="WaitOptions.Timeout"/>.
    /// </summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="options">Deadline and filters.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The message.</returns>
    /// <exception cref="WaitTimeoutException">No matching message before the deadline.</exception>
    public Task<SimpleMessage> WaitForMessageAsync(string address, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        WaitLoopAsync<SimpleMessage>(address, options, (message, _) => Task.FromResult<SimpleMessage?>(message), cancellationToken);

    /// <summary>
    /// Waits for a message with a one-time code and returns the code. Uses the code found on delivery, else the first
    /// code of the full message; a message without any code is skipped.
    /// </summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="options">Deadline and filters.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The code, e.g. <c>"048213"</c>.</returns>
    /// <exception cref="WaitTimeoutException">No message with a code before the deadline.</exception>
    public Task<string> WaitForCodeAsync(string address, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        WaitLoopAsync<string>(
            address,
            options,
            async (message, ct) =>
            {
                if (!string.IsNullOrEmpty(message.Code)) return message.Code;
                var full = await Messages.GetAsync(address, message.Id, ct).ConfigureAwait(false);
                return full.Codes.Count > 0 ? full.Codes[0] : null;
            },
            cancellationToken);

    /// <summary>
    /// Waits for a message with a confirm / sign-in link and returns its URL. Uses the link found on delivery, else the
    /// first link of the full message; a message without links is skipped.
    /// </summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="options">Deadline and filters.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The URL.</returns>
    /// <exception cref="WaitTimeoutException">No message with a link before the deadline.</exception>
    public Task<string> WaitForLinkAsync(string address, WaitOptions? options = null, CancellationToken cancellationToken = default) =>
        WaitLoopAsync<string>(
            address,
            options,
            async (message, ct) =>
            {
                if (!string.IsNullOrEmpty(message.ConfirmLink)) return message.ConfirmLink;
                var full = await Messages.GetAsync(address, message.Id, ct).ConfigureAwait(false);
                return full.Links.Count > 0 ? full.Links[0].Url : null;
            },
            cancellationToken);

    /// <summary>Disposes the HTTP client this instance created; an injected one stays open.</summary>
    public void Dispose()
    {
        if (_ownsHttpClient) _api.Transport.Http.Dispose();
    }

    private async Task<T> WaitLoopAsync<T>(
        string address,
        WaitOptions? options,
        Func<SimpleMessage, CancellationToken, Task<T?>> extract,
        CancellationToken ct)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(address);
        var hooks = _api.Transport.Hooks;
        var timeout = options?.Timeout ?? DefaultWaitTimeout;
        var deadline = hooks.Now() + timeout;
        var after = options?.After;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - hooks.Now();
            if (remaining <= TimeSpan.Zero) throw new WaitTimeoutException(address, timeout);
            var serverTimeout = (int)Math.Clamp(Math.Ceiling(remaining.TotalSeconds), 1, MaxServerWaitSeconds);
            var started = hooks.Now();
            SimpleMessage? message;
            try
            {
                message = await Messages.WaitOnceAsync(address, serverTimeout, after, options?.From, options?.Subject, deadline, ct)
                    .ConfigureAwait(false);
            }
            catch (RateLimitException ex)
            {
                // The request already used its retries; a 429 (usually too_many_waits) is still worth waiting out
                // while the caller's deadline allows.
                var pause = ex.RetryAfter ?? TimeSpan.FromSeconds(1);
                if (hooks.Now() + pause < deadline)
                {
                    await hooks.Sleep(pause, ct).ConfigureAwait(false);
                    continue;
                }

                throw;
            }

            if (message is null)
            {
                var left = deadline - hooks.Now();
                var pause = left < EarlyEmptyPause ? left : EarlyEmptyPause;
                if (hooks.Now() - started < EarlyEmptyPause && pause > TimeSpan.Zero) await hooks.Sleep(pause, ct).ConfigureAwait(false);
                continue;
            }

            // Continue strictly after this message whatever happens next: its id is exact, a time is not.
            after = message.Id;
            if (options?.Match is { } match && !match(message)) continue;
            var value = await extract(message, ct).ConfigureAwait(false);
            if (value is not null) return value;
        }
    }

    private static string ReadVersion()
    {
        var version = typeof(FakeEmailClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }
}
