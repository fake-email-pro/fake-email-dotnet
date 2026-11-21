using static FakeEmailPro.Tests.Fixtures;

namespace FakeEmailPro.Tests;

public sealed class WaitForMessageTests
{
    /// <summary>A wait reply that "holds" for the requested server timeout on the virtual clock, then answers 204.</summary>
    internal static Reply Hold() => R.Do((req, clock) =>
    {
        clock.Advance(TimeSpan.FromSeconds(int.Parse(req.Query("timeout") ?? "30", System.Globalization.CultureInfo.InvariantCulture)));
        return R.Response(204, null, null, []);
    });

    [Fact]
    public async Task Returns_the_first_message_and_passes_the_filters()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson())]);
        var res = await t.Client.WaitForMessageAsync(Address, new WaitOptions { From = "acme", Subject = "code", Timeout = TimeSpan.FromSeconds(30) });
        Assert.Equal("048213", res.Code);
        Assert.Equal(new Dictionary<string, string> { ["timeout"] = "30", ["from"] = "acme", ["subject"] = "code" }, t.Requests[0].QueryParams);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/wait", t.Requests[0].Path);
    }

    [Fact]
    public async Task Loops_on_204_with_timeout_min_of_remaining_and_60_s()
    {
        using var t = new TestSetup([Hold(), Hold(), R.Json(SimpleMessageJson())]);
        await t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(150) });
        Assert.Equal(["60", "60", "30"], t.Requests.Select(r => r.Query("timeout")));
    }

    [Fact]
    public async Task Defaults_to_a_60_s_deadline()
    {
        using var t = new TestSetup([Hold()]);
        await Assert.ThrowsAsync<WaitTimeoutException>(() => t.Client.WaitForMessageAsync(Address));
        Assert.Equal(["60"], t.Requests.Select(r => r.Query("timeout")));
    }

    [Fact]
    public async Task Throws_WaitTimeoutException_at_the_deadline()
    {
        using var t = new TestSetup([Hold(), Hold()]);
        var e = await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromMilliseconds(90_500) }));
        Assert.Equal(Address, e.Address);
        Assert.Equal(TimeSpan.FromMilliseconds(90_500), e.Timeout);
        Assert.Equal(["60", "31"], t.Requests.Select(r => r.Query("timeout")));
    }

    [Fact]
    public async Task Passes_after_as_an_id_or_a_time_on_the_first_call()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson()), R.Json(SimpleMessageJson())]);
        await t.Client.WaitForMessageAsync(Address, new WaitOptions { After = MessageId });
        Assert.Equal(MessageId, t.Requests[0].Query("after"));
        await t.Client.WaitForMessageAsync(Address, new WaitOptions { After = new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero) });
        Assert.Equal("2026-10-22T09:00:00.000Z", t.Requests[1].Query("after"));
    }

    [Fact]
    public async Task Skips_messages_failing_Match_and_continues_after_them()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson(id: "m1", subject: "Welcome")), R.Json(SimpleMessageJson(id: "m2", subject: "Reset your password"))]);
        var res = await t.Client.WaitForMessageAsync(Address, new WaitOptions { Match = m => m.Subject?.Contains("password", StringComparison.Ordinal) ?? false });
        Assert.Equal("m2", res.Id);
        Assert.Null(t.Requests[0].Query("after"));
        Assert.Equal("m1", t.Requests[1].Query("after"));
    }

    [Fact]
    public async Task Pauses_instead_of_spinning_when_a_204_comes_back_early()
    {
        using var t = new TestSetup([R.Empty(204), R.Json(SimpleMessageJson())]);
        await t.Client.WaitForMessageAsync(Address);
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal([1000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task The_early_pause_never_passes_the_deadline()
    {
        using var t = new TestSetup([R.Empty(204)]);
        await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromMilliseconds(300) }));
        Assert.Equal([300.0], t.Clock.Sleeps);
        Assert.Equal("1", t.Requests[0].Query("timeout"));
    }

    [Fact]
    public async Task Waits_out_too_many_waits_beyond_MaxRetries_while_the_deadline_allows()
    {
        Reply TooMany() => R.ApiError(429, "too_many_waits", "too many concurrent waits", ("Retry-After", "1"));
        using var t = new TestSetup([TooMany(), TooMany(), TooMany(), TooMany(), R.Json(SimpleMessageJson())]);
        var res = await t.Client.WaitForMessageAsync(Address);
        Assert.Equal("5d2e8a10-7b3c-4f1e-8a2d-9c4b6e1f3a57", res.Id);
        Assert.Equal(5, t.Requests.Count);
    }

    [Fact]
    public async Task A_429_without_Retry_After_is_waited_out_for_1_s()
    {
        using var t = new TestSetup([R.ApiError(429, "too_many_waits", "x"), R.Json(SimpleMessageJson())], o => o.MaxRetries = 0);
        await t.Client.WaitForMessageAsync(Address);
        Assert.Equal([1000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task A_429_whose_Retry_After_passes_the_deadline_sleeps_to_the_deadline_then_times_out()
    {
        using var t = new TestSetup([R.ApiError(429, "rate_limit_exceeded", "rate limit exceeded", ("Retry-After", "50"))]);
        var e = await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(10) }));
        Assert.IsType<RateLimitException>(e.InnerException);
        Assert.Single(t.Requests);
        Assert.Equal([10_000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task Does_not_swallow_a_404()
    {
        using var t = new TestSetup([R.ApiError(404, "not_found", "mailbox not found or expired")]);
        await Assert.ThrowsAsync<NotFoundException>(() => t.Client.WaitForMessageAsync(Address));
    }

    [Fact]
    public async Task Stops_when_the_token_is_cancelled()
    {
        using var cts = new CancellationTokenSource();
        using var t = new TestSetup([
            R.Do((_, _) =>
            {
                cts.Cancel();
                return R.Response(204, null, null, []);
            }),
            R.Json(SimpleMessageJson()),
        ]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.WaitForMessageAsync(Address, cancellationToken: cts.Token));
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Throws_at_once_with_an_already_cancelled_token()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson())]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.WaitForMessageAsync(Address, cancellationToken: new CancellationToken(true)));
        Assert.Empty(t.Requests);
    }
}

