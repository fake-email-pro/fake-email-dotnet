using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static FakeEmailPro.Tests.Fixtures;

namespace FakeEmailPro.Tests;

/// <summary>Checks the SDK against openapi/public.yaml, so a spec change shows up as a failing test.</summary>
public sealed class SpecTests
{
    private static readonly Spec Api = Spec.Load();

    public static TheoryData<Type, string> Models => new()
    {
        { typeof(Mailbox), "Mailbox" },
        { typeof(Page<Mailbox>), "MailboxPage" },
        { typeof(Page<SimpleMessage>), "MessagePage" },
        { typeof(Sender), "From" },
        { typeof(SimpleMessage), "SimpleMessage" },
        { typeof(DetailedMessage), "DetailedMessage" },
        { typeof(Attachment), "Attachment" },
        { typeof(Link), "Link" },
        { typeof(Domain), "Domain" },
        { typeof(Limits), "Limits" },
        { typeof(Quota), "Quota" },
        { typeof(Storage), "Storage" },
    };

    [Theory]
    [MemberData(nameof(Models))]
    public void Model_covers_exactly_the_schema(Type model, string schema)
    {
        var fields = model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .OrderBy(n => n, StringComparer.Ordinal);
        Assert.Equal(Api.Properties(schema).OrderBy(n => n, StringComparer.Ordinal), fields);
    }

    [Fact]
    public void Models_cover_every_response_schema()
    {
        // Request bodies, error envelopes, enums and the Domains wrapper are not models.
        string[] notModels = ["ErrorCode", "Error", "QuotaExhaustedError", "CreateMailboxRequest", "Domains", "DomainType", "BodyState"];
        var covered = Models.Select(row => (string)row[1]);
        Assert.Equal(
            Api.Schemas.Keys.Except(notModels).OrderBy(n => n, StringComparer.Ordinal),
            covered.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Domains_wrapper_has_only_domains() => Assert.Equal(["domains"], Api.Properties("Domains"));

    [Fact]
    public void CreateMailboxOptions_matches_the_request_body()
    {
        Assert.Equal(["domain", "localPart"], Api.Properties("CreateMailboxRequest").OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(["Domain", "LocalPart"], typeof(CreateMailboxOptions).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void QuotaExhaustedException_has_the_extra_fields()
    {
        var extra = Api.Properties("QuotaExhaustedError").Except(Api.Properties("Error")).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(["limit", "resetsAt", "upgradeUrl", "used"], extra);
        foreach (var name in extra)
        {
            Assert.NotNull(typeof(QuotaExhaustedException).GetProperty(char.ToUpperInvariant(name[0]) + name[1..]));
        }
    }

    [Fact]
    public void Enum_values_match()
    {
        Assert.Equal(Api.Enum("BodyState"), StaticValues<BodyState>(v => v.Value));
        Assert.Equal(Api.Enum("DomainType"), StaticValues<DomainType>(v => v.Value));
        var codes = typeof(ErrorCodes).GetFields().Select(f => (string)f.GetRawConstantValue()!);
        Assert.Equal(Api.Enum("ErrorCode"), codes);
    }

    private static IEnumerable<string> StaticValues<T>(Func<T, string> value) =>
        typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Static).Select(p => value((T)p.GetValue(null)!));

    private static readonly Dictionary<string, Func<FakeEmailClient, Task>> Calls = new()
    {
        ["createMailbox"] = c => c.Mailboxes.CreateAsync(new CreateMailboxOptions { LocalPart = "a", Domain = "b" }),
        ["listMailboxes"] = c => c.Mailboxes.ListAsync(0, 10),
        ["getMailbox"] = c => c.Mailboxes.GetAsync(Address),
        ["deleteMailbox"] = c => c.Mailboxes.DeleteAsync(Address),
        ["listMessages"] = c => c.Messages.ListAsync(Address, 0, 10),
        ["waitForMessage"] = c => c.Messages.WaitAsync(Address, new MessageWaitOptions { Timeout = TimeSpan.FromSeconds(1), After = MessageId, From = "a", Subject = "b" }),
        ["getMessage"] = c => c.Messages.GetAsync(Address, MessageId),
        ["deleteMessage"] = c => c.Messages.DeleteAsync(Address, MessageId),
        ["downloadMessageRaw"] = c => c.Messages.GetRawAsync(Address, MessageId),
        ["downloadAttachment"] = c => c.Messages.GetAttachmentAsync(Address, MessageId, AttachmentId),
        ["listDomains"] = c => c.Domains.ListAsync(),
        ["getLimits"] = c => c.Limits.GetAsync(),
    };

    [Fact]
    public void Every_operation_has_a_method()
    {
        Assert.Equal(12, Api.Operations.Count);
        Assert.Equal(Api.Operations.Select(o => o.Id).Order(StringComparer.Ordinal), Calls.Keys.Order(StringComparer.Ordinal));
    }

    public static TheoryData<string> OperationIds => new(Api.Operations.Select(o => o.Id));

    [Theory]
    [MemberData(nameof(OperationIds))]
    public async Task Operation_request_matches_the_spec(string operationId)
    {
        var op = Api.Operations.Single(o => o.Id == operationId);
        // A 404 stops the call right after the request, whatever the response type.
        using var t = new TestSetup([R.ApiError(404, "not_found", "not found")]);
        await Assert.ThrowsAsync<NotFoundException>(() => Calls[operationId](t.Client));

        var request = Assert.Single(t.Requests);
        Assert.Equal(op.Method, request.Method.Method);
        var pattern = "^" + Regex.Replace(Regex.Escape(op.Path), @"\\\{[^}]+}", "[^/]+") + "$";
        Assert.Matches(pattern, request.Path);
        Assert.Subset(op.QueryParams, request.QueryParams.Keys.ToHashSet());
    }

    /// <summary>Just enough of a YAML reader for this one, consistently indented spec.</summary>
    private sealed class Spec
    {
        private Spec(string[] lines) => Lines = lines;

        private string[] Lines { get; }

        public Dictionary<string, (int Start, int End)> Schemas { get; private set; } = new();

        public List<(string Id, string Method, string Path, HashSet<string> QueryParams)> Operations { get; } = new();

        public static Spec Load()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "openapi", "public.yaml"))) dir = dir.Parent;
            var spec = new Spec(File.ReadAllLines(Path.Combine(dir!.FullName, "openapi", "public.yaml")));
            spec.Schemas = spec.Children(spec.Block("components", "schemas"));
            var parameters = spec.Children(spec.Block("components", "parameters"))
                .ToDictionary(p => p.Key, p => (Name: spec.Scalar(p.Value, "name"), In: spec.Scalar(p.Value, "in")));
            foreach (var (path, range) in spec.Children(spec.Block("paths")))
            {
                foreach (var (method, opRange) in spec.Children(range))
                {
                    if (method == "parameters") continue;
                    var query = new HashSet<string>();
                    for (var i = opRange.Start; i < opRange.End; i++)
                    {
                        var line = spec.Lines[i].Trim();
                        var inline = Regex.Match(line, @"^- name: (\S+)$");
                        if (inline.Success && spec.Lines[i + 1].Trim() == "in: query") query.Add(inline.Groups[1].Value);
                        var reference = Regex.Match(line, @"^- \$ref: '#/components/parameters/(\w+)'$");
                        if (reference.Success && parameters[reference.Groups[1].Value].In == "query") query.Add(parameters[reference.Groups[1].Value].Name);
                    }

                    spec.Operations.Add((spec.Scalar(opRange, "operationId"), method.ToUpperInvariant(), path, query));
                }
            }

            return spec;
        }

        public List<string> Properties(string schema)
        {
            var (start, end) = Schemas[schema];
            var names = new List<string>();
            for (var i = start; i < end; i++)
            {
                var line = Lines[i];
                var indent = Indent(line);
                if (line.Trim() == "properties:")
                {
                    names.AddRange(Children((i + 1, BlockEnd(i, end))).Keys);
                }
                else if (line.Trim() == "allOf:")
                {
                    for (var j = i + 1; j < end && (Indent(Lines[j]) > indent || Lines[j].Trim().Length == 0); j++)
                    {
                        var m = Regex.Match(Lines[j], @"^\s*- \$ref: '#/components/schemas/(\w+)'$");
                        if (m.Success && Indent(Lines[j]) == indent + 2) names.AddRange(Properties(m.Groups[1].Value));
                    }
                }
            }

            return names.Distinct().ToList();
        }

        public List<string> Enum(string schema)
        {
            var (start, end) = Schemas[schema];
            for (var i = start; i < end; i++)
            {
                var inline = Regex.Match(Lines[i], @"^\s*enum: \[(.*)\]$");
                if (inline.Success) return inline.Groups[1].Value.Split(',').Select(v => v.Trim()).ToList();
                if (Lines[i].Trim() == "enum:")
                {
                    return Lines.Skip(i + 1).Take(BlockEnd(i, end) - i - 1)
                        .Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal))
                        .Select(l => l.Trim()[2..].Trim()).ToList();
                }
            }

