namespace FakeEmailPro.Tests;

public sealed class ContentDispositionTests
{
    [Theory]
    [InlineData("RFC 5987 UTF-8 wins over the ASCII fallback", "attachment; filename=\"invoice_.pdf\"; filename*=UTF-8''%D1%81%D1%87%D1%91%D1%82.pdf", "счёт.pdf")]
    [InlineData("filename* first", "attachment; filename*=UTF-8''a%20b.txt; filename=\"a b.txt\"", "a b.txt")]
    [InlineData("charset is case-insensitive, language tag allowed", "attachment; filename*=utf-8'en'na%C3%AFve.txt", "naïve.txt")]
    [InlineData("ISO-8859-1", "attachment; filename*=ISO-8859-1''caf%E9.txt", "café.txt")]
    [InlineData("quoted plain", "attachment; filename=\"invoice.pdf\"", "invoice.pdf")]
    [InlineData("quoted with escapes and ;", "attachment; filename=\"a \\\"b\\\"; c.pdf\"", "a \"b\"; c.pdf")]
    [InlineData("token", "attachment; filename=report.csv", "report.csv")]
    [InlineData("case-insensitive names", "Attachment; FileName=\"X.PDF\"", "X.PDF")]
    [InlineData("no name: the server's attachment id", "attachment; filename=\"9a7e2c44-0d0b-4f7a-a1c1-2b9d5f3e6a10\"", "9a7e2c44-0d0b-4f7a-a1c1-2b9d5f3e6a10")]
    [InlineData("malformed filename* falls back", "attachment; filename=\"ok.txt\"; filename*=UTF-8''%E2%82", "ok.txt")]
    [InlineData("broken percent escape falls back", "attachment; filename=\"ok.txt\"; filename*=UTF-8''a%2", "ok.txt")]
    [InlineData("unknown charset falls back", "attachment; filename=\"ok.txt\"; filename*=KOI8-R''%C1", "ok.txt")]
    public void Parses_the_file_name(string name, string header, string expected)
    {
        Assert.NotNull(name);
        Assert.Equal(expected, ContentDisposition.ParseFileName(header));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("attachment")]
    [InlineData("attachment; filename=\"\"")]
    public void Returns_null_without_a_header_or_a_name(string? header)
    {
        Assert.Null(ContentDisposition.ParseFileName(header));
    }
}
