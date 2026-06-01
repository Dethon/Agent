# Print Queue Filesystem Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a new MCP filesystem server, `McpServerPrinter`, that exposes `filesystem://print-queue` (mount `/print-queue`); copying/creating a file into it prints it on a single configured IPP/CUPS printer, and removing a not-yet-finished file cancels it.

**Architecture:** A domain `PrinterQueueFileSystem : IFileSystemBackend` holds the VFS logic. It depends on two contracts: `IPrinterClient` (IPP submit/list/cancel, implemented with SharpIppNext in Infrastructure) and `IPrintSpool` (disk-backed byte store + metadata index). Because cross-backend copies stream via discrete `fs_blob_write` calls with **no EOF signal**, ingested bytes accumulate in the spool and a debounced `PrintQueueCoordinator` submits a document once its writes go quiet, then prunes finished jobs so the queue auto-shows only active jobs. The server is a tool/filesystem server only (not a channel).

**Tech Stack:** .NET 10, C# 14, `ModelContextProtocol.AspNetCore` 1.3.0, SharpIppNext (IPP over HTTP), xUnit + Shouldly + hand-written fakes, Docker Compose.

---

## Background & Conventions (read before starting)

- **Error model.** Backend methods return `FsResult<T>` (`Ok`/`Err`). Build errors with the private `Error(code, message)` helper and `ToolError.Codes.*` constants. Throw exceptions only for genuine infrastructure failures — the MCP `AddCallToolFilter` wraps them.
- **No trailing newline** in any `.cs` file (including tests). The pre-commit hook dotnet-formats and re-stages whole files.
- **LINQ over loops** except where awaiting with side effects (allowed).
- **Tests** use xUnit `[Fact]`, Shouldly assertions, and real hand-written fakes (no NSubstitute). Journey-style multi-assertion tests are the house style (see `Tests/Unit/Domain/Scheduling/Vfs/ScheduleFileSystemJourneyTests.cs`).
- **`move` and `exec` are intentionally unsupported.** We achieve this by **not registering** `fs_move`/`fs_exec` tools on the server — `McpFileSystemBackend.CallToolAsync` already returns a clean `unsupported_operation` envelope for unregistered tools. The backend still implements `MoveAsync`/`ExecAsync` (interface requirement) returning `Unsupported`.
- **Filename is the logical key.** A document is `/print-queue/<filename>`. Submitting a name that already maps to a live job requires `overwrite=true`.

### Component / file map

Domain:
- `Domain/DTOs/Printing/PrintJobState.cs` — enum
- `Domain/DTOs/Printing/PrintJobHandle.cs` — `record(int JobId)`
- `Domain/DTOs/Printing/PrintJobStatus.cs` — `record(int JobId, string JobName, PrintJobState State)`
- `Domain/DTOs/Printing/SpoolEntry.cs` — spool metadata record
- `Domain/Contracts/IPrinterClient.cs`
- `Domain/Contracts/IPrintSpool.cs`
- `Domain/Tools/Printing/Vfs/PrinterQueuePath.cs` — path parser + node-kind enum
- `Domain/Tools/Printing/PrintQueueCoordinator.cs` — debounced submit + reconcile
- `Domain/Tools/Printing/Vfs/PrinterQueueFileSystem.cs` — the `IFileSystemBackend`
- `Domain/Prompts/PrintingPrompt.cs`

Infrastructure:
- `Infrastructure/Printing/PrintSpool.cs` — disk impl of `IPrintSpool`
- `Infrastructure/Clients/Printer/IppJobStateMapper.cs` — pure IPP→domain state map
- `Infrastructure/Clients/Printer/IppPrinterClient.cs` — `IPrinterClient` via SharpIppNext

McpServerPrinter (new project):
- `McpServerPrinter.csproj`, `Program.cs`, `Dockerfile`, `appsettings.json`
- `Settings/PrinterSettings.cs`
- `Modules/ConfigModule.cs`
- `McpResources/FileSystemResource.cs`
- `McpPrompts/McpSystemPrompt.cs`
- `Services/PrintSubmissionWorker.cs` — `BackgroundService` driving the coordinator
- `McpTools/Fs{Read,Info,Glob,Search,Create,Edit,Delete,Copy,BlobRead,BlobWrite}Tool.cs`

Infra wiring:
- `DockerCompose/docker-compose.yml` — `mcp-printer` service + spool volume
- `Agent/appsettings.json` — add endpoint to the `jonas` agent

Tests:
- `Tests/Unit/Domain/Printing/Vfs/PrinterQueuePathTests.cs`
- `Tests/Unit/Infrastructure/Printing/PrintSpoolTests.cs`
- `Tests/Unit/Infrastructure/Printing/IppJobStateMapperTests.cs`
- `Tests/Unit/Domain/Printing/PrintQueueCoordinatorTests.cs`
- `Tests/Unit/Domain/Printing/Vfs/PrinterQueueFileSystemTests.cs`
- `Tests/Unit/Domain/Printing/FakePrinterClient.cs` (shared test double)

---

## Task 1: Domain DTOs and contracts

**Files:**
- Create: `Domain/DTOs/Printing/PrintJobState.cs`
- Create: `Domain/DTOs/Printing/PrintJobHandle.cs`
- Create: `Domain/DTOs/Printing/PrintJobStatus.cs`
- Create: `Domain/DTOs/Printing/SpoolEntry.cs`
- Create: `Domain/Contracts/IPrinterClient.cs`
- Create: `Domain/Contracts/IPrintSpool.cs`

These are type declarations with no behavior, so there is no separate failing test; they are exercised by every later task and the solution must compile.

- [ ] **Step 1: Create the printer DTOs**

`Domain/DTOs/Printing/PrintJobState.cs`:

```csharp
namespace Domain.DTOs.Printing;

public enum PrintJobState
{
    Queued,
    Pending,
    Processing,
    Completed,
    Canceled,
    Aborted,
    Unknown
}
```

`Domain/DTOs/Printing/PrintJobHandle.cs`:

```csharp
namespace Domain.DTOs.Printing;

public sealed record PrintJobHandle(int JobId);
```

`Domain/DTOs/Printing/PrintJobStatus.cs`:

```csharp
namespace Domain.DTOs.Printing;

public sealed record PrintJobStatus(int JobId, string JobName, PrintJobState State);
```

`Domain/DTOs/Printing/SpoolEntry.cs`:

```csharp
namespace Domain.DTOs.Printing;

// One queued document. JobId is null until the coordinator submits it to the printer.
public sealed record SpoolEntry
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTimeOffset LastWriteAt { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
    public int? JobId { get; init; }

    public bool IsSubmitted => JobId is not null;
}
```

- [ ] **Step 2: Create `IPrinterClient`**

`Domain/Contracts/IPrinterClient.cs`:

```csharp
using Domain.DTOs.Printing;

namespace Domain.Contracts;

public interface IPrinterClient
{
    Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct);

    Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct);

    Task CancelAsync(int jobId, CancellationToken ct);
}
```

- [ ] **Step 3: Create `IPrintSpool`**

`Domain/Contracts/IPrintSpool.cs`:

```csharp
using Domain.DTOs.Printing;

namespace Domain.Contracts;

// Disk-backed buffer for queued documents. Bytes accumulate via offset-based writes
// (the fs_blob_write protocol has no EOF signal); metadata is tracked per filename.
public interface IPrintSpool
{
    Task WriteBytesAsync(string fileName, string contentType, ReadOnlyMemory<byte> bytes,
        long offset, bool overwrite, CancellationToken ct);

    Task<(byte[] Bytes, bool Eof, long TotalBytes)> ReadBytesAsync(string fileName, long offset, int length, CancellationToken ct);

    Task<byte[]?> ReadAllBytesAsync(string fileName, CancellationToken ct);

    Task<SpoolEntry?> GetAsync(string fileName, CancellationToken ct);

    Task<IReadOnlyList<SpoolEntry>> ListAsync(CancellationToken ct);

    Task MarkSubmittedAsync(string fileName, int jobId, DateTimeOffset submittedAt, CancellationToken ct);

    Task RemoveAsync(string fileName, CancellationToken ct);
}
```

- [ ] **Step 4: Build the solution**

Run: `dotnet build Domain/Domain.csproj`
Expected: PASS (compiles; no warnings about the new files).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Printing Domain/Contracts/IPrinterClient.cs Domain/Contracts/IPrintSpool.cs
git commit -m "feat(printer): add print-queue DTOs and contracts"
```

---

## Task 2: Path parser (`PrinterQueuePath`)

**Files:**
- Create: `Domain/Tools/Printing/Vfs/PrinterQueuePath.cs`
- Test: `Tests/Unit/Domain/Printing/Vfs/PrinterQueuePathTests.cs`

The mount is flat: the root, one file per document, and a reserved `status.json`. Paths arrive **relative to the mount** (the registry strips the `/print-queue` prefix), e.g. `report.pdf`, `status.json`, or empty for root.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Domain/Printing/Vfs/PrinterQueuePathTests.cs`:

