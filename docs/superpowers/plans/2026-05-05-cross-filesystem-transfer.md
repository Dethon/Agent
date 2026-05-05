# Cross-Filesystem Transfer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `VfsCopyTool` and extend `VfsMoveTool` so the agent can transfer files (text or binary, single file or directory tree) between any two virtual mounts in a single tool call.

**Architecture:** Three new MCP tools per filesystem-exposing server (`fs_copy`, `fs_blob_read`, `fs_blob_write`), three new methods on `IFileSystemBackend` (`CopyAsync`, `OpenReadStreamAsync`, `WriteFromStreamAsync`), and two domain tools that dispatch on (same-FS vs cross-FS) × (file vs directory) × (copy vs move). Same-FS operations delegate to the backend's native primitive; cross-FS operations stream through the agent process via the new blob tools. Best-effort directory transfer with per-entry result accumulation.

**Tech Stack:** .NET 10, ModelContextProtocol SDK, xUnit, Shouldly.

**Spec:** `docs/superpowers/specs/2026-05-05-cross-filesystem-transfer-design.md`

---

## Pre-flight

- [ ] **Step 0: Confirm working directory and clean tree**

Run: `git status`
Expected: clean working tree on the development branch (or worktree).

If subagent-driven execution is being used, ensure a worktree was created (per memory: worktree before subagent dev).

---

## Phase 1 — Domain base tools for new MCP server-side ops

These are pure domain classes that `McpServer{Vault,Sandbox,Library}` will inherit from. They live alongside existing `MoveTool` / `TextCreateTool`. Each tool validates the path is rooted under the configured server root and respects `allowedExtensions` where applicable (matching the existing pattern).

### Task 1: `CopyTool` base class

**Files:**
- Create: `Domain/Tools/Files/CopyTool.cs`
- Create: `Tests/Unit/Domain/Tools/Files/CopyToolTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class CopyToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableCopyTool _tool;

    public CopyToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"copytool-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableCopyTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Run_FileToNewFile_CopiesContent()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "hello");

        var result = _tool.TestRun("src.txt", "dst.txt", overwrite: false, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst.txt")).ShouldBe("hello");
        result["status"]!.GetValue<string>().ShouldBe("copied");
        result["bytes"]!.GetValue<long>().ShouldBe(5);
    }

    [Fact]
    public void Run_DestinationExistsAndOverwriteFalse_Throws()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "dst.txt"), "y");

        Should.Throw<IOException>(() =>
            _tool.TestRun("src.txt", "dst.txt", overwrite: false, createDirectories: true));
    }

    [Fact]
    public void Run_DestinationExistsAndOverwriteTrue_Replaces()
    {
        File.WriteAllText(Path.Combine(_root, "src.txt"), "new");
        File.WriteAllText(Path.Combine(_root, "dst.txt"), "old");

        _tool.TestRun("src.txt", "dst.txt", overwrite: true, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst.txt")).ShouldBe("new");
    }

    [Fact]
    public void Run_DirectorySource_CopiesRecursively()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "sub"));
        File.WriteAllText(Path.Combine(_root, "src", "a.txt"), "A");
        File.WriteAllText(Path.Combine(_root, "src", "sub", "b.txt"), "B");

        _tool.TestRun("src", "dst", overwrite: false, createDirectories: true);

        File.ReadAllText(Path.Combine(_root, "dst", "a.txt")).ShouldBe("A");
        File.ReadAllText(Path.Combine(_root, "dst", "sub", "b.txt")).ShouldBe("B");
    }

    [Fact]
    public void Run_PathOutsideRoot_Throws()
    {
        Should.Throw<UnauthorizedAccessException>(() =>
            _tool.TestRun("../escape.txt", "dst.txt", overwrite: false, createDirectories: true));
    }

    private class TestableCopyTool(string root) : CopyTool(root)
    {
        public JsonNode TestRun(string source, string destination, bool overwrite, bool createDirectories)
            => Run(source, destination, overwrite, createDirectories);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CopyToolTests" --nologo`
Expected: FAIL with compilation error (`CopyTool` not found).

- [ ] **Step 3: Implement `CopyTool`**

```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Files;

public class CopyTool(string rootPath)
{
    protected const string Description = """
        Copies a file or directory within this filesystem.
        Both arguments can be absolute paths under the filesystem root, or relative paths
        (resolved against the root). Source must exist; if destination exists, overwrite must be true.
        Parent directories are created automatically when createDirectories=true (default).
        """;

    protected JsonNode Run(string sourcePath, string destinationPath, bool overwrite, bool createDirectories)
    {
        var src = ResolveAndValidate(sourcePath);
        var dst = ResolveAndValidate(destinationPath);

        if (!File.Exists(src) && !Directory.Exists(src))
        {
            throw new IOException($"Source path does not exist: {sourcePath}");
        }

        if (createDirectories)
        {
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        long bytes;
        if (File.Exists(src))
        {
            if (File.Exists(dst) && !overwrite)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            File.Copy(src, dst, overwrite);
            bytes = new FileInfo(dst).Length;
        }
        else
        {
            if (Directory.Exists(dst) && !overwrite)
            {
                throw new IOException($"Destination already exists: {destinationPath}");
            }

            bytes = CopyDirectoryRecursive(src, dst, overwrite);
        }

        return new JsonObject
        {
            ["status"] = "copied",
            ["source"] = sourcePath,
            ["destination"] = destinationPath,
            ["bytes"] = bytes
        };
    }

    private static long CopyDirectoryRecursive(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);
        var fileBytes = Directory.EnumerateFiles(source).Sum(f =>
        {
            var target = Path.Combine(destination, Path.GetFileName(f));
            File.Copy(f, target, overwrite);
            return new FileInfo(target).Length;
        });
        var dirBytes = Directory.EnumerateDirectories(source).Sum(d =>
            CopyDirectoryRecursive(d, Path.Combine(destination, Path.GetFileName(d)), overwrite));
        return fileBytes + dirBytes;
    }

    private string ResolveAndValidate(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{nameof(CopyTool)} path must not contain '..' segments.");
        }

        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootPath, normalized));

        var canonicalRoot = Path.GetFullPath(rootPath);
        return fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new UnauthorizedAccessException($"Access denied: path must be within {canonicalRoot}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~CopyToolTests" --nologo`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Files/CopyTool.cs Tests/Unit/Domain/Tools/Files/CopyToolTests.cs
