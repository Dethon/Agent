using System.Collections.Concurrent;
using System.Diagnostics;

namespace Tests.E2E.Fixtures;

internal sealed record DockerBuildCommand(
    string FileName,
    string Arguments,
    IReadOnlyDictionary<string, string> Environment);

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
        EnsureImageAsync(solutionRoot, E2EImages.BaseSdk, ct);

    internal static Task EnsureImageAsync(string solutionRoot, E2EImageSpec spec, CancellationToken ct) =>
        EnsureImageAsync(solutionRoot, spec.Dockerfile, spec.ImageName, spec.WatchedDirs, ct);

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
            // `docker compose build`) would otherwise duplicate the same build, and a leaf
            // build could start while base-sdk:latest is still being re-tagged under its
            // own `FROM base-sdk:latest`.
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

            await RunDockerBuildAsync(solutionRoot, dockerfile, imageName, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // Driving the docker CLI rather than Testcontainers' ImageFromDockerfileBuilder is
    // deliberate. The builder posts to the Engine /build endpoint, which serves the legacy
    // builder: its layer cache is disjoint from the one docker compose and buildx use, so
    // alternating between `docker compose up --build` and the tests meant a cold rebuild every
    // time, and the legacy builder ignores the --mount=type=cache NuGet mounts. Asking
    // Testcontainers for BuildKit (ImageBuildParameters.Version = "2") is not an option:
    // Docker.DotNet.Enhanced types JSONMessage.Aux as an object, BuildKit sends it as a base64
    // string, and the build throws while deserialising its own progress stream.
    internal static DockerBuildCommand CreateBuildCommand(
        string solutionRoot,
        string dockerfile,
        string imageName) =>
        new(
            "docker",
            $"build --file \"{Path.Combine(solutionRoot, dockerfile)}\" --tag \"{imageName}\" "
            + $"--progress plain \"{solutionRoot}\"",
            new Dictionary<string, string> { ["DOCKER_BUILDKIT"] = "1" });

    private static async Task RunDockerBuildAsync(
        string solutionRoot,
        string dockerfile,
        string imageName,
        CancellationToken ct)
    {
        var command = CreateBuildCommand(solutionRoot, dockerfile, imageName);
        var psi = new ProcessStartInfo(command.FileName, command.Arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = solutionRoot
        };
        foreach (var (key, value) in command.Environment)
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start docker to build {imageName}.");

        // BuildKit writes its progress to stderr. Drain both pipes concurrently, or a build
        // chatty enough to fill one blocks forever while we wait on the other.
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode == 0)
        {
            return;
        }

        var output = string.Join(Environment.NewLine, await stdout, await stderr);
        var tail = string.Join(Environment.NewLine, output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(40)
            .Select(l => l.TrimEnd()));
        throw new InvalidOperationException(
            $"docker build failed for {imageName} (exit {process.ExitCode}):{Environment.NewLine}{tail}");
    }

    // An OS file handle opened with FileShare.None is released automatically if the process
    // dies, so no stale-lock cleanup is needed. Bounded by the caller's CancellationToken
    // (the fixture timeout).
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
            var psi = new ProcessStartInfo("docker",
                $"image inspect {imageName} --format={{{{.Created}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = Process.Start(psi)!;
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