```csharp
using Domain.Tools.Printing.Vfs;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing.Vfs;

public class PrinterQueuePathTests
{
    [Theory]
    [InlineData("", PrinterNodeKind.Root, null)]
    [InlineData("/", PrinterNodeKind.Root, null)]
    [InlineData("status.json", PrinterNodeKind.StatusFile, null)]
    [InlineData("/status.json", PrinterNodeKind.StatusFile, null)]
    [InlineData("report.pdf", PrinterNodeKind.DocumentFile, "report.pdf")]
    [InlineData("/report.pdf", PrinterNodeKind.DocumentFile, "report.pdf")]
    public void Parse_ClassifiesNodes(string path, PrinterNodeKind kind, string? fileName)
    {
        var node = PrinterQueuePath.Parse(path);
        node.Kind.ShouldBe(kind);
        node.FileName.ShouldBe(fileName);
    }

    [Theory]
    [InlineData("sub/report.pdf")]
    [InlineData("a/b")]
    [InlineData("../escape")]
    [InlineData("./report.pdf")]
    public void Parse_RejectsNestedOrTraversalPaths(string path)
    {
        PrinterQueuePath.Parse(path).Kind.ShouldBe(PrinterNodeKind.Unknown);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrinterQueuePathTests"`
Expected: FAIL — `PrinterQueuePath` / `PrinterNodeKind` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

`Domain/Tools/Printing/Vfs/PrinterQueuePath.cs`:

```csharp
namespace Domain.Tools.Printing.Vfs;

public enum PrinterNodeKind
{
    Root,
    DocumentFile,
    StatusFile,
    Unknown
}

public sealed record PrinterQueueNode(PrinterNodeKind Kind, string? FileName);

public static class PrinterQueuePath
{
    public const string StatusFileName = "status.json";

    public static PrinterQueueNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Array.Exists(segments, s => s is "." or ".."))
        {
            return new PrinterQueueNode(PrinterNodeKind.Unknown, null);
        }

        return segments switch
        {
            [] => new PrinterQueueNode(PrinterNodeKind.Root, null),
            [StatusFileName] => new PrinterQueueNode(PrinterNodeKind.StatusFile, null),
            [var file] => new PrinterQueueNode(PrinterNodeKind.DocumentFile, file),
            _ => new PrinterQueueNode(PrinterNodeKind.Unknown, null)
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrinterQueuePathTests"`
Expected: PASS (all theory cases green).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Printing/Vfs/PrinterQueuePath.cs Tests/Unit/Domain/Printing/Vfs/PrinterQueuePathTests.cs
git commit -m "feat(printer): add print-queue path parser"
```

---

## Task 3: Disk-backed spool (`PrintSpool`)

**Files:**
- Create: `Infrastructure/Printing/PrintSpool.cs`
- Test: `Tests/Unit/Infrastructure/Printing/PrintSpoolTests.cs`

Stores each document as two files under the spool root: `<escaped-name>.doc` (bytes) and `<escaped-name>.meta.json` (a serialized `SpoolEntry`). Offset-0 writes (re)create; later offsets append at position. Uses `TimeProvider` for `LastWriteAt`.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Infrastructure/Printing/PrintSpoolTests.cs`:

```csharp
using System.Text;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure.Printing;

public class PrintSpoolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printspool-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    private PrintSpool Build() => new(_root, _clock);

    [Fact]
    public async Task Write_Then_Get_And_ReadAll_RoundTrips()
    {
        var spool = Build();
        var bytes = Encoding.UTF8.GetBytes("hello world");

        await spool.WriteBytesAsync("a.txt", "text/plain", bytes, 0, true, CancellationToken.None);

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry.ShouldNotBeNull();
        entry!.FileName.ShouldBe("a.txt");
        entry.ContentType.ShouldBe("text/plain");
        entry.SizeBytes.ShouldBe(bytes.Length);
        entry.IsSubmitted.ShouldBeFalse();

        (await spool.ReadAllBytesAsync("a.txt", CancellationToken.None)).ShouldBe(bytes);
    }

    [Fact]
    public async Task Write_AppendsAtOffset()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.bin", "application/octet-stream", new byte[] { 1, 2, 3 }, 0, true, CancellationToken.None);
        await spool.WriteBytesAsync("a.bin", "application/octet-stream", new byte[] { 4, 5 }, 3, false, CancellationToken.None);

        (await spool.ReadAllBytesAsync("a.bin", CancellationToken.None)).ShouldBe(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task ReadBytes_ReportsEofAndTotal()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.bin", "application/octet-stream", new byte[] { 1, 2, 3, 4 }, 0, true, CancellationToken.None);

        var first = await spool.ReadBytesAsync("a.bin", 0, 3, CancellationToken.None);
        first.Bytes.ShouldBe(new byte[] { 1, 2, 3 });
        first.Eof.ShouldBeFalse();
        first.TotalBytes.ShouldBe(4);

        var second = await spool.ReadBytesAsync("a.bin", 3, 3, CancellationToken.None);
        second.Bytes.ShouldBe(new byte[] { 4 });
        second.Eof.ShouldBeTrue();
    }

    [Fact]
    public async Task MarkSubmitted_SetsJobIdAndTimestamp()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", new byte[] { 1 }, 0, true, CancellationToken.None);

        await spool.MarkSubmittedAsync("a.txt", 42, _clock.GetUtcNow(), CancellationToken.None);

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry!.JobId.ShouldBe(42);
        entry.IsSubmitted.ShouldBeTrue();
        entry.SubmittedAt.ShouldBe(_clock.GetUtcNow());
    }

    [Fact]
    public async Task List_ReturnsAllEntries_AndRemove_DeletesBytesAndMeta()
    {
        var spool = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", new byte[] { 1 }, 0, true, CancellationToken.None);
        await spool.WriteBytesAsync("b.txt", "text/plain", new byte[] { 2 }, 0, true, CancellationToken.None);

        (await spool.ListAsync(CancellationToken.None)).Select(e => e.FileName)
            .OrderBy(n => n).ShouldBe(new[] { "a.txt", "b.txt" });

        await spool.RemoveAsync("a.txt", CancellationToken.None);

        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldBeNull();
        (await spool.ListAsync(CancellationToken.None)).Select(e => e.FileName).ShouldBe(new[] { "b.txt" });
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrintSpoolTests"`
Expected: FAIL — `PrintSpool` does not exist.

> Note: `FakeTimeProvider` is in `Microsoft.Extensions.TimeProvider.Testing`. If the test project lacks it, add it: `dotnet add Tests/Tests.csproj package Microsoft.Extensions.TimeProvider.Testing`, then re-run.

- [ ] **Step 3: Write minimal implementation**

`Infrastructure/Printing/PrintSpool.cs`:

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Printing;

namespace Infrastructure.Printing;

public sealed class PrintSpool(string rootPath, TimeProvider clock) : IPrintSpool
{
    private const string DocSuffix = ".doc";
    private const string MetaSuffix = ".meta.json";

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteBytesAsync(string fileName, string contentType, ReadOnlyMemory<byte> bytes,
        long offset, bool overwrite, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(rootPath);
            var docPath = DocPath(fileName);

            if (offset == 0)
            {
                await File.WriteAllBytesAsync(docPath, bytes.ToArray(), ct);
            }
            else
            {
                await using var stream = new FileStream(docPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
                stream.Seek(offset, SeekOrigin.Begin);
                await stream.WriteAsync(bytes, ct);
            }

            var size = new FileInfo(docPath).Length;
            var existing = await ReadMetaAsync(fileName, ct);
            var entry = new SpoolEntry
            {
                FileName = fileName,
                ContentType = offset == 0 ? contentType : existing?.ContentType ?? contentType,
                SizeBytes = size,
                LastWriteAt = clock.GetUtcNow(),
                // A fresh offset-0 write restarts the lifecycle; a re-write clears any prior submission.
                SubmittedAt = offset == 0 ? null : existing?.SubmittedAt,
                JobId = offset == 0 ? null : existing?.JobId
            };
            await WriteMetaAsync(entry, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(byte[] Bytes, bool Eof, long TotalBytes)> ReadBytesAsync(string fileName, long offset, int length, CancellationToken ct)
    {
        var docPath = DocPath(fileName);
        if (!File.Exists(docPath))
        {
            return (Array.Empty<byte>(), true, 0);
        }

        var total = new FileInfo(docPath).Length;
        var available = Math.Max(0, total - offset);
        var toRead = (int)Math.Min(length, available);
        var buffer = new byte[toRead];
        if (toRead > 0)
        {
            await using var stream = File.OpenRead(docPath);
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < toRead)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, toRead - read), ct);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }
        }