git commit -m "feat: add CopyTool domain base class for cross-FS transfer"
```

---

### Task 2: `BlobReadTool` base class

**Files:**
- Create: `Domain/Tools/Files/BlobReadTool.cs`
- Create: `Tests/Unit/Domain/Tools/Files/BlobReadToolTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class BlobReadToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableBlobReadTool _tool;

    public BlobReadToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"blobread-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableBlobReadTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Run_ReadsChunkAndReportsEofWhenFullyRead()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), bytes);

        var result = _tool.TestRun("blob.bin", offset: 0, length: 200);

        var b64 = result["contentBase64"]!.GetValue<string>();
        Convert.FromBase64String(b64).ShouldBe(bytes);
        result["eof"]!.GetValue<bool>().ShouldBeTrue();
        result["totalBytes"]!.GetValue<long>().ShouldBe(100);
    }

    [Fact]
    public void Run_PartialReadReportsNotEof()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), bytes);

        var result = _tool.TestRun("blob.bin", offset: 0, length: 60);

        Convert.FromBase64String(result["contentBase64"]!.GetValue<string>()).Length.ShouldBe(60);
        result["eof"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Run_OffsetReadsFromMiddle()
    {
        var bytes = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), bytes);

        var result = _tool.TestRun("blob.bin", offset: 50, length: 30);

        var got = Convert.FromBase64String(result["contentBase64"]!.GetValue<string>());
        got.ShouldBe(bytes[50..80]);
        result["eof"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Run_MissingFile_Throws()
    {
        Should.Throw<FileNotFoundException>(() => _tool.TestRun("missing.bin", 0, 100));
    }

    [Fact]
    public void Run_LengthClampedToCap()
    {
        File.WriteAllBytes(Path.Combine(_root, "blob.bin"), new byte[10]);

        var result = _tool.TestRun("blob.bin", offset: 0, length: 10_000_000);

        // Tool should refuse over-cap reads, returning at most the cap (256 KiB).
        Convert.FromBase64String(result["contentBase64"]!.GetValue<string>()).Length.ShouldBe(10);
    }

    private class TestableBlobReadTool(string root) : BlobReadTool(root)
    {
        public JsonNode TestRun(string path, long offset, int length)
            => Run(path, offset, length);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BlobReadToolTests" --nologo`
Expected: FAIL (`BlobReadTool` not found).

- [ ] **Step 3: Implement `BlobReadTool`**

```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Files;

public class BlobReadTool(string rootPath)
{
    public const int MaxChunkSizeBytes = 256 * 1024;

    protected const string Description = """
        Reads a chunk of raw bytes from a file as base64. Used by the agent's cross-filesystem
        transfer machinery to stream binary content. `length` is clamped to 256 KiB per call.
        Returns { contentBase64, eof, totalBytes }.
        """;

    protected JsonNode Run(string path, long offset, int length)
    {
        var resolved = ResolveAndValidate(path);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"File not found: {path}");
        }

        var info = new FileInfo(resolved);
        var clampedLength = Math.Min(length, MaxChunkSizeBytes);
        var available = Math.Max(0, info.Length - offset);
        var toRead = (int)Math.Min(clampedLength, available);

        var buffer = new byte[toRead];
        if (toRead > 0)
        {
            using var stream = File.OpenRead(resolved);
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < toRead)
            {
                var n = stream.Read(buffer, read, toRead - read);
                if (n == 0) break;
                read += n;
            }
        }

        var eof = offset + toRead >= info.Length;
        return new JsonObject
        {
            ["contentBase64"] = Convert.ToBase64String(buffer),
            ["eof"] = eof,
            ["totalBytes"] = info.Length
        };
    }

    private string ResolveAndValidate(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{nameof(BlobReadTool)} path must not contain '..' segments.");
        }

        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootPath, normalized));

        var canonicalRoot = Path.GetFullPath(rootPath);
        return fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new UnauthorizedAccessException($"Access denied: path must be within {canonicalRoot}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BlobReadToolTests" --nologo`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Files/BlobReadTool.cs Tests/Unit/Domain/Tools/Files/BlobReadToolTests.cs
git commit -m "feat: add BlobReadTool for chunked binary reads"
```

---

### Task 3: `BlobWriteTool` base class

**Files:**
- Create: `Domain/Tools/Files/BlobWriteTool.cs`
- Create: `Tests/Unit/Domain/Tools/Files/BlobWriteToolTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Tools.Files;
using Shouldly;

namespace Tests.Unit.Domain.Tools.Files;

public class BlobWriteToolTests : IDisposable
{
    private readonly string _root;
    private readonly TestableBlobWriteTool _tool;

    public BlobWriteToolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"blobwrite-{Guid.NewGuid()}");
        Directory.CreateDirectory(_root);
        _tool = new TestableBlobWriteTool(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void Run_FirstChunkCreatesFile()
    {
        var data = new byte[] { 1, 2, 3, 4 };
        var b64 = Convert.ToBase64String(data);

        var result = _tool.TestRun("out.bin", b64, offset: 0, overwrite: false, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(data);
        result["bytesWritten"]!.GetValue<int>().ShouldBe(4);
        result["totalBytes"]!.GetValue<long>().ShouldBe(4);
    }

    [Fact]
    public void Run_AppendsAtOffset()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1, 2 });
        var b64 = Convert.ToBase64String(new byte[] { 3, 4 });

        var result = _tool.TestRun("out.bin", b64, offset: 2, overwrite: true, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 1, 2, 3, 4 });
        result["totalBytes"]!.GetValue<long>().ShouldBe(4);
    }

    [Fact]
    public void Run_OffsetZeroOverwriteFalseAndExists_Throws()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1 });
        var b64 = Convert.ToBase64String(new byte[] { 9 });

        Should.Throw<IOException>(() =>
            _tool.TestRun("out.bin", b64, offset: 0, overwrite: false, createDirectories: true));
    }

    [Fact]
    public void Run_OffsetZeroOverwriteTrueAndExists_Replaces()
    {
        File.WriteAllBytes(Path.Combine(_root, "out.bin"), new byte[] { 1, 2, 3 });
        var b64 = Convert.ToBase64String(new byte[] { 9 });

        _tool.TestRun("out.bin", b64, offset: 0, overwrite: true, createDirectories: true);

        File.ReadAllBytes(Path.Combine(_root, "out.bin")).ShouldBe(new byte[] { 9 });
    }

    [Fact]
    public void Run_CreatesParentDirectories()
    {
        var b64 = Convert.ToBase64String(new byte[] { 1 });

        _tool.TestRun("nested/dir/out.bin", b64, offset: 0, overwrite: false, createDirectories: true);

        File.Exists(Path.Combine(_root, "nested", "dir", "out.bin")).ShouldBeTrue();
    }

    private class TestableBlobWriteTool(string root) : BlobWriteTool(root)
    {
        public JsonNode TestRun(string path, string contentBase64, long offset, bool overwrite, bool createDirectories)
            => Run(path, contentBase64, offset, overwrite, createDirectories);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BlobWriteToolTests" --nologo`
Expected: FAIL (`BlobWriteTool` not found).

- [ ] **Step 3: Implement `BlobWriteTool`**

```csharp
using System.Text.Json.Nodes;

namespace Domain.Tools.Files;

public class BlobWriteTool(string rootPath)
{
    protected const string Description = """
        Writes a chunk of raw bytes (base64-encoded) to a file at the given offset.
        Used by the agent's cross-filesystem transfer machinery to stream binary content.
        offset=0 creates (or, with overwrite=true, truncates) the file; later calls append at offset.
        Returns { path, bytesWritten, totalBytes }.
        """;

    protected JsonNode Run(string path, string contentBase64, long offset, bool overwrite, bool createDirectories)
    {
        var resolved = ResolveAndValidate(path);
        var bytes = Convert.FromBase64String(contentBase64);

        if (createDirectories)
        {
            var parent = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        if (offset == 0)
        {
            if (File.Exists(resolved) && !overwrite)
            {
                throw new IOException($"File already exists: {path}");
            }
            File.WriteAllBytes(resolved, bytes);
        }
        else
        {
            using var stream = new FileStream(resolved, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.Seek(offset, SeekOrigin.Begin);
            stream.Write(bytes, 0, bytes.Length);
        }

        var info = new FileInfo(resolved);
        return new JsonObject
        {
            ["path"] = path,
            ["bytesWritten"] = bytes.Length,
            ["totalBytes"] = info.Length
        };
    }

    private string ResolveAndValidate(string path)
    {
        if (path.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{nameof(BlobWriteTool)} path must not contain '..' segments.");
        }

        var normalized = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(rootPath, normalized));

        var canonicalRoot = Path.GetFullPath(rootPath);
        return fullPath.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : throw new UnauthorizedAccessException($"Access denied: path must be within {canonicalRoot}");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~BlobWriteToolTests" --nologo`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Files/BlobWriteTool.cs Tests/Unit/Domain/Tools/Files/BlobWriteToolTests.cs
git commit -m "feat: add BlobWriteTool for chunked binary writes"
```

---

## Phase 2 — Wire MCP server tools

For each of the three filesystem-exposing servers (Vault, Sandbox, Library), add the three new tool wrappers and register them in `ConfigModule.cs`. The wrappers follow the existing pattern from `FsMoveTool` / `FsCreateTool`.

### Task 4: Vault MCP tools (`fs_copy`, `fs_blob_read`, `fs_blob_write`)

**Files:**
- Create: `McpServerVault/McpTools/FsCopyTool.cs`
- Create: `McpServerVault/McpTools/FsBlobReadTool.cs`
- Create: `McpServerVault/McpTools/FsBlobWriteTool.cs`
- Modify: `McpServerVault/Modules/ConfigModule.cs:50-57`

- [ ] **Step 1: Add `FsCopyTool` wrapper**

```csharp
using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsCopyTool(McpSettings settings) : CopyTool(settings.VaultPath)
{
    [McpServerTool(Name = "fs_copy")]
    [Description(Description)]
    public CallToolResult McpRun(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}
```

- [ ] **Step 2: Add `FsBlobReadTool` wrapper**

```csharp
using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsBlobReadTool(McpSettings settings) : BlobReadTool(settings.VaultPath)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        long offset = 0,
        int length = MaxChunkSizeBytes)
    {
        return ToolResponse.Create(Run(path, offset, length));
    }
}
```

- [ ] **Step 3: Add `FsBlobWriteTool` wrapper**

```csharp
using System.ComponentModel;
using Domain.Tools.Files;
using Infrastructure.Utils;
using McpServerVault.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerVault.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(McpSettings settings) : BlobWriteTool(settings.VaultPath)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        string contentBase64,
        long offset = 0,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, contentBase64, offset, overwrite, createDirectories));
    }
}
```

- [ ] **Step 4: Register the three tools**

In `McpServerVault/Modules/ConfigModule.cs`, locate the chained `.WithTools<...>()` calls (around line 50-57) and add:

```csharp
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
```

(Append after `.WithTools<FsInfoTool>()`.)

- [ ] **Step 5: Build to verify**

Run: `dotnet build McpServerVault/McpServerVault.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add McpServerVault/McpTools/FsCopyTool.cs McpServerVault/McpTools/FsBlobReadTool.cs McpServerVault/McpTools/FsBlobWriteTool.cs McpServerVault/Modules/ConfigModule.cs
git commit -m "feat: expose fs_copy, fs_blob_read, fs_blob_write on Vault MCP server"
```

---

### Task 5: Sandbox MCP tools

**Files:**
- Create: `McpServerSandbox/McpTools/FsCopyTool.cs`
- Create: `McpServerSandbox/McpTools/FsBlobReadTool.cs`
- Create: `McpServerSandbox/McpTools/FsBlobWriteTool.cs`
- Modify: `McpServerSandbox/Modules/ConfigModule.cs:60-68`

The sandbox base path comes from `LibraryPathConfig` (per the existing `FsMoveTool`'s constructor `(IFileSystemClient, LibraryPathConfig)`). Confirm by reading the existing `McpServerSandbox/McpTools/FsMoveTool.cs` — use the same DI source for path.

- [ ] **Step 1: Add `FsCopyTool` wrapper**

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsCopyTool(LibraryPathConfig libraryPath) : CopyTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_copy")]
    [Description(Description)]
    public CallToolResult McpRun(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}
```

