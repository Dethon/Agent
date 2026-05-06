# Chunked Blob Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `IFileSystemBackend.OpenReadStreamAsync` / `WriteFromStreamAsync` (which secretly buffer the full file in agent memory) with forward-only `ReadChunksAsync` / `WriteChunksAsync` so cross-filesystem transfers stream chunk-by-chunk and peak agent memory stays bounded at one 256 KiB chunk regardless of file size.

**Architecture:** New chunk-pair API on `IFileSystemBackend` expressed via `IAsyncEnumerable<ReadOnlyMemory<byte>>`. `McpFileSystemBackend.ReadChunksAsync` is an async iterator that yields one base64-decoded `fs_blob_read` chunk at a time; `WriteChunksAsync` consumes an `IAsyncEnumerable` and forwards each chunk via `fs_blob_write`. `VfsCopyTool` cross-FS branches collapse to a direct `WriteChunksAsync(ReadChunksAsync(...))` pipe — no intermediate `Stream`. Old stream methods are deleted, not deprecated; there are exactly two production callers and they migrate atomically. A unit-level streaming-property test pins the incremental-yield invariant by counting `fs_blob_read` calls after a single `MoveNextAsync()`.

**Tech Stack:** .NET 10, ModelContextProtocol SDK, xUnit, Moq, Shouldly.

**Spec:** `docs/superpowers/specs/2026-05-06-chunked-blob-transfer-design.md`

---

## File Structure

| File | Role | Action |
|------|------|--------|
| `Domain/Contracts/IFileSystemBackend.cs` | Backend contract | Modify: add `ReadChunksAsync`/`WriteChunksAsync`, remove old stream methods |
| `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` | MCP-backed implementation | Modify: replace stream methods with chunk methods; unseal class; expose `CallToolAsync` as `protected internal virtual` for test seam |
| `Domain/Tools/FileSystem/VfsCopyTool.cs` | Cross-FS dispatch | Modify: replace `await using` stream blocks with direct chunk-pipe in both `TransferFileAsync` and `TransferDirectoryAsync` |
| `Tests/Unit/Domain/Tools/FileSystem/AsyncEnumerableTestHelpers.cs` | Test helper | Create: `ToAsyncEnumerable(params byte[][])` for chunk-based mocks |
| `Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs` | Unit tests | Modify: cross-FS test uses chunk mocks |
| `Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs` | Unit tests | Modify: all cross-FS tests use chunk mocks |
| `Tests/Unit/Domain/Tools/FileSystem/VfsMoveToolCrossFsTests.cs` | Unit tests | Modify: cross-FS test uses chunk mocks |
| `Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs` | Unit test (new) | Create: streaming-property test — `MoveNextAsync()` once → exactly 1 `fs_blob_read` call |
| `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs` | Integration test | Delete (renamed into the chunk-tests file below) |
| `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs` | Integration test (new) | Create: 600 KiB and 5–10 MB roundtrips against real MCP transport |

`McpFileSystemBackend` becomes non-`sealed` to enable a counting test subclass. `CallToolAsync` is elevated from `private` to `protected internal virtual` (the `internal` is reachable from the `Tests` project via the existing `<InternalsVisibleTo Include="Tests" />` in `Infrastructure.csproj`).

---

## Pre-flight

### Step 0: Confirm clean tree on the right branch

- [ ] Run: `git status`

Expected: clean working tree on `cross-filesystem-ops` (or a worktree branched from it). Memory: worktree before subagent-driven dev.

- [ ] Run: `dotnet build`

Expected: build succeeds — establishes the baseline.

- [ ] Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Vfs|FullyQualifiedName~McpFileSystemBackend"`

Expected: existing transfer tests pass — establishes the green baseline before any refactor.

---

## Task 1: Add chunk methods to backend, drive incremental-yield via a streaming-property test

Adds `ReadChunksAsync` / `WriteChunksAsync` to `IFileSystemBackend` *alongside* the old stream methods (deleted in Task 5). Drives the implementation from a unit test that counts `fs_blob_read` calls — the test fails when the producer eagerly buffers (today's behavior) and passes only when the iterator yields incrementally.

**Files:**
- Modify: `Domain/Contracts/IFileSystemBackend.cs`
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`
- Create: `Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs`

- [ ] **Step 1: Add the new method declarations to `IFileSystemBackend` (alongside the existing stream methods).**

Open `Domain/Contracts/IFileSystemBackend.cs`. After the existing `WriteFromStreamAsync` declaration, add:

```csharp
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct);

    Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct);