        return (buffer, offset + toRead >= total, total);
    }

    public async Task<byte[]?> ReadAllBytesAsync(string fileName, CancellationToken ct)
    {
        var docPath = DocPath(fileName);
        return File.Exists(docPath) ? await File.ReadAllBytesAsync(docPath, ct) : null;
    }

    public Task<SpoolEntry?> GetAsync(string fileName, CancellationToken ct) => ReadMetaAsync(fileName, ct);

    public async Task<IReadOnlyList<SpoolEntry>> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        var names = Directory.EnumerateFiles(rootPath, "*" + MetaSuffix)
            .Select(p => Path.GetFileName(p)[..^MetaSuffix.Length]);

        var entries = await Task.WhenAll(names.Select(async escaped =>
            await ReadMetaByEscapedAsync(escaped, ct)));

        return entries.Where(e => e is not null).Select(e => e!).ToList();
    }

    public async Task MarkSubmittedAsync(string fileName, int jobId, DateTimeOffset submittedAt, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var existing = await ReadMetaAsync(fileName, ct);
            if (existing is null)
            {
                return;
            }

            await WriteMetaAsync(existing with { JobId = jobId, SubmittedAt = submittedAt }, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public Task RemoveAsync(string fileName, CancellationToken ct)
    {
        File.Delete(DocPath(fileName));
        File.Delete(MetaPath(fileName));
        return Task.CompletedTask;
    }

    private string DocPath(string fileName) => Path.Combine(rootPath, Uri.EscapeDataString(fileName) + DocSuffix);
    private string MetaPath(string fileName) => Path.Combine(rootPath, Uri.EscapeDataString(fileName) + MetaSuffix);

    private async Task<SpoolEntry?> ReadMetaAsync(string fileName, CancellationToken ct)
    {
        var metaPath = MetaPath(fileName);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        await using var stream = File.OpenRead(metaPath);
        return await JsonSerializer.DeserializeAsync<SpoolEntry>(stream, _json, ct);
    }

    private async Task<SpoolEntry?> ReadMetaByEscapedAsync(string escapedName, CancellationToken ct)
        => await ReadMetaAsync(Uri.UnescapeDataString(escapedName), ct);

    private async Task WriteMetaAsync(SpoolEntry entry, CancellationToken ct)
    {
        await using var stream = File.Create(MetaPath(entry.FileName));
        await JsonSerializer.SerializeAsync(stream, entry, _json, ct);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrintSpoolTests"`
Expected: PASS (5 facts green).

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Printing/PrintSpool.cs Tests/Unit/Infrastructure/Printing/PrintSpoolTests.cs
git commit -m "feat(printer): add disk-backed print spool"
```

---

## Task 4: Shared test double (`FakePrinterClient`)

**Files:**
- Create: `Tests/Unit/Domain/Printing/FakePrinterClient.cs`

A real in-memory fake (house style) used by Tasks 5 and 6. `SubmitAsync` assigns an incrementing job id and records the job as active; `CompleteJob`/`CancelAsync` move it out of the active set.

- [ ] **Step 1: Create the fake (no separate test — it is test infrastructure exercised by later tasks)**

`Tests/Unit/Domain/Printing/FakePrinterClient.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Printing;

namespace Tests.Unit.Domain.Printing;

public sealed class FakePrinterClient : IPrinterClient
{
    private readonly Dictionary<int, PrintJobStatus> _active = new();
    private int _nextId;

    public List<(string JobName, string ContentType, byte[] Document)> Submissions { get; } = new();
    public List<int> Canceled { get; } = new();

    public Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct)
    {
        var id = ++_nextId;
        Submissions.Add((jobName, contentType, document.ToArray()));
        _active[id] = new PrintJobStatus(id, jobName, PrintJobState.Pending);
        return Task.FromResult(new PrintJobHandle(id));
    }

    public Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<PrintJobStatus>>(_active.Values.ToList());

    public Task CancelAsync(int jobId, CancellationToken ct)
    {
        Canceled.Add(jobId);
        _active.Remove(jobId);
        return Task.CompletedTask;
    }

    // Test helpers:
    public void CompleteJob(int jobId) => _active.Remove(jobId);

    public void SetState(int jobId, PrintJobState state)
    {
        if (_active.TryGetValue(jobId, out var job))
        {
            _active[jobId] = job with { State = state };
        }
    }
}
```

- [ ] **Step 2: Build the test project**

Run: `dotnet build Tests/Tests.csproj`
Expected: PASS (compiles).

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Domain/Printing/FakePrinterClient.cs
git commit -m "test(printer): add in-memory fake printer client"
```

---

## Task 5: Coordinator (`PrintQueueCoordinator`) — debounced submit + reconcile

**Files:**
- Create: `Domain/Tools/Printing/PrintQueueCoordinator.cs`
- Test: `Tests/Unit/Domain/Printing/PrintQueueCoordinatorTests.cs`

`SubmitDueAsync` submits unsubmitted spool entries whose last write is older than the debounce window. `ReconcileAsync` prunes submitted entries whose job is no longer active (auto-disappear). `TickAsync` runs both. Driven periodically by the worker (Task 9) and lazily by the backend reads (Task 6).

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Domain/Printing/PrintQueueCoordinatorTests.cs`:

```csharp
using System.Text;
using Domain.Tools.Printing;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing;

public class PrintQueueCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printcoord-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePrinterClient _printer = new();

    private (PrintSpool Spool, PrintQueueCoordinator Coordinator) Build()
    {
        var spool = new PrintSpool(_root, _clock);
        return (spool, new PrintQueueCoordinator(spool, _printer, _clock, TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public async Task SubmitDue_WaitsForDebounce_ThenSubmits()
    {
        var (spool, coordinator) = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", Encoding.UTF8.GetBytes("hi"), 0, true, CancellationToken.None);

        // Too soon — still inside the debounce window.
        await coordinator.SubmitDueAsync(CancellationToken.None);
        _printer.Submissions.ShouldBeEmpty();
        (await spool.GetAsync("a.txt", CancellationToken.None))!.IsSubmitted.ShouldBeFalse();

        // After the window the document is submitted exactly once.
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.SubmitDueAsync(CancellationToken.None);
        _printer.Submissions.Count.ShouldBe(1);
        _printer.Submissions[0].JobName.ShouldBe("a.txt");

        var entry = await spool.GetAsync("a.txt", CancellationToken.None);
        entry!.IsSubmitted.ShouldBeTrue();

        // Idempotent — a second pass does not resubmit.
        await coordinator.SubmitDueAsync(CancellationToken.None);
        _printer.Submissions.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Reconcile_PrunesFinishedJobs_KeepsActiveOnes()
    {
        var (spool, coordinator) = Build();
        await spool.WriteBytesAsync("a.txt", "text/plain", Encoding.UTF8.GetBytes("hi"), 0, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await coordinator.SubmitDueAsync(CancellationToken.None);

        var jobId = (await spool.GetAsync("a.txt", CancellationToken.None))!.JobId!.Value;

        // Still printing → kept.
        await coordinator.ReconcileAsync(CancellationToken.None);
        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldNotBeNull();

        // Printer finished it → pruned (auto-disappear).
        _printer.CompleteJob(jobId);
        await coordinator.ReconcileAsync(CancellationToken.None);
        (await spool.GetAsync("a.txt", CancellationToken.None)).ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrintQueueCoordinatorTests"`
Expected: FAIL — `PrintQueueCoordinator` does not exist.

- [ ] **Step 3: Write minimal implementation**

`Domain/Tools/Printing/PrintQueueCoordinator.cs`:

```csharp
using Domain.Contracts;

namespace Domain.Tools.Printing;

// Submits documents once their writes go quiet (the fs_blob_write protocol gives no EOF),
// and prunes finished jobs so the queue only shows active work.
public sealed class PrintQueueCoordinator(
    IPrintSpool spool,
    IPrinterClient printer,
    TimeProvider clock,
    TimeSpan submitDebounce)
{
    public async Task TickAsync(CancellationToken ct)
    {
        await SubmitDueAsync(ct);
        await ReconcileAsync(ct);
    }

    public async Task SubmitDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var entries = await spool.ListAsync(ct);
        var due = entries.Where(e => !e.IsSubmitted && now - e.LastWriteAt >= submitDebounce);

        foreach (var entry in due)
        {
            var bytes = await spool.ReadAllBytesAsync(entry.FileName, ct);
            if (bytes is null)
            {
                continue;
            }

            var handle = await printer.SubmitAsync(entry.FileName, entry.ContentType, bytes, ct);
            await spool.MarkSubmittedAsync(entry.FileName, handle.JobId, clock.GetUtcNow(), ct);
        }
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        var entries = await spool.ListAsync(ct);
        var submitted = entries.Where(e => e.IsSubmitted).ToList();
        if (submitted.Count == 0)
        {
            return;
        }

        var activeIds = (await printer.GetActiveJobsAsync(ct)).Select(j => j.JobId).ToHashSet();
        var finished = submitted.Where(e => !activeIds.Contains(e.JobId!.Value));

        foreach (var entry in finished)
        {
            await spool.RemoveAsync(entry.FileName, ct);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrintQueueCoordinatorTests"`
Expected: PASS (2 facts green).

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Printing/PrintQueueCoordinator.cs Tests/Unit/Domain/Printing/PrintQueueCoordinatorTests.cs
git commit -m "feat(printer): add debounced submit + reconcile coordinator"
```

---

## Task 6: The backend (`PrinterQueueFileSystem`)

**Files:**
- Create: `Domain/Tools/Printing/Vfs/PrinterQueueFileSystem.cs`
- Test: `Tests/Unit/Domain/Printing/Vfs/PrinterQueueFileSystemTests.cs`

This is the core. It implements `IFileSystemBackend` over `IPrintSpool` + `IPrinterClient` + `PrintQueueCoordinator`. Read/list/info operations reconcile first so finished jobs disappear. Binary detection: a byte is "binary" if it contains a NUL or is not valid UTF-8.

The plan splits this into three TDD cycles (Steps grouped) so each commit is independently green: **(A)** contract + unsupported ops + status.json read-only, **(B)** create/delete/read/glob/info lifecycle, **(C)** edit/copy/search/blob round-trip.

### Cycle A — contract, unsupported ops, read-only status

- [ ] **Step 1: Write the failing test (Cycle A)**

`Tests/Unit/Domain/Printing/Vfs/PrinterQueueFileSystemTests.cs`:

```csharp
using System.Text;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Printing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Tests.Unit.Domain.Printing.Vfs;

public class PrinterQueueFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "printfs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakePrinterClient _printer = new();
    private PrintSpool _spool = null!;
    private PrintQueueCoordinator _coordinator = null!;

    private PrinterQueueFileSystem Build()
    {
        _spool = new PrintSpool(_root, _clock);
        _coordinator = new PrintQueueCoordinator(_spool, _printer, _clock, TimeSpan.FromMilliseconds(500));
        return new PrinterQueueFileSystem(_spool, _printer, _coordinator);
    }

    [Fact]
    public async Task Backend_Contract_ExposesNameAndUnsupportedOps()
    {
        var fs = Build();

        fs.ShouldBeAssignableTo<IFileSystemBackend>();
        fs.FilesystemName.ShouldBe("print-queue");

        var move = await fs.MoveAsync("a.pdf", "b.pdf", CancellationToken.None);
        move.ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var exec = await fs.ExecAsync("a.pdf", "anything", null, CancellationToken.None);
        exec.ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task StatusJson_IsReadOnly()
    {
        var fs = Build();

        var create = await fs.CreateAsync("status.json", "{}", true, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var delete = await fs.DeleteAsync("status.json", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var edit = await fs.EditAsync("status.json", new[] { new TextEdit("a", "b") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails (Cycle A)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrinterQueueFileSystemTests"`
Expected: FAIL — `PrinterQueueFileSystem` does not exist.

- [ ] **Step 3: Write the full backend implementation (Cycle A + B + C in one file)**

`Domain/Tools/Printing/Vfs/PrinterQueueFileSystem.cs`:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Printing;
using Domain.Tools;

namespace Domain.Tools.Printing.Vfs;

public sealed class PrinterQueueFileSystem(
    IPrintSpool spool,
    IPrinterClient printer,
    PrintQueueCoordinator coordinator) : IFileSystemBackend
{
    public string FilesystemName => "print-queue";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var node = PrinterQueuePath.Parse(path);

        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            var status = await RenderStatusAsync(ct);
            return Ok(path, status);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsReadResult>(path);
        }

        var bytes = await spool.ReadAllBytesAsync(node.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsReadResult>(path);
        }

        if (!IsText(bytes))
        {
            return new FsResult<FsReadResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.UnsupportedOperation,
                Message = $"'{node.FileName}' is a binary document and cannot be read as text.",
                Retryable = false,
                Hint = "Read /print-queue/status.json for its print state, or use fs_blob_read for raw bytes."
            });
        }

        return Ok(path, Encoding.UTF8.GetString(bytes));
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var node = PrinterQueuePath.Parse(path);

        if (node.Kind == PrinterNodeKind.Root)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
        }

        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = false });
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }

        var entry = await spool.GetAsync(node.FileName!, ct);
        if (entry is null)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }

        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = true,
            Path = path,
            IsDirectory = false,
            Size = entry.SizeBytes,
            LastModified = entry.LastWriteAt.UtcDateTime.ToString("O")
        });
    }

    public async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsCreateResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsCreateResult>($"Create a document at /print-queue/<filename> (got '{path}')");
        }

        if (!overwrite && await spool.GetAsync(node.FileName!, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.AlreadyExists,
                Message = $"A job named '{node.FileName}' is already queued. Pass overwrite=true to replace it.",
                Retryable = false
            });
        }

        await CancelIfSubmittedAsync(node.FileName!, ct);
        var bytes = Encoding.UTF8.GetBytes(content);
        await spool.WriteBytesAsync(node.FileName!, "text/plain", bytes, 0, true, ct);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "queued",
            FilePath = path,
            Size = bytes.Length.ToString(),
            Lines = content.Split('\n').Length
        });
    }

    public async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsEditResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsEditResult>(path);
        }

        var bytes = await spool.ReadAllBytesAsync(node.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsEditResult>(path);
        }

        if (!IsText(bytes))
        {
            return Unsupported<FsEditResult>($"'{node.FileName}' is a binary document and cannot be edited as text.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var total = 0;
        var details = new List<FsEditDetail>();
        foreach (var edit in edits)
        {
            var count = CountOccurrences(text, edit.OldString);
            if (count == 0)
            {
                return Invalid<FsEditResult>($"Text not found: '{edit.OldString}'");
            }

            var replaced = edit.ReplaceAll ? count : 1;
            text = ReplaceFirstOrAll(text, edit.OldString, edit.NewString, edit.ReplaceAll);
            total += replaced;
            details.Add(new FsEditDetail { OccurrencesReplaced = replaced, AffectedLines = new FsLineRange { Start = 0, End = 0 } });
        }

        // Replacing the spooled bytes (offset 0) resets the lifecycle to unsubmitted; cancel any in-flight job first.
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await spool.WriteBytesAsync(node.FileName!, "text/plain", Encoding.UTF8.GetBytes(text), 0, true, ct);

        return new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "queued",
            FilePath = path,
            TotalOccurrencesReplaced = total,
            Edits = details
        });
    }

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var entries = await spool.ListAsync(ct);
        var names = entries.Select(e => e.FileName).Append(PrinterQueuePath.StatusFileName);

        var regex = GlobToRegex(pattern);
        var matched = names.Where(n => regex.IsMatch(n)).OrderBy(n => n)
            .Select(n => "/" + n).ToList();

        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = matched,
            Truncated = false,
            Total = matched.Count
        });
    }

    public async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        await coordinator.ReconcileAsync(ct);
        var entries = await spool.ListAsync(ct);

        Regex matcher;
        try
        {
            matcher = new Regex(regex ? query : Regex.Escape(query), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        }
        catch (ArgumentException ex)
        {
            return Invalid<FsSearchResult>($"Invalid regex: {ex.Message}");
        }

        var results = new List<FsSearchFileResult>();
        var totalMatches = 0;
        foreach (var entry in entries)
        {
            var bytes = await spool.ReadAllBytesAsync(entry.FileName, ct);
            if (bytes is null || !IsText(bytes))
            {
                continue;
            }

            var lines = Encoding.UTF8.GetString(bytes).Split('\n');
            var matches = lines
                .Select((text, i) => (text, i))
                .Where(l => SafeMatch(matcher, l.text))
                .Select(l => new FsSearchMatch { Line = l.i + 1, Text = l.text })
                .Take(maxResults)
                .ToList();

            if (matches.Count == 0)
            {
                continue;
            }

            totalMatches += matches.Count;
            results.Add(outputMode == VfsTextSearchOutputMode.FilesOnly
                ? new FsSearchFileResult { File = "/" + entry.FileName, MatchCount = matches.Count }
                : new FsSearchFileResult { File = "/" + entry.FileName, Matches = matches });
        }

        return new FsResult<FsSearchResult>.Ok(new FsSearchResult
        {
            Query = query,
            Regex = regex,
            Path = path ?? "/",
            FilesSearched = entries.Count,
            FilesWithMatches = results.Count,
            TotalMatches = totalMatches,
            Truncated = false,
            Results = results
        });
    }

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsMoveResult>(
            "The print queue does not support move. Copy a document into /print-queue to print it."));

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsRemoveResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsRemoveResult>(path);
        }

        var entry = await spool.GetAsync(node.FileName!, ct);
        if (entry is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        // The crux: cancelling before the printer finishes means it will not print.
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await spool.RemoveAsync(node.FileName!, ct);

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = entry.IsSubmitted ? "Print job cancelled and removed from the queue." : "Removed from the queue before printing.",
            OriginalPath = path,
            TrashPath = ""
        });
    }

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        Task.FromResult(Unsupported<FsExecResult>("The print queue does not support exec."));

    public async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var src = PrinterQueuePath.Parse(sourcePath);
        var dst = PrinterQueuePath.Parse(destinationPath);
        if (src.Kind != PrinterNodeKind.DocumentFile || dst.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsCopyResult>("Copy within the print queue requires document file paths.");
        }

        var bytes = await spool.ReadAllBytesAsync(src.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsCopyResult>(sourcePath);
        }

        if (!overwrite && await spool.GetAsync(dst.FileName!, ct) is not null)
        {
            return new FsResult<FsCopyResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.AlreadyExists,
                Message = $"A job named '{dst.FileName}' is already queued.",
                Retryable = false
            });
        }

        var srcEntry = await spool.GetAsync(src.FileName!, ct);
        await CancelIfSubmittedAsync(dst.FileName!, ct);
        await spool.WriteBytesAsync(dst.FileName!, srcEntry!.ContentType, bytes, 0, true, ct);

        return new FsResult<FsCopyResult>.Ok(new FsCopyResult
        {
            Status = "queued",
            Source = sourcePath,
            Destination = destinationPath,
            Bytes = bytes.Length
        });
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        var bytes = node.Kind == PrinterNodeKind.DocumentFile
            ? await spool.ReadAllBytesAsync(node.FileName!, ct)
            : null;

        if (bytes is null)
        {
            yield break;
        }

        const int chunkSize = 256 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            yield return bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset));
        }
    }

    public async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            throw new InvalidOperationException($"Cannot write to '{path}' in the print queue.");
        }

        long offset = 0;
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            await spool.WriteBytesAsync(node.FileName!, "application/octet-stream", chunk, offset, overwrite && offset == 0, ct);
            offset += chunk.Length;
        }

        if (offset == 0)
        {
            await spool.WriteBytesAsync(node.FileName!, "application/octet-stream", ReadOnlyMemory<byte>.Empty, 0, true, ct);
        }

        return offset;
    }

    private async Task CancelIfSubmittedAsync(string fileName, CancellationToken ct)
    {
        var entry = await spool.GetAsync(fileName, ct);
        if (entry is { IsSubmitted: true })
        {
            await printer.CancelAsync(entry.JobId!.Value, ct);
        }
    }

    private async Task<string> RenderStatusAsync(CancellationToken ct)
    {
        var entries = await spool.ListAsync(ct);
        var active = (await printer.GetActiveJobsAsync(ct)).ToDictionary(j => j.JobId);

        var rows = entries.Select(e => new
        {
            filename = e.FileName,
            jobId = e.JobId,
            state = !e.IsSubmitted
                ? PrintJobState.Queued.ToString()
                : active.TryGetValue(e.JobId!.Value, out var job) ? job.State.ToString() : PrintJobState.Processing.ToString(),
            submittedAt = e.SubmittedAt?.UtcDateTime.ToString("O"),
            sizeBytes = e.SizeBytes
        }).OrderBy(r => r.filename);

        return JsonSerializer.Serialize(rows, _json);
    }

    private static bool SafeMatch(Regex matcher, string text)
    {
        try
        {
            return matcher.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string ReplaceFirstOrAll(string text, string oldValue, string newValue, bool all)
    {
        if (all)
        {
            return text.Replace(oldValue, newValue, StringComparison.Ordinal);
        }

        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0 ? text : text[..index] + newValue + text[(index + oldValue.Length)..];
    }

    private static Regex GlobToRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return new Regex("^" + escaped + "$", RegexOptions.IgnoreCase);
    }

    private static FsResult<FsReadResult> Ok(string path, string content) =>
        new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });

    private static FsResult<T> ReadOnly<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, $"{path} is read-only"));

    private static FsResult<T> Invalid<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.InvalidArgument, message));

    private static FsResult<T> NotFound<T>(string path) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.NotFound, $"Path not found: {path}"));

    private static FsResult<T> Unsupported<T>(string message) where T : class =>
        new FsResult<T>.Err(Error(ToolError.Codes.UnsupportedOperation, message));

    private static ToolErrorResult Error(string code, string message) =>
        new() { ErrorCode = code, Message = message, Retryable = false };
}
```

- [ ] **Step 4: Run test to verify Cycle A passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrinterQueueFileSystemTests"`
Expected: PASS for `Backend_Contract_ExposesNameAndUnsupportedOps` and `StatusJson_IsReadOnly`.