- [ ] **Step 2: Add `FsBlobReadTool` wrapper**

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsBlobReadTool(LibraryPathConfig libraryPath) : BlobReadTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        long offset = 0,
        int length = MaxChunkSizeBytes)
    {
        return ToolResponse.Create(Run(path, offset, length));
    }
}
```

- [ ] **Step 3: Add `FsBlobWriteTool` wrapper**

```csharp
using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(LibraryPathConfig libraryPath) : BlobWriteTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description(Description)]
    public CallToolResult McpRun(
        string path,
        string contentBase64,
        long offset = 0,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, contentBase64, offset, overwrite, createDirectories));
    }
}
```

- [ ] **Step 4: Register the three tools**

Append to the chained `.WithTools<...>()` calls in `McpServerSandbox/Modules/ConfigModule.cs` (after `.WithTools<FsInfoTool>()`):

```csharp
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build McpServerSandbox/McpServerSandbox.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add McpServerSandbox/McpTools/FsCopyTool.cs McpServerSandbox/McpTools/FsBlobReadTool.cs McpServerSandbox/McpTools/FsBlobWriteTool.cs McpServerSandbox/Modules/ConfigModule.cs
git commit -m "feat: expose fs_copy, fs_blob_read, fs_blob_write on Sandbox MCP server"
```

---

### Task 6: Library MCP tools

**Files:**
- Create: `McpServerLibrary/McpTools/FsCopyTool.cs`
- Create: `McpServerLibrary/McpTools/FsBlobReadTool.cs`
- Create: `McpServerLibrary/McpTools/FsBlobWriteTool.cs`
- Modify: `McpServerLibrary/Modules/ConfigModule.cs:88-90`

The library path source: read existing `McpServerLibrary/McpTools/FsMoveTool.cs` to confirm constructor — use the same source.

- [ ] **Step 1: Add `FsCopyTool` wrapper**

Use the same code as Task 5 Step 1 but with namespace `McpServerLibrary.McpTools`. If `McpServerLibrary/McpTools/FsMoveTool.cs` uses `LibraryPathConfig`, use the same; if it uses a different config (e.g. `McpSettings`), match it.

- [ ] **Step 2: Add `FsBlobReadTool` wrapper**

Same as Task 5 Step 2 with namespace `McpServerLibrary.McpTools`, matching the constructor source from Step 1.

- [ ] **Step 3: Add `FsBlobWriteTool` wrapper**

Same as Task 5 Step 3 with namespace `McpServerLibrary.McpTools`, matching the constructor source from Step 1.

- [ ] **Step 4: Register the three tools**

Append to the `.WithTools<...>()` chain in `McpServerLibrary/Modules/ConfigModule.cs` (after `.WithTools<FsInfoTool>()`):

```csharp
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build McpServerLibrary/McpServerLibrary.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add McpServerLibrary/McpTools/FsCopyTool.cs McpServerLibrary/McpTools/FsBlobReadTool.cs McpServerLibrary/McpTools/FsBlobWriteTool.cs McpServerLibrary/Modules/ConfigModule.cs
git commit -m "feat: expose fs_copy, fs_blob_read, fs_blob_write on Library MCP server"
```

---

## Phase 3 — Backend interface + MCP backend implementation

### Task 7: Extend `IFileSystemBackend` and stub `McpFileSystemBackend`

**Files:**
- Modify: `Domain/Contracts/IFileSystemBackend.cs`
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`

