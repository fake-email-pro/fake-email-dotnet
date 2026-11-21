using System.Text.Json;

namespace FakeEmailPro.Tests;

public sealed class ErrorTests
{
    private static readonly (string, string)[] RateLimitHeaders =
    [
        ("X-RateLimit-Limit", "60"),
        ("X-RateLimit-Remaining", "0"),
        ("X-RateLimit-Reset", "1792659660"),
    ];

    private static async Task<Exception> FailWith(Reply reply)
    {
        using var t = new TestSetup([reply], o => o.MaxRetries = 0);
        return await Record.ExceptionAsync(() => t.Client.Mailboxes.CreateAsync()) ?? throw new InvalidOperationException("no exception");
    }

    public static TheoryData<int, string, string, Type> Mappings => new()
    {
        { 400, "bad_request", "invalid localPart", typeof(BadRequestException) },
        { 401, "unauthorized", "missing or invalid API key", typeof(AuthenticationException) },
        { 402, "quota_exhausted", "monthly mailbox quota exhausted", typeof(QuotaExhaustedException) },
        { 403, "plan_forbidden", "plan does not allow custom localPart", typeof(PermissionDeniedException) },
        { 404, "not_found", "domain not found", typeof(NotFoundException) },
        { 409, "address_taken", "address taken", typeof(ConflictException) },
        { 429, "rate_limit_exceeded", "rate limit exceeded", typeof(RateLimitException) },
        { 500, "internal_server_error", "internal server error", typeof(InternalServerErrorException) },
        { 503, "internal_server_error", "service unavailable", typeof(InternalServerErrorException) },
        { 405, "method_not_allowed", "method not allowed", typeof(ApiException) },
        { 413, "payload_too_large", "request body too large", typeof(ApiException) },
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public async Task Maps_each_status_to_its_exception(int status, string code, string message, Type type)
    {
        var err = await FailWith(R.ApiError(status, code, message));
        Assert.IsType(type, err);
        var e = Assert.IsAssignableFrom<ApiException>(err);
        Assert.IsAssignableFrom<FakeEmailException>(err);
        Assert.Equal(status, e.Status);
        Assert.Equal(code, e.Code);
        Assert.Equal($"{status} {code}: {message}", e.Message);
        Assert.Equal($$"""{"error":"{{code}}","message":"{{message}}"}""", e.Body);
        Assert.Equal(JsonValueKind.Object, e.BodyJson?.ValueKind);
        Assert.Equal(message, e.BodyJson?.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Quota_exhausted_carries_the_quota_fields()
    {
        var err = await FailWith(R.Json("""
            {"error":"quota_exhausted","message":"monthly mailbox quota exhausted","limit":20,"used":20,
             "resetsAt":"2026-11-01T00:00:00.000Z","upgradeUrl":"https://fake-email.pro/pricing"}
            """, 402, RateLimitHeaders));
        var e = Assert.IsType<QuotaExhaustedException>(err);
        Assert.Equal("quota_exhausted", e.Code);
        Assert.Equal(ErrorCodes.QuotaExhausted, e.Code);
        Assert.Equal(20, e.Limit);
        Assert.Equal(20, e.Used);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), e.ResetsAt);
        Assert.Equal("https://fake-email.pro/pricing", e.UpgradeUrl);
        Assert.Equal(new RateLimitInfo(60, 0, 1792659660), e.RateLimit);
    }

    [Fact]
    public async Task Quota_fields_are_null_when_the_body_lacks_them()
    {
        var e = Assert.IsType<QuotaExhaustedException>(await FailWith(R.Text("quota", 402)));
        Assert.Null(e.Limit);
        Assert.Null(e.Used);
        Assert.Null(e.ResetsAt);
        Assert.Null(e.UpgradeUrl);
    }

    [Fact]
    public async Task Rate_limit_carries_RetryAfter_and_the_code_that_tells_the_two_cases_apart()
    {
        var err = await FailWith(R.ApiError(429, "too_many_waits", "too many concurrent waits",
            [("Retry-After", "1"), .. RateLimitHeaders]));
        var e = Assert.IsType<RateLimitException>(err);
        Assert.Equal(ErrorCodes.TooManyWaits, e.Code);
        Assert.Equal(TimeSpan.FromSeconds(1), e.RetryAfter);
        Assert.Equal(new RateLimitInfo(60, 0, 1792659660), e.RateLimit);
        Assert.Equal("1", e.Headers.NonValidated["Retry-After"].ToString());
    }

    [Fact]
    public async Task RetryAfter_is_rounded_up_and_reads_HTTP_dates()
    {
        var fractional = Assert.IsType<RateLimitException>(await FailWith(R.ApiError(429, "rate_limit_exceeded", "x", ("Retry-After", "1.2"))));
        Assert.Equal(TimeSpan.FromSeconds(2), fractional.RetryAfter);

        var at = (FakeClock.Start + TimeSpan.FromMilliseconds(2500)).ToString("r");
        var dated = Assert.IsType<RateLimitException>(await FailWith(R.ApiError(429, "rate_limit_exceeded", "x", ("Retry-After", at))));
        Assert.Equal(TimeSpan.FromSeconds(2), dated.RetryAfter); // "r" drops the milliseconds: 2 s exactly

        var none = Assert.IsType<RateLimitException>(await FailWith(R.ApiError(429, "rate_limit_exceeded", "x")));
        Assert.Null(none.RetryAfter);

        // Past what a TimeSpan holds: still a RateLimitException, never an OverflowException.
        var huge = Assert.IsType<RateLimitException>(await FailWith(R.ApiError(429, "rate_limit_exceeded", "x", ("Retry-After", "99999999999999"))));
        Assert.True(huge.RetryAfter > TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task RateLimit_is_null_without_the_headers()
    {
        var e = Assert.IsAssignableFrom<ApiException>(await FailWith(R.ApiError(401, "unauthorized", "missing or invalid API key")));
        Assert.Null(e.RateLimit);
    }

    [Fact]
    public async Task A_non_JSON_502_from_a_proxy_still_maps_by_status()
    {
        using var t = new TestSetup([R.Text("<html>502 Bad Gateway</html>", 502, "text/html")], o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<InternalServerErrorException>(() => t.Client.Domains.ListAsync());
        Assert.Equal(502, e.Status);
        Assert.Null(e.Code);
        Assert.Equal("<html>502 Bad Gateway</html>", e.Body);
        Assert.Null(e.BodyJson);
        Assert.Equal("502: HTTP 502", e.Message);
    }

    [Fact]
    public async Task A_JSON_body_without_the_envelope_uses_the_status_phrase()
    {
        var e = Assert.IsAssignableFrom<ApiException>(await FailWith(R.Json("""{"detail":"nope"}""", 418)));
        Assert.Equal("418: HTTP 418", e.Message);
        Assert.Null(e.Code);
    }

    [Fact]
    public async Task An_empty_error_body_maps_by_status()
    {
        using var t = new TestSetup([R.Empty(404)]);
        var e = await Assert.ThrowsAsync<NotFoundException>(() => t.Client.Domains.ListAsync());
        Assert.Null(e.Body);
        Assert.Null(e.BodyJson);
        Assert.Equal("404: HTTP 404", e.Message);
    }

    [Fact]
    public async Task A_network_failure_is_an_ApiConnectionException_with_the_cause()
    {
        var cause = new HttpRequestException("Connection refused");
        using var t = new TestSetup([R.Throw(cause)], o => o.MaxRetries = 0);
        var e = await Assert.ThrowsAsync<ApiConnectionException>(() => t.Client.Domains.ListAsync());
        Assert.IsNotType<ApiTimeoutException>(e);
        Assert.Same(cause, e.InnerException);
        Assert.Equal("connection error: Connection refused", e.Message);
    }

    [Fact]
    public async Task An_unparsable_success_body_is_a_FakeEmailException()
    {
        using var t = new TestSetup([R.Text("not json", 200)]);
        var e = await Assert.ThrowsAsync<FakeEmailException>(() => t.Client.Limits.GetAsync());
        Assert.IsAssignableFrom<JsonException>(e.InnerException);
    }

    [Fact]
    public void WaitTimeoutException_message()
    {
        var e = new WaitTimeoutException("a@example.org", TimeSpan.FromMilliseconds(90_500));
        Assert.Equal("no matching message in a@example.org within 90500 ms", e.Message);
        Assert.IsAssignableFrom<FakeEmailException>(e);
    }
}