- [ ] **Step 5: Commit (Cycle A)**

```bash
git add Domain/Tools/Printing/Vfs/PrinterQueueFileSystem.cs Tests/Unit/Domain/Printing/Vfs/PrinterQueueFileSystemTests.cs
git commit -m "feat(printer): add PrinterQueueFileSystem backend with unsupported/read-only guards"
```

### Cycle B — create/delete/glob/read/info lifecycle

- [ ] **Step 6: Add the failing lifecycle tests**

Append these methods inside `PrinterQueueFileSystemTests` (before `Dispose`):

```csharp
    [Fact]
    public async Task Create_QueuesText_Glob_Read_And_StatusReflectIt()
    {
        var fs = Build();

        var create = await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        create.ShouldBeOfType<FsResult<FsCreateResult>.Ok>().Value.Status.ShouldBe("queued");

        // Glob lists the doc plus status.json.
        var glob = (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldContain("/note.txt");
        glob.Entries.ShouldContain("/status.json");

        // Read returns the text content.
        var read = (await fs.ReadAsync("note.txt", null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldBe("print me");

        // status.json shows the job as queued (not yet submitted — inside the debounce window).
        var status = (await fs.ReadAsync("status.json", null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        status.Content.ShouldContain("note.txt");
        status.Content.ShouldContain("Queued");
    }

    [Fact]
    public async Task Create_DuplicateName_RequiresOverwrite()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "v1", false, true, CancellationToken.None);

        var dup = await fs.CreateAsync("note.txt", "v2", false, true, CancellationToken.None);
        dup.ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("already_exists");

        var ok = await fs.CreateAsync("note.txt", "v2", true, true, CancellationToken.None);
        ok.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        (await fs.ReadAsync("note.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("v2");
    }

    [Fact]
    public async Task Delete_BeforeSubmit_DoesNotPrint()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);

        var delete = await fs.DeleteAsync("note.txt", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();

        // Never submitted, never cancelled, gone from the queue.
        _printer.Submissions.ShouldBeEmpty();
        _printer.Canceled.ShouldBeEmpty();
        (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>()
            .Value.Entries.ShouldNotContain("/note.txt");
    }

    [Fact]
    public async Task Delete_AfterSubmit_CancelsActiveJob()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);

        // Drive submission past the debounce window.
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var delete = await fs.DeleteAsync("note.txt", CancellationToken.None);
        delete.ShouldBeOfType<FsResult<FsRemoveResult>.Ok>();
        _printer.Canceled.ShouldContain(jobId);
    }

    [Fact]
    public async Task ReadAndInfo_BinaryDocument_AreHandled()
    {
        var fs = Build();
        // A NUL byte makes this binary.
        await fs.WriteChunksAsync("scan.pdf", Single(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0x01 }), true, true, CancellationToken.None);

        var read = await fs.ReadAsync("scan.pdf", null, null, CancellationToken.None);
        read.ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");

        var info = (await fs.InfoAsync("scan.pdf", CancellationToken.None)).ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value;
        info.Exists.ShouldBeTrue();
        info.Size.ShouldBe(6);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Single(byte[] bytes)
    {
        yield return bytes;
        await Task.CompletedTask;
    }
```

