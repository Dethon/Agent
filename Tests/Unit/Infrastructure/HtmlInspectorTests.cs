using AngleSharp;
using AngleSharp.Dom;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class HtmlInspectorTests
{
    private static async Task<IDocument> ParseHtmlAsync(string html)
    {
        return await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
    }

    #region Main Content Detection Tests

    [Fact]
    public async Task InspectStructure_DetectsMainElement()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <header><h1>Header</h1></header>
                                <main id="content">
                                    <p>This is the main content area with lots of important text that should be detected.</p>
                                </main>
                                <footer>Footer</footer>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.MainContent.ShouldNotBeNull();
        result.MainContent.Selector.ShouldContain("main");
        result.MainContent.Preview!.ShouldContain("main content");
    }

    [Fact]
    public async Task InspectStructure_DetectsArticleAsMainContent()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <nav>Navigation</nav>
                                <article class="post-content">
                                    <h1>Article Title</h1>
                                    <p>This is a long article with lots of content that should be detected as the main content area.</p>
                                </article>
                                <aside>Sidebar</aside>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.MainContent.ShouldNotBeNull();
        result.MainContent!.Selector.ShouldContain("article");
    }

    #endregion

    #region Repeating Elements Detection Tests

    [Fact]
    public async Task InspectStructure_DetectsRepeatingElements()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div class="product-list">
                                    <div class="product-card">
                                        <h3>Product 1</h3>
                                        <span class="price">$19.99</span>
                                    </div>
                                    <div class="product-card">
                                        <h3>Product 2</h3>
                                        <span class="price">$29.99</span>
                                    </div>
                                    <div class="product-card">
                                        <h3>Product 3</h3>
                                        <span class="price">$39.99</span>
                                    </div>
                                    <div class="product-card">
                                        <h3>Product 4</h3>
                                        <span class="price">$49.99</span>
                                    </div>
                                </div>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.RepeatingElements.ShouldNotBeEmpty();
        var productCards = result.RepeatingElements.FirstOrDefault(r => r.Selector.Contains("product-card"));
        productCards.ShouldNotBeNull();
        productCards.Count.ShouldBe(4);
        productCards.DetectedFields!.ShouldContain("title");
        productCards.DetectedFields!.ShouldContain("price");
    }

    [Fact]
    public async Task InspectStructure_DetectsFieldsInRepeatingElements()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <ul class="results">
                                    <li class="result-item">
                                        <img src="img1.jpg">
                                        <a href="#">Link 1</a>
                                        <p class="description">Description 1</p>
                                        <span class="date">2024-01-01</span>
                                    </li>
                                    <li class="result-item">
                                        <img src="img2.jpg">
                                        <a href="#">Link 2</a>
                                        <p class="description">Description 2</p>
                                        <span class="date">2024-01-02</span>
                                    </li>
                                    <li class="result-item">
                                        <img src="img3.jpg">
                                        <a href="#">Link 3</a>
                                        <p class="description">Description 3</p>
                                        <span class="date">2024-01-03</span>
                                    </li>
                                </ul>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.RepeatingElements.ShouldNotBeEmpty();
        var items = result.RepeatingElements.FirstOrDefault(r => r.Selector.Contains("result-item"));
        items.ShouldNotBeNull();
        items.DetectedFields!.ShouldContain("image");
        items.DetectedFields!.ShouldContain("link");
        items.DetectedFields!.ShouldContain("description");
        items.DetectedFields!.ShouldContain("date");
    }

    #endregion

    #region Navigation Detection Tests

    [Fact]
    public async Task InspectStructure_DetectsPagination()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div class="content">Content here</div>
                                <div class="pagination">
                                    <a href="?page=1">1</a>
                                    <a href="?page=2">2</a>
                                    <a href="?page=3">3</a>
                                </div>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Navigation.ShouldNotBeNull();
        result.Navigation!.PaginationSelector.ShouldBe(".pagination");
    }

    [Fact]
    public async Task InspectStructure_DetectsNextPrevLinks()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div class="content">Content here</div>
                                <a class="prev" href="?page=1">Previous</a>
                                <a class="next" href="?page=3">Next</a>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Navigation.ShouldNotBeNull();
        result.Navigation!.NextPageSelector.ShouldNotBeNull();
        result.Navigation.PrevPageSelector.ShouldNotBeNull();
    }

    [Fact]
    public async Task InspectStructure_DetectsNavMenu()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <nav>
                                    <a href="/">Home</a>
                                    <a href="/about">About</a>
                                    <a href="/contact">Contact</a>
                                </nav>
                                <main>Content</main>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Navigation.ShouldNotBeNull();
        result.Navigation.MenuSelector!.ShouldContain("nav");
    }

    #endregion

    #region Outline Tests

    [Fact]
    public async Task InspectStructure_BuildsHierarchicalOutline()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <header>Header content</header>
                                <main>
                                    <article>Article content</article>
                                    <aside>Sidebar</aside>
                                </main>
                                <footer>Footer content</footer>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Outline.ShouldNotBeEmpty();

        // Should have header, main, footer at top level
        result.Outline.Select(o => o.Tag).ShouldContain("header");
        result.Outline.Select(o => o.Tag).ShouldContain("main");
        result.Outline.Select(o => o.Tag).ShouldContain("footer");

        // Main should have children
        var mainNode = result.Outline.FirstOrDefault(o => o.Tag == "main");
        mainNode.ShouldNotBeNull();
        mainNode.Children.ShouldNotBeNull();
        mainNode.Children.Select(c => c.Tag).ShouldContain("article");
    }

    [Fact]
    public async Task InspectStructure_OutlineIncludesPreviews()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <header>This is the header with navigation links</header>
                                <main>This is the main content area</main>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        var headerNode = result.Outline.FirstOrDefault(o => o.Tag == "header");
        headerNode.ShouldNotBeNull();
        headerNode.Preview!.ShouldContain("header");
    }

    #endregion

    #region Suggestions Tests

    [Fact]
    public async Task InspectStructure_GeneratesSuggestions()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <main>
                                    <article>Long article content here that should be detected as main content.</article>
                                </main>
                                <div class="pagination">
                                    <a href="?page=2">Next</a>
                                </div>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Suggestions.ShouldNotBeEmpty();
        // Should suggest main content selector
        result.Suggestions.ShouldContain(s => s.Contains("selector="));
    }

    [Fact]
    public async Task InspectStructure_SuggestsRepeatingElements()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div class="item">Item 1</div>
                                <div class="item">Item 2</div>
                                <div class="item">Item 3</div>
                                <div class="item">Item 4</div>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Suggestions.ShouldContain(s => s.Contains("4 items"));
    }

    #endregion

    #region Scoping Tests

    [Fact]
    public async Task InspectStructure_WithScope_OnlyInspectsWithinScope()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div class="products">
                                    <div class="card">Product 1</div>
                                    <div class="card">Product 2</div>
                                    <div class="card">Product 3</div>
                                </div>
                                <div class="other">
                                    <div class="card">Other 1</div>
                                    <div class="card">Other 2</div>
                                    <div class="card">Other 3</div>
                                </div>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, ".products");

        // Should only see cards within .products scope
        var cards = result.RepeatingElements.FirstOrDefault(r => r.Selector.Contains("card"));
        cards.ShouldNotBeNull();
        cards.Count.ShouldBe(3); // Only 3 from .products, not 6 total
    }

    [Fact]
    public async Task InspectStructure_WithNonMatchingScope_ReturnsEmpty()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div>Content</div>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, ".nonexistent");

        result.MainContent.ShouldBeNull();
        result.RepeatingElements.ShouldBeEmpty();
        result.Outline.ShouldBeEmpty();
        result.TotalTextLength.ShouldBe(0);
    }

    #endregion

    #region Search Tests

    [Fact]
    public async Task SearchText_FindsTextMatches()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <p>First paragraph with price $19.99</p>
                                <p>Second paragraph with price $29.99</p>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.SearchText(document, "price", false, 10, null);

        result.TotalMatches.ShouldBe(2);
        result.Matches.ShouldAllBe(m => m.Context.Contains("price"));
    }

    [Fact]
    public async Task SearchText_WithRegex_FindsPatterns()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <p>Order #12345</p>
                                <p>Order #67890</p>
                                <p>No order here</p>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.SearchText(document, @"#\d{5}", true, 10, null);

        result.TotalMatches.ShouldBe(2);
    }

    [Fact]
    public async Task SearchText_WithScope_SearchesOnlyInScope()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <header><p>Header price $10</p></header>
                                <main id="content"><p>Main price $20</p></main>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.SearchText(document, "price", false, 10, "#content");

        result.TotalMatches.ShouldBe(1);
        result.Matches[0].Context.ShouldContain("$20");
    }

    [Fact]
    public async Task SearchText_WithEmptyQuery_ReturnsNoMatches()
    {
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <body><p>Some content</p></body>
                   </html>
                   """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.SearchText(document, "", false, 10, null);

        result.TotalMatches.ShouldBe(0);
    }

    #endregion

    #region Form Inspection Tests

    [Fact]
    public async Task InspectForms_ExtractsFormDetails()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <form id="login" action="/login" method="POST">
                                    <input type="text" name="username" placeholder="Username">
                                    <input type="password" name="password" placeholder="Password">
                                    <button type="submit">Login</button>
                                </form>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var forms = HtmlInspector.InspectForms(document, null);

        forms.Count.ShouldBe(1);
        forms[0].Selector.ShouldContain("login");
        forms[0].Fields.Count.ShouldBe(2);
        forms[0].Buttons.Count.ShouldBe(1);
    }

    [Fact]
    public async Task InspectForms_WithScope_OnlyReturnsFormsInScope()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <div class="sidebar">
                                    <form id="search"><input name="q"></form>
                                </div>
                                <main>
                                    <form id="contact"><input name="email"></form>
                                </main>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var forms = HtmlInspector.InspectForms(document, "main");

        forms.Count.ShouldBe(1);
        forms[0].Selector.ShouldContain("contact");
    }

    #endregion

    #region Interactive Element Tests

    [Fact]
    public async Task InspectInteractive_FindsButtonsAndLinks()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <button>Click me</button>
                                <a href="#">Link 1</a>
                                <a href="#">Link 2</a>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectInteractive(document, null);

        result.Buttons.Count.ShouldBe(1);
        result.Links.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task InspectInteractive_GroupsSimilarElements()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <button class="delete">Delete</button>
                                <button class="delete">Delete</button>
                                <button class="delete">Delete</button>
                                <button class="edit">Edit</button>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectInteractive(document, null);

        var deleteButtons = result.Buttons.FirstOrDefault(b => b.Text == "Delete");
        deleteButtons.ShouldNotBeNull();
        deleteButtons.Count.ShouldBe(3);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task InspectStructure_WithEmptyPage_ReturnsEmptyResults()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body></body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.MainContent.ShouldBeNull();
        result.RepeatingElements.ShouldBeEmpty();
    }

    [Fact]
    public async Task InspectStructure_SuggestsFallbackWhenNoStructureDetected()
    {
        const string html = """
                            <!DOCTYPE html>
                            <html>
                            <body>
                                <p>Just some text</p>
                            </body>
                            </html>
                            """;

        var document = await ParseHtmlAsync(html);
        var result = HtmlInspector.InspectStructure(document, null);

        result.Suggestions.ShouldContain(s => s.Contains("interactive"));
    }

    #endregion
}