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
    /// collections start concurrently.  If the image already exists in the local Docker
    /// daemon it is reused as-is (no rebuild).
    /// </summary>
    internal static async Task EnsureBaseSdkImageAsync(string solutionRoot, CancellationToken ct)
    {
        await _baseSdkBuildLock.WaitAsync(ct);
        try
        {
            // Skip the build if the image is already present.
            if (await DockerImageExistsAsync("base-sdk:latest", ct))
            {
                return;
            }

            var baseSdkImage = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(solutionRoot)
                .WithDockerfile("Dockerfile.base-sdk")
                .WithName("base-sdk:latest")
                .WithDeleteIfExists(false)
                .WithCleanUp(false)
                .Build();
            await baseSdkImage.CreateAsync(ct);
        }
        finally
        {
            _baseSdkBuildLock.Release();
        }
    }

    private static async Task<bool> DockerImageExistsAsync(string imageName, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker", $"image inspect {imageName} --format={{{{.Id}}}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            await process.WaitForExitAsync(ct);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