- [ ] **Step 7: Run lifecycle tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrinterQueueFileSystemTests"`
Expected: PASS (Cycle A + B all green; the backend already implements these).

- [ ] **Step 8: Commit (Cycle B)**

```bash
git add Tests/Unit/Domain/Printing/Vfs/PrinterQueueFileSystemTests.cs
git commit -m "test(printer): cover create/delete/glob/read/info lifecycle"
```

### Cycle C — edit / copy / search / reconcile-disappear

- [ ] **Step 9: Add the failing edit/copy/search tests**

Append inside `PrinterQueueFileSystemTests` (before the `Single` helper):

```csharp
    [Fact]
    public async Task Edit_ReplacesText_AndCancelsPriorSubmission()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "hello world", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        var edit = await fs.EditAsync("note.txt", new[] { new TextEdit("world", "there") }, CancellationToken.None);
        edit.ShouldBeOfType<FsResult<FsEditResult>.Ok>().Value.TotalOccurrencesReplaced.ShouldBe(1);

        _printer.Canceled.ShouldContain(jobId);
        (await fs.ReadAsync("note.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("hello there");
        // Reset to unsubmitted, ready for resubmission.
        (await _spool.GetAsync("note.txt", CancellationToken.None))!.IsSubmitted.ShouldBeFalse();
    }

    [Fact]
    public async Task Copy_DuplicatesDocumentAsNewQueueEntry()
    {
        var fs = Build();
        await fs.CreateAsync("a.txt", "content", false, true, CancellationToken.None);

        var copy = await fs.CopyAsync("a.txt", "b.txt", false, true, CancellationToken.None);
        copy.ShouldBeOfType<FsResult<FsCopyResult>.Ok>();

        (await fs.ReadAsync("b.txt", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content.ShouldBe("content");
    }

    [Fact]
    public async Task Search_FindsTextAcrossQueuedDocuments()
    {
        var fs = Build();
        await fs.CreateAsync("a.txt", "the quick brown fox", false, true, CancellationToken.None);
        await fs.CreateAsync("b.txt", "lazy dog", false, true, CancellationToken.None);

        var search = (await fs.SearchAsync("quick", false, null, null, "*", 50, 0, VfsTextSearchOutputMode.Content, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsSearchResult>.Ok>().Value;
        search.FilesWithMatches.ShouldBe(1);
        search.Results[0].File.ShouldBe("/a.txt");
    }

    [Fact]
    public async Task FinishedJob_DisappearsFromQueue_OnNextListing()
    {
        var fs = Build();
        await fs.CreateAsync("note.txt", "print me", false, true, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMilliseconds(600));
        await _coordinator.SubmitDueAsync(CancellationToken.None);
        var jobId = (await _spool.GetAsync("note.txt", CancellationToken.None))!.JobId!.Value;

        // Printer finishes the job; the next glob reconciles it away.
        _printer.CompleteJob(jobId);
        var glob = (await fs.GlobAsync("/", "*", CancellationToken.None)).ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        glob.Entries.ShouldNotContain("/note.txt");
    }
```

