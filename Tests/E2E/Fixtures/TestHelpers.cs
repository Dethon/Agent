using DotNet.Testcontainers.Builders;

namespace Tests.E2E.Fixtures;

internal static class TestHelpers
{
    // Serialises concurrent base-sdk builds across all test-collection fixtures.
    private static readonly SemaphoreSlim _baseSdkBuildLock = new(1, 1);

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

    /// <summary>
    /// Builds the shared base-sdk:latest image exactly once, even when multiple test
    /// collections start concurrently.  If the image already exists but is older than
    /// the newest source file in Domain/ or Infrastructure/, it is rebuilt automatically.
    /// </summary>
    internal static async Task EnsureBaseSdkImageAsync(string solutionRoot, CancellationToken ct)
    {
        await _baseSdkBuildLock.WaitAsync(ct);
        try
        {
            var imageCreatedAt = await GetDockerImageCreatedAtAsync("base-sdk:latest", ct);
            if (imageCreatedAt.HasValue)
            {
                var newestSource = GetNewestSourceTimestamp(solutionRoot);
                if (newestSource <= imageCreatedAt.Value)
                {
                    return;
                }
            }

            var baseSdkImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(solutionRoot)
                .WithDockerfile("Dockerfile.base-sdk")
                .WithName("base-sdk:latest")
                .WithDeleteIfExists(true)
                .WithCleanUp(false)
                .Build();
            await baseSdkImage.CreateAsync(ct);
        }
        finally
        {
            _baseSdkBuildLock.Release();
        }
    }

    private static readonly string[] BuildOutputDirs = ["bin", "obj"];

    private static DateTimeOffset GetNewestSourceTimestamp(string solutionRoot)
    {
        var dirs = new[] { "Domain", "Infrastructure" };
        return dirs
            .Select(d => Path.Combine(solutionRoot, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                .Where(f => !BuildOutputDirs.Any(b =>
                    f.Contains($"{Path.DirectorySeparatorChar}{b}{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))))
            .Select(f => new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero))
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