- [ ] **Step 1: Add three methods to the interface**

In `Domain/Contracts/IFileSystemBackend.cs`, add after `ExecAsync`:

```csharp
    Task<JsonNode> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct);

    Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct);

    Task WriteFromStreamAsync(string path, Stream content,
        bool overwrite, bool createDirectories, CancellationToken ct);
```

- [ ] **Step 2: Stub the methods in `McpFileSystemBackend`**

Add to `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` (anywhere alongside the other methods):

```csharp
    public async Task<JsonNode> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        return await CallToolAsync("fs_copy", new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }, ct);
    }

    public Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct)
        => throw new NotImplementedException();

    public Task WriteFromStreamAsync(string path, Stream content,
        bool overwrite, bool createDirectories, CancellationToken ct)
        => throw new NotImplementedException();
```

- [ ] **Step 3: Build to verify the solution still compiles**

Run: `dotnet build Agent.sln --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Domain/Contracts/IFileSystemBackend.cs Infrastructure/Agents/Mcp/McpFileSystemBackend.cs
git commit -m "feat: add CopyAsync + streaming methods to IFileSystemBackend"
```

---

### Task 8: Integration test for `McpFileSystemBackend.CopyAsync`

**Files:**
- Modify: `Tests/Integration/Fixtures/MultiFileSystemFixture.cs`
- Create: `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendCopyTests.cs`

- [ ] **Step 1: Wire the new tools into `MultiFileSystemFixture`**

In `Tests/Integration/Fixtures/MultiFileSystemFixture.cs`, inside `BuildVaultHost`, append to the `.WithTools<...>()` chain:

```csharp
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
```

These reference `McpServerVault.McpTools.{FsCopyTool, FsBlobReadTool, FsBlobWriteTool}` (added in Task 4). Add `using McpServerVault.McpTools;` if not already present.

- [ ] **Step 2: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.DTOs;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure.Mcp;

[Collection("MultiFileSystem")]
public class McpFileSystemBackendCopyTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task CopyAsync_FileWithinSameBackend_CopiesContent()
    {
        fx.CreateLibraryFile("note.md", "hello");
        var backend = await CreateBackend(fx.LibraryEndpoint, "library");

        var result = await backend.CopyAsync("note.md", "note-copy.md",
            overwrite: false, createDirectories: true, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("copied");
        File.ReadAllText(Path.Combine(fx.LibraryPath, "note-copy.md")).ShouldBe("hello");
    }

    [Fact]
    public async Task CopyAsync_DirectoryWithinSameBackend_CopiesRecursively()
    {
        fx.CreateLibraryFile("src/a.md", "A");
        fx.CreateLibraryFile("src/sub/b.md", "B");
        var backend = await CreateBackend(fx.LibraryEndpoint, "library");

        await backend.CopyAsync("src", "dst",
            overwrite: false, createDirectories: true, CancellationToken.None);

        File.ReadAllText(Path.Combine(fx.LibraryPath, "dst", "a.md")).ShouldBe("A");
        File.ReadAllText(Path.Combine(fx.LibraryPath, "dst", "sub", "b.md")).ShouldBe("B");
    }

    private static async Task<McpFileSystemBackend> CreateBackend(string endpoint, string name)
    {
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
        return new McpFileSystemBackend(client, name);
    }
}

[CollectionDefinition("MultiFileSystem")]
public class MultiFileSystemCollection : ICollectionFixture<MultiFileSystemFixture> { }
```

(If a collection definition for `MultiFileSystemFixture` already exists, omit the `CollectionDefinition` declaration.)

- [ ] **Step 3: Run test — should pass since fs_copy and CopyAsync are wired**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpFileSystemBackendCopyTests" --nologo`
Expected: 2 passed.

If this fails because Library extension allowlist rejects `.md`, confirm `MultiFileSystemFixture.BuildVaultHost` includes `.md` in `AllowedExtensions` (it does as of writing: `[".md", ".txt", ".json"]`).

- [ ] **Step 4: Commit**

```bash
git add Tests/Integration/Fixtures/MultiFileSystemFixture.cs Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendCopyTests.cs
git commit -m "test: integration coverage for McpFileSystemBackend.CopyAsync"
```

---

### Task 9: Implement `OpenReadStreamAsync` (chunked materialising stream)

**Files:**
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`
- Create: `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs`

For v1, materialise the file by looping `fs_blob_read` calls until `eof`, then return a `MemoryStream`. This keeps the implementation simple. A lazy chunked stream is a future optimisation.

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Infrastructure.Mcp;

[Collection("MultiFileSystem")]
public class McpFileSystemBackendStreamTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task OpenReadStreamAsync_LargeFile_ReadsAllBytesCorrectly()
    {
        // 600 KiB triggers at least 3 chunks (256 KiB chunk cap).
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "big.bin"), bytes);

        var backend = await CreateBackend(fx.LibraryEndpoint, "library");

        await using var stream = await backend.OpenReadStreamAsync("big.bin", CancellationToken.None);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);

        ms.ToArray().ShouldBe(bytes);
    }

    private static async Task<McpFileSystemBackend> CreateBackend(string endpoint, string name)
    {
        var client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
        return new McpFileSystemBackend(client, name);
    }
}
```

Note: the test writes a `.bin` file, which is not in the fixture's `AllowedExtensions`. The blob read tool does not enforce allowed extensions (it's a backend-level read, not a domain create), so this should work. If fixture validation rejects it, extend `AllowedExtensions` in the fixture to include `.bin` as part of this task.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenReadStreamAsync_LargeFile" --nologo`
Expected: FAIL with `NotImplementedException`.

- [ ] **Step 3: Implement `OpenReadStreamAsync`**

Replace the stub in `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`:

```csharp
    public async Task<Stream> OpenReadStreamAsync(string path, CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
        var buffer = new MemoryStream();
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

            var b64 = node["contentBase64"]!.GetValue<string>();
            var bytes = Convert.FromBase64String(b64);
            buffer.Write(bytes, 0, bytes.Length);
            offset += bytes.Length;

            if (node["eof"]!.GetValue<bool>()) break;
            if (bytes.Length == 0) break; // safety: prevent infinite loop on misbehaving server
        }

        buffer.Position = 0;
        return buffer;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenReadStreamAsync_LargeFile" --nologo`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/Mcp/McpFileSystemBackend.cs Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs Tests/Integration/Fixtures/MultiFileSystemFixture.cs
git commit -m "feat: McpFileSystemBackend.OpenReadStreamAsync via chunked fs_blob_read"
```

---

### Task 10: Implement `WriteFromStreamAsync`

**Files:**
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`
- Modify: `Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs`