- [ ] **Step 10: Run the full backend test suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~PrinterQueueFileSystemTests"`
Expected: PASS (all Cycle A/B/C facts green).

- [ ] **Step 11: Commit (Cycle C)**

```bash
git add Tests/Unit/Domain/Printing/Vfs/PrinterQueueFileSystemTests.cs
git commit -m "test(printer): cover edit/copy/search and auto-disappear"
```

---

## Task 7: IPP job-state mapper

**Files:**
- Create: `Infrastructure/Clients/Printer/IppJobStateMapper.cs`
- Test: `Tests/Unit/Infrastructure/Printing/IppJobStateMapperTests.cs`

SharpIppNext exposes `SharpIpp.Protocol.Models.JobState`. This pure function maps it to our `PrintJobState` and is the only state-translation logic, so it is unit-tested directly.

- [ ] **Step 1: Write the failing test**

`Tests/Unit/Infrastructure/Printing/IppJobStateMapperTests.cs`:

```csharp
using Domain.DTOs.Printing;
using Infrastructure.Clients.Printer;
using SharpIpp.Protocol.Models;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure.Printing;

public class IppJobStateMapperTests
{
    [Theory]
    [InlineData(JobState.Pending, PrintJobState.Pending)]
    [InlineData(JobState.PendingHeld, PrintJobState.Pending)]
    [InlineData(JobState.Processing, PrintJobState.Processing)]
    [InlineData(JobState.ProcessingStopped, PrintJobState.Processing)]
    [InlineData(JobState.Completed, PrintJobState.Completed)]
    [InlineData(JobState.Canceled, PrintJobState.Canceled)]
    [InlineData(JobState.Aborted, PrintJobState.Aborted)]
    public void Map_TranslatesKnownStates(JobState ipp, PrintJobState expected)
    {
        IppJobStateMapper.Map(ipp).ShouldBe(expected);
    }

    [Theory]
    [InlineData(JobState.Pending, true)]
    [InlineData(JobState.Processing, true)]
    [InlineData(JobState.Completed, false)]
    [InlineData(JobState.Canceled, false)]
    [InlineData(JobState.Aborted, false)]
    public void IsActive_TrueOnlyForPendingOrProcessing(JobState ipp, bool active)
    {
        IppJobStateMapper.IsActive(ipp).ShouldBe(active);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~IppJobStateMapperTests"`
Expected: FAIL — `IppJobStateMapper` does not exist (and `SharpIpp` not yet referenced by Infrastructure; this is resolved in Task 8 Step 1 which adds the package. If this task runs first, do Task 8 Step 1 now).

- [ ] **Step 3: Write minimal implementation**

`Infrastructure/Clients/Printer/IppJobStateMapper.cs`:

```csharp
using Domain.DTOs.Printing;
using SharpIpp.Protocol.Models;

namespace Infrastructure.Clients.Printer;

public static class IppJobStateMapper
{
    public static PrintJobState Map(JobState state) => state switch
    {
        JobState.Pending or JobState.PendingHeld => PrintJobState.Pending,
        JobState.Processing or JobState.ProcessingStopped => PrintJobState.Processing,
        JobState.Completed => PrintJobState.Completed,
        JobState.Canceled => PrintJobState.Canceled,
        JobState.Aborted => PrintJobState.Aborted,
        _ => PrintJobState.Unknown
    };

    public static bool IsActive(JobState state) =>
        Map(state) is PrintJobState.Pending or PrintJobState.Processing;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~IppJobStateMapperTests"`
Expected: PASS.

> If the test fails to compile because a `JobState` enum member name differs in the installed SharpIppNext version, open the package's `SharpIpp.Protocol.Models.JobState` (F12 / decompile) and adjust the member names in both the test and mapper to match. The mapping intent (pending/held→Pending, processing/stopped→Processing, etc.) stays the same.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Printer/IppJobStateMapper.cs Tests/Unit/Infrastructure/Printing/IppJobStateMapperTests.cs
git commit -m "feat(printer): map IPP job states to domain states"
```

---

## Task 8: IPP client (`IppPrinterClient`)

**Files:**
- Modify: `Infrastructure/Infrastructure.csproj` (add SharpIppNext package)
- Create: `Infrastructure/Clients/Printer/IppPrinterClient.cs`

This is a thin SharpIppNext adapter. It has no unit test (it requires a live IPP endpoint); the bulk of behavior is covered by the mapper test (Task 7) and the backend/coordinator tests. A live-CUPS integration test is a documented opt-in (see Task 13).

- [ ] **Step 1: Add the SharpIppNext package to Infrastructure**

Run:
```bash
dotnet add Infrastructure/Infrastructure.csproj package SharpIppNext
dotnet restore Infrastructure/Infrastructure.csproj
```
Expected: the package is added and restores cleanly. Note the resolved version (pin it in the `.csproj`).

- [ ] **Step 2: Write the client implementation**

`Infrastructure/Clients/Printer/IppPrinterClient.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Printing;
using SharpIpp;
using SharpIpp.Models;
using SharpIpp.Protocol.Models;

namespace Infrastructure.Clients.Printer;

// Talks IPP over HTTP to a single configured printer (a CUPS server or a direct-IPP printer).
public sealed class IppPrinterClient(ISharpIppClient client, Uri printerUri) : IPrinterClient
{
    public async Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct)
    {
        await using var stream = new MemoryStream(document.ToArray(), writable: false);
        var request = new PrintJobRequest
        {
            Document = stream,
            OperationAttributes = new PrintJobOperationAttributes
            {
                PrinterUri = printerUri,
                JobName = jobName,
                DocumentName = jobName,
                DocumentFormat = contentType
            }
        };

        var response = await client.PrintJobAsync(request, ct);
        return new PrintJobHandle(response.JobId);
    }

    public async Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct)
    {
        var request = new GetJobsRequest
        {
            OperationAttributes = new GetJobsOperationAttributes
            {
                PrinterUri = printerUri,
                WhichJobs = WhichJobs.NotCompleted
            }
        };

        var response = await client.GetJobsAsync(request, ct);
        return response.Jobs
            .Where(j => j.JobState is not null && IppJobStateMapper.IsActive(j.JobState.Value))
            .Select(j => new PrintJobStatus(
                j.JobId ?? 0,
                j.JobName ?? string.Empty,
                IppJobStateMapper.Map(j.JobState!.Value)))
            .ToList();
    }

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        var request = new CancelJobRequest
        {
            OperationAttributes = new CancelJobOperationAttributes
            {
                PrinterUri = printerUri,
                JobId = jobId
            }
        };

        await client.CancelJobAsync(request, ct);
    }
}
```

> **Verify against the installed SharpIppNext API.** The exact request property names (`PrintJobOperationAttributes` vs an inline initializer, `WhichJobs`, the `GetJobsResponse.Jobs` element type and its `JobId`/`JobName`/`JobState` members, and whether `ISharpIppClient` exists or you use the concrete `SharpIppClient`) can differ by version. After writing, build Infrastructure and fix names to match the package. If `ISharpIppClient` is not provided by the library, change the constructor to take `SharpIppClient` directly and register that in DI (Task 10).

- [ ] **Step 3: Build Infrastructure**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: PASS. Fix any SharpIppNext member-name mismatches until it compiles.

- [ ] **Step 4: Commit**

```bash
git add Infrastructure/Infrastructure.csproj Infrastructure/Clients/Printer/IppPrinterClient.cs
git commit -m "feat(printer): add SharpIppNext-backed IPP printer client"
```

---

## Task 9: The MCP server project scaffold

**Files:**
- Create: `McpServerPrinter/McpServerPrinter.csproj`
- Create: `McpServerPrinter/Program.cs`
- Create: `McpServerPrinter/Settings/PrinterSettings.cs`
- Create: `McpServerPrinter/appsettings.json`
- Create: `McpServerPrinter/Dockerfile`
- Modify: the solution file (`*.sln`)

- [ ] **Step 1: Create the csproj**

`McpServerPrinter/McpServerPrinter.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="..\.dockerignore">
      <Link>.dockerignore</Link>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.8" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Tests" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create the settings record**

