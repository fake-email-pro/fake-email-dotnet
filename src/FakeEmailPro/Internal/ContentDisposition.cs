using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FakeEmailPro;

internal static class ContentDisposition
{
    // name=token | name="quoted \" string"; the leading disposition type has no "=" and is skipped.
    private static readonly Regex Param = new(
        """;\s*([^\s=;]+)\s*=\s*(?:"((?:[^"\\]|\\.)*)"|([^;]*))""",
        RegexOptions.CultureInvariant);

    private static readonly Regex Escape = new(@"\\(.)", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    // charset'language'value (RFC 5987); \w is spelled out so it stays ASCII as in the JS SDK.
    private static readonly Regex ExtValue = new(
        @"^([A-Za-z0-9_!#$%&+^`{}~-]*)'[A-Za-z0-9_-]*'(.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex Percent = new("%([0-9a-fA-F]{2})", RegexOptions.CultureInvariant);

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary><c>filename*</c> (RFC 5987/6266) wins over <c>filename</c>; null when neither is present.</summary>
    public static string? ParseFileName(string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;
        var parameters = SplitParams(header);
        if (parameters.TryGetValue("filename*", out var extended))
        {
            var m = ExtValue.Match(extended);
            if (m.Success)
            {
                var decoded = DecodeExtValue(m.Groups[2].Value, m.Groups[1].Value.ToLowerInvariant());
                if (decoded is not null) return decoded;
            }
        }

        return parameters.TryGetValue("filename", out var plain) && plain.Length > 0 ? plain : null;
    }

    private static Dictionary<string, string> SplitParams(string header)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Param.Matches(header))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            var value = m.Groups[2].Success ? Escape.Replace(m.Groups[2].Value, "$1") : m.Groups[3].Value.Trim();
            result.TryAdd(name, value);
        }

        return result;
    }

    private static string? DecodeExtValue(string value, string charset)
    {
        if (charset is "utf-8" or "") return DecodeUtf8(value);
        if (charset == "iso-8859-1")
        {
            return Percent.Replace(value, m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToString());
        }

        return null;
    }

    /// <summary>Like JavaScript's <c>decodeURIComponent</c>: null on a broken escape or invalid UTF-8 (then the plain name is used).</summary>
    private static string? DecodeUtf8(string value)
    {
        var result = new StringBuilder(value.Length);
        var bytes = new List<byte>();
        try
        {
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '%')
                {
                    if (i + 2 >= value.Length) return null;
                    if (!byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b)) return null;
                    bytes.Add(b);
                    i += 2;
                    continue;
                }

                Flush();
                result.Append(value[i]);
            }

            Flush();
            return result.ToString();
        }
        catch (DecoderFallbackException)
        {
            return null;
        }

        void Flush()
        {
            if (bytes.Count == 0) return;
            result.Append(StrictUtf8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
    }
}
