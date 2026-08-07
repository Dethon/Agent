using System.Diagnostics;

namespace Tests.Integration.Fixtures;

// Owns the precondition both lemonade suites share: the lemonade:latest image exists. It reuses the
// compose-built image when present and otherwise builds it from DockerCompose/lemonade, so a machine
// that has never run the compose stack runs the tests instead of skipping them. The result is
// memoized process-wide because the two suites are separate fixtures xunit may initialize in
// parallel, and one build per test run is enough. Only an unusable docker or a failing build yields
// a SkipReason.
public class LemonadeImageFixture : IAsyncLifetime
{
    public const string Image = "lemonade:latest";

    private static readonly SemaphoreSlim _buildGate = new(1, 1);
    private static Task<string?>? _ensured;

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync() => SkipReason = await EnsureAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public static async Task<string?> EnsureAsync()
    {
        await _buildGate.WaitAsync();
        try
        {
            _ensured ??= BuildIfMissingAsync();
        }
        finally
        {
            _buildGate.Release();
        }
        return await _ensured;
    }

    private static async Task<string?> BuildIfMissingAsync()
    {
        try
        {
            if (await DockerAsync(TimeSpan.FromSeconds(60), "image", "inspect", Image) is (0, _))
            {
                return null;
            }

            var dockerfileDir = Path.Combine(
                E2E.Fixtures.TestHelpers.FindSolutionRoot(), "DockerCompose", "lemonade");
            var (exit, stdErr) = await DockerAsync(
                TimeSpan.FromMinutes(20), "build", "-t", Image, dockerfileDir);
            return exit == 0 ? null : $"docker image {Image} could not be built: {Tail(stdErr)}";
        }
        catch (Exception ex)
        {
            return $"docker image {Image} unavailable: {ex.Message}";
        }
    }

    // Shelling out instead of using testcontainers' ImageFromDockerfileBuilder: that builder posts to
    // the legacy /build endpoint, which rejects this Dockerfile's `COPY --chmod` ("the --chmod option
    // requires BuildKit"), so the build always failed and both suites skipped for a reason that read
    // like a missing image. The CLI builds with BuildKit, the same way the compose stack builds it.
    private static async Task<(int Exit, string StdErr)> DockerAsync(TimeSpan timeout, params string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        // Read both pipes before waiting: a build fills them well past the OS buffer, and a full pipe
        // blocks the child forever.
        var stdOut = process.StandardOutput.ReadToEndAsync();
        var stdErr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(true);
            throw new TimeoutException($"docker {args[0]} timed out after {timeout}");
        }

        await stdOut;
        return (process.ExitCode, await stdErr);
    }

    private static string Tail(string output) =>
        output.Length <= 400 ? output.Trim() : output[^400..].Trim();
}