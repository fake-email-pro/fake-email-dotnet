using System.Text;
using System.Text.Json;
using static FakeEmailPro.Tests.Fixtures;

namespace FakeEmailPro.Tests;

public sealed class MailboxEndpointTests
{
    [Fact]
    public async Task Create_sends_POST_with_the_given_fields_and_parses_the_mailbox()
    {
        using var t = new TestSetup([R.Json(MailboxJson, 201)]);
        var mb = await t.Client.Mailboxes.CreateAsync(new CreateMailboxOptions { LocalPart = "ci-runner-7", Domain = "example.org" });

        Assert.Equal(Address, mb.Address);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 9, 0, 0, 481, TimeSpan.Zero), mb.ActiveSince);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 0, 0, 481, TimeSpan.Zero), mb.ExpiresAt);
        Assert.Equal(18422, mb.StorageUsedBytes);
        Assert.Equal(3, mb.MessagesCount);

        var r = t.Requests[0];
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("/v1/mailboxes", r.Path);
        Assert.Equal("", r.Search);
        Assert.Equal("""{"localPart":"ci-runner-7","domain":"example.org"}""", r.Body);
        Assert.Equal("application/json", r.Header("Content-Type"));
    }

    [Fact]
    public async Task Create_with_no_options_sends_an_empty_object()
    {
        using var t = new TestSetup([R.Json(MailboxJson, 201), R.Json(MailboxJson, 201)]);
        await t.Client.Mailboxes.CreateAsync();
        Assert.Equal("{}", t.Requests[0].Body);
        await t.Client.Mailboxes.CreateAsync(new CreateMailboxOptions { Domain = "example.org" });
        Assert.Equal("""{"domain":"example.org"}""", t.Requests[1].Body);
    }

    [Fact]
    public async Task List_passes_page_and_size_and_omits_unset_ones()
    {
        using var t = new TestSetup([R.Json(PageJson([MailboxJson], totalPages: 1, size: 10)), R.Json(PageJson([]))]);
        var res = await t.Client.Mailboxes.ListAsync(page: 2, size: 10);
        Assert.Equal(Address, Assert.Single(res.Content).Address);
        Assert.Equal(0, res.PageNumber);
        Assert.Equal(10, res.Size);
        Assert.Equal(1, res.TotalPages);
        Assert.Equal(1, res.TotalElements);
        Assert.False(res.HasNext);
        Assert.Equal("/v1/mailboxes", t.Requests[0].Path);
        Assert.Equal("?page=2&size=10", t.Requests[0].Search);

        await t.Client.Mailboxes.ListAsync();
        Assert.Equal("", t.Requests[1].Search);
    }

    [Fact]
    public async Task ListAll_walks_every_page()
    {
        using var t = new TestSetup([
            R.Json(PageJson([MailboxWith("a@example.org"), MailboxWith("b@example.org")], 0, 2)),
            R.Json(PageJson([MailboxWith("c@example.org")], 1, 2)),
        ]);
        var all = new List<string>();
        await foreach (var m in t.Client.Mailboxes.ListAllAsync()) all.Add(m.Address);
        Assert.Equal(["a@example.org", "b@example.org", "c@example.org"], all);
        Assert.Equal(["?page=0&size=50", "?page=1&size=50"], t.Requests.Select(r => r.Search));
    }

    [Fact]
    public async Task ListAll_stops_on_an_empty_first_page()
    {
        using var t = new TestSetup([R.Json(PageJson([], 0, 0))]);
        var all = new List<Mailbox>();
        await foreach (var m in t.Client.Mailboxes.ListAllAsync(size: 5)) all.Add(m);
        Assert.Empty(all);
        Assert.Single(t.Requests);
        Assert.Equal("5", t.Requests[0].Query("size"));
    }

    [Fact]
    public async Task ListAll_honors_cancellation_between_pages()
    {
        using var cts = new CancellationTokenSource();
        using var t = new TestSetup([R.Json(PageJson([MailboxJson], 0, 2)), R.Json(PageJson([MailboxJson], 1, 2))]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in t.Client.Mailboxes.ListAllAsync().WithCancellation(cts.Token)) cts.Cancel();
        });
        Assert.Single(t.Requests);
    }

    [Fact]
    public async Task Get_encodes_the_address()
    {
        using var t = new TestSetup([R.Json(MailboxJson)]);
        await t.Client.Mailboxes.GetAsync(Address);
        Assert.Equal(HttpMethod.Get, t.Requests[0].Method);
        Assert.Equal($"/v1/mailboxes/{Encoded}", t.Requests[0].Path);
    }

    [Fact]
    public async Task Get_encodes_characters_that_would_break_the_path()
    {
        using var t = new TestSetup([R.Json(MailboxJson)]);
        await t.Client.Mailboxes.GetAsync("a/b?c#d@example.org");
        Assert.Equal("/v1/mailboxes/a%2Fb%3Fc%23d%40example.org", t.Requests[0].Path);
        Assert.Equal("", t.Requests[0].Search);
    }

    [Fact]
    public async Task Delete_sends_DELETE_and_completes_on_204()
    {
        using var t = new TestSetup([R.Empty(204)]);
        await t.Client.Mailboxes.DeleteAsync(Address);
        Assert.Equal(HttpMethod.Delete, t.Requests[0].Method);
        Assert.Equal($"/v1/mailboxes/{Encoded}", t.Requests[0].Path);
        Assert.Null(t.Requests[0].Body);
    }
}

