using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FakeEmailPro;

/// <summary>
/// Base class of every exception this SDK throws. A call cancelled through its <see cref="CancellationToken"/> throws
/// <see cref="OperationCanceledException"/> instead.
/// </summary>
public class FakeEmailException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public FakeEmailException(string message) : base(message) { }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public FakeEmailException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
/// The server answered with a non-2xx status. The message is <c>"&lt;status&gt; &lt;code&gt;: &lt;message&gt;"</c>, or
/// <c>"&lt;status&gt;: HTTP &lt;status&gt;"</c> when the body is not the API's JSON envelope (e.g. a proxy's 502).
/// </summary>
public class ApiException : FakeEmailException
{
    internal ApiException(ApiErrorResponse response) : base(FormatMessage(response))
    {
        Status = response.Status;
        Code = ErrorField(response.Json, "error");
        Body = response.Body;
        BodyJson = response.Json;
        Headers = response.Headers;
        RateLimit = ParseRateLimit(response.Headers);
    }

    /// <summary>The HTTP status.</summary>
    public int Status { get; }

    /// <summary>
    /// The <c>error</c> field of the body (see <see cref="ErrorCodes"/>); <see langword="null"/> when the body is not
    /// the JSON envelope.
    /// </summary>
    public string? Code { get; }

    /// <summary>The raw response body; <see langword="null"/> when empty.</summary>
    public string? Body { get; }

    /// <summary>The parsed body when it is JSON; <see langword="null"/> otherwise.</summary>
    public JsonElement? BodyJson { get; }

    /// <summary>The response headers.</summary>
    public HttpResponseHeaders Headers { get; }

    /// <summary>
    /// The window state from <c>X-RateLimit-*</c>; <see langword="null"/> when the response did not carry them (the
    /// state is unknown, not zero).
    /// </summary>
    public RateLimitInfo? RateLimit { get; }

    private static string FormatMessage(ApiErrorResponse response)
    {
        var error = ErrorField(response.Json, "error");
        var phrase = ErrorField(response.Json, "message") ?? $"HTTP {response.Status}";
        return error is null ? $"{response.Status}: {phrase}" : $"{response.Status} {error}: {phrase}";
    }

    private static string? ErrorField(JsonElement? json, string name) =>
        json is { ValueKind: JsonValueKind.Object } obj
        && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static RateLimitInfo? ParseRateLimit(HttpResponseHeaders headers)
    {
        var limit = Header(headers, "X-RateLimit-Limit");
        var remaining = Header(headers, "X-RateLimit-Remaining");
        var reset = Header(headers, "X-RateLimit-Reset");
        return TryLong(limit, out var l) && TryLong(remaining, out var r) && TryLong(reset, out var s)
            ? new RateLimitInfo(l, r, s)
            : null;
    }

    private static bool TryLong(string? value, out long result) =>
        long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);

    internal static string? Header(HttpResponseHeaders headers, string name) =>
        headers.NonValidated.TryGetValues(name, out var values) ? values.ToString() : null;
}

/// <summary>400 <c>bad_request</c>: a parameter or the body is invalid.</summary>
public sealed class BadRequestException : ApiException
{
    internal BadRequestException(ApiErrorResponse response) : base(response) { }
}

/// <summary>401 <c>unauthorized</c>: the key is missing, unknown, revoked or expired.</summary>
public sealed class AuthenticationException : ApiException
{
    internal AuthenticationException(ApiErrorResponse response) : base(response) { }
}

/// <summary>402 <c>quota_exhausted</c>: the monthly mailbox quota is spent. Not retried: it renews at <see cref="ResetsAt"/>.</summary>
public sealed class QuotaExhaustedException : ApiException
{
    internal QuotaExhaustedException(ApiErrorResponse response) : base(response)
    {
        if (response.Json is not { ValueKind: JsonValueKind.Object } body) return;
        if (body.TryGetProperty("limit", out var limit) && limit.TryGetInt32(out var l)) Limit = l;
        if (body.TryGetProperty("used", out var used) && used.TryGetInt32(out var u)) Used = u;
        if (body.TryGetProperty("resetsAt", out var resetsAt) && resetsAt.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(resetsAt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r))
        {
            ResetsAt = r;
        }

        if (body.TryGetProperty("upgradeUrl", out var url) && url.ValueKind == JsonValueKind.String) UpgradeUrl = url.GetString();
    }