- [ ] **Step 1: Add the failing test**

Append to `McpFileSystemBackendStreamTests`:

```csharp
    [Fact]
    public async Task WriteFromStreamAsync_LargeFile_WritesAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        var backend = await CreateBackend(fx.NotesEndpoint, "notes");

        await using var input = new MemoryStream(bytes);
        await backend.WriteFromStreamAsync("written.bin", input,
            overwrite: false, createDirectories: true, CancellationToken.None);

        File.ReadAllBytes(Path.Combine(fx.NotesPath, "written.bin")).ShouldBe(bytes);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WriteFromStreamAsync_LargeFile" --nologo`
Expected: FAIL with `NotImplementedException`.

- [ ] **Step 3: Implement `WriteFromStreamAsync`**

Replace the stub:

```csharp
    public async Task WriteFromStreamAsync(string path, Stream content,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
        var buffer = new byte[chunkSize];
        long offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = await content.ReadAsync(buffer.AsMemory(0, chunkSize), ct);
            if (read == 0) break;

            var chunk = read == chunkSize ? buffer : buffer[..read];
            var node = await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = Convert.ToBase64String(chunk),
                ["offset"] = offset,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }, ct);

            if (node is JsonObject obj && obj["ok"] is JsonValue ok && !ok.GetValue<bool>())
            {
                throw new IOException($"fs_blob_write failed: {obj["message"]?.GetValue<string>()}");
            }

            offset += read;
        }

        // If the input stream was empty, ensure the file is created.
        if (offset == 0)
        {
            await CallToolAsync("fs_blob_write", new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = "",
                ["offset"] = 0L,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }, ct);
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WriteFromStreamAsync_LargeFile" --nologo`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/Mcp/McpFileSystemBackend.cs Tests/Integration/Infrastructure/Mcp/McpFileSystemBackendStreamTests.cs
git commit -m "feat: McpFileSystemBackend.WriteFromStreamAsync via chunked fs_blob_write"
```

---

## Phase 4 — Domain VFS tools

### Task 11: `VfsCopyTool` — file source

**Files:**
- Create: `Domain/Tools/FileSystem/VfsCopyTool.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsCopyToolTests
{
    [Fact]
    public async Task RunAsync_SameFsFile_DelegatesToBackendCopyAsync()
    {
        var backend = Substitute.For<IFileSystemBackend>();
        backend.FilesystemName.Returns("vault");
        backend.InfoAsync("notes/a.md", Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["type"] = "file", ["bytes"] = 42 });
        backend.CopyAsync("notes/a.md", "notes/b.md", false, true, Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["status"] = "copied", ["bytes"] = 42 });

        var registry = Substitute.For<IVirtualFileSystemRegistry>();
        registry.Resolve("/vault/notes/a.md").Returns(new FileSystemResolution(backend, "notes/a.md"));
        registry.Resolve("/vault/notes/b.md").Returns(new FileSystemResolution(backend, "notes/b.md"));

        var tool = new VfsCopyTool(registry);
        var result = await tool.RunAsync("/vault/notes/a.md", "/vault/notes/b.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["source"]!.GetValue<string>().ShouldBe("/vault/notes/a.md");
        result["destination"]!.GetValue<string>().ShouldBe("/vault/notes/b.md");
        await backend.Received(1).CopyAsync("notes/a.md", "notes/b.md", false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsThroughAgent()
    {
        var src = Substitute.For<IFileSystemBackend>();
        src.FilesystemName.Returns("vault");
        src.InfoAsync("a.md", Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["type"] = "file", ["bytes"] = 5 });
        src.OpenReadStreamAsync("a.md", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello")));

        var dst = Substitute.For<IFileSystemBackend>();
        dst.FilesystemName.Returns("sandbox");

        var registry = Substitute.For<IVirtualFileSystemRegistry>();
        registry.Resolve("/vault/a.md").Returns(new FileSystemResolution(src, "a.md"));
        registry.Resolve("/sandbox/a.md").Returns(new FileSystemResolution(dst, "a.md"));

        var tool = new VfsCopyTool(registry);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        await dst.Received(1).WriteFromStreamAsync(
            "a.md", Arg.Any<Stream>(), false, true, Arg.Any<CancellationToken>());
        await src.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsCopyToolTests" --nologo`
Expected: FAIL (`VfsCopyTool` not found).

- [ ] **Step 3: Implement file-source dispatch**

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsCopyTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "copy";
    public const string Name = "copy";

    public const string ToolDescription = """
        Copies a file or directory between any two virtual paths, including across different filesystems.
        Same-filesystem copies use the backend's native primitive. Cross-filesystem copies stream content
        through the agent. Directory sources are recursed automatically. Best-effort: per-file failures
        do not abort the rest of the transfer.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")] string sourcePath,
        [Description("Virtual path to destination")] string destinationPath,
        [Description("Overwrite destination if it exists (default: false)")] bool overwrite = false,
        [Description("Create destination parent directories if missing (default: true)")] bool createDirectories = true,
        CancellationToken cancellationToken = default)
    {
        var src = registry.Resolve(sourcePath);
        var dst = registry.Resolve(destinationPath);

        var info = await src.Backend.InfoAsync(src.RelativePath, cancellationToken);
        var isDirectory = info["type"]?.GetValue<string>() == "directory";

        if (isDirectory)
        {
            return await TransferDirectoryAsync(src, dst, sourcePath, destinationPath,
                overwrite, createDirectories, deleteSource: false, cancellationToken);
        }

        return await TransferFileAsync(src, dst, sourcePath, destinationPath,
            overwrite, createDirectories, deleteSource: false, cancellationToken);
    }

    internal static async Task<JsonNode> TransferFileAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite, bool createDirectories, bool deleteSource,
        CancellationToken ct)
    {
        if (ReferenceEquals(src.Backend, dst.Backend))
        {
            JsonNode native;
            if (deleteSource)
            {
                native = await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct);
            }
            else
            {
                native = await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath,
                    overwrite, createDirectories, ct);
            }
            return new JsonObject
            {
                ["status"] = "ok",
                ["source"] = srcVirtual,
                ["destination"] = dstVirtual,
                ["bytes"] = native["bytes"]?.DeepClone()
            };
        }

        long bytes;
        await using (var stream = await src.Backend.OpenReadStreamAsync(src.RelativePath, ct))
        {
            await dst.Backend.WriteFromStreamAsync(dst.RelativePath, stream, overwrite, createDirectories, ct);
            bytes = stream.CanSeek ? stream.Length : -1;
        }

        if (deleteSource)
        {
            await src.Backend.DeleteAsync(src.RelativePath, ct);
        }

        return new JsonObject
        {
            ["status"] = "ok",
            ["source"] = srcVirtual,
            ["destination"] = dstVirtual,
            ["bytes"] = bytes
        };
    }

    internal static Task<JsonNode> TransferDirectoryAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite, bool createDirectories, bool deleteSource,
        CancellationToken ct)
    {
        // Implemented in Task 13.
        throw new NotImplementedException();
    }
}
```

- [ ] **Step 4: Run test to verify file tests pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsCopyToolTests" --nologo`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/VfsCopyTool.cs Tests/Unit/Domain/Tools/FileSystem/VfsCopyToolTests.cs
git commit -m "feat: VfsCopyTool for same-FS and cross-FS file copies"
```

---

### Task 12: Extend `VfsMoveTool` — drop cross-FS rejection, add streaming path

**Files:**
- Modify: `Domain/Tools/FileSystem/VfsMoveTool.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/VfsMoveToolCrossFsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.FileSystem;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsMoveToolCrossFsTests
{
    [Fact]
    public async Task RunAsync_CrossFsFile_StreamsAndDeletesSource()
    {
        var src = Substitute.For<IFileSystemBackend>();
        src.FilesystemName.Returns("vault");
        src.InfoAsync("a.md", Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["type"] = "file", ["bytes"] = 5 });
        src.OpenReadStreamAsync("a.md", Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        src.DeleteAsync("a.md", Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["status"] = "deleted" });

        var dst = Substitute.For<IFileSystemBackend>();
        dst.FilesystemName.Returns("sandbox");

        var registry = Substitute.For<IVirtualFileSystemRegistry>();
        registry.Resolve("/vault/a.md").Returns(new FileSystemResolution(src, "a.md"));
        registry.Resolve("/sandbox/a.md").Returns(new FileSystemResolution(dst, "a.md"));

        var tool = new VfsMoveTool(registry);
        var result = await tool.RunAsync("/vault/a.md", "/sandbox/a.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        await dst.Received(1).WriteFromStreamAsync("a.md", Arg.Any<Stream>(),
            false, true, Arg.Any<CancellationToken>());
        await src.Received(1).DeleteAsync("a.md", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_SameFsFile_StillUsesNativeMoveAsync()
    {
        var backend = Substitute.For<IFileSystemBackend>();
        backend.FilesystemName.Returns("vault");
        backend.InfoAsync("a.md", Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["type"] = "file" });
        backend.MoveAsync("a.md", "b.md", Arg.Any<CancellationToken>())
            .Returns(new JsonObject { ["status"] = "moved" });

        var registry = Substitute.For<IVirtualFileSystemRegistry>();
        registry.Resolve("/vault/a.md").Returns(new FileSystemResolution(backend, "a.md"));
        registry.Resolve("/vault/b.md").Returns(new FileSystemResolution(backend, "b.md"));

        var tool = new VfsMoveTool(registry);
        await tool.RunAsync("/vault/a.md", "/vault/b.md");

        await backend.Received(1).MoveAsync("a.md", "b.md", Arg.Any<CancellationToken>());
        await backend.DidNotReceive().OpenReadStreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsMoveToolCrossFsTests" --nologo`
Expected: cross-FS test FAILS (returns `CrossFilesystem` envelope), same-FS test passes.

- [ ] **Step 3: Replace `VfsMoveTool.RunAsync`**

Replace the entire body of `Domain/Tools/FileSystem/VfsMoveTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsMoveTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "move";
    public const string Name = "move";

    public const string ToolDescription = """
        Moves and/or renames a file or directory. Source and destination can be on the same
        filesystem (atomic native move) or on different filesystems (streamed copy + delete; not atomic).
        Directory sources are handled recursively for cross-FS moves.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to source file or directory")] string sourcePath,
        [Description("Virtual path to destination")] string destinationPath,
        [Description("Overwrite destination if it exists (default: false)")] bool overwrite = false,
        [Description("Create destination parent directories if missing (default: true)")] bool createDirectories = true,
        CancellationToken cancellationToken = default)
    {
        var src = registry.Resolve(sourcePath);
        var dst = registry.Resolve(destinationPath);

        var info = await src.Backend.InfoAsync(src.RelativePath, cancellationToken);
        var isDirectory = info["type"]?.GetValue<string>() == "directory";

        if (isDirectory)
        {
            return await VfsCopyTool.TransferDirectoryAsync(src, dst, sourcePath, destinationPath,
                overwrite, createDirectories, deleteSource: true, cancellationToken);
        }

        return await VfsCopyTool.TransferFileAsync(src, dst, sourcePath, destinationPath,
            overwrite, createDirectories, deleteSource: true, cancellationToken);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsMoveTool" --nologo`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/VfsMoveTool.cs Tests/Unit/Domain/Tools/FileSystem/VfsMoveToolCrossFsTests.cs
git commit -m "feat: VfsMoveTool now supports cross-filesystem moves via streaming"
```

---

### Task 13: Directory recursion with best-effort and per-entry results

**Files:**
- Modify: `Domain/Tools/FileSystem/VfsCopyTool.cs`
- Create: `Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools;
using Domain.Tools.FileSystem;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsTransferDirectoryTests
{
    [Fact]
    public async Task TransferDirectoryAsync_CrossFsCopy_RecordsPerEntryResults()
    {
        var src = Substitute.For<IFileSystemBackend>();
        src.GlobAsync("src", "**/*", VfsGlobMode.Files, Arg.Any<CancellationToken>())
            .Returns(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "src/sub/b.md" }
            });
        src.OpenReadStreamAsync("src/a.md", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("A")));
        src.OpenReadStreamAsync("src/sub/b.md", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("BB")));

        var dst = Substitute.For<IFileSystemBackend>();

        var srcRes = new FileSystemResolution(src, "src");
        var dstRes = new FileSystemResolution(dst, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(2);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(0);
        result["entries"]!.AsArray().Count.ShouldBe(2);
        await dst.Received(1).WriteFromStreamAsync("dst/a.md", Arg.Any<Stream>(),
            false, true, Arg.Any<CancellationToken>());
        await dst.Received(1).WriteFromStreamAsync("dst/sub/b.md", Arg.Any<Stream>(),
            false, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TransferDirectoryAsync_PartialFailure_StatusIsPartial()
    {
        var src = Substitute.For<IFileSystemBackend>();
        src.GlobAsync("src", "**/*", VfsGlobMode.Files, Arg.Any<CancellationToken>())
            .Returns(new JsonArray
            {
                new JsonObject { ["path"] = "src/a.md" },
                new JsonObject { ["path"] = "src/b.md" }
            });
        src.OpenReadStreamAsync("src/a.md", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("A")));
        src.OpenReadStreamAsync("src/b.md", Arg.Any<CancellationToken>())
            .Returns<Task<Stream>>(_ => Task.FromException<Stream>(new IOException("boom")));

        var dst = Substitute.For<IFileSystemBackend>();
        var srcRes = new FileSystemResolution(src, "src");
        var dstRes = new FileSystemResolution(dst, "dst");

        var result = await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: false, CancellationToken.None);

        result["status"]!.GetValue<string>().ShouldBe("partial");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(1);
        result["summary"]!["failed"]!.GetValue<int>().ShouldBe(1);
    }

    [Fact]
    public async Task TransferDirectoryAsync_MoveOnSuccessfulCopy_DeletesSource()
    {
        var src = Substitute.For<IFileSystemBackend>();
        src.GlobAsync("src", "**/*", VfsGlobMode.Files, Arg.Any<CancellationToken>())
            .Returns(new JsonArray { new JsonObject { ["path"] = "src/a.md" } });
        src.OpenReadStreamAsync("src/a.md", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("A")));

        var dst = Substitute.For<IFileSystemBackend>();
        var srcRes = new FileSystemResolution(src, "src");
        var dstRes = new FileSystemResolution(dst, "dst");

        await VfsCopyTool.TransferDirectoryAsync(
            srcRes, dstRes, "/vault/src", "/sandbox/dst",
            overwrite: false, createDirectories: true, deleteSource: true, CancellationToken.None);

        await src.Received(1).DeleteAsync("src/a.md", Arg.Any<CancellationToken>());
    }
}
```

The test references `VfsGlobMode` from `Domain.DTOs`. Confirm by reading `Domain/DTOs/` if needed.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTransferDirectoryTests" --nologo`
Expected: FAIL with `NotImplementedException` from `TransferDirectoryAsync`.