public sealed class MessageEndpointTests
{
    [Fact]
    public async Task List_parses_simple_messages()
    {
        using var t = new TestSetup([R.Json(PageJson([SimpleMessageJson()]))]);
        var res = await t.Client.Messages.ListAsync(Address, page: 0, size: 5);
        var m = Assert.Single(res.Content);
        Assert.Equal("5d2e8a10-7b3c-4f1e-8a2d-9c4b6e1f3a57", m.Id);
        Assert.Equal(Address, m.MailboxAddress);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 9, 6, 40, 102, TimeSpan.Zero), m.CreatedAt);
        Assert.Equal(9120, m.SizeBytes);
        Assert.Equal(new Sender { Name = "Acme", Address = "no-reply@acme.example" }, m.From);
        Assert.Equal("Your verification code", m.Subject);
        Assert.Equal(BodyState.Stored, m.BodyState);
        Assert.Equal(0, m.AttachmentsCount);
        Assert.Equal("048213", m.Code);
        Assert.Equal("https://acme.example/verify?t=3f9a", m.ConfirmLink);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages", t.Requests[0].Path);
        Assert.Equal("?page=0&size=5", t.Requests[0].Search);
    }

    [Fact]
    public async Task ListAll_walks_every_page()
    {
        using var t = new TestSetup([R.Json(PageJson([SimpleMessageJson(id: "1")], 0, 2)), R.Json(PageJson([SimpleMessageJson(id: "2")], 1, 2))]);
        var ids = new List<string>();
        await foreach (var m in t.Client.Messages.ListAllAsync(Address)) ids.Add(m.Id);
        Assert.Equal(["1", "2"], ids);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages", t.Requests[1].Path);
        Assert.Equal("?page=1&size=50", t.Requests[1].Search);
    }

    [Fact]
    public async Task Wait_sends_every_filter_and_returns_the_message()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson())]);
        var res = await t.Client.Messages.WaitAsync(Address, new MessageWaitOptions
        {
            Timeout = TimeSpan.FromSeconds(45),
            After = MessageId,
            From = "acme",
            Subject = "code",
        });
        Assert.NotNull(res);
        Assert.Equal("048213", res.Code);
        var r = t.Requests[0];
        Assert.Equal(HttpMethod.Get, r.Method);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/wait", r.Path);
        Assert.Equal(
            new Dictionary<string, string> { ["timeout"] = "45", ["after"] = MessageId, ["from"] = "acme", ["subject"] = "code" },
            r.QueryParams);
    }

    [Fact]
    public async Task Wait_returns_null_on_204()
    {
        using var t = new TestSetup([R.Empty(204)]);
        Assert.Null(await t.Client.Messages.WaitAsync(Address));
        Assert.Equal("", t.Requests[0].Search);
    }

    [Fact]
    public async Task Wait_sends_a_time_as_UTC_with_milliseconds_and_encodes_offsets()
    {
        using var t = new TestSetup([R.Empty(204), R.Empty(204), R.Empty(204)]);
        await t.Client.Messages.WaitAsync(Address, new MessageWaitOptions { After = new DateTimeOffset(2026, 10, 22, 12, 0, 0, 5, TimeSpan.FromHours(3)) });
        Assert.Equal("2026-10-22T09:00:00.005Z", t.Requests[0].Query("after"));
        Assert.Contains("after=2026-10-22T09%3A00%3A00.005Z", t.Requests[0].Search, StringComparison.Ordinal);

        await t.Client.Messages.WaitAsync(Address, new MessageWaitOptions { After = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc) });
        Assert.Equal("2026-10-22T09:00:00.000Z", t.Requests[1].Query("after"));

        // A string is an id and is sent as-is, so an RFC 3339 string with an offset works too.
        await t.Client.Messages.WaitAsync(Address, new MessageWaitOptions { After = "2026-10-22T12:00:00+03:00" });
        Assert.Contains("after=2026-10-22T12%3A00%3A00%2B03%3A00", t.Requests[2].Search, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wait_rounds_the_server_timeout_up_to_whole_seconds()
    {
        using var t = new TestSetup([R.Empty(204)]);
        await t.Client.Messages.WaitAsync(Address, new MessageWaitOptions { Timeout = TimeSpan.FromMilliseconds(1500) });
        Assert.Equal("2", t.Requests[0].Query("timeout"));
    }

    [Fact]
    public async Task Wait_gives_the_HTTP_attempt_10_s_more_than_the_server_timeout()
    {
        var timers = new List<TimeSpan>();
        using var t = new TestSetup(
            [R.Hang(), R.Empty(204), R.Empty(204), R.Empty(204)],
            o => o.MaxRetries = 0,
            attemptTimer: timeout =>
            {
                timers.Add(timeout);
                // The first attempt "runs out of time" almost at once; the others never do.
                return timers.Count == 1 ? new CancellationTokenSource(TimeSpan.FromMilliseconds(50)) : new CancellationTokenSource();
            });

        var ex = await Assert.ThrowsAsync<ApiTimeoutException>(() => t.Client.Messages.WaitAsync(Address, new MessageWaitOptions { Timeout = TimeSpan.FromSeconds(1) }));
        Assert.Equal("request timed out after 11000 ms", ex.Message);
        await t.Client.Messages.WaitAsync(Address);
        await t.Client.Mailboxes.DeleteAsync(Address);
        await t.Client.WithOptions(new RequestOptions { Timeout = TimeSpan.FromSeconds(5) }).Messages.WaitAsync(Address);

        Assert.Equal([TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)], timers);
    }

    [Fact]
    public async Task Get_returns_the_full_message()
    {
        var attachments = """[{"id":"9a7e2c44-0d0b-4f7a-a1c1-2b9d5f3e6a10","contentType":"application/pdf","fileName":"invoice.pdf","sizeBytes":51234}]""";
        var links = """[{"url":"https://acme.example/verify","text":"Verify"},{"url":"https://acme.example/help","text":null}]""";
        using var t = new TestSetup([R.Json(DetailedMessageJson(codes: """["123456"]""", links: links, attachments: attachments))]);
        var full = await t.Client.Messages.GetAsync(Address, MessageId);

        Assert.Equal(new Sender { Name = null, Address = "sender@example.com" }, full.From);
        Assert.Equal("Welcome", full.Subject);
        Assert.Equal(BodyState.Stored, full.BodyState);
        Assert.Equal("<p>hi</p>", full.Html);
        Assert.Null(full.Text);
        Assert.Equal(["123456"], full.Codes);
        Assert.Equal(new Link { Url = "https://acme.example/verify", Text = "Verify" }, full.Links[0]);
        Assert.Null(full.Links[1].Text);
        Assert.Equal(new Attachment { Id = AttachmentId, ContentType = "application/pdf", FileName = "invoice.pdf", SizeBytes = 51234 }, Assert.Single(full.Attachments));
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/{MessageId}", t.Requests[0].Path);
    }

    [Fact]
    public async Task Delete()
    {
        using var t = new TestSetup([R.Empty(204)]);
        await t.Client.Messages.DeleteAsync(Address, MessageId);
        Assert.Equal(HttpMethod.Delete, t.Requests[0].Method);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/{MessageId}", t.Requests[0].Path);
    }

    [Fact]
    public async Task GetRaw_returns_the_EML_bytes()
    {
        var eml = Encoding.UTF8.GetBytes("Received: by fake-email\r\nSubject: hi\r\n\r\nbody");
        using var t = new TestSetup([R.Bytes(eml, 200, ("Content-Type", "message/rfc822"))]);
        var bytes = await t.Client.Messages.GetRawAsync(Address, MessageId);
        Assert.Equal(eml, bytes);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/{MessageId}/raw", t.Requests[0].Path);
    }

    [Fact]
    public async Task GetAttachment_returns_bytes_type_and_the_RFC_5987_file_name()
    {
        using var t = new TestSetup([
            R.Bytes([1, 2, 3], 200,
                ("Content-Type", "application/pdf"),
                ("Content-Disposition", "attachment; filename=\"Rechnung ?.pdf\"; filename*=UTF-8''Rechnung%20%E2%82%AC.pdf")),
            R.Bytes([4], 200),
        ]);
        var att = await t.Client.Messages.GetAttachmentAsync(Address, MessageId, AttachmentId);
        Assert.Equal(new byte[] { 1, 2, 3 }, att.Data);
        Assert.Equal("application/pdf", att.ContentType);
        Assert.Equal("Rechnung €.pdf", att.FileName);
        Assert.Equal($"/v1/mailboxes/{Encoded}/messages/{MessageId}/attachments/{AttachmentId}", t.Requests[0].Path);

        var bare = await t.Client.Messages.GetAttachmentAsync(Address, MessageId, AttachmentId);
        Assert.Equal("application/octet-stream", bare.ContentType);
        Assert.Null(bare.FileName);
    }
}

