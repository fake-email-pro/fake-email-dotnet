using static FakeEmailPro.Tests.Fixtures;

namespace FakeEmailPro.Tests;

public sealed class RetryTests
{
    private static Reply Busy() => R.ApiError(503, "internal_server_error", "service unavailable", ("Retry-After", "1"));

    private static Reply Limited(string retryAfter) => R.ApiError(429, "rate_limit_exceeded", "rate limit exceeded", ("Retry-After", retryAfter));

    private static Reply BadGateway() => R.Text("bad gateway", 502);

    private static Reply Domains() => R.Json("""{"domains":[]}""");

    [Fact]
    public async Task Retries_429_honoring_Retry_After()
    {
        using var t = new TestSetup([Limited("7"), Domains()]);
        Assert.Empty(await t.Client.Domains.ListAsync());
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal([7000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task Accepts_Retry_After_as_an_HTTP_date()
    {
        var at = (FakeClock.Start + TimeSpan.FromSeconds(3)).ToString("r");
        using var t = new TestSetup([Limited(at), Domains()]);
        await t.Client.Domains.ListAsync();
        Assert.Equal([3000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task Caps_Retry_After_at_60_s()
    {
        using var t = new TestSetup([Limited("3600"), Domains()]);
        await t.Client.Domains.ListAsync();
        Assert.Equal([60_000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task A_Retry_After_too_large_for_a_TimeSpan_is_capped_too()
    {
        using var t = new TestSetup([Limited("99999999999999"), Domains()]);
        await t.Client.Domains.ListAsync();
        Assert.Equal([60_000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task Retries_429_and_503_on_POST_too()
    {
        using var t = new TestSetup([Limited("1"), Busy(), R.Json(MailboxJson, 201)]);
        var mb = await t.Client.Mailboxes.CreateAsync();
        Assert.Equal(Address, mb.Address);
        Assert.Equal([HttpMethod.Post, HttpMethod.Post, HttpMethod.Post], t.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task Gives_up_after_MaxRetries_and_throws_the_last_error()
    {
        using var t = new TestSetup([Limited("1"), Limited("1"), Limited("1")]);
        await Assert.ThrowsAsync<RateLimitException>(() => t.Client.Domains.ListAsync());
        Assert.Equal(3, t.Requests.Count);
    }

    [Fact]
    public async Task Respects_a_per_call_MaxRetries()
    {
        using var t = new TestSetup([Limited("1")]);
        await Assert.ThrowsAsync<RateLimitException>(() => t.Client.WithOptions(new RequestOptions { MaxRetries = 0 }).Domains.ListAsync());
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Does_not_retry_POST_on_502()
    {
        using var t = new TestSetup([BadGateway(), R.Json(MailboxJson, 201)]);
        await Assert.ThrowsAsync<InternalServerErrorException>(() => t.Client.Mailboxes.CreateAsync());
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Does_not_retry_POST_on_a_connection_error()
    {
        using var t = new TestSetup([R.Throw(new HttpRequestException("reset")), R.Json(MailboxJson, 201)]);
        await Assert.ThrowsAsync<ApiConnectionException>(() => t.Client.Mailboxes.CreateAsync());
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Retries_GET_on_502_and_504_with_exponential_backoff()
    {
        using var t = new TestSetup([BadGateway(), R.Empty(504), BadGateway(), Domains()], o => o.MaxRetries = 3);
        await t.Client.Domains.ListAsync();
        Assert.Equal([500.0, 1000.0, 2000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task Backoff_is_jittered_downward_and_capped_at_8_s()
    {
        var replies = Enumerable.Range(0, 7).Select(_ => BadGateway()).Append(Domains());
        using var t = new TestSetup(replies, o => o.MaxRetries = 7, random: () => 0.999);
        await t.Client.Domains.ListAsync();
        Assert.Equal(7, t.Clock.Sleeps.Count);
        Assert.All(t.Clock.Sleeps.Select((ms, i) => (ms, i)), x => Assert.True(x.ms <= Math.Min(500 * Math.Pow(2, x.i), 8000)));
        Assert.True(t.Clock.Sleeps[^1] >= 6000);
        Assert.True(t.Clock.Sleeps.Max() <= 8000);
    }

    [Fact]
    public async Task Retries_DELETE_on_a_connection_error()
    {
        using var t = new TestSetup([R.Throw(new HttpRequestException("reset")), R.Empty(204)]);
        await t.Client.Mailboxes.DeleteAsync("a@example.org");
        Assert.Equal([HttpMethod.Delete, HttpMethod.Delete], t.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task Does_not_retry_500_or_4xx()
    {
        using var t = new TestSetup([R.ApiError(500, "internal_server_error", "internal server error")]);
        await Assert.ThrowsAsync<InternalServerErrorException>(() => t.Client.Domains.ListAsync());
        Assert.Single(t.Requests);

        using var t2 = new TestSetup([R.ApiError(404, "not_found", "x")]);
        await Assert.ThrowsAsync<NotFoundException>(() => t2.Client.Domains.ListAsync());
        Assert.Single(t2.Requests);
    }

    [Fact]
    public async Task An_HTTP_attempt_over_the_timeout_is_an_ApiTimeoutException()
    {
        using var t = new TestSetup([R.Hang()], o =>
        {
            o.Timeout = TimeSpan.FromMilliseconds(20);
            o.MaxRetries = 0;
        });
        var e = await Assert.ThrowsAsync<ApiTimeoutException>(() => t.Client.Domains.ListAsync());
        Assert.Equal("request timed out after 20 ms", e.Message);
    }

    [Fact]
    public async Task A_timed_out_GET_is_retried()
    {
        using var t = new TestSetup([R.Hang(), Domains()], o => o.Timeout = TimeSpan.FromMilliseconds(20));
        await t.Client.Domains.ListAsync();
        Assert.Equal(2, t.Requests.Count);
        Assert.Equal([500.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task The_Timeout_of_an_injected_HttpClient_is_an_ApiTimeoutException_too()
    {
        using var t = new TestSetup([R.Hang()], o => o.MaxRetries = 0);
        t.Http.Timeout = TimeSpan.FromMilliseconds(20);
        await Assert.ThrowsAsync<ApiTimeoutException>(() => t.Client.Domains.ListAsync());
    }

    [Fact]
    public async Task Cancellation_throws_OperationCanceledException_and_is_not_retried()
    {
        using var cts = new CancellationTokenSource();
        using var t = new TestSetup([
            (req, _) =>
            {
                cts.CancelAfter(5);
                return R.Hang()(req, null!);
            },
            Domains(),
        ]);
        var e = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.Domains.ListAsync(cts.Token));
        Assert.Equal(cts.Token, e.CancellationToken);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task An_already_cancelled_token_sends_nothing()
    {
        using var t = new TestSetup([Domains()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.Domains.ListAsync(new CancellationToken(canceled: true)));
        Assert.Empty(t.Requests);
    }

    [Fact]
    public async Task Cancellation_during_a_retry_sleep_stops()
    {
        using var cts = new CancellationTokenSource();
        using var t = new TestSetup([Limited("1"), Domains()], o => o.Hooks = new ClientHooks
        {
            Sleep = (_, ct) =>
            {
                cts.Cancel();
                return Task.Delay(Timeout.Infinite, ct);
            },
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.Domains.ListAsync(cts.Token));
        Assert.Single(t.Requests);
    }

    [Theory]
    [InlineData(520)]
    [InlineData(521)]
    [InlineData(522)]
    [InlineData(523)]
    [InlineData(524)]
    public async Task Retries_GET_and_DELETE_on_Cloudflare_52x(int status)
    {
        using var t = new TestSetup([R.Text("cloudflare", status), Domains(), R.Text("cloudflare", status), R.Empty(204)]);
        await t.Client.Domains.ListAsync();
        await t.Client.Mailboxes.DeleteAsync(Address);
        Assert.Equal(4, t.Requests.Count);
    }

    [Fact]
    public async Task Does_not_retry_POST_on_52x()
    {
        using var t = new TestSetup([R.Text("cloudflare", 522), R.Json(MailboxJson, 201)]);
        var e = await Assert.ThrowsAsync<InternalServerErrorException>(() => t.Client.Mailboxes.CreateAsync());
        Assert.Equal(522, e.Status);
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task A_503_with_a_long_Retry_After_is_thrown_at_once()
    {
        using var t = new TestSetup([
            R.ApiError(503, "internal_server_error", "service unavailable", ("Retry-After", "60")),
            R.Json(MailboxJson, 201),
        ]);
        var e = await Assert.ThrowsAsync<InternalServerErrorException>(() =>
            t.Client.Mailboxes.CreateAsync(new CreateMailboxOptions { Domain = "example.org" }));
        Assert.Equal(503, e.Status);
        Assert.Single(t.Requests);
        Assert.Empty(t.Clock.Sleeps);

        using var t2 = new TestSetup([R.ApiError(503, "internal_server_error", "service unavailable", ("Retry-After", "6")), Domains()]);
        await Assert.ThrowsAsync<InternalServerErrorException>(() => t2.Client.Domains.ListAsync());
        Assert.Single(t2.Requests);
    }

    [Fact]
    public async Task A_503_with_a_short_or_no_Retry_After_is_still_retried()
    {
        using var t = new TestSetup([
            R.ApiError(503, "internal_server_error", "service unavailable", ("Retry-After", "5")),
            R.ApiError(503, "internal_server_error", "service unavailable"),
            R.Json(MailboxJson, 201),
        ]);
        await t.Client.Mailboxes.CreateAsync();
        Assert.Equal([5000.0, 1000.0], t.Clock.Sleeps);
    }

    [Fact]
    public async Task A_DELETE_retried_after_a_lost_response_takes_404_as_done()
    {
        using var t = new TestSetup([
            R.Throw(new HttpRequestException("reset")),
            R.ApiError(404, "not_found", "mailbox not found"),
            BadGateway(),
            R.ApiError(404, "not_found", "message not found"),
            R.Text("cloudflare", 524),
            R.ApiError(429, "rate_limit_exceeded", "x", ("Retry-After", "1")),
            R.ApiError(404, "not_found", "mailbox not found"),
        ]);
        await t.Client.Mailboxes.DeleteAsync(Address);
        await t.Client.Messages.DeleteAsync(Address, MessageId);
        await t.Client.Mailboxes.DeleteAsync(Address);
        Assert.Equal(7, t.Requests.Count);
    }

    [Fact]
    public async Task A_timed_out_DELETE_then_404_is_done()
    {
        using var t = new TestSetup([R.Hang(), R.ApiError(404, "not_found", "mailbox not found")], o => o.Timeout = TimeSpan.FromMilliseconds(20));
        await t.Client.Mailboxes.DeleteAsync(Address);
        Assert.Equal(2, t.Requests.Count);
    }

    [Fact]
    public async Task A_404_on_the_first_DELETE_or_after_a_refused_one_is_NotFound()
    {
        using var t = new TestSetup([R.ApiError(404, "not_found", "mailbox not found")]);
        await Assert.ThrowsAsync<NotFoundException>(() => t.Client.Mailboxes.DeleteAsync(Address));

        // 429 and 503 are refused before any effect: the 404 after them is real.
        using var t2 = new TestSetup([Limited("1"), Busy(), R.ApiError(404, "not_found", "mailbox not found")]);
        await Assert.ThrowsAsync<NotFoundException>(() => t2.Client.Mailboxes.DeleteAsync(Address));
        Assert.Equal(3, t2.Requests.Count);
    }

    [Fact]
    public async Task A_GET_retried_after_a_lost_response_still_throws_404()
    {
        using var t = new TestSetup([BadGateway(), R.ApiError(404, "not_found", "x")]);
        await Assert.ThrowsAsync<NotFoundException>(() => t.Client.Mailboxes.GetAsync(Address));
        Assert.Equal(2, t.Requests.Count);
    }
}
