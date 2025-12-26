# Web Search Tools Specification

> MCP tools for searching the web and retrieving information from URLs

## Problem Statement

AI agents often need access to current information that:
1. **Isn't in training data** - Recent events, updated documentation, new releases
2. **Requires verification** - Facts that need authoritative sources
3. **Is domain-specific** - Technical documentation, API references, research papers
4. **Changes frequently** - Pricing, availability, schedules, news

Current media library agent lacks the ability to:
- Search for information about movies, TV shows, music
- Find release dates, cast information, reviews
- Look up technical documentation for troubleshooting
- Access current news and updates

## Solution Overview

Two complementary MCP tools for web information retrieval:

| Tool | Purpose |
|------|---------|
| **WebSearch** | Search the web and return relevant results with snippets |
| **WebFetch** | Retrieve and extract content from a specific URL |

---

## Tool 1: WebSearch

### Purpose
Performs a web search and returns structured results with titles, URLs, and snippets.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `query` | string | Yes | The search query |
| `maxResults` | int | No | Maximum results to return (default: 10, max: 20) |
| `site` | string | No | Limit search to specific domain (e.g., "imdb.com") |
| `dateRange` | string | No | Time filter: `day`, `week`, `month`, `year` |
| `safeSearch` | boolean | No | Enable safe search filtering (default: true) |

### Returns

```json
{
  "query": "Dune Part Two release date",
  "totalResults": 1250000,
  "results": [
    {
      "title": "Dune: Part Two (2024) - Release Info - IMDb",
      "url": "https://www.imdb.com/title/tt15239678/releaseinfo/",
      "snippet": "Dune: Part Two was released on March 1, 2024 in the United States. The film premiered at the 74th Berlin International Film Festival on February 15, 2024.",
      "domain": "imdb.com",
      "datePublished": "2024-02-20"
    },
    {
      "title": "Dune: Part Two | Official Website | March 1, 2024",
      "url": "https://www.dunemovie.com/",
      "snippet": "Experience Dune: Part Two, the epic continuation of Denis Villeneuve's adaptation of Frank Herbert's novel. In theaters March 1, 2024.",
      "domain": "dunemovie.com",
      "datePublished": null
    },
    {
      "title": "Dune: Part Two - Wikipedia",
      "url": "https://en.wikipedia.org/wiki/Dune:_Part_Two",
      "snippet": "Dune: Part Two is a 2024 American epic science fiction film. The theatrical release was originally scheduled for November 3, 2023, but was delayed...",
      "domain": "wikipedia.org",
      "datePublished": "2024-03-15"
    }
  ],
  "searchEngine": "brave",
  "searchTime": 0.42
}
```

No results:
```json
{
  "query": "xyzzy123 nonexistent movie 2099",
  "totalResults": 0,
  "results": [],
  "suggestion": "No results found. Try broader search terms or check spelling."
}
```

### Behavior

1. **Query processing**: Send query to search API as-is, let the engine handle parsing
2. **Result deduplication**: Remove duplicate URLs from results
3. **Domain extraction**: Parse domain from URL for easy filtering
4. **Date parsing**: Extract publication dates when available
5. **Snippet truncation**: Limit snippets to ~200 characters
6. **Rate limiting**: Respect API rate limits, queue requests if needed

### Description for LLM

```
Searches the web and returns relevant results with titles, snippets, and URLs.

Parameters:
- query: The search query (be specific for better results)
- maxResults: Number of results (default: 10, max: 20)
- site: Limit to domain (e.g., "imdb.com", "wikipedia.org")
- dateRange: Filter by recency: 'day', 'week', 'month', 'year'
- safeSearch: Enable content filtering (default: true)

Use cases:
- Find movie/show information: query="Dune Part Two cast"
- Look up release dates: query="The Last of Us season 2 release date"
- Find documentation: query="qBittorrent API documentation" site="github.com"
- Get recent news: query="Netflix new releases" dateRange="week"

Tips:
- Use site: parameter when you know the authoritative source
- Use dateRange: for time-sensitive information
- Include specific terms like actor names, years, or version numbers
- For ambiguous queries, add context (e.g., "Dune 2024 movie" not just "Dune")
```

