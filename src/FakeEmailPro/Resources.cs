using System.Runtime.CompilerServices;
using System.Text.Json;

namespace FakeEmailPro;

/// <summary>Mailboxes: <c>/v1/mailboxes</c>.</summary>
public sealed class MailboxesResource
{
    private readonly ApiCaller _api;

    internal MailboxesResource(ApiCaller api) => _api = api;

    /// <summary>Creates a mailbox (spends one unit of the monthly quota). No options: a random address on a random domain.</summary>
    /// <param name="options">The local part and domain to use.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The new mailbox.</returns>
    public Task<Mailbox> CreateAsync(CreateMailboxOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, string>();
        if (options?.LocalPart is { } localPart) body["localPart"] = localPart;
        if (options?.Domain is { } domain) body["domain"] = domain;
        var request = new ApiRequest
        {
            Method = HttpMethod.Post,
            Path = "/v1/mailboxes",
            JsonBody = JsonSerializer.SerializeToUtf8Bytes(body, FakeEmailJsonContext.Default.DictionaryStringString),
        };
        return _api.JsonAsync(request, FakeEmailJsonContext.Default.Mailbox, cancellationToken);
    }

    /// <summary>One page of active mailboxes, newest first.</summary>
    /// <param name="page">Zero-based page number (server default 0).</param>
    /// <param name="size">Page size, 1..50 (server default 20).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<Mailbox>> ListAsync(int? page = null, int? size = null, CancellationToken cancellationToken = default) =>
        _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = "/v1/mailboxes", Query = PageQuery(page, size) },
            FakeEmailJsonContext.Default.PageMailbox,
            cancellationToken);

    /// <summary>Every active mailbox, fetching pages as needed.</summary>
    /// <param name="size">Page size used while iterating, 1..50 (default 50).</param>
    /// <param name="cancellationToken">Cancels the iteration.</param>
    /// <returns>The mailboxes, newest first.</returns>
    public IAsyncEnumerable<Mailbox> ListAllAsync(int size = 50, CancellationToken cancellationToken = default) =>
        Pagination.Walk((page, ct) => ListAsync(page, size, ct), cancellationToken);

    /// <summary>One mailbox.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The mailbox.</returns>
    public Task<Mailbox> GetAsync(string address, CancellationToken cancellationToken = default) =>
        _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = MailboxPath(address) },
            FakeEmailJsonContext.Default.Mailbox,
            cancellationToken);

    /// <summary>Deletes the mailbox and its messages. The address is never issued again; the quota is not given back.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the mailbox is deleted.</returns>
    public async Task DeleteAsync(string address, CancellationToken cancellationToken = default) =>
        await _api.SendAsync(new ApiRequest { Method = HttpMethod.Delete, Path = MailboxPath(address) }, cancellationToken)
            .ConfigureAwait(false);

    internal static string MailboxPath(string address) => "/v1/mailboxes/" + Url.Segment(address);

    internal static KeyValuePair<string, string?>[] PageQuery(int? page, int? size) =>
    [
        new("page", page?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        new("size", size?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];
}

/// <summary>Messages of a mailbox: <c>/v1/mailboxes/{address}/messages</c>.</summary>
public sealed class MessagesResource
{
    private static readonly TimeSpan DefaultServerWait = TimeSpan.FromSeconds(30);

    // The HTTP attempt must outlive the server's hold, or a healthy long poll would be cut off as a timeout.
    private static readonly TimeSpan WaitHttpMargin = TimeSpan.FromSeconds(10);

    private readonly ApiCaller _api;

    internal MessagesResource(ApiCaller api) => _api = api;

    /// <summary>One page of the mailbox's messages, newest first. To watch for new mail use <see cref="WaitAsync"/>, not polling.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="page">Zero-based page number (server default 0).</param>
    /// <param name="size">Page size, 1..50 (server default 20).</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The page.</returns>
    public Task<Page<SimpleMessage>> ListAsync(string address, int? page = null, int? size = null, CancellationToken cancellationToken = default) =>
        _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = MessagesPath(address), Query = MailboxesResource.PageQuery(page, size) },
            FakeEmailJsonContext.Default.PageSimpleMessage,
            cancellationToken);

    /// <summary>Every message of the mailbox, newest first, fetching pages as needed.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="size">Page size used while iterating, 1..50 (default 50).</param>
    /// <param name="cancellationToken">Cancels the iteration.</param>
    /// <returns>The messages.</returns>
    public IAsyncEnumerable<SimpleMessage> ListAllAsync(string address, int size = 50, CancellationToken cancellationToken = default) =>
        Pagination.Walk((page, ct) => ListAsync(address, page, size, ct), cancellationToken);

    /// <summary>
    /// One long poll: the earliest matching message (strictly after <see cref="MessageWaitOptions.After"/>), or
    /// <see langword="null"/> when the server's timeout passed without one. For a loop with a client-side deadline, use
    /// <see cref="FakeEmailClient.WaitForMessageAsync"/>. The HTTP attempt gets the server timeout + 10 s; a longer
    /// <see cref="RequestOptions.Timeout"/> applies, a shorter one does not.
    /// </summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="options">Server timeout and filters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The message, or <see langword="null"/> on the server's timeout.</returns>
    public Task<SimpleMessage?> WaitAsync(string address, MessageWaitOptions? options = null, CancellationToken cancellationToken = default)
    {
        int? seconds = options?.Timeout is { } t ? (int)Math.Ceiling(t.TotalSeconds) : null;
        return WaitOnceAsync(address, seconds, options?.After, options?.From, options?.Subject, null, cancellationToken);
    }

    // maxRetries overrides the retries: the wait loop passes 0, as it owns them.
    internal Task<SimpleMessage?> WaitOnceAsync(
        string address,
        int? timeoutSeconds,
        WaitAfter? after,
        string? from,
        string? subject,
        int? maxRetries,
        CancellationToken cancellationToken)
    {
        var serverWait = timeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : DefaultServerWait;
        var request = new ApiRequest
        {
            Method = HttpMethod.Get,
            Path = MessagesPath(address) + "/wait",
            Query =
            [
                new("timeout", timeoutSeconds?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("after", after?.QueryValue),
                new("from", from),
                new("subject", subject),
            ],
            Timeout = WaitAttemptTimeout(serverWait, _api.Overrides.Timeout),
            MaxRetries = maxRetries,
        };
        return _api.JsonOrNullAsync(request, FakeEmailJsonContext.Default.SimpleMessage, cancellationToken);
    }

    /// <summary>The server's hold + 10 s; an override can make it longer (or infinite), never shorter.</summary>
    internal static TimeSpan WaitAttemptTimeout(TimeSpan serverWait, TimeSpan? overrideTimeout)
    {
        var floor = serverWait + WaitHttpMargin;
        return overrideTimeout is { } o && (o == Timeout.InfiniteTimeSpan || o > floor) ? o : floor;
    }

    /// <summary>The full message: bodies, attachments, every code and link.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="id">The message id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The message.</returns>
    public Task<DetailedMessage> GetAsync(string address, string id, CancellationToken cancellationToken = default) =>
        _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = MessagePath(address, id) },
            FakeEmailJsonContext.Default.DetailedMessage,
            cancellationToken);

    /// <summary>
    /// <see cref="GetAsync"/> inside a wait loop: one attempt, cut short at <paramref name="deadline"/>; the loop
    /// decides whether to try again.
    /// </summary>
    internal Task<DetailedMessage> GetWithinAsync(string address, string id, DateTimeOffset deadline, CancellationToken cancellationToken) =>
        _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = MessagePath(address, id), MaxRetries = 0, Deadline = deadline },
            FakeEmailJsonContext.Default.DetailedMessage,
            cancellationToken);

    /// <summary>Deletes the message.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="id">The message id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes when the message is deleted.</returns>
    public async Task DeleteAsync(string address, string id, CancellationToken cancellationToken = default) =>
        await _api.SendAsync(new ApiRequest { Method = HttpMethod.Delete, Path = MessagePath(address, id) }, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>The raw EML, byte for byte.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="id">The message id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The message as received.</returns>
    public async Task<byte[]> GetRawAsync(string address, string id, CancellationToken cancellationToken = default)
    {
        var response = await _api.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = MessagePath(address, id) + "/raw" },
            cancellationToken).ConfigureAwait(false);
        return response.Body;
    }

    /// <summary>The decoded bytes of an attachment, with its type and file name.</summary>
    /// <param name="address">The mailbox address.</param>
    /// <param name="id">The message id.</param>
    /// <param name="attachmentId">The attachment id, from <see cref="DetailedMessage.Attachments"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The attachment.</returns>
    public async Task<DownloadedAttachment> GetAttachmentAsync(string address, string id, string attachmentId, CancellationToken cancellationToken = default)
    {
        var response = await _api.SendAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = MessagePath(address, id) + "/attachments/" + Url.Segment(attachmentId) },
            cancellationToken).ConfigureAwait(false);
        return new DownloadedAttachment
        {
            Data = response.Body,
            ContentType = response.ContentHeader("Content-Type") ?? "application/octet-stream",
            FileName = ContentDisposition.ParseFileName(response.ContentHeader("Content-Disposition")),
        };
    }

    private static string MessagesPath(string address) => MailboxesResource.MailboxPath(address) + "/messages";

    private static string MessagePath(string address, string id) => MessagesPath(address) + "/" + Url.Segment(id);
}

