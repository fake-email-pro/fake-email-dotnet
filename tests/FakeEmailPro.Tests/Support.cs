using System.Net;
using System.Text;
using System.Web;

namespace FakeEmailPro.Tests;

public sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    Dictionary<string, string> Headers,
    string? Body,
    CancellationToken Token)
{
    public string Path => Uri.AbsolutePath;

    public string Search => Uri.Query;

    public Dictionary<string, string> QueryParams
    {
        get
        {
            var parsed = HttpUtility.ParseQueryString(Uri.Query);
            return parsed.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => parsed[k]!);
        }
    }

    public string? Query(string name) => QueryParams.TryGetValue(name, out var v) ? v : null;

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out var v) ? v : null;
}

/// <summary>Virtual clock: sleeping advances it instantly, so retry and wait tests take no real time.</summary>
public sealed class FakeClock
{
    public static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    public DateTimeOffset T { get; private set; } = Start;

    public List<double> Sleeps { get; } = new();

    public DateTimeOffset Now() => T;

    public void Advance(TimeSpan by) => T += by;

    public Task Sleep(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Sleeps.Add(delay.TotalMilliseconds);
        T += delay;
        return Task.CompletedTask;
    }
}

/// <summary>A reply: a response, an exception to throw, or a function of the request.</summary>
public delegate Task<HttpResponseMessage> Reply(RecordedRequest request, FakeClock clock);

/// <summary>An HttpMessageHandler that answers from a queue of replies and records every request.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Queue<Reply> _queue;
    private readonly FakeClock _clock;

    public FakeHandler(IEnumerable<Reply> replies, FakeClock clock)
    {
        _queue = new Queue<Reply>(replies);
        _clock = clock;
    }

    public List<RecordedRequest> Requests { get; } = new();

    public int Remaining => _queue.Count;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.NonValidated.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
        if (request.Content is not null)
        {
            foreach (var h in request.Content.Headers.NonValidated) headers[h.Key.ToLowerInvariant()] = h.Value.ToString();
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body, cancellationToken);
        Requests.Add(recorded);
        if (!_queue.TryDequeue(out var next)) throw new InvalidOperationException($"unexpected request {request.Method} {request.RequestUri}");
        return await next(recorded, _clock);
    }
}

public sealed class TestSetup : IDisposable
{
    public TestSetup(IEnumerable<Reply> replies, Action<FakeEmailClientOptions>? configure = null, Func<double>? random = null,
        Func<TimeSpan, CancellationTokenSource>? attemptTimer = null)
    {
        Clock = new FakeClock();
        Handler = new FakeHandler(replies, Clock);
        Http = new HttpClient(Handler);
        var options = new FakeEmailClientOptions
        {
            ApiKey = "fep_test",
            BaseUrl = "https://api.test",
            HttpClient = Http,
            Hooks = new ClientHooks
            {
                Now = Clock.Now,
                Sleep = Clock.Sleep,
                Random = random ?? (() => 0),
                AttemptTimer = attemptTimer ?? (t => new CancellationTokenSource(t)),
            },
        };
        configure?.Invoke(options);
        Client = new FakeEmailClient(options);
    }

    public FakeClock Clock { get; }

    public FakeHandler Handler { get; }

    public HttpClient Http { get; }

    public FakeEmailClient Client { get; }

    public List<RecordedRequest> Requests => Handler.Requests;

    public void Dispose()
    {
        Client.Dispose();
        Http.Dispose();
    }
}

public static class R
{
    public static Reply Json(string json, int status = 200, params (string Name, string Value)[] headers) =>
        (_, _) => Task.FromResult(Response(status, Encoding.UTF8.GetBytes(json), "application/json", headers));

    public static Reply Empty(int status = 204, params (string Name, string Value)[] headers) =>
        (_, _) => Task.FromResult(Response(status, null, null, headers));