---

## Tool 2: WebFetch

### Purpose
Retrieves content from a URL and extracts readable text, optionally targeting specific elements.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `url` | string | Yes | The URL to fetch |
| `selector` | string | No | CSS selector to target specific content |
| `format` | string | No | Output format: `text`, `markdown`, `html` (default: `markdown`) |
| `maxLength` | int | No | Maximum characters to return (default: 10000) |
| `includeLinks` | boolean | No | Include hyperlinks in output (default: true) |
| `includeImages` | boolean | No | Include image alt text/captions (default: false) |

### Returns

Success:
```json
{
  "url": "https://www.imdb.com/title/tt15239678/",
  "status": "success",
  "title": "Dune: Part Two (2024) - IMDb",
  "content": "# Dune: Part Two\n\n**2024** · PG-13 · 2h 46m\n\nPaul Atreides unites with Chani and the Fremen while seeking revenge against the conspirators who destroyed his family.\n\n## Cast\n\n- Timothée Chalamet as Paul Atreides\n- Zendaya as Chani\n- Rebecca Ferguson as Lady Jessica\n- Josh Brolin as Gurney Halleck\n...",
  "contentLength": 4523,
  "truncated": false,
  "metadata": {
    "description": "Paul Atreides unites with Chani and the Fremen...",
    "author": null,
    "datePublished": "2024-03-01",
    "siteName": "IMDb"
  },
  "links": [
    { "text": "Timothée Chalamet", "url": "https://www.imdb.com/name/nm3154303/" },
    { "text": "Zendaya", "url": "https://www.imdb.com/name/nm3918035/" }
  ]
}
```

With CSS selector:
```json
{
  "url": "https://en.wikipedia.org/wiki/Dune:_Part_Two",
  "selector": ".infobox",
  "status": "success",
  "title": "Dune: Part Two - Wikipedia",
  "content": "| Dune: Part Two |\n|---|\n| Directed by | Denis Villeneuve |\n| Written by | Denis Villeneuve, Jon Spaihts |\n| Based on | Dune by Frank Herbert |\n| Produced by | Mary Parent, Cale Boyter, Denis Villeneuve |\n| Starring | See cast section |\n| Music by | Hans Zimmer |",
  "contentLength": 892,
  "truncated": false,
  "selectorMatched": true
}
```

Error cases:
```json
{
  "url": "https://example.com/404-page",
  "status": "error",
  "errorCode": 404,
  "message": "Page not found",
  "suggestion": "Verify the URL is correct or search for the content"
}
```

```json
{
  "url": "https://paywalled-site.com/article",
  "status": "partial",
  "title": "Premium Article Title",
  "content": "This article requires a subscription...",
  "contentLength": 150,
  "truncated": true,
  "message": "Content appears to be behind a paywall"
}
```

### Behavior

1. **Content extraction**: Use readability algorithm to extract main content
2. **JavaScript handling**: Does NOT execute JavaScript (static fetch only)
3. **Redirect following**: Follow up to 5 redirects
4. **Timeout**: 30 second timeout for slow pages
5. **User-Agent**: Use standard browser user-agent
6. **Robots.txt**: Respect robots.txt directives
7. **Encoding detection**: Handle various character encodings

### Description for LLM

```
Fetches and extracts readable content from a URL.

Parameters:
- url: The web page to fetch
- selector: CSS selector to target specific content (e.g., ".main-content", "#article")
- format: Output format - 'text', 'markdown', or 'html' (default: 'markdown')
- maxLength: Maximum characters (default: 10000)
- includeLinks: Include hyperlinks (default: true)
- includeImages: Include image descriptions (default: false)

Use after WebSearch to:
1. Get full details from a promising search result
2. Extract specific data from known pages
3. Read documentation or articles

Limitations:
- Cannot execute JavaScript (some sites may not work)
- May be blocked by some sites
- Paywalled content will be partial
- Large pages are truncated

Examples:
- Get movie details: url="https://www.imdb.com/title/tt15239678/"
- Extract Wikipedia infobox: url="...", selector=".infobox"
- Read documentation: url="https://docs.example.com/api", format="markdown"
```