/// <summary>Domains: <c>/v1/domains</c>.</summary>
public sealed class DomainsResource
{
    private readonly ApiCaller _api;

    internal DomainsResource(ApiCaller api) => _api = api;

    /// <summary>Domains you can create a mailbox on, sorted by name.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The domains.</returns>
    public async Task<IReadOnlyList<Domain>> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = await _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = "/v1/domains" },
            FakeEmailJsonContext.Default.DomainList,
            cancellationToken).ConfigureAwait(false);
        return list.Domains;
    }
}

/// <summary>Limits: <c>/v1/limits</c>.</summary>
public sealed class LimitsResource
{
    private readonly ApiCaller _api;

    internal LimitsResource(ApiCaller api) => _api = api;

    /// <summary>Your quota, rate limit, concurrent waits, storage and mailbox lifetime. Does not spend quota.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The limits.</returns>
    public Task<Limits> GetAsync(CancellationToken cancellationToken = default) =>
        _api.JsonAsync(
            new ApiRequest { Method = HttpMethod.Get, Path = "/v1/limits" },
            FakeEmailJsonContext.Default.Limits,
            cancellationToken);
}

internal static class Pagination
{
    /// <summary>Walks pages from 0 until the last one. New items arriving meanwhile shift pages (lists are newest first).</summary>
    public static async IAsyncEnumerable<T> Walk<T>(
        Func<int, CancellationToken, Task<Page<T>>> fetchPage,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var page = 0; ; page++)
        {
            var result = await fetchPage(page, cancellationToken).ConfigureAwait(false);
            foreach (var item in result.Content) yield return item;
            if (result.Content.Count == 0 || page + 1 >= result.TotalPages) yield break;
        }
    }
}
