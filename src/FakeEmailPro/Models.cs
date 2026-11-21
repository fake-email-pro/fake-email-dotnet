using System.Text.Json.Serialization;

namespace FakeEmailPro;

// Response models of the Public API. Unknown JSON fields are ignored, and enum-like values are kept as strings
// (see BodyState, DomainType), so a field or a value the server adds later does not break parsing.

/// <summary>An active API mailbox. It accepts mail for 24 hours after creation.</summary>
public sealed record Mailbox
{
    /// <summary>The full address, e.g. <c>quiet.harbor.42@example.org</c>.</summary>
    public string Address { get; init; } = "";

    /// <summary>When the mailbox was created.</summary>
    public DateTimeOffset ActiveSince { get; init; }

    /// <summary>When the mailbox stops accepting mail and starts answering 404.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Bytes the mailbox's messages take from your storage.</summary>
    public long StorageUsedBytes { get; init; }

    /// <summary>Messages in the mailbox.</summary>
    public int MessagesCount { get; init; }
}

/// <summary>One page of a list. Pages are zero-based; lists are newest first.</summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed record Page<T>
{
    /// <summary>The items of this page.</summary>
    public IReadOnlyList<T> Content { get; init; } = Array.Empty<T>();

    /// <summary>The zero-based number of this page (<c>page</c> in the JSON).</summary>
    [JsonPropertyName("page")]
    public int PageNumber { get; init; }

    /// <summary>The page size that was applied.</summary>
    public int Size { get; init; }

    /// <summary>The number of pages.</summary>
    public int TotalPages { get; init; }

    /// <summary>The number of items over all pages.</summary>
    public long TotalElements { get; init; }

    /// <summary>Whether a page follows this one.</summary>
    [JsonIgnore]
    public bool HasNext => PageNumber + 1 < TotalPages;
}

/// <summary>The sender, from the <c>From</c> header (<c>From</c> in the OpenAPI spec).</summary>
public sealed record Sender
{
    /// <summary>The display name, or <see langword="null"/> when the header has none.</summary>
    public string? Name { get; init; }

    /// <summary>The sender's address.</summary>
    public string Address { get; init; } = "";
}

/// <summary>
/// A message in a list and in the <c>wait</c> response. <see cref="Code"/> and <see cref="ConfirmLink"/> are the best
/// one-time code and "confirm / sign in" link, computed on delivery.
/// </summary>
public sealed record SimpleMessage
{
    /// <summary>The message id (a UUID).</summary>
    public string Id { get; init; } = "";

    /// <summary>The mailbox the message was delivered to.</summary>
    public string MailboxAddress { get; init; } = "";

    /// <summary>When the message was received, by the server's clock.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Size of the stored EML in bytes (0 for <see cref="BodyState.Overflow"/>).</summary>
    public long SizeBytes { get; init; }

    /// <summary>The sender; <see langword="null"/> for <see cref="BodyState.Unparsed"/> and a message without <c>From</c>.</summary>
    public Sender? From { get; init; }

    /// <summary>The subject; <see langword="null"/> if there is none or the message was not parsed.</summary>
    public string? Subject { get; init; }

    /// <summary>How much of the message is kept.</summary>
    public BodyState BodyState { get; init; }

    /// <summary>The number of attachments.</summary>
    public int AttachmentsCount { get; init; }

    /// <summary>The best one-time code found on delivery, e.g. <c>"048213"</c>; <see langword="null"/> if none.</summary>
    public string? Code { get; init; }

    /// <summary>The best "confirm / sign in" link found on delivery; <see langword="null"/> if none.</summary>
    public string? ConfirmLink { get; init; }
}

/// <summary>The full message: bodies, attachments, every code and link. <c>Codes[0]</c> and <c>Links[0]</c> are the best guesses.</summary>
public sealed record DetailedMessage
{
    /// <summary>The message id (a UUID).</summary>
    public string Id { get; init; } = "";

    /// <summary>When the message was received, by the server's clock.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The sender; <see langword="null"/> for <see cref="BodyState.Unparsed"/> and a message without <c>From</c>.</summary>
    public Sender? From { get; init; }

    /// <summary>The subject; <see langword="null"/> if there is none or the message was not parsed.</summary>
    public string? Subject { get; init; }

