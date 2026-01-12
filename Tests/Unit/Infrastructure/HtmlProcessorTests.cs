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
        result.Content!.Length.ShouldBeLessThanOrEqualTo(520); // Actual returned content should be truncated
        result.ContentLength.ShouldBeGreaterThan(500); // ContentLength is total length (for pagination)
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
    public async Task ProcessAsync_WithClassSelector_ReturnsAllMatches()
    {
        // Arrange - Multiple elements with same class
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div class="product">
                           <p>Product 1</p>
                       </div>
                       <div class="product">
                           <p>Product 2</p>
                       </div>
                       <div class="product">
                           <p>Product 3</p>
                       </div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".product");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert - Should return ALL matches, separated by ---
        result.IsPartial.ShouldBeFalse();
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("Product 1");
        result.Content.ShouldContain("Product 2");
        result.Content.ShouldContain("Product 3");
        result.Content.ShouldContain("---"); // Separator between matches
    }

    [Fact]
    public async Task ProcessAsync_WithClassSelector_CombinesLinksFromAllMatches()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div class="card">
                           <a href="https://example.com/1">Link 1</a>
                       </div>
                       <div class="card">
                           <a href="https://example.com/2">Link 2</a>
                       </div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".card",
            IncludeLinks: true);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert - Links from all matches should be combined
        result.Links.ShouldNotBeNull();
        result.Links.Count.ShouldBe(2);
        result.Links.ShouldContain(l => l.Url == "https://example.com/1");
        result.Links.ShouldContain(l => l.Url == "https://example.com/2");
    }

    [Fact]
    public async Task ProcessAsync_WithOffset_ReturnsContentFromOffset()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <p>First paragraph with some content.</p>
                       <p>Second paragraph with more content.</p>
                       <p>Third paragraph with final content.</p>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Offset: 0, MaxLength: 50);

        // Act - First chunk
        var result1 = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Act - Second chunk starting from offset
        var request2 = request with { Offset = 50 };
        var result2 = await HtmlProcessor.ProcessAsync(request2, html, CancellationToken.None);

        // Assert
        result1.Truncated.ShouldBeTrue();
        result2.Content.ShouldNotBeNull();
        result1.Content.ShouldNotBe(result2.Content); // Different chunks
        result1.ContentLength.ShouldBe(result2.ContentLength); // Same total length
    }

    [Fact]
    public async Task ProcessAsync_WithOffsetBeyondContent_ReturnsEmptyContent()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body><p>Short content</p></body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Offset: 100000);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content.ShouldBe("");
    }

    [Fact]
    public async Task ProcessAsync_WithMultiClassSelector_ReturnsHtml()
    {
        // Arrange - selector with multiple classes, returning HTML format
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <ul>
                           <li class="item active"><strong>Item 1</strong> active</li>
                           <li class="item">Item 2 not active</li>
                           <li class="item active special"><em>Item 3</em> active special</li>
                           <li class="item active">Item 4 active</li>
                       </ul>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "http://example.com/test",
            Selector: "li.item.active",
            Format: WebFetchOutputFormat.Html);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert - Should match li elements with BOTH item AND active classes, preserving HTML
        result.IsPartial.ShouldBeFalse();
        result.Content!.ShouldContain("<strong>Item 1</strong>");
        result.Content!.ShouldContain("<em>Item 3</em>");
        result.Content!.ShouldNotContain("Item 2 not active"); // Missing 'active' class
        result.Content!.ShouldContain("Item 4 active");
    }

    [Fact]
    public async Task ProcessAsync_WithMultiClassSelector_ReturnsMarkdown()
    {
        // Arrange - selector with multiple classes, returning markdown format
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div class="video-list">
                           <li class="pcVideoListItem js-pop">
                               <h3>Video 1 Title</h3>
                               <span class="duration">10:30</span>
                               <a href="/video/1">Watch</a>
                           </li>
                           <li class="pcVideoListItem other">
                               <h3>Video 2 Title</h3>
                               <span class="duration">5:45</span>
                           </li>
                           <li class="pcVideoListItem js-pop">
                               <h3>Video 3 Title</h3>
                               <span class="duration">8:20</span>
                               <a href="/video/3">Watch</a>
                           </li>
                       </div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "http://example.com/test",
            Selector: "li.pcVideoListItem.js-pop",
            Format: WebFetchOutputFormat.Markdown);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert - Should return markdown for matched elements only
        result.IsPartial.ShouldBeFalse();
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("Video 1 Title");
        result.Content.ShouldContain("Video 3 Title");
        result.Content.ShouldNotContain("Video 2 Title"); // Missing 'js-pop' class
        result.Content.ShouldContain("---"); // Separator between matches
        result.Content.ShouldContain("10:30");
        result.Content.ShouldContain("8:20");
        result.Content.ShouldNotContain("5:45"); // From excluded element
    }

    [Fact]
    public async Task ProcessAsync_WithIdSelector_ReturnsExactElement()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div id="before">Before content</div>
                       <div id="target">
                           <p>Target content</p>
                       </div>
                       <div id="after">After content</div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: "#target");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeFalse();
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("Target content");
        result.Content.ShouldNotContain("Before content");
        result.Content.ShouldNotContain("After content");
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