    public static Reply Text(string text, int status, string contentType = "text/plain") =>
        (_, _) => Task.FromResult(Response(status, Encoding.UTF8.GetBytes(text), contentType, Array.Empty<(string, string)>()));

    public static Reply Bytes(byte[] bytes, int status, params (string Name, string Value)[] headers) =>
        (_, _) => Task.FromResult(Response(status, bytes, null, headers));

    public static Reply ApiError(int status, string error, string message, params (string Name, string Value)[] headers) =>
        Json($$"""{"error":"{{error}}","message":"{{message}}"}""", status, headers);

    public static Reply Throw(Exception ex) => (_, _) => Task.FromException<HttpResponseMessage>(ex);

    public static Reply Do(Func<RecordedRequest, FakeClock, HttpResponseMessage> f) => (req, clock) => Task.FromResult(f(req, clock));

    /// <summary>Never answers; ends when the attempt's token is cancelled.</summary>
    public static Reply Hang() => async (req, _) =>
    {
        await Task.Delay(Timeout.Infinite, req.Token);
        throw new InvalidOperationException("unreachable");
    };

    public static HttpResponseMessage Response(int status, byte[]? body, string? contentType, IEnumerable<(string Name, string Value)> headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent(body ?? Array.Empty<byte>()) };
        if (contentType is not null) response.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        foreach (var (name, value) in headers)
        {
            if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }
}

public static class Fixtures
{
    public const string Address = "ci-runner-7@example.org";
    public const string Encoded = "ci-runner-7%40example.org";
    public const string MessageId = "3c1b5f0e-6a8e-4b0e-9d3e-1f2a7c9b8e41";
    public const string AttachmentId = "9a7e2c44-0d0b-4f7a-a1c1-2b9d5f3e6a10";

    public const string MailboxJson = """
        {"address":"ci-runner-7@example.org","activeSince":"2026-10-22T09:00:00.481Z","expiresAt":"2026-10-23T09:00:00.481Z","storageUsedBytes":18422,"messagesCount":3}
        """;

    public static string MailboxWith(string address) => MailboxJson.Replace("ci-runner-7@example.org", address, StringComparison.Ordinal);

    public static string SimpleMessageJson(
        string id = "5d2e8a10-7b3c-4f1e-8a2d-9c4b6e1f3a57",
        string? code = "048213",
        string? confirmLink = "https://acme.example/verify?t=3f9a",
        string? subject = "Your verification code",
        string fromAddress = "no-reply@acme.example") =>
        $$"""
        {"id":"{{id}}","mailboxAddress":"ci-runner-7@example.org","createdAt":"2026-10-22T09:06:40.102Z","sizeBytes":9120,
         "from":{"name":"Acme","address":"{{fromAddress}}"},"subject":{{Str(subject)}},"bodyState":"STORED","attachmentsCount":0,
         "code":{{Str(code)}},"confirmLink":{{Str(confirmLink)}}}
        """;

    public static string DetailedMessageJson(string id = "5d2e8a10-7b3c-4f1e-8a2d-9c4b6e1f3a57", string codes = "[]", string links = "[]", string attachments = "[]") =>
        $$"""
        {"id":"{{id}}","createdAt":"2026-10-22T09:06:40.102Z","from":{"name":null,"address":"sender@example.com"},"subject":"Welcome",
         "bodyState":"STORED","html":"<p>hi</p>","text":null,"attachments":{{attachments}},"codes":{{codes}},"links":{{links}}}
        """;

    public static string PageJson(IEnumerable<string> items, int page = 0, int totalPages = 1, int size = 50)
    {
        var list = items.ToList();
        return $$"""{"content":[{{string.Join(",", list)}}],"page":{{page}},"size":{{size}},"totalPages":{{totalPages}},"totalElements":{{list.Count}}}""";
    }

    private static string Str(string? value) => value is null ? "null" : $"\"{value}\"";
}

[CollectionDefinition("Environment", DisableParallelization = true)]
public sealed class EnvironmentCollection
{
}
