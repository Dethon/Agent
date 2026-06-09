using Domain.DTOs;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class HtmlConverterTests
{
    [Fact]
    public void Convert_HtmlDeclaresLegacyCharset_PreservesUnicodeAccents()
    {
        // Convert(string) backs the readability path. The input is an already-decoded Unicode
        // string; it must not be re-decoded through any <meta charset> in the markup, which would
        // double-encode accents (é -> Ã©) on pages declaring a legacy charset.
        var html = """
                   <html>
                   <head><meta charset="ISO-8859-15"></head>
                   <body><p>El miércoles en Cáceres, máxima 30°C.</p></body>
                   </html>
                   """;

        var markdown = HtmlConverter.Convert(html, WebFetchOutputFormat.Markdown);

        markdown.ShouldNotContain("Ã");
        markdown.ShouldNotContain("Â");
        markdown.ShouldContain("miércoles");
        markdown.ShouldContain("Cáceres");
        markdown.ShouldContain("30°C");
    }
}