---

## Workflow Examples

### Example 1: Find Movie Information

```
1. User asks: "When does Dune Part 2 come out?"

2. Agent calls WebSearch
   query: "Dune Part Two release date 2024"
   maxResults: 5
   
   → Returns results from IMDb, Wikipedia, official site

3. Agent synthesizes answer from snippets
   → "Dune: Part Two was released on March 1, 2024"
```

### Example 2: Get Detailed Cast Information

```
1. User asks: "Who's in the cast of The Last of Us?"

2. Agent calls WebSearch
   query: "The Last of Us TV series cast"
   site: "imdb.com"
   maxResults: 3
   
   → Returns IMDb cast page URL

3. Agent calls WebFetch
   url: "https://www.imdb.com/title/tt3581920/fullcredits/"
   selector: ".cast_list"
   format: "markdown"
   
   → Returns formatted cast list

4. Agent presents structured cast information
```

### Example 3: Find Download/Streaming Information

```
1. User asks: "Where can I watch Oppenheimer?"

2. Agent calls WebSearch
   query: "Oppenheimer streaming where to watch 2024"
   dateRange: "month"
   
   → Returns results about streaming availability

3. Agent summarizes availability from snippets
   → "Oppenheimer is available on Peacock and for purchase on Amazon Prime, Apple TV+"
```

### Example 4: Research Technical Information

```
1. User needs help with torrent client API

2. Agent calls WebSearch
   query: "qBittorrent Web API documentation"
   site: "github.com"
   
   → Returns GitHub wiki page

3. Agent calls WebFetch
   url: "https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-(qBittorrent-4.1)"
   format: "markdown"
   maxLength: 20000
   
   → Returns API documentation

4. Agent uses documentation to help user
```

### Example 5: Check Recent News

```
1. User asks: "Any news about season 2 of Shogun?"

2. Agent calls WebSearch
   query: "Shogun FX season 2 news"
   dateRange: "month"
   maxResults: 5
   
   → Returns recent articles

3. Agent synthesizes news from snippets
   → "FX announced Shogun has been renewed for seasons 2 and 3..."
```

---

## Integration with Media Library

### Synergy with Existing Tools

| Media Library Tool | Web Search Enhancement |
|-------------------|------------------------|
| **ContentRecommendation** | Supplement with IMDb ratings, Rotten Tomatoes scores |
| **FileSearch** | Find metadata for unidentified files |
| **FileDownload** | Search for torrent availability, alternatives |
| **Move/Organize** | Verify correct movie/show titles and years |

### Search Context Enrichment

The agent can use web search to enrich its recommendations:

```
1. User: "Find me something like Severance"

2. Agent calls ContentRecommendation
   → Returns matches from local library

3. Agent calls WebSearch
   query: "TV shows similar to Severance 2024"
   → Gets professional recommendations

4. Agent combines results for comprehensive suggestions
```

### Metadata Verification

```
1. Agent finds file: "The.Batman.2022.BluRay.mkv"

2. Agent calls WebSearch
   query: "The Batman 2022 movie runtime cast"
   site: "imdb.com"
   
3. Agent verifies/enriches metadata
   → Confirms year, gets runtime, director, genre
```

---

## Implementation Notes

### File Location

- `McpServerLibrary/McpTools/McpWebSearchTool.cs`
- `McpServerLibrary/McpTools/McpWebFetchTool.cs`
- `McpServerLibrary/Modules/WebSearch/`

### Domain Contracts

