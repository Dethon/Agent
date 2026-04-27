using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Infrastructure.Clients.Bash;
using Shouldly;
using Xunit;

namespace Tests.Unit.Infrastructure;

public class BashRunnerTests
{
    private readonly BashRunnerOptions _settings = new()
    {
        ContainerRoot = "/",
        HomeDir = "/tmp",
        DefaultTimeoutSeconds = 2,
        MaxTimeoutSeconds = 3,
        OutputCapBytes = 1024
    };

    private static void SkipIfNotLinux()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "BashRunner requires Linux");
    }

    [SkippableFact]
    public async Task RunAsync_SimpleCommand_ReturnsStdoutAndZeroExit()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "echo hello", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        result["stdout"]!.GetValue<string>().ShouldBe("hello\n");
        result["timedOut"]!.GetValue<bool>().ShouldBeFalse();
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [SkippableFact]
    public async Task RunAsync_NonZeroExit_ReturnedAsData()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "false", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(1);
        result["timedOut"]!.GetValue<bool>().ShouldBeFalse();
    }

    [SkippableFact]
    public async Task RunAsync_EmptyPath_UsesHomeDirAsCwd()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "pwd", null, CancellationToken.None);

        result["stdout"]!.GetValue<string>().Trim().ShouldBe("/tmp");
        result["cwd"]!.GetValue<string>().ShouldBe("/tmp");
    }

    [SkippableFact]
    public async Task RunAsync_DotPath_UsesHomeDirAsCwd()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync(".", "pwd", null, CancellationToken.None);

        result["stdout"]!.GetValue<string>().Trim().ShouldBe("/tmp");
    }

    [SkippableFact]
    public async Task RunAsync_RelativePath_UsesContainerRootCombined()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("tmp", "pwd", null, CancellationToken.None);

        result["stdout"]!.GetValue<string>().Trim().ShouldBe("/tmp");
    }

    [SkippableFact]
    public async Task RunAsync_NonExistentCwd_ReturnsErrorJson()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("does/not/exist", "echo hi", null, CancellationToken.None);

        result["error"]!.GetValue<bool>().ShouldBeTrue();
        result["message"]!.GetValue<string>().ShouldContain("does not exist");
    }

    [SkippableFact]
    public async Task RunAsync_Timeout_KillsProcessAndReportsTimedOut()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "sleep 30", timeoutSeconds: 1, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
        // After SIGKILL, exit code is typically 137 (128+SIGKILL=9) or -1 sentinel
    }

    [SkippableFact]
    public async Task RunAsync_OutputExceedsCap_TruncatedTrue()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // 1024 byte cap: emit 4096 bytes
        var result = await runner.RunAsync("", "yes a | head -c 4096", null, CancellationToken.None);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["stdout"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(_settings.OutputCapBytes);
    }

    [SkippableFact]
    public async Task RunAsync_TimeoutExceedsMax_ClampedToMax()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // Max is 3s. Request 999s. Then `sleep 30` should still time out (clamped to 3s).
        var result = await runner.RunAsync("", "sleep 30", timeoutSeconds: 999, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task RunAsync_NullTimeout_UsesDefault()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // Default is 2s. `sleep 30` should time out.
        var result = await runner.RunAsync("", "sleep 30", null, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task RunAsync_StderrCaptured()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "echo oops 1>&2", null, CancellationToken.None);

        result["stderr"]!.GetValue<string>().ShouldBe("oops\n");
    }

    [SkippableFact]
    public async Task RunAsync_CallerCancels_KillsProcessTreeBeforeThrowing()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // Sentinel file the bash process touches after a delay; if the process is killed
        // promptly the file should never appear, but if it leaks it will.
        var sentinel = Path.Combine(Path.GetTempPath(), $"bash-cancel-{Guid.NewGuid():N}.flag");
        var command = $"sleep 3 && touch '{sentinel}'";

        using var cts = new CancellationTokenSource();
        var task = runner.RunAsync("", command, timeoutSeconds: 30, cts.Token);

        // Give bash time to start, then cancel.
        await Task.Delay(300);
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(task);

        // Wait past the sleep duration; if the process leaked the sentinel will be created.
        await Task.Delay(3500);
        File.Exists(sentinel).ShouldBeFalse(
            $"bash subprocess leaked after caller cancellation; sentinel '{sentinel}' was created");
    }
}
