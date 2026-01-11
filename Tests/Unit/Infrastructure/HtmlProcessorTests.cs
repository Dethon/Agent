using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class HtmlProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithValidHtml_ReturnsContent()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test Page</title></head>
                   <body>
                       <article>
                           <h1>Hello World</h1>
                           <p>This is test content.</p>
                       </article>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeFalse();
        result.Title.ShouldBe("Test Page");
        result.Content.ShouldNotBeNullOrEmpty();
        result.Content.ShouldContain("Hello World");
    }

    [Fact]
    public async Task ProcessAsync_WithCssSelector_ReturnsTargetedContent()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test Page</title></head>
                   <body>
                       <div class="header">Header content</div>
                       <div class="main-content">
                           <p>Main content here</p>
                       </div>
                       <div class="footer">Footer content</div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".main-content");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeFalse();
        result.Content!.ShouldContain("Main content here");
        result.Content!.ShouldNotContain("Header content");
        result.Content!.ShouldNotContain("Footer content");
    }

    [Fact]
    public async Task ProcessAsync_WithNonMatchingSelector_ReturnsPartialStatus()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test Page</title></head>
                   <body><p>Content</p></body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".nonexistent");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeTrue();
        result.ErrorMessage!.ShouldContain("nonexistent");
    }

    [Fact]
    public async Task ProcessAsync_ExtractsMetadata()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head>
                       <title>Test Page</title>
                       <meta name="description" content="Page description">
                       <meta name="author" content="John Doe">
                       <meta property="og:site_name" content="Example Site">
                       <meta property="article:published_time" content="2024-01-15T10:00:00Z">
                   </head>
                   <body><p>Content</p></body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Metadata.ShouldNotBeNull();
        result.Metadata.Description.ShouldBe("Page description");
        result.Metadata.Author.ShouldBe("John Doe");
        result.Metadata.SiteName.ShouldBe("Example Site");
        result.Metadata.DatePublished.ShouldBe(new DateOnly(2024, 1, 15));
    }

    [Fact]
    public async Task ProcessAsync_ExtractsLinks()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test Page</title></head>
                   <body>
                       <a href="https://example.com/page1">Link 1</a>
                       <a href="https://example.com/page2">Link 2</a>
                       <a href="/relative">Relative Link</a>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", IncludeLinks: true);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Links.ShouldNotBeNull();
        result.Links.Count.ShouldBe(2); // Only absolute URLs
        result.Links.ShouldContain(l => l.Text == "Link 1" && l.Url == "https://example.com/page1");
        result.Links.ShouldContain(l => l.Text == "Link 2" && l.Url == "https://example.com/page2");
    }

    [Fact]
    public async Task ProcessAsync_WithMarkdownFormat_ConvertsToMarkdown()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <h1>Title</h1>
                       <p>Text with <strong>bold</strong> and <em>italic</em>.</p>
                       <a href="https://example.com">Link</a>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test",
            Format: WebFetchOutputFormat.Markdown);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content!.ShouldContain("# Title");
        result.Content!.ShouldContain("**bold**");
        result.Content!.ShouldContain("*italic*");
        result.Content!.ShouldContain("[Link](https://example.com)");
    }

    [Fact]
    public async Task ProcessAsync_TruncatesLongContent()
    {
        // Arrange
        var longContent = string.Join("\n",
            Enumerable.Range(1, 1000).Select(i => $"<p>Paragraph {i} with some content.</p>"));
        var html = $"""
                    <!DOCTYPE html>
                    <html>
                    <head><title>Test</title></head>
                    <body>{longContent}</body>
                    </html>
                    """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", MaxLength: 500);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Truncated.ShouldBeTrue();
        result.ContentLength.ShouldBeLessThanOrEqualTo(520); // Some tolerance for truncation message
        result.Content!.ShouldContain("[Content truncated...]");
    }

    [Fact]
    public async Task ProcessAsync_WithHtmlFormat_ReturnsRawHtml()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div class="container">
                           <h1>Title</h1>
                           <p>Paragraph with <strong>bold</strong> text.</p>
                       </div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test",
            Format: WebFetchOutputFormat.Html);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("<div class=\"container\">");
        result.Content.ShouldContain("<h1>Title</h1>");
        result.Content.ShouldContain("<strong>bold</strong>");
    }

    [Fact]
    public async Task ProcessAsync_WithHtmlFormat_TruncatesWithValidHtml()
    {
        // Arrange
        var longContent = string.Join("\n",
            Enumerable.Range(1, 100).Select(i => $"<div><p>Paragraph {i} with some content.</p></div>"));
        var html = $"""
                    <!DOCTYPE html>
                    <html>
                    <head><title>Test</title></head>
                    <body><main>{longContent}</main></body>
                    </html>
                    """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test",
            Format: WebFetchOutputFormat.Html, MaxLength: 500);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Truncated.ShouldBeTrue();
        result.Content.ShouldNotBeNull();
        // HTML truncation should use HTML comment
        result.Content.ShouldContain("<!-- Content truncated -->");
        // Should not end with unclosed tags
        result.Content.ShouldNotEndWith("<");
        result.Content.ShouldNotEndWith("<div");
        result.Content.ShouldNotEndWith("<p");
    }
}