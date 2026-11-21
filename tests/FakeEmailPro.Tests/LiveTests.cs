namespace FakeEmailPro.Tests;

/// <summary>Runs only with FAKE_EMAIL_LIVE=1 and FAKE_EMAIL_API_KEY set; FAKE_EMAIL_BASE_URL points it at another environment.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FAKE_EMAIL_LIVE") != "1"
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAKE_EMAIL_API_KEY")))
        {
            Skip = "set FAKE_EMAIL_LIVE=1 and FAKE_EMAIL_API_KEY to run against the real API";
        }
    }
}

// Live smoke test against a real server. Spends one mailbox of the monthly quota.
public sealed class LiveTests
{
    [LiveFact]
    public async Task Creates_reads_and_deletes_a_mailbox()
    {
        var baseUrl = Environment.GetEnvironmentVariable("FAKE_EMAIL_BASE_URL");
        using var client = new FakeEmailClient(new FakeEmailClientOptions
        {
            BaseUrl = string.IsNullOrEmpty(baseUrl) ? FakeEmailClient.DefaultBaseUrl : baseUrl,
            UserAgent = "fake-email-pro-dotnet-live-test",
        });

        Assert.NotEmpty(await client.Domains.ListAsync());
        Assert.True((await client.Limits.GetAsync()).RequestsPerMinute > 0);

        var mailbox = await client.Mailboxes.CreateAsync();
        try
        {
            Assert.Contains("@", mailbox.Address, StringComparison.Ordinal);
            Assert.Equal(mailbox.Address, (await client.Mailboxes.GetAsync(mailbox.Address)).Address);
            Assert.Empty((await client.Messages.ListAsync(mailbox.Address)).Content);
            Assert.Null(await client.Messages.WaitAsync(mailbox.Address, new MessageWaitOptions { Timeout = TimeSpan.FromSeconds(1) }));
        }
        finally
        {
            await client.Mailboxes.DeleteAsync(mailbox.Address);
        }

        await Assert.ThrowsAsync<NotFoundException>(() => client.Mailboxes.GetAsync(mailbox.Address));
    }
}
