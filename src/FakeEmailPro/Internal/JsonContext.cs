using System.Text.Json;
using System.Text.Json.Serialization;

namespace FakeEmailPro;

/// <summary>Source-generated (de)serializers: camelCase, unknown fields ignored, no reflection (trimming and AOT safe).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(Mailbox))]
[JsonSerializable(typeof(Page<Mailbox>))]
[JsonSerializable(typeof(SimpleMessage))]
[JsonSerializable(typeof(Page<SimpleMessage>))]
[JsonSerializable(typeof(DetailedMessage))]
[JsonSerializable(typeof(DomainList))]
[JsonSerializable(typeof(Limits))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class FakeEmailJsonContext : JsonSerializerContext
{
}
