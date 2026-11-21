using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeEmailPro;

// The API may add enum values. These are string-backed structs rather than C# enums, so a value this version does
// not know is kept as-is instead of failing the whole response: compare with the static members, read Value.

/// <summary>
/// How much of a message is kept: <see cref="Stored"/> (parsed), <see cref="Unparsed"/> (only the raw EML) or
/// <see cref="Overflow"/> (only the headers). Any other value from a newer server is kept in <see cref="Value"/>.
/// </summary>
/// <param name="Value">The value as the server sent it.</param>
[JsonConverter(typeof(BodyStateConverter))]
public readonly record struct BodyState(string Value)
{
    /// <summary>Parsed: the body and the EML are kept.</summary>
    public static BodyState Stored { get; } = new("STORED");

    /// <summary>Could not be parsed: only the EML is kept (no body, attachments, <c>From</c> or subject).</summary>
    public static BodyState Unparsed { get; } = new("UNPARSED");

    /// <summary>Larger than the account's storage: only the headers are kept, no EML.</summary>
    public static BodyState Overflow { get; } = new("OVERFLOW");

    /// <inheritdoc/>
    public override string ToString() => Value ?? "";
}

/// <summary>
/// The kind of a domain: <see cref="Public"/> or <see cref="Premium"/>. Any other value from a newer server is kept
/// in <see cref="Value"/>.
/// </summary>
/// <param name="Value">The value as the server sent it.</param>
[JsonConverter(typeof(DomainTypeConverter))]
public readonly record struct DomainType(string Value)
{
    /// <summary>Available to every plan.</summary>
    public static DomainType Public { get; } = new("PUBLIC");

    /// <summary>Available to plans that allow premium domains.</summary>
    public static DomainType Premium { get; } = new("PREMIUM");

    /// <inheritdoc/>
    public override string ToString() => Value ?? "";
}

/// <summary>The values of <see cref="ApiException.Code"/> (the <c>error</c> field of an error response).</summary>
public static class ErrorCodes
{
    /// <summary>400: a parameter or the body is invalid.</summary>
    public const string BadRequest = "bad_request";

    /// <summary>401: the key is missing, unknown, revoked or expired.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>402: the monthly mailbox quota is spent.</summary>
    public const string QuotaExhausted = "quota_exhausted";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string OAuthRequired = "oauth_required";

    /// <summary>403: the plan has no API access, or does not allow a custom local part.</summary>
    public const string PlanForbidden = "plan_forbidden";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string PlanLimitExceeded = "plan_limit_exceeded";

    /// <summary>404: the mailbox, message or attachment is not there.</summary>
    public const string NotFound = "not_found";

    /// <summary>405: the method is not allowed on the path.</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>409: the address was issued before, to anyone.</summary>
    public const string AddressTaken = "address_taken";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string DomainUnavailable = "domain_unavailable";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string ConfirmationRequired = "confirmation_required";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string InvalidState = "invalid_state";

    /// <summary>Used by other fake-email.pro APIs, not returned by the Public API.</summary>
    public const string DomainExists = "domain_exists";

    /// <summary>413: the request body is too large.</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>415: the request body is not JSON.</summary>
    public const string UnsupportedMediaType = "unsupported_media_type";

    /// <summary>429: requests per minute exceeded.</summary>
    public const string RateLimitExceeded = "rate_limit_exceeded";

    /// <summary>429: too many concurrent <c>wait</c> requests.</summary>
    public const string TooManyWaits = "too_many_waits";

    /// <summary>500 and 503.</summary>
    public const string InternalServerError = "internal_server_error";
}

internal abstract class StringValueConverter<T> : JsonConverter<T>
{
    protected abstract T Create(string value);

    protected abstract string ValueOf(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String
            ? Create(reader.GetString()!)
            : throw new JsonException($"expected a string for {typeof(T).Name}, got {reader.TokenType}");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ValueOf(value));
}

internal sealed class BodyStateConverter : StringValueConverter<BodyState>
{
    protected override BodyState Create(string value) => new(value);

    protected override string ValueOf(BodyState value) => value.Value;
}

internal sealed class DomainTypeConverter : StringValueConverter<DomainType>
{
    protected override DomainType Create(string value) => new(value);

    protected override string ValueOf(DomainType value) => value.Value;
}