`McpServerPrinter/Settings/PrinterSettings.cs`:

```csharp
namespace McpServerPrinter.Settings;

public record PrinterSettings
{
    public required string PrinterUri { get; init; }
    public string SpoolPath { get; init; } = "/spool";
    public int SubmitDebounceMilliseconds { get; init; } = 750;
    public int TickIntervalMilliseconds { get; init; } = 500;
}
```

- [ ] **Step 3: Create appsettings.json**

`McpServerPrinter/appsettings.json`:

```json
{
  "PrinterUri": "ipp://cups:631/printers/Main",
  "SpoolPath": "/spool",
  "SubmitDebounceMilliseconds": 750,
  "TickIntervalMilliseconds": 500,
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

- [ ] **Step 4: Create Program.cs (temporarily minimal; ConfigModule comes in Task 10)**

`McpServerPrinter/Program.cs`:

```csharp
using McpServerPrinter.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigurePrinter(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();
```

- [ ] **Step 5: Create the Dockerfile**

`McpServerPrinter/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerPrinter/McpServerPrinter.csproj", "McpServerPrinter/"]
RUN dotnet restore "McpServerPrinter/McpServerPrinter.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerPrinter/", "McpServerPrinter/"]
WORKDIR "/src/McpServerPrinter"
RUN dotnet publish "./McpServerPrinter.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpServerPrinter.dll"]
```

- [ ] **Step 6: Add the project to the solution**

Run:
```bash
SLN=$(ls *.sln | head -n1)
dotnet sln "$SLN" add McpServerPrinter/McpServerPrinter.csproj
```
Expected: "Project ... added to the solution."

> The build will not succeed yet — `GetSettings`/`ConfigurePrinter` are added in Task 10. Do not build at this step. Commit the scaffold.

- [ ] **Step 7: Commit**

```bash
git add McpServerPrinter *.sln
git commit -m "chore(printer): scaffold McpServerPrinter project"
```

---

## Task 10: Server wiring — ConfigModule, resource, prompt, tools, worker

**Files:**
- Create: `McpServerPrinter/Modules/ConfigModule.cs`
- Create: `McpServerPrinter/McpResources/FileSystemResource.cs`
- Create: `Domain/Prompts/PrintingPrompt.cs`
- Create: `McpServerPrinter/McpPrompts/McpSystemPrompt.cs`
- Create: `McpServerPrinter/Services/PrintSubmissionWorker.cs`
- Create: `McpServerPrinter/McpTools/Fs{Read,Info,Glob,Search,Create,Edit,Delete,Copy,BlobRead,BlobWrite}Tool.cs`

- [ ] **Step 1: Create the printing prompt**

`Domain/Prompts/PrintingPrompt.cs`:

```csharp
namespace Domain.Prompts;

public static class PrintingPrompt
{
    public const string Name = "printing_prompt";

    public const string Description =
        "Explains how to print documents via the /print-queue filesystem (copy to print, remove to cancel).";

    public const string Prompt =
        """
        ## Printing

        The `/print-queue` filesystem is a printer. To print a document, copy or create it into
        `/print-queue/<filename>` (e.g. copy `/vault/report.pdf` to `/print-queue/report.pdf`, or
        `fs_create` a text file there). It is sent to the configured printer automatically.

        - To **cancel** a job that has not finished printing yet, remove it with `fs_delete`.
          If it has already finished, it is gone from the queue and removal is a no-op.
        - Read `/print-queue/status.json` to see every queued job and its state
          (queued / pending / processing).
        - Finished jobs disappear from the listing automatically.
        - `move` and `exec` are not supported on this filesystem. Re-printing an edited document:
          use `fs_edit` (text only); it cancels the old job and queues the new version.
        """;
}
```

- [ ] **Step 2: Create the filesystem resource**

`McpServerPrinter/McpResources/FileSystemResource.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(UriTemplate = "filesystem://print-queue", Name = "Print Queue Filesystem", MimeType = "application/json")]
    [Description("Printer queue exposed as a filesystem")]
    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = "print-queue",
        mountPoint = "/print-queue",
        description =
            "A printer exposed as a flat filesystem. Copy or create a document at /print-queue/<filename> " +
            "to print it on the configured printer; the document is submitted automatically. Remove a file " +
            "with fs_delete to cancel it if it has not finished printing yet. Read /print-queue/status.json " +
            "for the state of every queued job (queued/pending/processing). Finished jobs disappear from the " +
            "listing automatically. Supported: read, create, edit (text only), copy, glob, search, delete, and " +
            "binary copy-in. Not supported: move and exec."
    });
}
```

- [ ] **Step 3: Create the MCP prompt wrapper**

`McpServerPrinter/McpPrompts/McpSystemPrompt.cs`:

```csharp
using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    [McpServerPrompt(Name = PrintingPrompt.Name)]
    [Description(PrintingPrompt.Description)]
    public string GetPrintingPrompt() => PrintingPrompt.Prompt;
}
```

- [ ] **Step 4: Create the background worker**

`McpServerPrinter/Services/PrintSubmissionWorker.cs`:

```csharp
using Domain.Tools.Printing;
using McpServerPrinter.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerPrinter.Services;

// Periodically submits documents whose writes have gone quiet and prunes finished jobs.
public sealed class PrintSubmissionWorker(
    PrintQueueCoordinator coordinator,
    PrinterSettings settings,
    ILogger<PrintSubmissionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(settings.TickIntervalMilliseconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await coordinator.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Print queue tick failed");
            }
        }
    }
}
```

- [ ] **Step 5: Create the MCP tools**

`McpServerPrinter/McpTools/FsReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsReadTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a queued document's text, or read status.json for the queue state.")]
    public async Task<CallToolResult> McpRun(string path, int? offset = null, int? limit = null, CancellationToken ct = default)
        => ToolResponse.Create(await fs.ReadAsync(path, offset, limit, ct));
}
```

`McpServerPrinter/McpTools/FsInfoTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsInfoTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_info")]
    [Description("Get metadata for a queued document or the queue root.")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.InfoAsync(path, ct));
}
```

`McpServerPrinter/McpTools/FsGlobTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsGlobTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_glob")]
    [Description("List queued documents (plus status.json) matching a glob pattern.")]
    public async Task<CallToolResult> McpRun(string basePath, string pattern, CancellationToken ct = default)
        => ToolResponse.Create(await fs.GlobAsync(basePath, pattern, ct));
}
```

`McpServerPrinter/McpTools/FsSearchTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsSearchTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_search")]
    [Description("Search the text content of queued documents.")]
    public async Task<CallToolResult> McpRun(
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = "*",
        int maxResults = 50,
        int contextLines = 0,
        VfsTextSearchOutputMode outputMode = VfsTextSearchOutputMode.Content,
        CancellationToken ct = default)
        => ToolResponse.Create(await fs.SearchAsync(query, regex, path, directoryPath, filePattern!, maxResults, contextLines, outputMode, ct));
}
```

`McpServerPrinter/McpTools/FsCreateTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsCreateTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_create")]
    [Description("Queue a new text document for printing at /print-queue/<filename>.")]
    public async Task<CallToolResult> McpRun(
        string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CreateAsync(path, content, overwrite, createDirectories, ct));
}
```

`McpServerPrinter/McpTools/FsEditTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsEditTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_edit")]
    [Description("Edit a queued text document; cancels the old job and re-queues the new version.")]
    public async Task<CallToolResult> McpRun(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default)
        => ToolResponse.Create(await fs.EditAsync(path, edits, ct));
}
```

`McpServerPrinter/McpTools/FsDeleteTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsDeleteTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Remove a queued document. Cancels it if it has not finished printing.")]
    public async Task<CallToolResult> McpRun(string path, CancellationToken ct = default)
        => ToolResponse.Create(await fs.DeleteAsync(path, ct));
}
```

`McpServerPrinter/McpTools/FsCopyTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsCopyTool(PrinterQueueFileSystem fs)
{
    [McpServerTool(Name = "fs_copy")]
    [Description("Duplicate a queued document under a new name (queues another print job).")]
    public async Task<CallToolResult> McpRun(
        string sourcePath, string destinationPath, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
        => ToolResponse.Create(await fs.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct));
}
```

`McpServerPrinter/McpTools/FsBlobReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsBlobReadTool(IPrintSpool spool)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description("Read a chunk of a queued document's raw bytes as base64. Returns { contentBase64, eof, totalBytes }.")]
    public async Task<CallToolResult> McpRun(string path, long offset = 0, int length = 262144, CancellationToken ct = default)
    {
        var fileName = path.TrimStart('/');
        var (bytes, eof, total) = await spool.ReadBytesAsync(fileName, offset, length, ct);
        return ToolResponse.Create(FsResultContract.ToNode(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(bytes),
            Eof = eof,
            TotalBytes = total
        }));
    }
}
```

`McpServerPrinter/McpTools/FsBlobWriteTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(IPrintSpool spool)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description("Write a chunk of raw bytes (base64) to a queued document. offset=0 starts it; the document prints once writes go quiet.")]
    public async Task<CallToolResult> McpRun(
        string path, string contentBase64, long offset = 0, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
    {
        var fileName = path.TrimStart('/');
        var bytes = Convert.FromBase64String(contentBase64);
        await spool.WriteBytesAsync(fileName, "application/octet-stream", bytes, offset, overwrite, ct);
        var entry = await spool.GetAsync(fileName, ct);
        return ToolResponse.Create(FsResultContract.ToNode(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = entry?.SizeBytes ?? bytes.Length
        }));
    }
}
```

- [ ] **Step 6: Create the ConfigModule**

`McpServerPrinter/Modules/ConfigModule.cs`:

```csharp
using Domain.Contracts;
using Domain.Tools.Printing;
using Domain.Tools.Printing.Vfs;
using Infrastructure.Clients.Printer;
using Infrastructure.Printing;
using Infrastructure.Utils;
using McpServerPrinter.McpPrompts;
using McpServerPrinter.McpResources;
using McpServerPrinter.McpTools;
using McpServerPrinter.Services;
using McpServerPrinter.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpIpp;