- [ ] **Step 3: Implement `TransferDirectoryAsync`**

Replace the `throw new NotImplementedException();` body in `Domain/Tools/FileSystem/VfsCopyTool.cs`:

```csharp
    internal static async Task<JsonNode> TransferDirectoryAsync(
        FileSystemResolution src, FileSystemResolution dst,
        string srcVirtual, string dstVirtual,
        bool overwrite, bool createDirectories, bool deleteSource,
        CancellationToken ct)
    {
        // Same-FS shortcut: native copy/move handles directories atomically.
        if (ReferenceEquals(src.Backend, dst.Backend))
        {
            var native = deleteSource
                ? await src.Backend.MoveAsync(src.RelativePath, dst.RelativePath, ct)
                : await src.Backend.CopyAsync(src.RelativePath, dst.RelativePath, overwrite, createDirectories, ct);

            return new JsonObject
            {
                ["status"] = "ok",
                ["source"] = srcVirtual,
                ["destination"] = dstVirtual,
                ["bytes"] = native["bytes"]?.DeepClone()
            };
        }

        var glob = await src.Backend.GlobAsync(src.RelativePath, "**/*", Domain.DTOs.VfsGlobMode.Files, ct);
        var entries = glob is JsonArray arr ? arr : (glob["entries"] as JsonArray ?? new JsonArray());

        var perEntry = new JsonArray();
        var transferred = 0;
        var failed = 0;
        long totalBytes = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var srcRel = entry is JsonValue v ? v.GetValue<string>() : entry!["path"]!.GetValue<string>();
            var tail = srcRel.StartsWith(src.RelativePath, StringComparison.Ordinal)
                ? srcRel[src.RelativePath.Length..].TrimStart('/')
                : srcRel;
            var dstRel = string.IsNullOrEmpty(tail) ? dst.RelativePath : $"{dst.RelativePath.TrimEnd('/')}/{tail}";
            var dstVirtualEntry = $"{dstVirtual.TrimEnd('/')}/{tail}";
            var srcVirtualEntry = $"{srcVirtual.TrimEnd('/')}/{tail}";

            try
            {
                long bytes;
                await using (var stream = await src.Backend.OpenReadStreamAsync(srcRel, ct))
                {
                    await dst.Backend.WriteFromStreamAsync(dstRel, stream, overwrite, createDirectories, ct);
                    bytes = stream.CanSeek ? stream.Length : 0;
                }

                if (deleteSource)
                {
                    await src.Backend.DeleteAsync(srcRel, ct);
                }

                perEntry.Add(new JsonObject
                {
                    ["source"] = srcVirtualEntry,
                    ["destination"] = dstVirtualEntry,
                    ["status"] = "ok",
                    ["bytes"] = bytes
                });
                transferred++;
                totalBytes += bytes;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                perEntry.Add(new JsonObject
                {
                    ["source"] = srcVirtualEntry,
                    ["destination"] = dstVirtualEntry,
                    ["status"] = "failed",
                    ["error"] = ex.Message
                });
                failed++;
            }
        }

        var status = (transferred, failed) switch
        {
            (_, 0) => "ok",
            (0, _) => "failed",
            _ => "partial"
        };

        return new JsonObject
        {
            ["status"] = status,
            ["summary"] = new JsonObject
            {
                ["transferred"] = transferred,
                ["failed"] = failed,
                ["skipped"] = 0,
                ["totalBytes"] = totalBytes
            },
            ["entries"] = perEntry
        };
    }
```