public sealed class DomainAndLimitEndpointTests
{
    [Fact]
    public async Task Domains_List_unwraps_the_list()
    {
        using var t = new TestSetup([R.Json("""{"domains":[{"name":"example.net","type":"PREMIUM"},{"name":"example.org","type":"PUBLIC"}]}""")]);
        var domains = await t.Client.Domains.ListAsync();
        Assert.Equal([new Domain { Name = "example.net", Type = DomainType.Premium }, new Domain { Name = "example.org", Type = DomainType.Public }], domains);
        Assert.Equal(HttpMethod.Get, t.Requests[0].Method);
        Assert.Equal("/v1/domains", t.Requests[0].Path);
    }

    [Fact]
    public async Task Limits_Get()
    {
        using var t = new TestSetup([R.Json("""
            {"quota":{"limit":20,"used":3,"resetsAt":"2026-11-01T00:00:00.000Z"},"requestsPerMinute":60,"concurrentWaits":10,
             "storage":{"usedBytes":18422,"limitBytes":52428800},"mailboxTtlSeconds":86400}
            """)]);
        var limits = await t.Client.Limits.GetAsync();
        Assert.Equal(new Quota { Limit = 20, Used = 3, ResetsAt = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) }, limits.Quota);
        Assert.Equal(60, limits.RequestsPerMinute);
        Assert.Equal(10, limits.ConcurrentWaits);
        Assert.Equal(new Storage { UsedBytes = 18422, LimitBytes = 52428800 }, limits.Storage);
        Assert.Equal(86400, limits.MailboxTtlSeconds);
        Assert.Equal(HttpMethod.Get, t.Requests[0].Method);
        Assert.Equal("/v1/limits", t.Requests[0].Path);
    }
}

