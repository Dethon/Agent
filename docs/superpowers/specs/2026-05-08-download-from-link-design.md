# download_file from a link — design

Status: approved
Date: 2026-05-08

## Goal & scope

Captain Agent (the Library/downloader agent) currently has one path to start a
download: `file_search` (Jackett) → `download_file(searchResultId)`. This change
adds a second path so the agent can fall back to its existing web tools
(`web_search`, `web_browse`, `web_snapshot`) when Jackett comes up empty: it
finds a magnet URI or `.torrent` URL on an indexer site and passes that link
directly to `download_file`. The new path must produce an int download id
usable by the existing `download_status`, `download_cleanup`, and
`download_resubscribe` tools — those tools do not change.

Out of scope:

- The web tools themselves (already implemented in `McpServerWebSearch`).
- Non-torrent download backends.
- Persistence of the search-results cache across agent restarts.

## Tool surface

`download_file` becomes polymorphic. Exactly one of `searchResultId` or
(`link` + `title`) must be supplied.

```
download_file(
  searchResultId: int?     // existing path: id from a prior file_search
  link:           string?  // new path: magnet:?xt=... or http(s)://*.torrent
  title:          string?  // required iff link is set; release name agent saw on the page
)
```

The MCP `Description` is rewritten to spell out the contract:

> Provide either `searchResultId` (from a prior `file_search` call) or `link`
> + `title` (when the link was obtained via web tools). Do not provide both.

Validation runs at the MCP tool boundary and returns `ToolError` envelopes with
`retryable: false`:

- Both `searchResultId` and `link` set → `InvalidArgument`.
- Neither set → `InvalidArgument`.
- `link` set but `title` missing/blank → `InvalidArgument`.
- `link` set but doesn't start with `magnet:`, `http://`, or `https://` →
  `InvalidArgument`.

## Domain layer changes

`Domain/Tools/Downloads/FileDownloadTool.cs` gets two protected entry points
and one shared private helper:

```csharp
protected Task<JsonNode> Run(string sessionId, int searchResultId, CancellationToken ct);
    // existing path; resolves SearchResult from searchResultsManager,
    // then delegates to StartDownload.

protected Task<JsonNode> Run(string sessionId, string link, string title, CancellationToken ct);
    // new path. Derives id = link.GetHashCode() (matches JackettSearchClient
    // convention), seeds searchResultsManager with a synthetic SearchResult
    // { Id, Title = title, Link = link, Category = null, Size = null,
    //   Seeders = null, Peers = null }, then delegates to StartDownload.

private Task<JsonNode> StartDownload(string sessionId, int id, string link, CancellationToken ct);
    // duplicate-check via IDownloadClient.GetDownloadItem(id), then
    // client.Download(link, savePath, id) and trackedDownloadsManager.Add.
    // Returns the existing success / AlreadyExists envelopes.
```

Why hash the link for the id: matches `JackettSearchClient.ParseTorznabItem`
(`Id = link.GetHashCode()`). The same magnet from Jackett or the web yields the
same id within a process, so the duplicate check naturally short-circuits if
the agent web-finds a link Jackett already returned.

Why seed the search-results cache: `download_status` calls
`searchResultsManager.Get(sessionId, id)` for the title. Without seeding,
link-mode downloads would show "Missing Title". No other tool reads from this
cache.

No changes to `IDownloadClient`, `ITrackedDownloadsManager`,
`ISearchResultsManager`, the DTOs, or the cleanup/status/resubscribe tools.

## MCP layer changes

`McpServerLibrary/McpTools/McpFileDownloadTool.cs` gains two optional
parameters and dispatches to the matching `Run` overload after validation:

```csharp
[McpServerTool(Name = Name)]
[Description(Description)]
public async Task<CallToolResult> McpRun(
    RequestContext<CallToolRequestParams> context,
    [Description("Id from a prior file_search result. Mutually exclusive with link.")]
    int? searchResultId,
    [Description("Magnet URI or .torrent URL obtained via web tools. Requires title. Mutually exclusive with searchResultId.")]
    string? link,
    [Description("Descriptive title for the download (required when link is provided).")]
    string? title,
    CancellationToken cancellationToken)
{
    var sessionId = context.Server.StateKey;
    var validation = ValidateInputs(searchResultId, link, title);
    if (validation is not null) return ToolResponse.Create(validation);

    var result = searchResultId.HasValue
        ? await Run(sessionId, searchResultId.Value, cancellationToken)
        : await Run(sessionId, link!, title!, cancellationToken);

    await context.Server.SendNotificationAsync(
        "notifications/resources/list_changed",
        cancellationToken: cancellationToken);
    return ToolResponse.Create(result);
}
```