            throw new InvalidOperationException($"{schema} has no enum");
        }

        private (int Start, int End) Block(params string[] keys)
        {
            (int Start, int End) range = (0, Lines.Length);
            var indent = 0;
            foreach (var key in keys)
            {
                var at = Array.FindIndex(Lines, range.Start, range.End - range.Start, l => Indent(l) == indent && l.Trim() == key + ":");
                range = (at + 1, BlockEnd(at, range.End));
                indent += 2;
            }

            return range;
        }

        /// <summary>The keys one level below a block, each with the range of lines under it.</summary>
        private Dictionary<string, (int Start, int End)> Children((int Start, int End) range)
        {
            var result = new Dictionary<string, (int, int)>();
            var childIndent = -1;
            for (var i = range.Start; i < range.End; i++)
            {
                var line = Lines[i];
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
                if (childIndent < 0) childIndent = Indent(line);
                if (Indent(line) != childIndent) continue;
                var m = Regex.Match(line, @"^\s*('?[^:'\s]+'?):\s*$");
                if (m.Success) result[m.Groups[1].Value.Trim('\'')] = (i + 1, BlockEnd(i, range.End));
            }

            return result;
        }

        private string Scalar((int Start, int End) range, string key)
        {
            var indent = Lines.Skip(range.Start).Take(range.End - range.Start).Where(l => l.Trim().Length > 0).Min(Indent);
            for (var i = range.Start; i < range.End; i++)
            {
                var m = Regex.Match(Lines[i], $@"^\s*{key}: (.+)$");
                if (m.Success && Indent(Lines[i]) == indent) return m.Groups[1].Value.Trim().Trim('\'');
            }

            throw new InvalidOperationException($"no {key}");
        }

        private int BlockEnd(int keyLine, int limit)
        {
            var indent = Indent(Lines[keyLine]);
            var i = keyLine + 1;
            while (i < limit && (Lines[i].Trim().Length == 0 || Indent(Lines[i]) > indent)) i++;
            return i;
        }

        private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;
    }
}