Note: the exact shape of `GlobAsync`'s return value (array of strings vs array of objects with `path` field vs object with `entries` field) depends on the existing backend serialisation. Read `McpServerVault/McpTools/FsGlobTool.cs` (or whichever base it inherits from) to confirm the shape and adjust `srcRel` extraction accordingly.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsTransferDirectoryTests" --nologo`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/FileSystem/VfsCopyTool.cs Tests/Unit/Domain/Tools/FileSystem/VfsTransferDirectoryTests.cs
git commit -m "feat: cross-FS directory recursion with best-effort per-entry results"
```

---

### Task 14: Register `VfsCopyTool` in `FileSystemToolFeature`

**Files:**
- Modify: `Domain/Tools/FileSystem/FileSystemToolFeature.cs:11-16, 24-35`

- [ ] **Step 1: Add the tool key to `AllToolKeys`**

In `Domain/Tools/FileSystem/FileSystemToolFeature.cs`, change:

```csharp
    public static readonly IReadOnlySet<string> AllToolKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        VfsTextReadTool.Key, VfsTextCreateTool.Key, VfsTextEditTool.Key,
        VfsGlobFilesTool.Key, VfsTextSearchTool.Key, VfsMoveTool.Key, VfsRemoveTool.Key,
        VfsExecTool.Key, VfsFileInfoTool.Key
    };
```

to:

```csharp
    public static readonly IReadOnlySet<string> AllToolKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        VfsTextReadTool.Key, VfsTextCreateTool.Key, VfsTextEditTool.Key,
        VfsGlobFilesTool.Key, VfsTextSearchTool.Key, VfsMoveTool.Key, VfsCopyTool.Key, VfsRemoveTool.Key,
        VfsExecTool.Key, VfsFileInfoTool.Key
    };
```

- [ ] **Step 2: Register the factory**

In the `tools` array in `GetTools`, add after the `VfsMoveTool` line:

```csharp
            (VfsCopyTool.Key, () => AIFunctionFactory.Create(new VfsCopyTool(registry).RunAsync, name: $"domain__{Feature}__{VfsCopyTool.Name}")),
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Domain/Domain.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Domain/Tools/FileSystem/FileSystemToolFeature.cs
git commit -m "feat: register VfsCopyTool in FileSystemToolFeature"
```

---

## Phase 5 — Filter raw blob/copy tools from agent surface

### Task 15: Add new tool names to the raw-fs filter list

**Files:**
- Modify: `Infrastructure/Agents/ThreadSession.cs:82`

- [ ] **Step 1: Inspect current filter**

Confirm the current filter set at line 82:

```csharp
        "fs_read", "fs_create", "fs_edit", "fs_glob", "fs_search", "fs_move", "fs_delete", "fs_exec"