public sealed class ModelTests
{
    [Fact]
    public async Task Unknown_fields_and_enum_values_do_not_break_parsing()
    {
        var json = SimpleMessageJson().Replace("\"STORED\"", "\"QUARANTINED\"", StringComparison.Ordinal).TrimEnd().TrimEnd('}')
            + ""","isRead":true,"priority":{"x":1}}""";
        using var t = new TestSetup([
            R.Json(json),
            R.Json("""{"domains":[{"name":"x.example","type":"PRIVATE","verified":true}],"total":1}"""),
        ]);
        var m = await t.Client.Messages.WaitAsync(Address);
        Assert.NotNull(m);
        Assert.Equal("QUARANTINED", m.BodyState.Value);
        Assert.NotEqual(BodyState.Stored, m.BodyState);
        Assert.Equal("048213", m.Code);

        var d = Assert.Single(await t.Client.Domains.ListAsync());
        Assert.Equal(new DomainType("PRIVATE"), d.Type);
        Assert.Equal("PRIVATE", d.Type.ToString());
    }

    [Fact]
    public async Task Nullable_fields_accept_null()
    {
        using var t = new TestSetup([R.Json(SimpleMessageJson(code: null, confirmLink: null, subject: null).Replace(
            """{"name":"Acme","address":"no-reply@acme.example"}""", "null", StringComparison.Ordinal))]);
        var m = await t.Client.Messages.WaitAsync(Address);
        Assert.NotNull(m);
        Assert.Null(m.From);
        Assert.Null(m.Subject);
        Assert.Null(m.Code);
        Assert.Null(m.ConfirmLink);
    }

    [Fact]
    public void Enum_like_values_compare_by_value()
    {
        Assert.Equal(BodyState.Overflow, new BodyState("OVERFLOW"));
        Assert.Equal("UNPARSED", BodyState.Unparsed.ToString());
        Assert.Equal("", default(BodyState).ToString());
        Assert.Equal("\"PREMIUM\"", JsonSerializer.Serialize(DomainType.Premium, FakeEmailJsonContext.Default.Options));
    }

    [Fact]
    public void WaitAfter_converts_from_an_id_and_from_times()
    {
        WaitAfter id = "m1";
        Assert.Equal("m1", id.MessageId);
        Assert.Equal("m1", id.ToString());
        WaitAfter at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(-2));
        Assert.Equal("2026-01-02T05:04:05.000Z", at.ToString());
        Assert.Equal(WaitAfter.Message("m1"), id);
        Assert.Throws<ArgumentNullException>(() => WaitAfter.Message(null!));
    }
}