`ValidateInputs` is a private static returning `JsonNode?` — null on success,
`ToolError` envelope on failure. Per `.claude/rules/mcp-tools.md` we do not
catch other exceptions; the global filter in `ConfigModule` handles them.

The `Description` constant lives on the Domain base class (existing pattern)
and is rewritten as quoted above.

## Captain Agent prompt update

`Domain/Prompts/DownloaderPrompt.cs` — Phase 1 ("The Hunt") gains a new bullet
near the end, before the "moment a suitable treasure is identified" line:

> **When the Indexers Run Dry:** If your volleys with `file_search` come up
> empty after exhausting reasonable variations (different separators, looser
> query, alternate translations of the title), board the open seas. Use
> `web_search` to find indexer sites or torrent listings, then `web_browse`
> and `web_snapshot` to drill into a page and extract the magnet URI or
> `.torrent` URL. When ye find one, pass it directly to the `download_file`
> tool with the `link` and a descriptive `title` ye gleaned from the page
> (e.g., the release name with quality and group). The same quality bar from
> Phase 1 still applies — don't accept low-seeder or wrong-quality booty just
> because ye found it on the web.

Phase 2 ("The Plunder") does not change — `download_file` is still the one
tool that initiates downloads; only its arguments are richer. The
`AgentDescription` block is unchanged.

## Testing

**Unit (`Tests/Unit/Domain/FileDownloadToolTests.cs` — new file, flat under `Tests/Unit/Domain/` to match existing tool-test layout):**

- Existing search-id tests stay untouched (happy path; id not in cache).
- New link-path happy case: synthetic `SearchResult` lands in
  `searchResultsManager` with the supplied title; `IDownloadClient.Download`
  called with `(link, "<base>/<hash>", hash)`; `trackedDownloadsManager.Add`
  called.
- New link-path duplicate: when `IDownloadClient.GetDownloadItem(hash)` already
  returns a value, return the `AlreadyExists` envelope without re-adding.

**Validation tests at the MCP layer:**

- Both `searchResultId` and `link` set → `InvalidArgument`.
- Neither set → `InvalidArgument`.
- `link` set, `title` missing or whitespace → `InvalidArgument`.
- `link` with disallowed prefix → `InvalidArgument`.

**Integration (`Tests/Integration/McpServerTests/McpLibraryServerTests.cs`):**

Add a case that calls `download_file` with `link` + `title` against the real
`QBittorrentFixture`, then asserts `download_status` returns the supplied
title and `download_cleanup` succeeds using the same id.

**TDD discipline:** RED step first, then GREEN, then REVIEW. One triplet per
layer (Domain, then MCP, then Integration). Auto-commit after each successful
triplet per `.claude/rules/tdd.md` and the project's "auto-commit after
triplets" rule.

## Risks & accepted limitations

- **`string.GetHashCode()` is randomized per process in .NET.** The same
  magnet hashes to a different int in a different process. The Jackett path
  already has this limitation — `download_resubscribe` after a restart relies
  on the agent remembering the id from conversation history. The link path
  inherits this; not fixed here.
- **Hash collisions** between two different links are theoretically possible
  in 32-bit space. Pre-existing in the Jackett path; impact is the same (the
  second download appears to "already exist"). Not a new risk.
- **No rejection of plain web pages.** A `magnet:` / `http://` / `https://`
  prefix passes validation, but the agent could still send an HTML URL.
  qBittorrent rejects it and that error surfaces as a download-add failure.
  Stricter validation (e.g., requiring `.torrent` suffix on http(s)) would
  also reject torznab-style download URLs that lack the suffix, which is
  worse.
- **Title quality** is the agent's responsibility. Bad titles affect status
  reports only, not download behavior. The prompt update directs the agent to
  use the release name from the page.

## Files touched

- `Domain/Tools/Downloads/FileDownloadTool.cs` — new overload + helper.
- `Domain/Prompts/DownloaderPrompt.cs` — Phase 1 fallback bullet.
- `McpServerLibrary/McpTools/McpFileDownloadTool.cs` — new params, validation,
  dispatch.
- `Tests/Unit/Domain/FileDownloadToolTests.cs` — new file with link-path and
  search-id-path cases.
- `Tests/Unit/McpServerLibrary/McpFileDownloadToolTests.cs` — new file with
  the four MCP-boundary validation cases.
- `Tests/Integration/McpServerTests/McpLibraryServerTests.cs` — new
  end-to-end case.