    /// <summary>How much of the message is kept.</summary>
    public BodyState BodyState { get; init; }

    /// <summary>The HTML part; <see langword="null"/> if there is none.</summary>
    public string? Html { get; init; }

    /// <summary>The plain-text part (never derived from the HTML); <see langword="null"/> if there is none.</summary>
    public string? Text { get; init; }

    /// <summary>The attachments; download one with <see cref="MessagesResource.GetAttachmentAsync"/>.</summary>
    public IReadOnlyList<Attachment> Attachments { get; init; } = Array.Empty<Attachment>();

    /// <summary>One-time codes, the best guess first.</summary>
    public IReadOnlyList<string> Codes { get; init; } = Array.Empty<string>();

    /// <summary>Links ranked by how likely they are to "confirm / sign in", the best guess first.</summary>
    public IReadOnlyList<Link> Links { get; init; } = Array.Empty<Link>();
}

/// <summary>An attachment of a message.</summary>
public sealed record Attachment
{
    /// <summary>The attachment id (a UUID).</summary>
    public string Id { get; init; } = "";

    /// <summary>The MIME type, e.g. <c>application/pdf</c>.</summary>
    public string ContentType { get; init; } = "";

    /// <summary>The file name; <see langword="null"/> if the message did not give one.</summary>
    public string? FileName { get; init; }

    /// <summary>The decoded size in bytes.</summary>
    public long SizeBytes { get; init; }
}

/// <summary>A link found in a message.</summary>
public sealed record Link
{
    /// <summary>The URL.</summary>
    public string Url { get; init; } = "";

    /// <summary>The link text; <see langword="null"/> if there is none.</summary>
    public string? Text { get; init; }
}

/// <summary>A domain you can create a mailbox on.</summary>
public sealed record Domain
{
    /// <summary>The domain name, e.g. <c>example.org</c>.</summary>
    public string Name { get; init; } = "";

    /// <summary>Public or premium.</summary>
    public DomainType Type { get; init; }
}

/// <summary>The numbers that apply to you right now, under your current plan.</summary>
public sealed record Limits
{
    /// <summary>The monthly quota of API mailboxes.</summary>
    public Quota Quota { get; init; } = new();

    /// <summary>Requests per minute.</summary>
    public int RequestsPerMinute { get; init; }

    /// <summary>Concurrent <c>wait</c> requests.</summary>
    public int ConcurrentWaits { get; init; }

    /// <summary>Message storage.</summary>
    public Storage Storage { get; init; } = new();

    /// <summary>How long a mailbox accepts mail, in seconds.</summary>
    public int MailboxTtlSeconds { get; init; }
}

/// <summary>The monthly quota of Public API mailboxes (calendar month in UTC).</summary>
public sealed record Quota
{
    /// <summary>Mailboxes per month.</summary>
    public int Limit { get; init; }

    /// <summary>Mailboxes created this month.</summary>
    public int Used { get; init; }

    /// <summary>When the quota renews: the 1st of next month, 00:00 UTC.</summary>
    public DateTimeOffset ResetsAt { get; init; }
}

/// <summary>Message storage of the account.</summary>
public sealed record Storage
{
    /// <summary>Bytes in use.</summary>
    public long UsedBytes { get; init; }

    /// <summary>Bytes allowed.</summary>
    public long LimitBytes { get; init; }
}

/// <summary>The decoded bytes of an attachment, with the type and name the server sent along.</summary>
public sealed record DownloadedAttachment
{
    /// <summary>The decoded bytes.</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <summary>From <c>Content-Type</c>; <c>application/octet-stream</c> when absent.</summary>
    public string ContentType { get; init; } = "application/octet-stream";

    /// <summary>From <c>Content-Disposition</c> (RFC 5987 <c>filename*</c> preferred); <see langword="null"/> if absent.</summary>
    public string? FileName { get; init; }
}

/// <summary>The rate-limit window from the <c>X-RateLimit-*</c> headers.</summary>
/// <param name="Limit">Requests per minute on the plan.</param>
/// <param name="Remaining">Requests left in the current window.</param>
/// <param name="Reset">End of the window, Unix seconds.</param>
public sealed record RateLimitInfo(long Limit, long Remaining, long Reset);

internal sealed record DomainList
{
    public IReadOnlyList<Domain> Domains { get; init; } = Array.Empty<Domain>();
}
