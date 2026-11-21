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
    public async Task Gives_up_on_a_429_whose_Retry_After_passes_the_deadline()
    {
        using var t = new TestSetup([R.ApiError(429, "rate_limit_exceeded", "rate limit exceeded", ("Retry-After", "50"))]);
        await Assert.ThrowsAsync<RateLimitException>(() =>
            t.Client.WaitForMessageAsync(Address, new WaitOptions { Timeout = TimeSpan.FromSeconds(10) }));
        Assert.Single(t.Requests);
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
