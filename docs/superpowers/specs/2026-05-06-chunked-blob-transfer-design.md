# Chunked Blob Transfer Design

**Date:** 2026-05-06
**Status:** Draft

## Problem

The cross-filesystem transfer machinery from
[2026-05-05-cross-filesystem-transfer-design.md](./2026-05-05-cross-filesystem-transfer-design.md)
added `OpenReadStreamAsync` / `WriteFromStreamAsync` on `IFileSystemBackend` to bridge
mounts. The `Stream` returned by `McpFileSystemBackend.OpenReadStreamAsync` is a
`MemoryStream` populated by reading the entire source file via repeated `fs_blob_read`
calls before returning — defeating the purpose of the chunked MCP tool.

A multi-GB transfer (sandbox → vault for a video file or DB dump) OOMs the agent
process. The `Stream` contract is also misleading: nothing about a returned `Stream`
signals to consumers that it secretly buffers the full file, and the byte-count probe
that originally lived at `VfsCopyTool.TransferFileAsync` (`stream.CanSeek ? stream.Length : -1`)
worked only because of this buffering.

## Goals

- Cap peak agent-side memory per cross-FS transfer at one chunk (~256 KiB plus its
  base64-encoded form, ~340 KiB) regardless of total file size.
- Express forward-only chunked semantics in the type system, so the contract cannot
  lie about buffering.
- Keep the existing pull-based, single-chunk-in-flight throughput shape (no prefetch).
- Preserve current error and cancellation semantics.

## Non-goals

- Throughput optimization via prefetch / concurrent chunk pipelining. Possible
  follow-up.
- Resumable transfers across agent restarts.
- Buffer pooling / `ArrayPool<byte>` reuse. Not needed at current allocation rates.
- Changing the per-chunk size (256 KiB) — stays aligned with
  `BlobReadTool.MaxChunkSizeBytes`.
- Compatibility shims. There are no external consumers of `IFileSystemBackend`.

## Architecture

`IFileSystemBackend`'s streaming pair is replaced by a chunk pair:

```csharp
IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
    string path, CancellationToken ct);

Task<long> WriteChunksAsync(
    string path,
    IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
    bool overwrite, bool createDirectories,
    CancellationToken ct);
```

Both shapes are forward-only by construction. There is no `Position` / `Length` /
`Seek` affordance to lie about. The previous `OpenReadStreamAsync` /
`WriteFromStreamAsync` methods are removed entirely — there are exactly two production
callers (in `VfsCopyTool`) and they migrate atomically.

Cancellation is observed:

- In `ReadChunksAsync` via `[EnumeratorCancellation]` plus an explicit
  `ct.ThrowIfCancellationRequested()` between chunks.
- In `WriteChunksAsync` via `await foreach (... .WithCancellation(ct))` and propagation
  into each `fs_blob_write` MCP call.

## `McpFileSystemBackend` implementation

`ReadChunksAsync` becomes an async iterator:

```csharp
public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
    string path, [EnumeratorCancellation] CancellationToken ct)
{
    long offset = 0;
    while (true)
    {
        ct.ThrowIfCancellationRequested();
        var node = await CallToolAsync("fs_blob_read", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["offset"] = offset,
            ["length"] = 256 * 1024
        }, ct);

        if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
        {
            throw new IOException(
                $"fs_blob_read failed: {obj["message"]?.GetValue<string>()}");
        }

        var bytes = Convert.FromBase64String(node["contentBase64"]!.GetValue<string>());
        if (bytes.Length > 0)
        {
            offset += bytes.Length;
            yield return bytes;
        }

        if (node["eof"]!.GetValue<bool>()) yield break;
        if (bytes.Length == 0) yield break;   // defensive: server said !eof but sent nothing
    }
}
```

`WriteChunksAsync` mirrors today's `WriteFromStreamAsync`, replacing the `ReadAsync`
loop with `await foreach`:

```csharp
public async Task<long> WriteChunksAsync(
    string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
    bool overwrite, bool createDirectories, CancellationToken ct)
{
    long offset = 0;
    await foreach (var chunk in chunks.WithCancellation(ct))
    {
        var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["contentBase64"] = Convert.ToBase64String(chunk.Span),
            ["offset"] = offset,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);

        if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
        {
            throw new IOException(
                $"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
        }

        offset += chunk.Length;
    }

    if (offset == 0)
    {
        // Empty source: still create the file (matches pre-change semantics).
        var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
        {
            ["path"] = path,
            ["contentBase64"] = "",
            ["offset"] = 0L,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);

        if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
        {
            throw new IOException(
                $"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
        }
    }

    return offset;
}
```

## `VfsCopyTool` rewiring

Both cross-FS branches collapse to a direct pipe — no `await using` block, no
intermediate `Stream`:

```csharp
// TransferFileAsync — cross-FS branch
bytes = await dst.Backend.WriteChunksAsync(
    dst.RelativePath,
    src.Backend.ReadChunksAsync(src.RelativePath, ct),
    overwrite, createDirectories, ct);

// TransferDirectoryAsync — same shape inside the per-entry try block
bytes = await dst.Backend.WriteChunksAsync(
    dstRel,
    src.Backend.ReadChunksAsync(srcRel, ct),
    overwrite, createDirectories, ct);
```

Peak agent memory per concurrent transfer: one 256 KiB chunk plus its base64 string.
The byte count returned by `WriteChunksAsync` is what flows into the result envelope's
`bytes` field (already the case after the `WriteFromStreamAsync` return-value fix).

## Error handling

| Failure                                  | Behavior                                                                                       |
|------------------------------------------|------------------------------------------------------------------------------------------------|
| `{ ok:false }` envelope on `fs_blob_read` | `IOException` with the server's message, thrown on the next `await foreach` pull               |
| `{ ok:false }` envelope on `fs_blob_write`| `IOException` with the server's message, thrown synchronously from `WriteChunksAsync`          |
| `OperationCanceledException`              | Propagates through both producer and consumer; iterator is disposed on the way out             |
| Per-entry directory failure               | Caught in `VfsCopyTool.TransferDirectoryAsync` and recorded as `failed` (unchanged)            |
| Consumer abandons the iterator early      | `IAsyncEnumerator` is disposed by the runtime; no in-flight `fs_blob_read` continues past `ct` |

## Testing

Three layers, mirroring the existing structure:

**Unit (rewritten).** `Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs`,
`VfsTransferDirectoryTests.cs`, and `VfsMoveToolCrossFsTests.cs` move from
`MemoryStream`-returning mocks to a tiny `ToAsyncEnumerable(params byte[][] chunks)`
test helper. Existing scenarios (cross-FS file streams through agent, partial-failure
records `partial`, move deletes source on success, glob entry outside source dir is
rejected) carry over unchanged in semantics — only the mock shape changes.

**Streaming-property (new).** `Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs`
adds `ReadChunksAsync_YieldsFirstChunkBeforeReadingRest`. Inject a fake `IMcpClient`
whose `fs_blob_read` increments a call counter and returns a fixed 256 KiB synthetic
chunk with `eof=false` for the first 40 calls, then `eof=true` on the 41st (simulates
a ~10 MB file). The test gets the iterator, calls `MoveNextAsync()` exactly once, and
asserts:

- `current` is the first chunk (256 KiB).
- The fake client's call counter is exactly `1`.

Without the fix, the producer would have called `fs_blob_read` 41 times before the
first `MoveNextAsync()` returned. With the fix, the count is 1. This pins the
incremental-yield property directly, without process-RSS introspection.

**Integration (rewritten).** `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs`
becomes `McpFileSystemBackendChunkTests.cs`. The existing 600 KiB roundtrip stays;
add a 5–10 MB roundtrip that exercises `fs_blob_read` across ~20–40 chunks against
the real MCP transport, asserting byte-for-byte equality of source and destination.

**Smoke (manual, optional).** Inside the running stack, copy a moderately large file
(1+ GB if practical) sandbox → vault and watch the agent's RSS via `docker stats`
to confirm memory stays flat. Not required for merge; useful as a one-off sanity check.

## Decisions made inline

- **Fresh `byte[]` per yield, not a reused buffer.** The writer materializes
  `Convert.ToBase64String(chunk.Span)` before the next pull, so reuse would be safe
  today — but yielding fresh arrays insulates future consumers (e.g., a
  hash-then-write pipeline) from action-at-a-distance bugs. Allocation cost: one
  `byte[256 KiB]` per chunk transferred — negligible compared to the base64
  round-trip already happening per chunk.
- **`ReadOnlyMemory<byte>` over `byte[]` in the public signature.** Identical
  ergonomics for callers; avoids committing to "always a fresh array" if pooling
  is ever introduced.
- **No prefetch / single chunk in flight.** Adding one read in flight while the
  previous chunk writes would roughly double throughput but also double peak memory
  and complicate the iterator's cancellation story. Deferred.
- **Hard-coded 256 KiB chunk size.** Matches `BlobReadTool.MaxChunkSizeBytes`. Making
  it configurable adds a knob nobody would tune.

## Migration

Contract-breaking change to `IFileSystemBackend`. One production implementation
(`McpFileSystemBackend`), two production callers (`VfsCopyTool.TransferFileAsync`,
`TransferDirectoryAsync`). All change in the same commit. Test mocks and integration
tests are updated in lockstep.

No deprecation path. `OpenReadStreamAsync` and `WriteFromStreamAsync` are deleted, not
marked obsolete — no external consumers exist.