public sealed class WaitForCodeTests
{
    [Fact]
    public async Task Returns_the_code_computed_on_delivery()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson(code: "550712"))]);
        Assert.Equal("550712", await t.Client.WaitForCodeAsync(Address));
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Falls_back_to_the_first_code_of_the_full_message()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson(id: "m1", code: null)), R.Json(DetailedMessageJson(id: "m1", codes: """["9911","1234"]"""))]);
        Assert.Equal("9911", await t.Client.WaitForCodeAsync(Address));
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/m1", t.Requests[1].Path);
    }

    [Fact]
    public async Task Keeps_waiting_after_a_message_without_any_code()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1", code: null)),
            R.Json(DetailedMessageJson(id: "m1")),
            R.Json(SimpleMessageJson(id: "m2", code: "777888")),
        ]);
        Assert.Equal("777888", await t.Client.WaitForCodeAsync(Address, new WaitOptions { Subject = "code" }));
        Assert.Equal("m1", t.Requests[2].Query("after"));
        Assert.Equal("code", t.Requests[2].Query("subject"));
    }

    [Fact]
    public async Task Applies_Match_before_extracting()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1", code: "111111", fromAddress: "a@other.example")),
            R.Json(SimpleMessageJson(id: "m2", code: "222222")),
        ]);
        var code = await t.Client.WaitForCodeAsync(Address, new WaitOptions { Match = m => m.From?.Address == "no-reply@acme.example" });
        Assert.Equal("222222", code);
    }
}

public sealed class WaitForLinkTests
{
    [Fact]
    public async Task Returns_ConfirmLink()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson(confirmLink: "https://acme.example/v?t=1"))]);
        Assert.Equal("https://acme.example/v?t=1", await t.Client.WaitForLinkAsync(Address));
    }

    [Fact]
    public async Task Falls_back_to_the_first_link_then_keeps_waiting()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1", confirmLink: null)),
            R.Json(DetailedMessageJson(id: "m1")),
            R.Json(SimpleMessageJson(id: "m2", confirmLink: null)),
            R.Json(DetailedMessageJson(id: "m2", links: """[{"url":"https://acme.example/magic","text":"Sign in"}]""")),
        ]);
        Assert.Equal("https://acme.example/magic", await t.Client.WaitForLinkAsync(Address));
        Assert.Equal("m1", t.Requests[2].Query("after"));
    }
}

