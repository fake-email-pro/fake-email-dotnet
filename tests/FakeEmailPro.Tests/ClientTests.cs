using System.Text.RegularExpressions;
using static FakeEmailPro.Tests.Fixtures;

namespace FakeEmailPro.Tests;

[Collection("Environment")]
public sealed class ClientTests : IDisposable
{
    private readonly string? _savedKey = Environment.GetEnvironmentVariable("FAKE_EMAIL_API_KEY");

    public void Dispose() => Environment.SetEnvironmentVariable("FAKE_EMAIL_API_KEY", _savedKey);

    [Fact]
    public async Task Reads_the_key_from_FAKE_EMAIL_API_KEY()
    {
        Environment.SetEnvironmentVariable("FAKE_EMAIL_API_KEY", "fep_from_env");
        using var t = new TestSetup([R.Json(MailboxJson)], o => o.ApiKey = null);
        await t.Client.Mailboxes.GetAsync("a@example.org");
        Assert.Equal("Bearer fep_from_env", t.Requests[0].Header("Authorization"));
    }

    [Fact]
    public async Task Prefers_an_explicit_key_over_the_environment()
    {
        Environment.SetEnvironmentVariable("FAKE_EMAIL_API_KEY", "fep_from_env");
        using var t = new TestSetup([R.Json(MailboxJson)], o => o.ApiKey = "fep_explicit");
        await t.Client.Mailboxes.GetAsync("a@example.org");
        Assert.Equal("Bearer fep_explicit", t.Requests[0].Header("Authorization"));
    }

    [Fact]
    public void Throws_without_a_key()
    {
        Environment.SetEnvironmentVariable("FAKE_EMAIL_API_KEY", null);
        var ex = Assert.Throws<FakeEmailException>(() => new FakeEmailClient());
        Assert.Contains("FAKE_EMAIL_API_KEY", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https://fake-email.pro/settings/api-keys", ex.Message, StringComparison.Ordinal);

        Environment.SetEnvironmentVariable("FAKE_EMAIL_API_KEY", "");
        Assert.Throws<FakeEmailException>(() => new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "" }));
    }

    [Fact]
    public void Defaults_to_the_production_base_url()
    {
        using var client = new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "fep_x" });
        Assert.Equal("https://api.fake-email.pro", client.BaseUrl);
        Assert.Equal(FakeEmailClient.DefaultBaseUrl, client.BaseUrl);
    }

    [Fact]
    public async Task Tolerates_a_trailing_slash_and_a_path_prefix_in_BaseUrl()
    {
        using var t = new TestSetup([R.Json(MailboxJson)], o => o.BaseUrl = "https://gw.test/fake//");
        await t.Client.Mailboxes.GetAsync("a@example.org");
        Assert.Equal("https://gw.test/fake/v1/mailboxes/a%40example.org", t.Requests[0].Uri.AbsoluteUri);
        Assert.Equal("https://gw.test/fake", t.Client.BaseUrl);
    }

    [Fact]
    public void Rejects_invalid_settings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "k", MaxRetries = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "k", Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "k", BaseUrl = " " }));
    }

    [Fact]
    public async Task Sends_auth_accept_and_the_sdk_user_agent()
    {
        using var t = new TestSetup([R.Json(MailboxJson)]);
        await t.Client.Mailboxes.GetAsync("a@example.org");
        var r = t.Requests[0];
        Assert.Equal("Bearer fep_test", r.Header("Authorization"));
        Assert.Equal("application/json", r.Header("Accept"));
        Assert.Equal($"fake-email-pro-dotnet/{PropsVersion()}", r.Header("User-Agent"));
        Assert.Null(r.Header("Content-Type"));
    }

    [Fact]
    public async Task Appends_a_custom_user_agent()
    {
        using var t = new TestSetup([R.Json(MailboxJson)], o => o.UserAgent = "my-suite/1.0");
        await t.Client.Mailboxes.GetAsync("a@example.org");
        Assert.Equal($"fake-email-pro-dotnet/{PropsVersion()} my-suite/1.0", t.Requests[0].Header("User-Agent"));
    }

    [Fact]
    public void Version_matches_Directory_Build_props()
    {
        Assert.Equal(PropsVersion(), FakeEmailClient.Version);
        Assert.Matches(@"^\d+\.\d+\.\d+", FakeEmailClient.Version);
    }

    [Fact]
    public async Task Disposing_leaves_an_injected_HttpClient_open()
    {
        using var t = new TestSetup([R.Json(MailboxJson)]);
        t.Client.Dispose();
        using var again = new FakeEmailClient(new FakeEmailClientOptions { ApiKey = "fep_x", BaseUrl = "https://api.test", HttpClient = t.Http });
        var mailbox = await again.Mailboxes.GetAsync(Address);
        Assert.Equal(Address, mailbox.Address);
    }

    [Fact]
    public async Task WithOptions_overrides_retries_for_the_view_only()
    {
        var limited = R.ApiError(429, "rate_limit_exceeded", "rate limit exceeded", ("Retry-After", "1"));
        using var t = new TestSetup([limited, limited, R.Json("""{"domains":[]}""")]);
        await Assert.ThrowsAsync<RateLimitException>(() => t.Client.WithOptions(new RequestOptions { MaxRetries = 0 }).Domains.ListAsync());
        Assert.Single(t.Requests);
        await t.Client.Domains.ListAsync();
        Assert.Equal(3, t.Requests.Count);
    }

    internal static string PropsVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var props = File.ReadAllText(Path.Combine(dir.FullName, "Directory.Build.props"));
        return Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
    }
}