    /// <summary>Mailboxes per month on the plan.</summary>
    public int? Limit { get; }

    /// <summary>Mailboxes created this month.</summary>
    public int? Used { get; }

    /// <summary>When the quota renews: the 1st of next month, 00:00 UTC.</summary>
    public DateTimeOffset? ResetsAt { get; }

    /// <summary>Where to get a bigger quota.</summary>
    public string? UpgradeUrl { get; }
}

/// <summary>403 <c>plan_forbidden</c>: the plan has no API access, or does not allow a custom local part.</summary>
public sealed class PermissionDeniedException : ApiException
{
    internal PermissionDeniedException(ApiErrorResponse response) : base(response) { }
}

/// <summary>404 <c>not_found</c>: the mailbox is not an active API mailbox of yours, or the message or attachment is not there.</summary>
public sealed class NotFoundException : ApiException
{
    internal NotFoundException(ApiErrorResponse response) : base(response) { }
}

/// <summary>409 <c>address_taken</c>: the address was issued before, to anyone.</summary>
public sealed class ConflictException : ApiException
{
    internal ConflictException(ApiErrorResponse response) : base(response) { }
}

/// <summary>
/// 429: <see cref="ApiException.Code"/> is <c>rate_limit_exceeded</c> (requests per minute) or <c>too_many_waits</c>
/// (concurrent waits).
/// </summary>
public sealed class RateLimitException : ApiException
{
    internal RateLimitException(ApiErrorResponse response) : base(response)
    {
        var delay = RetryAfterHeader.Parse(Header(response.Headers, "Retry-After"), response.ReceivedAt);
        RetryAfter = delay is null ? null : TimeSpan.FromSeconds(Math.Ceiling(delay.Value.TotalSeconds));
    }

    /// <summary>From <c>Retry-After</c>, rounded up to whole seconds; <see langword="null"/> if absent.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>5xx, including a proxy's 502 during a server restart and 503 <c>service unavailable</c>.</summary>
public sealed class InternalServerErrorException : ApiException
{
    internal InternalServerErrorException(ApiErrorResponse response) : base(response) { }
}

/// <summary>The request did not get a response (DNS, refused connection, reset).</summary>
public class ApiConnectionException : FakeEmailException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ApiConnectionException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>An HTTP attempt took longer than the timeout.</summary>
public sealed class ApiTimeoutException : ApiConnectionException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ApiTimeoutException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// <see cref="FakeEmailClient.WaitForMessageAsync"/>, <see cref="FakeEmailClient.WaitForCodeAsync"/> or
/// <see cref="FakeEmailClient.WaitForLinkAsync"/> reached the deadline without a matching message.
/// </summary>
public sealed class WaitTimeoutException : FakeEmailException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="address">The mailbox that was watched.</param>
    /// <param name="timeout">The deadline of the wait.</param>
    public WaitTimeoutException(string address, TimeSpan timeout)
        : base($"no matching message in {address} within {timeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms")
    {
        Address = address;
        Timeout = timeout;
    }

    /// <summary>The mailbox that was watched.</summary>
    public string Address { get; }

    /// <summary>The deadline of the wait.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>What an error response is made of, read once by the transport.</summary>
internal sealed record ApiErrorResponse(
    int Status,
    HttpResponseHeaders Headers,
    string? Body,
    JsonElement? Json,
    DateTimeOffset ReceivedAt)
{
    public static ApiException ToException(ApiErrorResponse r) => r.Status switch
    {
        400 => new BadRequestException(r),
        401 => new AuthenticationException(r),
        402 => new QuotaExhaustedException(r),
        403 => new PermissionDeniedException(r),
        404 => new NotFoundException(r),
        409 => new ConflictException(r),
        429 => new RateLimitException(r),
        >= 500 => new InternalServerErrorException(r),
        _ => new ApiException(r),
    };
}