```

- [ ] **Step 2: Add new entries**

Replace with:

```csharp
        "fs_read", "fs_create", "fs_edit", "fs_glob", "fs_search", "fs_move", "fs_delete", "fs_exec",
        "fs_copy", "fs_blob_read", "fs_blob_write"
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Infrastructure/Infrastructure.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Infrastructure/Agents/ThreadSession.cs
git commit -m "fix: filter fs_copy and fs_blob_* raw tools when domain VFS is active"
```

---

## Phase 6 — End-to-end integration coverage

### Task 16: Cross-FS file copy (text) integration test

**Files:**
- Create: `Tests/Integration/Domain/Tools/FileSystem/VfsCopyToolIntegrationTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Domain.DTOs;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Domain.Tools.FileSystem;

[Collection("MultiFileSystem")]
public class VfsCopyToolIntegrationTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task RunAsync_CrossFsTextFile_CopiesAndPreservesSource()
    {
        fx.CreateLibraryFile("hello.md", "from library");
        var registry = await BuildRegistryAsync();
        var tool = new VfsCopyTool(registry);

        var result = await tool.RunAsync("/library/hello.md", "/notes/hello.md");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.Exists(Path.Combine(fx.LibraryPath, "hello.md")).ShouldBeTrue();
        File.ReadAllText(Path.Combine(fx.NotesPath, "hello.md")).ShouldBe("from library");
    }

    private async Task<VirtualFileSystemRegistry> BuildRegistryAsync()
    {
        var registry = new VirtualFileSystemRegistry();
        var libClient = await Connect(fx.LibraryEndpoint);
        var notesClient = await Connect(fx.NotesEndpoint);
        registry.Mount(new FileSystemMount("library", "/library", "lib"), new McpFileSystemBackend(libClient, "library"));
        registry.Mount(new FileSystemMount("notes", "/notes", "notes"), new McpFileSystemBackend(notesClient, "notes"));
        return registry;
    }

    private static async Task<McpClient> Connect(string endpoint)
        => await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(endpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsCopyToolIntegrationTests" --nologo`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/Domain/Tools/FileSystem/VfsCopyToolIntegrationTests.cs
git commit -m "test: integration coverage for cross-FS VfsCopyTool"
```

---

### Task 17: Cross-FS binary file roundtrip integration test

**Files:**
- Modify: `Tests/Integration/Domain/Tools/FileSystem/VfsCopyToolIntegrationTests.cs`

- [ ] **Step 1: Add the test**

Append to `VfsCopyToolIntegrationTests`:

```csharp
    [Fact]
    public async Task RunAsync_CrossFsBinaryFile_RoundtripsAllBytes()
    {
        var bytes = Enumerable.Range(0, 600 * 1024).Select(i => (byte)(i % 256)).ToArray();
        File.WriteAllBytes(Path.Combine(fx.LibraryPath, "blob.bin"), bytes);
        var registry = await BuildRegistryAsync();
        var tool = new VfsCopyTool(registry);

        var result = await tool.RunAsync("/library/blob.bin", "/notes/blob.bin");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        File.ReadAllBytes(Path.Combine(fx.NotesPath, "blob.bin")).ShouldBe(bytes);
    }
```

If the fixture's `AllowedExtensions` does not include `.bin`, extend it (the bytes go through `fs_blob_write`, which does not enforce extension allowlists by design — but if `fs_create`-style validation rejects `.bin` anywhere upstream, add it).

- [ ] **Step 2: Run test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RunAsync_CrossFsBinaryFile" --nologo`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/Domain/Tools/FileSystem/VfsCopyToolIntegrationTests.cs
git commit -m "test: cross-FS binary roundtrip via VfsCopyTool"
```

---

### Task 18: Cross-FS directory move integration test

**Files:**
- Create: `Tests/Integration/Domain/Tools/FileSystem/VfsMoveToolIntegrationTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using Domain.DTOs;
using Domain.Tools.FileSystem;
using Infrastructure.Agents;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Domain.Tools.FileSystem;

[Collection("MultiFileSystem")]
public class VfsMoveToolIntegrationTests(MultiFileSystemFixture fx)
{
    [Fact]
    public async Task RunAsync_CrossFsDirectory_MovesAllFilesAndRemovesSource()
    {
        fx.CreateLibraryFile("project/a.md", "alpha");
        fx.CreateLibraryFile("project/sub/b.md", "beta");
        var registry = await BuildRegistryAsync();
        var tool = new VfsMoveTool(registry);

        var result = await tool.RunAsync("/library/project", "/notes/project");

        result["status"]!.GetValue<string>().ShouldBe("ok");
        result["summary"]!["transferred"]!.GetValue<int>().ShouldBe(2);
        File.Exists(Path.Combine(fx.LibraryPath, "project", "a.md")).ShouldBeFalse();
        File.Exists(Path.Combine(fx.LibraryPath, "project", "sub", "b.md")).ShouldBeFalse();
        File.ReadAllText(Path.Combine(fx.NotesPath, "project", "a.md")).ShouldBe("alpha");
        File.ReadAllText(Path.Combine(fx.NotesPath, "project", "sub", "b.md")).ShouldBe("beta");
    }

    private async Task<VirtualFileSystemRegistry> BuildRegistryAsync()
    {
        var registry = new VirtualFileSystemRegistry();
        var libClient = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(fx.LibraryEndpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
        var notesClient = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(fx.NotesEndpoint)
        }), loggerFactory: NullLoggerFactory.Instance);
        registry.Mount(new FileSystemMount("library", "/library", "lib"), new McpFileSystemBackend(libClient, "library"));
        registry.Mount(new FileSystemMount("notes", "/notes", "notes"), new McpFileSystemBackend(notesClient, "notes"));
        return registry;
    }
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VfsMoveToolIntegrationTests" --nologo`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/Domain/Tools/FileSystem/VfsMoveToolIntegrationTests.cs
git commit -m "test: cross-FS directory move integration coverage"
```

---

## Final verification

### Task 19: Full test run + lint

- [ ] **Step 1: Run the full unit + integration test suite**

Run: `dotnet test Tests/Tests.csproj --nologo`
Expected: All tests pass; no regressions in unrelated suites.

- [ ] **Step 2: Build the full solution**

Run: `dotnet build Agent.sln --nologo`
Expected: Build succeeded with zero errors. Address any warnings introduced by the new code.

- [ ] **Step 3: Confirm spec coverage**

Skim `docs/superpowers/specs/2026-05-05-cross-filesystem-transfer-design.md` and verify each section is implemented:

- Backend interface: `CopyAsync`, `OpenReadStreamAsync`, `WriteFromStreamAsync` ✓ (Task 7)
- Same-FS dispatch (file + dir, copy + move): ✓ (Tasks 11, 12, 13)
- Cross-FS dispatch (file + dir, copy + move): ✓ (Tasks 11, 12, 13)
- Best-effort per-entry results: ✓ (Task 13)
- Result schema: ✓ (Tasks 11, 13)
- All three filesystem-exposing servers wired: ✓ (Tasks 4, 5, 6)
- Raw tools filtered from agent surface: ✓ (Task 15)

- [ ] **Step 4: Final commit if anything was tweaked during verification**

```bash
git status
# If any changes:
git add -A
git commit -m "chore: post-verification cleanup"
```

---

## Open follow-ups (deferred)

- Lazy chunked read stream (current `OpenReadStreamAsync` materialises into a `MemoryStream`; fine up to ~100 MB).
- Progress events to the LLM during long transfers.
- Native cross-FS optimisation (e.g., one MCP server fetching from another directly).
