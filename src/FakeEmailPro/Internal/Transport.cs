using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace FakeEmailPro;

/// <summary>Test seams: the clock, sleeps, jitter and per-attempt timers. The defaults are the real ones.</summary>
internal sealed class ClientHooks
{
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public Func<TimeSpan, CancellationToken, Task> Sleep { get; init; } = (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Source of backoff jitter in [0, 1).</summary>
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    /// <summary>A token source cancelled when one HTTP attempt runs out of time.</summary>
    public Func<TimeSpan, CancellationTokenSource> AttemptTimer { get; init; } = timeout => new CancellationTokenSource(timeout);
}

internal sealed class ApiRequest
{
    public required HttpMethod Method { get; init; }

    public required string Path { get; init; }

    public IReadOnlyList<KeyValuePair<string, string?>>? Query { get; init; }

    public byte[]? JsonBody { get; init; }

    /// <summary>Overrides the timeout of each HTTP attempt.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>A time (hooks clock) past which a retry is pointless: the caller has given up by then.</summary>
    public DateTimeOffset? Deadline { get; init; }
}

internal sealed record ApiResponse(int Status, HttpResponseHeaders Headers, HttpContentHeaders ContentHeaders, byte[] Body)
{
    public string? ContentHeader(string name) =>
        ContentHeaders.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;
}

/// <summary>The HTTP layer shared by a client and every view of it made with <c>WithOptions</c>.</summary>
internal sealed class ApiTransport
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);
    private const double BackoffBaseMs = 500;
    private const double BackoffMaxMs = 8_000;

    public ApiTransport(HttpClient http, string apiKey, string baseUrl, string userAgent, TimeSpan timeout, int maxRetries, ClientHooks hooks)
    {
        Http = http;
        ApiKey = apiKey;
        BaseUrl = baseUrl;
        UserAgent = userAgent;
        Timeout = timeout;
        MaxRetries = maxRetries;
        Hooks = hooks;
    }

    public HttpClient Http { get; }

    public string ApiKey { get; }

    public string BaseUrl { get; }

    public string UserAgent { get; }

    public TimeSpan Timeout { get; }

    public int MaxRetries { get; }

    public ClientHooks Hooks { get; }

    public async Task<ApiResponse> SendAsync(ApiRequest request, int maxRetries, TimeSpan timeout, CancellationToken ct)
    {
        // DELETE is idempotent here too: a repeated delete is a 404 the caller can tolerate, never a second effect.
        var idempotent = request.Method != HttpMethod.Post;
        var url = BuildUrl(request.Path, request.Query);

        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            ApiResponse response;
            try
            {
                response = await AttemptAsync(request, url, timeout, ct).ConfigureAwait(false);
            }
            catch (ApiConnectionException)
            {
                var delay = Backoff(attempt);
                if (idempotent && attempt < maxRetries && Fits(delay, request.Deadline))
                {
                    await Hooks.Sleep(delay, ct).ConfigureAwait(false);
                    continue;
                }

                throw;
            }

            if (response.Status is >= 200 and < 300) return response;

            var error = ToError(response);
            var status = response.Status;
            // 429 and 503 are refused before any effect, so even POST is safe to repeat; 502/504 may have reached
            // the handler, so only idempotent requests are repeated.
            var retryable = status is 429 or 503 || (idempotent && status is 502 or 504);
            if (retryable && attempt < maxRetries)
            {
                var retryAfter = RetryAfterHeader.Parse(ApiException.Header(response.Headers, "Retry-After"), Hooks.Now());
                var delay = retryAfter is null ? Backoff(attempt) : Min(retryAfter.Value, MaxRetryAfter);
                if (Fits(delay, request.Deadline))
                {
                    await Hooks.Sleep(delay, ct).ConfigureAwait(false);
                    continue;
                }
            }

            throw error;
        }
    }

    private async Task<ApiResponse> AttemptAsync(ApiRequest request, Uri url, TimeSpan timeout, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(request.Method, url);
        message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + ApiKey);
        message.Headers.TryAddWithoutValidation("Accept", "application/json");
        message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (request.JsonBody is not null)
        {
            message.Content = new ByteArrayContent(request.JsonBody);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var timer = Hooks.AttemptTimer(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        try
        {
            // The default completion option buffers the body, so the attempt timeout covers reading it too.
            using var response = await Http.SendAsync(message, linked.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false);
            return new ApiResponse((int)response.StatusCode, response.Headers, response.Content.Headers, body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // Our timer, or the Timeout of an injected HttpClient.
            var ms = timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture);
            throw new ApiTimeoutException($"request timed out after {ms} ms", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new ApiConnectionException($"connection error: {Describe(ex)}", ex);
        }
    }

    private ApiException ToError(ApiResponse response)
    {
        string? text = null;
        JsonElement? json = null;
        if (response.Body.Length > 0)
        {
            text = Encoding.UTF8.GetString(response.Body);
            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                json = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // A proxy's HTML or plain-text error page: keep the text, map by status.
            }
        }

        return ApiErrorResponse.ToException(new ApiErrorResponse(response.Status, response.Headers, text, json, Hooks.Now()));
    }

    private Uri BuildUrl(string path, IReadOnlyList<KeyValuePair<string, string?>>? query)
    {
        var sb = new StringBuilder(BaseUrl.TrimEnd('/')).Append(path);
        var separator = '?';
        foreach (var (key, value) in query ?? Array.Empty<KeyValuePair<string, string?>>())
        {
            if (value is null) continue;
            sb.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return new Uri(sb.ToString(), UriKind.Absolute);
    }

    private TimeSpan Backoff(int attempt)
    {
        var baseMs = Math.Min(BackoffBaseMs * Math.Pow(2, attempt), BackoffMaxMs);
        // Up to 25% jitter downwards, so parallel test workers do not retry in lockstep.
        return TimeSpan.FromMilliseconds(Math.Round(baseMs * (1 - (0.25 * Hooks.Random())), MidpointRounding.AwayFromZero));
    }

    private bool Fits(TimeSpan delay, DateTimeOffset? deadline) => deadline is null || Hooks.Now() + delay < deadline.Value;

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private static string Describe(Exception ex) =>
        ex.InnerException is { } inner ? $"{ex.Message} ({inner.Message})" : ex.Message;
}

/// <summary>A client's transport plus the per-call overrides of a <c>WithOptions</c> view.</summary>
internal sealed class ApiCaller
{
    public ApiCaller(ApiTransport transport, RequestOptions overrides)
    {
        Transport = transport;
        Overrides = overrides;
    }

    public ApiTransport Transport { get; }

    public RequestOptions Overrides { get; }

    public Task<ApiResponse> SendAsync(ApiRequest request, CancellationToken ct) =>
        Transport.SendAsync(
            request,
            Overrides.MaxRetries ?? Transport.MaxRetries,
            request.Timeout ?? Overrides.Timeout ?? Transport.Timeout,
            ct);

    public async Task<T> JsonAsync<T>(ApiRequest request, JsonTypeInfo<T> type, CancellationToken ct)
    {
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        return Deserialize(response.Body, type) ?? throw new FakeEmailException($"empty response body from {request.Method} {request.Path}");
    }

    /// <summary>For <c>wait</c>, which answers <c>204</c> on timeout.</summary>
    public async Task<T?> JsonOrNullAsync<T>(ApiRequest request, JsonTypeInfo<T> type, CancellationToken ct)
        where T : class
    {
        var response = await SendAsync(request, ct).ConfigureAwait(false);
        return response.Status == 204 || response.Body.Length == 0 ? null : Deserialize(response.Body, type);
    }

    private static T? Deserialize<T>(byte[] body, JsonTypeInfo<T> type)
    {
        try
        {
            return JsonSerializer.Deserialize(body, type);
        }
        catch (JsonException ex)
        {
            throw new FakeEmailException($"unexpected response body: {ex.Message}", ex);
        }
    }
}

internal static class RetryAfterHeader
{
    private static readonly Regex DeltaSeconds = new(@"^\d+(\.\d+)?$", RegexOptions.CultureInvariant);

    // Far beyond any pause the client waits, far below what TimeSpan.FromSeconds overflows on.
    private static readonly TimeSpan MaxParsed = TimeSpan.FromDays(365);

    /// <summary><c>Retry-After</c> as a delay from <paramref name="now"/> (delta-seconds or an HTTP date); null if absent or unreadable.</summary>
    public static TimeSpan? Parse(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (DeltaSeconds.IsMatch(v)) return TimeSpan.FromSeconds(Math.Min(double.Parse(v, CultureInfo.InvariantCulture), MaxParsed.TotalSeconds));
        if (!DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return null;
        var delay = date - now;
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}

internal static class Url
{
    /// <summary>
    /// A path segment encoded like JavaScript's <c>encodeURIComponent</c>: everything but <c>A-Z a-z 0-9 - _ . ! ~ * ' ( )</c>,
    /// so <c>@</c> becomes <c>%40</c> (the server accepts it).
    /// </summary>
    public static string Segment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value)
            .Replace("%21", "!", StringComparison.Ordinal)
            .Replace("%27", "'", StringComparison.Ordinal)
            .Replace("%28", "(", StringComparison.Ordinal)
            .Replace("%29", ")", StringComparison.Ordinal)
            .Replace("%2A", "*", StringComparison.Ordinal);
    }

    /// <summary>RFC 3339 in UTC with milliseconds, like JavaScript's <c>Date.toISOString</c>.</summary>
    public static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
