namespace FakeEmailPro;

/// <summary>Settings of a <see cref="FakeEmailClient"/>.</summary>
public sealed class FakeEmailClientOptions
{
    /// <summary>API key (<c>fep_…</c>). Defaults to the <c>FAKE_EMAIL_API_KEY</c> environment variable.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Default <c>https://api.fake-email.pro</c>; trailing slashes are ignored.</summary>
    public string BaseUrl { get; set; } = FakeEmailClient.DefaultBaseUrl;

    /// <summary>Per HTTP attempt (default 30 s). A <c>wait</c> request gets its server timeout + 10 s instead.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Retries on 429/503, and on 502/504/520–524/network errors for GET and DELETE (default 2). The wait helpers retry
    /// on their own until their deadline instead.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Appended to the SDK's own <c>User-Agent</c> token, e.g. <c>"my-e2e-suite/1.0"</c>.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// The <see cref="System.Net.Http.HttpClient"/> to send requests with, e.g. one from <c>IHttpClientFactory</c>.
    /// It stays yours to dispose. The SDK applies its own per-attempt timeout, so give it
    /// <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> or at least 70 s as its <c>Timeout</c> (a <c>wait</c> is
    /// held up to 60 s). Default: a client owned and disposed by <see cref="FakeEmailClient"/>.
    /// </summary>
    public HttpClient? HttpClient { get; set; }

    internal ClientHooks? Hooks { get; set; }
}

/// <summary>Per-call overrides for a view of the client made with <see cref="FakeEmailClient.WithOptions"/>.</summary>
public sealed class RequestOptions
{
    /// <summary>
    /// Overrides the client's <see cref="FakeEmailClientOptions.Timeout"/> for each HTTP attempt. A <c>wait</c> request
    /// keeps at least its server timeout + 10 s: a longer value applies to it, a shorter one does not cut a long poll.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Overrides the client's <see cref="FakeEmailClientOptions.MaxRetries"/>.</summary>
    public int? MaxRetries { get; set; }
}

/// <summary>What <see cref="MailboxesResource.CreateAsync"/> creates. Empty: a random address on a random domain.</summary>
public sealed class CreateMailboxOptions
{
    /// <summary>
    /// The local part; requires <see cref="Domain"/> and a plan that allows custom local parts. The server lowercases
    /// it, so use the returned <see cref="Mailbox.Address"/>.
    /// </summary>
    public string? LocalPart { get; set; }

    /// <summary>A domain from <see cref="DomainsResource.ListAsync"/>; random if omitted.</summary>
    public string? Domain { get; set; }
}

/// <summary>Filters of one long poll, <see cref="MessagesResource.WaitAsync"/>.</summary>
public sealed class MessageWaitOptions
{
    /// <summary>How long the server holds the request, 1..60 s (server default 30 s); sent in whole seconds, rounded up.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Only messages strictly after this message id or time (server clock). See <see cref="WaitAfter"/>.</summary>
    public WaitAfter? After { get; set; }

    /// <summary>Case-insensitive substring of the sender address.</summary>
    public string? From { get; set; }

    /// <summary>Case-insensitive substring of the subject.</summary>
    public string? Subject { get; set; }
}

/// <summary>Options of <see cref="FakeEmailClient.WaitForMessageAsync"/>, <see cref="FakeEmailClient.WaitForCodeAsync"/> and <see cref="FakeEmailClient.WaitForLinkAsync"/>.</summary>
public sealed class WaitOptions
{
    /// <summary>
    /// Client-side deadline for the whole wait (default 60 s); then <see cref="WaitTimeoutException"/>. Zero or less is
    /// already expired: the wait throws at once.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>Only messages strictly after this message id or time (server clock). See <see cref="WaitAfter"/>.</summary>
    public WaitAfter? After { get; set; }

    /// <summary>Case-insensitive substring of the sender address (server-side filter).</summary>
    public string? From { get; set; }

    /// <summary>Case-insensitive substring of the subject (server-side filter).</summary>
    public string? Subject { get; set; }

    /// <summary>Client-side filter; a message that fails it is skipped and the wait continues after it.</summary>
    public Func<SimpleMessage, bool>? Match { get; set; }
}

/// <summary>
/// "Strictly after" for a wait: a message id (exact; to continue after a message you got) or a time by the server's
/// clock. Converts implicitly from <see cref="string"/> (an id), <see cref="DateTimeOffset"/> and <see cref="DateTime"/>.
/// Do not pass a message's <c>CreatedAt</c>: a second message in the same millisecond would be skipped.
/// <c>default(WaitAfter)</c>, and a <see langword="null"/> or empty string converted to it, mean "not set": no
/// <c>after</c> is sent.
/// </summary>
public readonly record struct WaitAfter
{
    private WaitAfter(string? messageId, DateTimeOffset? time)
    {
        MessageId = messageId;
        Time = time;
    }

    /// <summary>The message id, when made from one.</summary>
    public string? MessageId { get; }

    /// <summary>The time, when made from one.</summary>
    public DateTimeOffset? Time { get; }

    /// <summary>The <c>after</c> query value; <see langword="null"/> when not set.</summary>
    internal string? QueryValue => MessageId ?? (Time is { } t ? Url.Time(t) : null);

    /// <summary>After the message with this id.</summary>
    /// <param name="messageId">A message id.</param>
    /// <returns>The cursor.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="messageId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="messageId"/> is empty.</exception>
    public static WaitAfter Message(string messageId)
    {
        ArgumentException.ThrowIfNullOrEmpty(messageId);
        return new WaitAfter(messageId, null);
    }

    /// <summary>After this moment, by the server's clock; sent as RFC 3339 UTC with milliseconds.</summary>
    /// <param name="time">The moment.</param>
    /// <returns>The cursor.</returns>
    public static WaitAfter At(DateTimeOffset time) => new(null, time);

    /// <summary>After the message with this id; <see langword="null"/> or empty is "not set".</summary>
    /// <param name="messageId">A message id.</param>
    public static implicit operator WaitAfter(string? messageId) =>
        string.IsNullOrEmpty(messageId) ? default : Message(messageId);

    /// <summary>After this moment.</summary>
    /// <param name="time">The moment.</param>
    public static implicit operator WaitAfter(DateTimeOffset time) => At(time);

    /// <summary>After this moment; a <see cref="DateTimeKind.Unspecified"/> time is taken as local.</summary>
    /// <param name="time">The moment.</param>
    public static implicit operator WaitAfter(DateTime time) => At(new DateTimeOffset(time));

    /// <summary>The value sent as the <c>after</c> query parameter.</summary>
    /// <returns>The id, the time as RFC 3339 UTC with milliseconds, or <c>""</c> when not set.</returns>
    public override string ToString() => QueryValue ?? "";
}