```csharp
public interface IWebSearchProvider
{
    Task<WebSearchResult> SearchAsync(WebSearchQuery query, CancellationToken ct);
}

public interface IWebFetcher
{
    Task<WebFetchResult> FetchAsync(WebFetchRequest request, CancellationToken ct);
}

public record WebSearchQuery(
    string Query,
    int MaxResults = 10,
    string? Site = null,
    DateRange? DateRange = null,
    bool SafeSearch = true);

public record WebSearchResult(
    string Query,
    long TotalResults,
    IReadOnlyList<SearchResultItem> Results,
    string SearchEngine,
    double SearchTime);

public record SearchResultItem(
    string Title,
    string Url,
    string Snippet,
    string Domain,
    DateOnly? DatePublished);

public record WebFetchRequest(
    string Url,
    string? Selector = null,
    OutputFormat Format = OutputFormat.Markdown,
    int MaxLength = 10000,
    bool IncludeLinks = true,
    bool IncludeImages = false);

public record WebFetchResult(
    string Url,
    FetchStatus Status,
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    IReadOnlyList<ExtractedLink>? Links,
    string? ErrorMessage);

public enum OutputFormat { Text, Markdown, Html }
public enum FetchStatus { Success, Partial, Error }
public enum DateRange { Day, Week, Month, Year }
```

### Search Provider Options

Several search APIs could be used:

| Provider | Pros | Cons |
|----------|------|------|
| **Brave Search API** | Privacy-focused, good quality, generous free tier | Requires API key |
| **SearXNG** | Self-hosted, no API limits, aggregates sources | Requires hosting |
| **DuckDuckGo** | No API key needed | Limited/unofficial API |
| **Google Custom Search** | Best results | Expensive, 100 queries/day free |
| **Bing Search API** | Good quality | Requires Azure subscription |

Recommended: **Brave Search API** for best balance of quality, privacy, and cost.

### Content Extraction

For WebFetch, use a readability library:

```csharp
// Options for .NET content extraction:
// 1. SmartReader (port of Mozilla Readability)
// 2. AngleSharp + custom extraction
// 3. Html Agility Pack + custom extraction
```

### Rate Limiting

```csharp
public class RateLimiter
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _interval;
    
    // Example: 1 request per second
    public RateLimiter(int maxRequests, TimeSpan interval)
    {
        _semaphore = new SemaphoreSlim(maxRequests);
        _interval = interval;
    }
}
```

### Caching Strategy

```csharp
public class SearchCache
{
    // Cache search results for 1 hour
    // Cache fetched pages for 24 hours
    // Use content hash for cache key
}
```

### Security Considerations

1. **URL validation**: Only allow http/https schemes
2. **Domain blocklist**: Block known malicious domains
3. **Content sanitization**: Strip scripts and dangerous elements
4. **Size limits**: Cap response size to prevent memory issues
5. **Timeout enforcement**: Prevent hanging on slow servers
6. **SSRF prevention**: Block internal/private IP ranges
7. **Credential handling**: Never include credentials in requests

### Error Handling

| Error | Response |
|-------|----------|
| Network timeout | Return error with suggestion to retry |
| 404 Not Found | Return error with suggestion to search |
| 403 Forbidden | Return error indicating access denied |
| Rate limited | Queue and retry with backoff |
| Invalid URL | Return error with URL validation feedback |
| Content too large | Return truncated with warning |

---

## Configuration

### Settings

```json
{
  "WebSearch": {
    "Provider": "brave",
    "ApiKey": "${BRAVE_API_KEY}",
    "MaxResultsPerQuery": 20,
    "DefaultSafeSearch": true,
    "CacheDurationMinutes": 60,
    "RateLimitPerMinute": 30
  },
  "WebFetch": {
    "TimeoutSeconds": 30,
    "MaxContentLength": 100000,
    "UserAgent": "Mozilla/5.0 (compatible; JackAgent/1.0)",
    "RespectRobotsTxt": true,
    "CacheDurationMinutes": 1440
  }
}
```

### Environment Variables

```
BRAVE_API_KEY=your-api-key-here
WEB_SEARCH_ENABLED=true
WEB_FETCH_ENABLED=true
```

---

## Future Enhancements

1. **Image search**: Search for movie posters, album covers
2. **News aggregation**: Dedicated news search with source filtering
3. **Structured data extraction**: Parse JSON-LD, OpenGraph, schema.org
4. **Page monitoring**: Watch pages for changes (price drops, availability)
5. **Search history**: Track queries for context in conversations
6. **Multi-language support**: Search in different languages/regions
7. **PDF extraction**: Handle PDF documents from URLs
8. **API documentation parsing**: Special handling for API docs sites