namespace McpServerPrinter.Modules;

public static class ConfigModule
{
    public static PrinterSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        return config.Get<PrinterSettings>()
               ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigurePrinter(this IServiceCollection services, PrinterSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddSingleton(TimeProvider.System)
            .AddSingleton<ISharpIppClient>(_ => new SharpIppClient())
            .AddSingleton<IPrinterClient>(sp => new IppPrinterClient(
                sp.GetRequiredService<ISharpIppClient>(), new Uri(settings.PrinterUri)))
            .AddSingleton<IPrintSpool>(sp => new PrintSpool(settings.SpoolPath, sp.GetRequiredService<TimeProvider>()))
            .AddSingleton(sp => new PrintQueueCoordinator(
                sp.GetRequiredService<IPrintSpool>(),
                sp.GetRequiredService<IPrinterClient>(),
                sp.GetRequiredService<TimeProvider>(),
                TimeSpan.FromMilliseconds(settings.SubmitDebounceMilliseconds)))
            .AddSingleton<PrinterQueueFileSystem>()
            .AddHostedService<PrintSubmissionWorker>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<FsReadTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
            .WithResources<FileSystemResource>()
            .WithPrompts<McpSystemPrompt>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }));

        return services;
    }
}
```

> Note: `fs_move` and `fs_exec` are deliberately **not** registered. The agent's `McpFileSystemBackend` returns a clean `unsupported_operation` envelope for unregistered tools.
> If `ISharpIppClient` does not exist in the installed package, replace its two registrations with `SharpIppClient` (concrete) and update `IppPrinterClient`'s constructor accordingly (see Task 8 Step 2 note).

- [ ] **Step 7: Build the server project**

Run: `dotnet build McpServerPrinter/McpServerPrinter.csproj`
Expected: PASS. Fix any MCP API or SharpIppNext name mismatches.

- [ ] **Step 8: Run the full test suite to confirm nothing regressed**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Printing"`
Expected: PASS (all printer unit tests green).

- [ ] **Step 9: Commit**

```bash
git add Domain/Prompts/PrintingPrompt.cs McpServerPrinter
git commit -m "feat(printer): wire MCP server (tools, resource, prompt, worker, DI)"
```

---

## Task 11: Docker Compose + Agent endpoint wiring

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Modify: `Agent/appsettings.json`

- [ ] **Step 1: Add the `mcp-printer` service to docker-compose**

In `DockerCompose/docker-compose.yml`, add this service block alongside the other `mcp-*` services (mirror `mcp-scheduling`, but with a spool volume and the printer env vars; pick the next free host port `6014`):

```yaml
  mcp-printer:
    image: mcp-printer:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: mcp-printer
    ports:
      - "6014:8080"
    build:
      context: ${REPOSITORY_PATH}
      dockerfile: McpServerPrinter/Dockerfile
      cache_from:
        - mcp-printer:latest
      args:
        - BUILDKIT_INLINE_CACHE=1
    environment:
      - PRINTER__PRINTERURI=${PRINTER_URI:-ipp://cups:631/printers/Main}
      - PRINTER__SPOOLPATH=/spool
    volumes:
      - printer-spool:/spool
    restart: unless-stopped
    env_file:
      - .env
    networks:
      - jackbot
    depends_on:
      base-sdk:
        condition: service_started
```

> These map `PrinterSettings.PrinterUri` and `.SpoolPath` (the `__` is the .NET config section separator). `PRINTER_URI` is non-secret, so it has a default here and does **not** belong in `.env`; override it in your shell or compose `.env` only if you want a different printer.

- [ ] **Step 2: Declare the named volume**

In the top-level `volumes:` section of `docker-compose.yml` (create the section if it does not exist), add:

```yaml
volumes:
  printer-spool:
```

- [ ] **Step 3: Add the endpoint to the `jonas` agent**

In `Agent/appsettings.json`, in the `jonas` agent's `mcpServerEndpoints` array, add the printer endpoint after the scheduling one:

```json
"http://mcp-printer:8080/mcp"
```

The `jonas` agent already enables the `filesystem` feature and whitelists `domain__filesystem*`, so the new `/print-queue` mount and its tools are picked up automatically — no other agent changes needed.

- [ ] **Step 4: Validate compose syntax**

Run: `docker compose -f DockerCompose/docker-compose.yml config >/dev/null && echo OK`
Expected: `OK` (no YAML/interpolation errors).

- [ ] **Step 5: Commit**

```bash
git add DockerCompose/docker-compose.yml Agent/appsettings.json
git commit -m "chore(printer): add mcp-printer service, spool volume, and agent endpoint"
```

---

## Task 12: Full-solution build, format, and test gate

**Files:** none (verification only)

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build`
Expected: PASS with no errors. Resolve any remaining cross-project reference or SharpIppNext API issues here.

- [ ] **Step 2: Run the whole non-E2E test suite**

Run: `dotnet test --filter "Category!=E2E"`
Expected: all **printer** tests pass. (Per the repo baseline, ~148 pre-existing `DockerUnavailableException` failures may occur in this WSL env and are not regressions — confirm any failures are pre-existing and unrelated to `Printing`.)

- [ ] **Step 3: Confirm formatting matches the pre-commit hook**

Run: `dotnet format --verify-no-changes` (or let the pre-commit hook run on commit)
Expected: no formatting diffs. If it reports changes, run `dotnet format` and re-stage.

- [ ] **Step 4: Commit any formatting fixups**

```bash
git add -A
git commit -m "chore(printer): formatting and build fixups" --allow-empty
```

---

## Task 13: (Optional, documented) Live CUPS integration test

**Files:**
- Create: `Tests/Integration/Printing/IppPrinterClientIntegrationTests.cs`

This is opt-in and Docker-gated; it requires a reachable CUPS/IPP endpoint and is expected to be skipped in the WSL dev env (consistent with the repo's E2E baseline). Include it only if a CUPS container is available.

- [ ] **Step 1: Write a guarded integration test**

`Tests/Integration/Printing/IppPrinterClientIntegrationTests.cs`:

```csharp
using Infrastructure.Clients.Printer;
using SharpIpp;
using Shouldly;
using Xunit;

namespace Tests.Integration.Printing;

[Trait("Category", "Integration")]
public class IppPrinterClientIntegrationTests
{
    private static string? PrinterUri => Environment.GetEnvironmentVariable("PRINTER_TEST_URI");

    [SkippableFact]
    public async Task SubmitListCancel_RoundTripsAgainstRealCups()
    {
        Skip.If(string.IsNullOrWhiteSpace(PrinterUri), "PRINTER_TEST_URI not set; skipping live CUPS test.");

        var client = new IppPrinterClient(new SharpIppClient(), new Uri(PrinterUri!));
        var handle = await client.SubmitAsync("plan-test.txt", "text/plain",
            System.Text.Encoding.UTF8.GetBytes("integration test page"), CancellationToken.None);

        handle.JobId.ShouldBeGreaterThan(0);

        await client.CancelAsync(handle.JobId, CancellationToken.None);
    }
}
```

> `SkippableFact`/`Skip` come from `Xunit.SkippableFact`. If the test project does not already reference it, either add the package or convert this to a plain `[Fact]` that early-returns when `PRINTER_TEST_URI` is unset. Verify which the repo already uses before adding a new dependency.

- [ ] **Step 2: Commit**

```bash
git add Tests/Integration/Printing/IppPrinterClientIntegrationTests.cs
git commit -m "test(printer): add opt-in live CUPS integration test"
```

---

## Done — Definition of Done

- `dotnet build` succeeds for the whole solution including `McpServerPrinter`.
- All `Printing` unit tests pass.
- `docker compose config` validates with the `mcp-printer` service and `printer-spool` volume.
- The `jonas` agent lists `http://mcp-printer:8080/mcp`; on session start the agent mounts `/print-queue`.
- Copying a file into `/print-queue` submits it to the printer; removing it before completion cancels it; `move`/`exec` are unsupported; finished jobs disappear from the listing.
```
