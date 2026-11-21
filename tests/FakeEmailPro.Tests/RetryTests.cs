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
}