```

The full file is now:

```csharp
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Contracts;

public interface IFileSystemBackend
{
    string FilesystemName { get; }

    Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct);
    Task<JsonNode> InfoAsync(string path, CancellationToken ct);
    Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct);
    Task<JsonNode> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct);
    Task<JsonNode> GlobAsync(string basePath, string pattern, VfsGlobMode mode, CancellationToken ct);
    Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct);
    Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct);
    Task<JsonNode> DeleteAsync(string path, CancellationToken ct);
    Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct);

    Task<JsonNode> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct);

    Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct);

    Task<long> WriteFromStreamAsync(string path, Stream content,
        bool overwrite, bool createDirectories, CancellationToken ct);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct);

    Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct);
}
```

- [ ] **Step 2: Unseal `McpFileSystemBackend`, elevate `CallToolAsync`, and add stub implementations of the new methods.**

In `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`:

1. Change the class declaration from `internal sealed class` to `internal class`.
2. Add `using System.Runtime.CompilerServices;` to the file header (needed for `[EnumeratorCancellation]` in Step 6).
3. Change `private async Task<JsonNode> CallToolAsync(...)` to `protected internal virtual async Task<JsonNode> CallToolAsync(...)`.
4. Append two stub implementations that throw `NotImplementedException` so the test in Step 5 compiles and fails for the right reason.

Append the two stubs immediately above the `CallToolAsync` method:

```csharp
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct)
    {
        throw new NotImplementedException();
    }

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        throw new NotImplementedException();
    }
```

- [ ] **Step 3: Run `dotnet build` to confirm the contract compiles.**

Run: `dotnet build`
Expected: build succeeds. Any concrete implementer of `IFileSystemBackend` outside `McpFileSystemBackend` would now fail — there are none in production, but Moq-based mocks pick the new methods up automatically (Moq generates default returns).

- [ ] **Step 4: Create `Tests/Unit/Infrastructure/Mcp/` directory if missing, then write the failing streaming-property test.**

Run (only if needed): `mkdir -p Tests/Unit/Infrastructure/Mcp`

Create `Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpFileSystemBackendChunkTests
{
    [Fact]
    public async Task ReadChunksAsync_YieldsFirstChunkBeforeReadingRest()
    {
        // Simulates a ~10 MB file: 40 full 256 KiB chunks then EOF on the 41st call.
        var backend = new CountingBackend(totalChunks: 40);

        var enumerator = backend.ReadChunksAsync("any.bin", CancellationToken.None).GetAsyncEnumerator();
        try
        {
            (await enumerator.MoveNextAsync()).ShouldBeTrue();

            enumerator.Current.Length.ShouldBe(256 * 1024);
            backend.CallCount.ShouldBe(1);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    private sealed class CountingBackend(int totalChunks) : McpFileSystemBackend(null!, "test")
    {
        public int CallCount { get; private set; }

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            CallCount++;
            var bytes = new byte[256 * 1024];
            var eof = CallCount > totalChunks;
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["contentBase64"] = Convert.ToBase64String(bytes),
                ["eof"] = eof
            });
        }
    }
}
```

- [ ] **Step 5: Run the failing test to verify it fails for the right reason.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReadChunksAsync_YieldsFirstChunkBeforeReadingRest"`

Expected: FAIL with `System.NotImplementedException` thrown from `ReadChunksAsync`. This is the RED state — the contract exists, the test compiles, the implementation is absent.

- [ ] **Step 6: Replace the stub `ReadChunksAsync` with the real async iterator.**

In `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`, replace the stub `ReadChunksAsync` with the real implementation:

```csharp
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
        long offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var node = await CallToolAsync("fs_blob_read", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["offset"] = offset,
                ["length"] = chunkSize
            }, ct);

            if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
            {
                throw new IOException($"fs_blob_read failed: {obj["message"]?.GetValue<string>()}");
            }

            var bytes = Convert.FromBase64String(node["contentBase64"]!.GetValue<string>());
            if (bytes.Length > 0)
            {
                offset += bytes.Length;
                yield return bytes;
            }

            if (node["eof"]!.GetValue<bool>())
            {
                yield break;
            }

            if (bytes.Length == 0)
            {
                // Defensive: server reported !eof but sent nothing — break to avoid infinite loop.
                yield break;
            }
        }
    }
```

- [ ] **Step 7: Replace the stub `WriteChunksAsync` with the real implementation.**

```csharp
    public async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
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
                throw new IOException($"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
            }

            offset += chunk.Length;
        }

        if (offset == 0)
        {
            // Empty source: still create the file (matches pre-change semantics of WriteFromStreamAsync).
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
                throw new IOException($"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
            }
        }

        return offset;
    }
```

- [ ] **Step 8: Run the streaming-property test to verify it passes.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReadChunksAsync_YieldsFirstChunkBeforeReadingRest"`

Expected: PASS — `MoveNextAsync()` triggered exactly one `CallToolAsync` invocation; `Current` is 256 KiB.

- [ ] **Step 9: Run the full unit suite to confirm no regression.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`

Expected: all unit tests pass. The old stream methods are still in place, so nothing else has moved.

- [ ] **Step 10: Commit.**

```bash
git add Domain/Contracts/IFileSystemBackend.cs \
        Infrastructure/Agents/Mcp/McpFileSystemBackend.cs \
        Tests/Unit/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs
git commit -m "feat(fs): add chunked ReadChunksAsync/WriteChunksAsync with streaming-property test"
```

---

## Task 2: Migrate `VfsCopyTool.TransferFileAsync` cross-FS branch to the chunk pipe

The single-file cross-FS path swaps the `await using (stream)` block for a direct `WriteChunksAsync(ReadChunksAsync(...))` pipe. Drives the production change from updating the existing cross-FS unit test to assert against the new contract.

**Files:**
- Create: `Tests/Unit/Domain/Tools/FileSystem/AsyncEnumerableTestHelpers.cs`
- Modify: `Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs`
- Modify: `Domain/Tools/FileSystem/VfsCopyTool.cs`

- [ ] **Step 1: Create the chunk-mock helper used by every cross-FS unit test.**

Create `Tests/Unit/Domain/Tools/FileSystem/AsyncEnumerableTestHelpers.cs`:

```csharp
namespace Tests.Unit.Domain.Tools.FileSystem;

