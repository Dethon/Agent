using System.Collections.Concurrent;
using DotNet.Testcontainers.Builders;

namespace Tests.E2E.Fixtures;

internal static class TestHelpers
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _imageLocks = new();

    internal static string FindSolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find solution root directory.");
    }

    internal static Task EnsureBaseSdkImageAsync(string solutionRoot, CancellationToken ct) =>
        EnsureImageAsync(
            solutionRoot,
            "Dockerfile.base-sdk",
            "base-sdk:latest",
            ["Domain", "Infrastructure"],
            ct);

    /// <summary>
    /// Builds a Docker image under a stable tag, replacing any prior image with the same name.
    /// Uses a per-image semaphore so concurrent fixtures sharing a tag serialise their builds,
    /// and a source-timestamp check to skip rebuilds when nothing in the watched dirs has changed.
    /// </summary>
    internal static async Task EnsureImageAsync(
        string solutionRoot,
        string dockerfile,
        string imageName,
        IReadOnlyList<string> watchedDirs,
        CancellationToken ct)
    {
        var gate = _imageLocks.GetOrAdd(imageName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // The semaphore above only serialises threads within this process. Separate
            // processes (E2E vs benchmark as distinct `dotnet test` jobs, or a concurrent
            // `docker compose build`) would otherwise race the destructive
            // WithDeleteIfExists(true) + build on the same tag — including deleting
            // base-sdk:latest out from under another process's `FROM base-sdk:latest`.
            await using var fileLock = await AcquireImageFileLockAsync(imageName, ct);

            var imageCreatedAt = await GetDockerImageCreatedAtAsync(imageName, ct);
            if (imageCreatedAt.HasValue)
            {
                var newestSource = GetNewestSourceTimestamp(solutionRoot, watchedDirs, dockerfile);
                if (newestSource <= imageCreatedAt.Value)
                {
                    return;
                }
            }

            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(solutionRoot)
                .WithDockerfile(dockerfile)
                .WithName(imageName)
                .WithDeleteIfExists(true)
                .WithCleanUp(false)
                .Build();
            await image.CreateAsync(ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // A cross-process exclusive lock keyed by image name. An OS file handle opened with
    // FileShare.None is released automatically if the process dies, so no stale-lock
    // cleanup is needed. Bounded by the caller's CancellationToken (the fixture timeout).
    private static async Task<FileStream> AcquireImageFileLockAsync(string imageName, CancellationToken ct)
    {
        var safeName = string.Concat(imageName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        var lockPath = Path.Combine(Path.GetTempPath(), $"agent-tests-image-{safeName}.lock");
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
        }
    }

    private static readonly string[] _buildOutputDirs = ["bin", "obj"];

    private static DateTimeOffset GetNewestSourceTimestamp(
        string solutionRoot,
        IReadOnlyList<string> watchedDirs,
        string dockerfile)
    {
        var dirTimestamps = watchedDirs
            .Select(d => Path.Combine(solutionRoot, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .Where(f => !_buildOutputDirs.Any(b =>
                    f.Contains($"{Path.DirectorySeparatorChar}{b}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))))
            .Select(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero));

        var dockerfilePath = Path.Combine(solutionRoot, dockerfile);
        var dockerfileTimestamp = File.Exists(dockerfilePath)
            ? new DateTimeOffset(File.GetLastWriteTimeUtc(dockerfilePath), TimeSpan.Zero)
            : DateTimeOffset.MinValue;

        return dirTimestamps
            .Append(dockerfileTimestamp)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Max();
    }

    private static async Task<DateTimeOffset?> GetDockerImageCreatedAtAsync(string imageName, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker",
                $"image inspect {imageName} --format={{{{.Created}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                return null;
            }

            return DateTimeOffset.TryParse(output.Trim(), out var created) ? created : null;
        }
        catch
        {
            return null;
        }
    }
}