public sealed class WaitLoopRetryTests
{
    private static Reply Busy(string? retryAfter = null) => retryAfter is null
        ? R.ApiError(503, "internal_server_error", "service unavailable")
        : R.ApiError(503, "internal_server_error", "service unavailable", ("Retry-After", retryAfter));

    private static Reply AfterNotFound() => R.ApiError(404, "not_found", "message in after not found");

    [Fact]
    public async Task Waits_out_every_transient_error_with_one_request_each()
    {
        using var t = new TestSetup([
            R.Text("bad gateway", 502),
            Busy("2"),
            R.Empty(504),
            R.Text("origin down", 520),
            R.Text("timeout", 524),
            R.Throw(new HttpRequestException("reset")),
            R.ApiError(429, "too_many_waits", "x"),
            R.Json(SimpleMessageJson()),
        ]);
        var res = await t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(60) });
        Assert.Equal("048213", res.Code);
        Assert.Equal(8, t.Requests.Count);
        Assert.Equal([1000.0, 2000.0, 1000.0, 1000.0, 1000.0, 1000.0, 1000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task An_HTTP_timeout_of_a_wait_attempt_is_waited_out_too()
    {
        var timers = 0;
        using var t = new TestSetup(
            [R.Hang(), R.Json(SimpleMessageJson())],
            attemptTimer: _ => ++timers == 1 ? new CancellationTokenSource(TimeSpan.FromMilliseconds(20)) : new CancellationTokenSource());
        await t.Client.WaitForMessageAsync(Address);
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal([1000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task Each_attempt_asks_for_the_time_left()
    {
        using var t = new TestSetup([Busy("5"), Busy("5"), R.Json(SimpleMessageJson())]);
        await t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(30) });
        Assert.Equal(["30", "25", "20"], t.Requests.Select(r => r.Query("timeout")));
    }

    [Fact]
    public async Task Never_sleeps_past_the_deadline_and_ends_with_WaitTimeoutException()
    {
        using var t = new TestSetup([Busy("2"), Busy("2")]);
        var e = await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(3) }));
        Assert.Equal([2000.0, 1000.0], t.Clock.Sleeps);
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal(FakeClock.Start + TimeSpan.FromSeconds(3), t.Clock.T);
        var inner = Assert.IsType<InternalServerErrorException>(e.InnerException);
        Assert.Equal(503, inner.Status);
    }

    [Fact]
    public async Task A_transport_retry_of_the_wait_request_is_never_made()
    {
        // MaxRetries would allow 5 transport retries; the loop sends each wait once and owns the pauses, so the
        // pause that reaches the deadline ends the wait instead of a retry.
        using var t = new TestSetup([Busy("1"), R.Json(SimpleMessageJson())], o => o.MaxRetries = 5);
        await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(1) }));
        Assert.Single(t.Requests);
        Assert.Equal([1000.0], t.Clock.Sleeps);
    }

    [Theory]
    [InlineData(400, "bad_request", typeof(BadRequestException))]
    [InlineData(401, "unauthorized", typeof(AuthenticationException))]
    [InlineData(402, "quota_exhausted", typeof(QuotaExhaustedException))]
    [InlineData(403, "plan_forbidden", typeof(PermissionDeniedException))]
    [InlineData(404, "not_found", typeof(NotFoundException))]
    [InlineData(409, "address_taken", typeof(ConflictException))]
    [InlineData(500, "internal_server_error", typeof(InternalServerErrorException))]
    public async Task Other_errors_are_thrown_at_once(int status, string code, Type expected)
    {
        using var t = new TestSetup([R.ApiError(status, code, "x"), R.Json(SimpleMessageJson())]);
        var e = await Assert.ThrowsAnyAsync<ApiException>(() => t.Client.WaitForMessageAsync(Address));
        Assert.IsType(expected, e);
        Assert.Single(t.Requests);
        Assert.Empty(t.Clock.Sleeps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_timeout_of_zero_or_less_is_already_expired(int seconds)
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson())]);
        await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForCodeAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(seconds) }));
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task A_short_WithOptions_timeout_does_not_cut_the_long_poll()
    {
        var timers = new List<TimeSpan>();
        using var t = new TestSetup(
            [WaitForMessageTests.Hold(), R.Json(SimpleMessageJson())],
            attemptTimer: timeout =>
            {
                timers.Add(timeout);
                return new CancellationTokenSource();
            });
        await t.Client.WithOptions(new RequestOptions { Timeout = TimeSpan.FromSeconds(2) })
            .WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(90) });
        Assert.Equal([TimeSpan.FromSeconds(70), TimeSpan.FromSeconds(40)], timers);
    }

    [Fact]
    public async Task After_a_skipped_message_is_deleted_the_wait_continues_after_its_time()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1", subject: "Welcome")),
            AfterNotFound(),
            R.Json(SimpleMessageJson(id: "m2", subject: "Reset your password")),
        ]);
        var res = await t.Client.WaitForMessageAsync(Address, new WaitOptions { Match = m => m.Subject?.Contains("password", StringComparison.Ordinal) ?? false });
        Assert.Equal("m2", res.Id);
        Assert.Equal("m1", t.Requests[1].Query("after"));
        Assert.Equal("2026-10-22T09:06:40.102Z", t.Requests[2].Query("after"));
    }

    [Fact]
    public async Task The_fallback_applies_to_a_message_without_a_code_too()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1", code: null)),
            R.Json(DetailedMessageJson(id: "m1")),
            AfterNotFound(),
            R.Json(SimpleMessageJson(id: "m2", code: "777888")),
        ]);
        Assert.Equal("777888", await t.Client.WaitForCodeAsync(Address));
        Assert.Equal("2026-10-22T09:06:40.102Z", t.Requests[3].Query("after"));
    }

    [Fact]
    public async Task An_After_id_of_the_caller_that_is_gone_is_still_a_NotFoundException()
    {
        using var t = new TestSetup([AfterNotFound()]);
        await Assert.ThrowsAsync<NotFoundException>(() => t.Client.WaitForMessageAsync(Address, new WaitOptions { After = MessageId }));
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task A_404_for_the_mailbox_after_a_skip_is_still_thrown()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1")),
            R.ApiError(404, "not_found", "mailbox not found or expired"),
        ]);
        await Assert.ThrowsAsync<NotFoundException>(() => t.Client.WaitForMessageAsync(Address, new WaitOptions { Match = _ => false }));
        Assert.Equal(2, t.Requests.Count);
    }

    [Fact]
    public async Task A_transient_error_of_the_full_message_read_is_waited_out_without_losing_the_message()
    {
        using var t = new TestSetup([
            R.Json(SimpleMessageJson(id: "m1", code: null)),
            Busy("1"),
            R.Json(SimpleMessageJson(id: "m1", code: null)),
            R.Json(DetailedMessageJson(id: "m1", codes: """["4242"]""")),
        ], o => o.MaxRetries = 5);
        Assert.Equal("4242", await t.Client.WaitForCodeAsync(Address));
        Assert.Equal(["wait", "m1", "wait", "m1"], t.Requests.Select(r => r.Path.Split('/')[^1]));
        Assert.Null(t.Requests[2].Query("after"));
        Assert.Equal([1000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task The_full_message_read_is_cut_short_at_the_deadline()
    {
        var timers = new List<TimeSpan>();
        using var t = new TestSetup(
            [
                R.Do((_, clock) =>
                {
                    clock.Advance(TimeSpan.FromSeconds(4));
                    return R.Response(200, System.Text.Encoding.UTF8.GetBytes(SimpleMessageJson(id: "m1", confirmLink: null)), "application/json", []);
                }),
                R.Hang(),
            ],
            attemptTimer: timeout =>
            {
                timers.Add(timeout);
                return timers.Count == 2 ? new CancellationTokenSource(TimeSpan.FromMilliseconds(20)) : new CancellationTokenSource();
            });
        var e = await Assert.ThrowsAsync<WaitTimeoutException>(() =>
            t.Client.WaitForLinkAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(5) }));
        Assert.IsType<ApiTimeoutException>(e.InnerException);
        Assert.Equal(TimeSpan.FromSeconds(1), timers[1]);
        Assert.Equal(2, t.Requests.Count);
    }
}