internal static class AsyncEnumerableTestHelpers
{
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ToAsyncEnumerable(params byte[][] chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
```

`Task.Yield()` keeps the iterator genuinely asynchronous so consumers don't observe synchronous completion as a special case.

- [ ] **Step 2: Rewrite the cross-FS file test in `VfsCopyToolTests.cs` to use the chunk contract.**

Replace the body of `RunAsync_CrossFsFile_StreamsThroughAgent` with the chunk-based version below. Leave `RunAsync_SameFsFile_DelegatesToBackendCopyAsync` and `RunAsync_SameFsFile_BackendOmitsBytes_ReturnsMinusOne` unchanged — they don't touch streams.

```csharp
    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsThroughAgent()
    {
        var src = new Mock<IFileSystemBackend>();
        src.SetupGet(b => b.FilesystemName).Returns("vault");
        src.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["isDirectory"] = false, ["bytes"] = 5 });
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(System.Text.Encoding.UTF8.GetBytes("hello")));

        var dst = new Mock<IFileSystemBackend>();
        dst.SetupGet(b => b.FilesystemName).Returns("sandbox");
        dst.Setup(b => b.WriteChunksAsync(
                "a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(src.Object, "a.md"));
        registry.Setup(r => r.Resolve("/sandbox/a.md"))
            .Returns(new FileSystemResolution(dst.Object, "a.md"));

        var tool = new VfsCopyTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["bytes"]!.GetValue<long>().ShouldBe(5L);
        dst.Verify(b => b.WriteChunksAsync(
            "a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
```

- [ ] **Step 3: Run the test to confirm it fails for the right reason.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RunAsync_CrossFsFile_StreamsThroughAgent"`

Expected: FAIL — production `TransferFileAsync` still calls `OpenReadStreamAsync`/`WriteFromStreamAsync`, so the `WriteChunksAsync` mock returns Moq's default `Task<long>` (which is `null` → NRE) and the verification at the end never matches. The exact failure may surface as `NullReferenceException` from awaiting a default `Task<long>`, or as a Moq verification failure — either is a valid RED.

- [ ] **Step 4: Replace the cross-FS branch in `TransferFileAsync` with the chunk pipe.**

In `Domain/Tools/FileSystem/VfsCopyTool.cs`, replace the `await using` block in `TransferFileAsync`:

```csharp
        long bytes;
        await using (var stream = await src.Backend.OpenReadStreamAsync(src.RelativePath, ct))
        {
            bytes = await dst.Backend.WriteFromStreamAsync(dst.RelativePath, stream, overwrite, createDirectories, ct);
        }
```

with:

```csharp
        var bytes = await dst.Backend.WriteChunksAsync(
            dst.RelativePath,
            src.Backend.ReadChunksAsync(src.RelativePath, ct),
            overwrite, createDirectories, ct);
```

- [ ] **Step 5: Run the targeted test to verify it passes.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RunAsync_CrossFsFile_StreamsThroughAgent"`

Expected: PASS.

- [ ] **Step 6: Run the full `VfsCopyToolTests` class to confirm same-FS and bytes-omitted tests still pass.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsCopyToolTests"`

Expected: all 3 tests in `VfsCopyToolTests` pass.

- [ ] **Step 7: Commit.**

```bash
git add Tests/Unit/Domain/Tools/FileSystem/AsyncEnumerableTestHelpers.cs \
        Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs \
        Domain/Tools/FileSystem/VfsCopyTool.cs
git commit -m "refactor(vfs): pipe TransferFileAsync cross-FS via ReadChunksAsync/WriteChunksAsync"
```

---

## Task 3: Migrate `VfsCopyTool.TransferDirectoryAsync` per-entry block to the chunk pipe

Same shape as Task 2, applied to the directory-recursion path. All four scenarios in `VfsTransferDirectoryTests` switch from `MemoryStream` mocks to chunk mocks.

**Files:**
- Modify: `Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs`
- Modify: `Domain/Tools/FileSystem/VfsCopyTool.cs`

- [ ] **Step 1: Rewrite all four test methods in `VfsTransferDirectoryTests.cs` to use chunk mocks.**

Replace the entire file with:

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTransferDirectoryTests
{
    [Fact]
    public async Task TransferDirectoryAsync_CrossFsCopy_RecordsPerEntryResults()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "src/sub/b.md" }
            });
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.ReadChunksAsync("src/sub/b.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("BB")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(2);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(0);
        result["entries"]!.AsArray().Count.ShouldBe(2);
        dst.Verify(b => b.WriteChunksAsync("dst/a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        dst.Verify(b => b.WriteChunksAsync("dst/sub/b.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TransferDirectoryAsync_PartialFailure_StatusIsPartial()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "src/b.md" }
            });
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));
        src.Setup(b => b.ReadChunksAsync("src/b.md", It.IsAny<CancellationToken>()))
            .Throws(new IOException("boom"));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("partial");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task TransferDirectoryAsync_GlobEntryNotUnderSourceDir_RecordsFailedAndDoesNotWrite()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "elsewhere/secret.md" }
            });
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("partial");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(1);

        var failedEntry = result["entries"]!.AsArray()
            .Single(e => e!["status"]!.GetValue<string>() == "failed")!;
        failedEntry["source"]!.GetValue<string>().ShouldBe("elsewhere/secret.md");
        failedEntry["error"]!.GetValue<string>().ShouldContain("not under source directory");
        failedEntry["destination"].ShouldBeNull();

        dst.Verify(b => b.WriteChunksAsync(
                It.Is<string>(p => p.Contains("secret")),
                It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TransferDirectoryAsync_MoveOnSuccessfulCopy_DeletesSource()
    {
        var src = new Mock<IFileSystemBackend>();
        src.Setup(b => b.GlobAsync("src", "**/*", VfsGlobMode.Files, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonArray { new JsonObject { ["path"] = "src/a.md" } });
        src.Setup(b => b.ReadChunksAsync("src/a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("A")));

        var dst = new Mock<IFileSystemBackend>();
        dst.Setup(b => b.WriteChunksAsync(
                It.IsAny<string>(), It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(1L);

        var srcRes = new FileSystemResolution(src.Object, "src");
        var dstRes = new FileSystemResolution(dst.Object, "dst");

        await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        src.Verify(b => b.DeleteAsync("src/a.md", It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run the four directory tests to confirm they fail.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTransferDirectoryTests"`

Expected: FAIL — production `TransferDirectoryAsync` still calls `OpenReadStreamAsync`/`WriteFromStreamAsync`. Failures will manifest as Moq verification failures or NREs from awaiting `default(Task<long>)`.

- [ ] **Step 3: Replace the per-entry stream block in `TransferDirectoryAsync` with the chunk pipe.**

In `Domain/Tools/FileSystem/VfsCopyTool.cs`, inside the `try { ... }` block of the `foreach (var entry in entries)` loop, replace:

```csharp
                long bytes;
                await using (var stream = await src.Backend.OpenReadStreamAsync(srcRel, ct))
                {
                    bytes = await dst.Backend.WriteFromStreamAsync(dstRel, stream, overwrite, createDirectories, ct);
                }
```

with:

```csharp
                var bytes = await dst.Backend.WriteChunksAsync(
                    dstRel,
                    src.Backend.ReadChunksAsync(srcRel, ct),
                    overwrite, createDirectories, ct);
```

- [ ] **Step 4: Run the four directory tests to verify they pass.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTransferDirectoryTests"`

Expected: all 4 tests pass.

- [ ] **Step 5: Commit.**

```bash
git add Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs \
        Domain/Tools/FileSystem/VfsCopyTool.cs
git commit -m "refactor(vfs): pipe TransferDirectoryAsync per-entry via ReadChunksAsync/WriteChunksAsync"
```

---

## Task 4: Update `VfsMoveToolCrossFsTests` mocks to the chunk contract

`VfsMoveTool` delegates to `VfsCopyTool.TransferFileAsync` / `TransferDirectoryAsync`, so production is already migrated. Only the test mocks for the cross-FS scenario need to switch from `OpenReadStreamAsync` / `WriteFromStreamAsync` to `ReadChunksAsync` / `WriteChunksAsync`. The same-FS test is unaffected (it uses `MoveAsync`).

**Files:**
- Modify: `Tests/Unit/Domain/Tools/FileSystem/VfsMoveToolCrossFsTests.cs`

- [ ] **Step 1: Run the cross-FS move test to confirm it currently fails after Tasks 2–3.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RunAsync_CrossFsFile_StreamsAndDeletesSource"`

Expected: FAIL — production calls `WriteChunksAsync` on the destination mock, which Moq returns as `default(Task<long>)`, so the verification fails or the test throws on awaiting `null`.

- [ ] **Step 2: Replace `RunAsync_CrossFsFile_StreamsAndDeletesSource` with the chunk-mocked version.**

Replace the test body with:

```csharp
    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsAndDeletesSource()
    {
        var src = new Mock<IFileSystemBackend>();
        src.SetupGet(b => b.FilesystemName).Returns("vault");
        src.Setup(b => b.InfoAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["isDirectory"] = false, ["bytes"] = 5 });
        src.Setup(b => b.ReadChunksAsync("a.md", It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerableTestHelpers.ToAsyncEnumerable(Encoding.UTF8.GetBytes("hello")));
        src.Setup(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonObject { ["status"] = "deleted" });

        var dst = new Mock<IFileSystemBackend>();
        dst.SetupGet(b => b.FilesystemName).Returns("sandbox");
        dst.Setup(b => b.WriteChunksAsync(
                "a.md", It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
                false, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5L);

        var registry = new Mock<IVirtualFileSystemRegistry>();
        registry.Setup(r => r.Resolve("/vault/a.md"))
            .Returns(new FileSystemResolution(src.Object, "a.md"));
        registry.Setup(r => r.Resolve("/sandbox/a.md"))
            .Returns(new FileSystemResolution(dst.Object, "a.md"));

        var tool = new VfsMoveTool(registry.Object);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        dst.Verify(b => b.WriteChunksAsync("a.md",
            It.IsAny<IAsyncEnumerable<ReadOnlyMemory<byte>>>(),
            false, true, It.IsAny<CancellationToken>()), Times.Once);
        src.Verify(b => b.DeleteAsync("a.md", It.IsAny<CancellationToken>()), Times.Once);
    }
```

- [ ] **Step 3: Update the same-FS test's negative-verification line to use the new method name.**

In the same file, in `RunAsync_SameFsFile_StillUsesNativeMoveAsync`, replace:

```csharp
        backend.Verify(b => b.OpenReadStreamAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
```

with:

```csharp
        backend.Verify(b => b.ReadChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
```

- [ ] **Step 4: Run both tests in `VfsMoveToolCrossFsTests` to verify they pass.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsMoveToolCrossFsTests"`

Expected: both tests pass.

- [ ] **Step 5: Commit.**

```bash
git add Tests/Unit/Domain/Tools/FileSystem/VfsMoveToolCrossFsTests.cs
git commit -m "test(vfs): update cross-FS move tests to chunk-based mocks"
```

---

## Task 5: Delete the old stream methods and rewrite the integration tests

With every production caller and unit test migrated, `OpenReadStreamAsync` / `WriteFromStreamAsync` are dead. Deleting them shrinks the contract from "either chunked or buffered, the consumer can't tell" to "always chunked." The integration test file is renamed and rewritten to exercise `ReadChunksAsync` / `WriteChunksAsync` directly, including a multi-megabyte roundtrip across the real MCP transport.

**Files:**
- Modify: `Domain/Contracts/IFileSystemBackend.cs`
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`
- Delete: `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs`
- Create: `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs`

- [ ] **Step 1: Confirm zero remaining callers of the old methods.**

Run: `grep -rn "OpenReadStreamAsync\|WriteFromStreamAsync" --include="*.cs" .`

Expected: only the four lines inside `Domain/Contracts/IFileSystemBackend.cs`, `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`, and the soon-to-be-deleted integration test. If any unit test still references them, return to the prior task and fix it before proceeding.

- [ ] **Step 2: Remove the old method declarations from `IFileSystemBackend`.**

In `Domain/Contracts/IFileSystemBackend.cs`, delete:

```csharp
    Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct);

    Task<long> WriteFromStreamAsync(string path, Stream content,
        bool overwrite, bool createDirectories, CancellationToken ct);
```

- [ ] **Step 3: Remove the old method implementations from `McpFileSystemBackend`.**

In `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`, delete the `OpenReadStreamAsync` method (lines 122–161 in the pre-change file) and the `WriteFromStreamAsync` method (lines 163–215 in the pre-change file). Both are now reachable only through code paths that no longer exist.

- [ ] **Step 4: Build to confirm the deletion is clean.**

Run: `dotnet build`

Expected: build succeeds. If any file still calls the old methods, the compiler reports it now — fix the callers, do not re-add the methods.

- [ ] **Step 5: Delete the old integration test file.**

Run: `git rm Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs`

- [ ] **Step 6: Create the new integration test file.**

Create `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs`:

```csharp
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure.Mcp;

[Collection("MultiFileSystem")]
public class McpFileSystemBackendChunkTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task ReadChunksAsync_LargeFile_ReadsAllBytesCorrectly()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "big.bin"), bytes);

        await using var client = await CreateClient(fx.LibraryEndpoint);
        var backend = new McpFileSystemBackend(client, "library");

        using var ms = new MemoryStream();
        await foreach (var chunk in backend.ReadChunksAsync("big.bin", CancellationToken.None))
        {
            ms.Write(chunk.Span);
        }

        ms.ToArray().ShouldBe(bytes);
    }

    [Fact]
    public async Task WriteChunksAsync_LargeFile_WritesAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        await using var client = await CreateClient(fx.NotesEndpoint);
        var backend = new McpFileSystemBackend(client, "notes");

        var written = await backend.WriteChunksAsync("written.bin", SingleChunk(bytes),
            overwrite: false, createDirectories: true, CancellationToken.None);

        written.ShouldBe(bytes.Length);
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "written.bin")).ShouldBe(bytes);
    }

    [Fact]
    public async Task WriteChunksAsync_EmptyEnumerable_CreatesEmptyFile()
    {
        await using var client = await CreateClient(fx.NotesEndpoint);
        var backend = new McpFileSystemBackend(client, "notes");

        var written = await backend.WriteChunksAsync("empty.bin", Empty(),
            overwrite: false, createDirectories: true, CancellationToken.None);

        written.ShouldBe(0L);
        var path = Path.Combine(fx.NotesPath, "empty.bin");
        File.Exists(path).ShouldBeTrue();
        File.ReadAllBytes(path).Length.ShouldBe(0);
    }

    [Fact]
    public async Task ReadChunksAsync_MultiMegabyteFile_RoundTripsByteForByte()
    {
        // ~8 MB — exercises ~32 fs_blob_read calls against the real transport.
        var bytes = Enumerable.Range(0, 8 * 1024 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "huge.bin"), bytes);

        await using var readClient = await CreateClient(fx.LibraryEndpoint);
        await using var writeClient = await CreateClient(fx.NotesEndpoint);
        var src = new McpFileSystemBackend(readClient, "library");
        var dst = new McpFileSystemBackend(writeClient, "notes");

        var written = await dst.WriteChunksAsync("huge.bin",
            src.ReadChunksAsync("huge.bin", CancellationToken.None),
            overwrite: false, createDirectories: true, CancellationToken.None);

        written.ShouldBe(bytes.Length);
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "huge.bin")).ShouldBe(bytes);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> SingleChunk(byte[] bytes)
    {
        await Task.Yield();
        yield return bytes;
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Empty()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<McpClient> CreateClient(string endpoint)
    {
        return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
    }
}
```

- [ ] **Step 7: Run the integration suite scoped to chunk tests.**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpFileSystemBackendChunkTests"`

Expected: 4 unit tests (the streaming-property test from Task 1) and 4 integration tests pass — 8 in total. The integration tests require the in-process MCP fixture set up by `MultiFileSystemFixture`; no external Docker stack is needed.

- [ ] **Step 8: Run the full test suite to confirm no regressions anywhere else.**

Run: `dotnet test`

Expected: all tests pass. If any unrelated test references the old stream API through transitive code, the build would have failed at Step 4 — but run the suite to be sure no behavioral regression slipped through.

- [ ] **Step 9: Commit.**

```bash
git add Domain/Contracts/IFileSystemBackend.cs \
        Infrastructure/Agents/Mcp/McpFileSystemBackend.cs \
        Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendChunkTests.cs
git rm --cached Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs 2>/dev/null || true
git commit -m "refactor(fs): remove buffered stream API; chunked transfer is the only contract"
```

(`git rm` in Step 5 already staged the deletion, so the second `git rm --cached` is a no-op safety net — keep it in case the deletion was untracked.)

---

## Final verification checklist

- [ ] `dotnet build` succeeds with zero warnings introduced by this change.
- [ ] `dotnet test` passes the full suite.
- [ ] `grep -rn "OpenReadStreamAsync\|WriteFromStreamAsync" --include="*.cs" .` returns no matches.
- [ ] `Domain/Contracts/IFileSystemBackend.cs` exposes only `ReadChunksAsync` / `WriteChunksAsync` for blob transfer.
- [ ] `McpFileSystemBackend` is `internal class` (not `sealed`) and `CallToolAsync` is `protected internal virtual`.
- [ ] Cross-FS branches in `VfsCopyTool.TransferFileAsync` and `TransferDirectoryAsync` are single-line `WriteChunksAsync(ReadChunksAsync(...))` pipes — no `await using`, no `Stream` references.
- [ ] The streaming-property test asserts call count = 1 after one `MoveNextAsync()`.

## Optional manual smoke (not required for merge)

Inside the running stack, copy a moderately large file (1 GB+) sandbox → vault and watch the agent's RSS via `docker stats`. With chunked transfer, RSS stays flat (one ~256 KiB chunk plus its base64 form ≈ 340 KiB live). Without it, RSS grows linearly with file size until OOM. Useful one-off sanity check; not part of the test suite.
