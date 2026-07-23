using System.Diagnostics;
using System.Text.Json.Nodes;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

// Pins the config.json that DockerCompose/lemonade/entrypoint.sh writes for lemond, via the
// STT_CONFIG_ONLY seam (no server start, no model pull): the STT_BACKEND device mapping plus
// the whispercpp.args line that restores the Wyoming-era decode-quality flags (Silero VAD,
// Castilian initial prompt, beam size). The script is bind-mounted from the repo, so this
// tests the working tree, not whatever entrypoint the image was built with. Runs with
// --network none: VAD-model presence is controlled by seeding the file, and the download
// path degrades to no-VAD (fail-open) instead of hitting the network. Requires docker and
// the lemonade:latest image; skips otherwise.
public class LemonadeEntrypointConfigTests : IDisposable
{
    private const string Image = "lemonade:latest";
    private const string VadModelFile = "ggml-silero-v5.1.2.bin";

    // Probing docker must never throw: on a host with no docker binary Process.Start raises
    // Win32Exception, and (as a Lazy) that exception would be cached and re-thrown from inside
    // every Skip.IfNot, turning skips into errors. Treat any launch failure as "not available".
    private static readonly Lazy<bool> _imageAvailable = new(() =>
    {
        try
        {
            return Run("docker", ["image", "inspect", Image]).Exit == 0;
        }
        catch
        {
            return false;
        }
    });

    private readonly string _configDir;

    public LemonadeEntrypointConfigTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), $"lemonade-entrypoint-{Guid.NewGuid()}");
        Directory.CreateDirectory(_configDir);
        // The image runs as UID 10001; the mount must be writable for config.json. Guarded because
        // File.SetUnixFileMode throws PlatformNotSupportedException on Windows — which would fail
        // the constructor (an error, not a skip) before RunEntrypoint's platform gate can fire.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_configDir, (UnixFileMode)0b111_111_111);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_configDir))
        {
            Directory.Delete(_configDir, true);
        }
    }

    private void SeedVadModel()
    {
        var vadDir = Path.Combine(_configDir, "vad");
        Directory.CreateDirectory(vadDir);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(vadDir, (UnixFileMode)0b111_111_111);
        }
        File.WriteAllBytes(Path.Combine(vadDir, VadModelFile), [1, 2, 3]);
    }

    private JsonObject RunEntrypoint(params (string Key, string Value)[] env)
    {
        // The entrypoint is a Linux shell run through a Linux container over a unix-mode bind
        // mount; only assert it on a Linux host rather than hard-failing elsewhere.
        Skip.IfNot(OperatingSystem.IsLinux(), "lemonade entrypoint test requires a Linux docker host");
        Skip.IfNot(_imageAvailable.Value, $"docker image {Image} not available");

        var script = Path.Combine(RepoRoot(), "DockerCompose", "lemonade", "entrypoint.sh");
        List<string> args =
        [
            "run", "--rm", "--network", "none",
            "--entrypoint", "sh",
            "-e", "STT_CONFIG_ONLY=1",
            "-e", "LEMONADE_CONFIG_DIR=/cfg",
            "-v", $"{script}:/entrypoint.sh:ro",
            "-v", $"{_configDir}:/cfg"
        ];
        foreach (var (key, value) in env)
        {
            args.AddRange(["-e", $"{key}={value}"]);
        }
        args.AddRange([Image, "/entrypoint.sh"]);

        var result = Run("docker", args);
        result.Exit.ShouldBe(0, $"entrypoint failed: {result.StdErr}");

        var config = File.ReadAllText(Path.Combine(_configDir, "config.json"));
        return JsonNode.Parse(config)!.AsObject();
    }

    private static string WhisperArgs(JsonObject config) =>
        config["whispercpp"]!["args"]!.GetValue<string>();

    [SkippableFact]
    public void Entrypoint_Defaults_RestoreVadPromptAndBeamSize()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "cpu"));

        config["whispercpp"]!["backend"]!.GetValue<string>().ShouldBe("cpu");
        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldContain("--beam-size 5");
        whisperArgs.ShouldContain("--prompt \"Asistente de voz en español de España");
        whisperArgs.ShouldContain("Valladolid.\"");
        whisperArgs.ShouldContain($"--vad --vad-model /cfg/vad/{VadModelFile} --vad-threshold 0.6");
    }

    [SkippableFact]
    public void Entrypoint_EmptyKnobs_DisableTheirFlags()
    {
        SeedVadModel();

        var config = RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_VAD_THRESHOLD", ""),
            ("STT_INITIAL_PROMPT", ""));

        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldContain("--beam-size 5");
        whisperArgs.ShouldNotContain("--vad");
        whisperArgs.ShouldNotContain("--prompt");
    }

    [SkippableFact]
    public void Entrypoint_Overrides_PropagateToArgs()
    {
        SeedVadModel();

        var config = RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_BEAM_SIZE", "3"),
            ("STT_VAD_THRESHOLD", "0.7"),
            ("STT_INITIAL_PROMPT", "hola caracola"));

        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldContain("--beam-size 3");
        whisperArgs.ShouldContain("--vad-threshold 0.7");
        whisperArgs.ShouldContain("--prompt \"hola caracola\"");
    }

    [SkippableFact]
    public void Entrypoint_VadModelUnavailable_FailsOpenWithoutVad()
    {
        // No seeded model + --network none: the download can't succeed, so the entrypoint
        // must start whisper without VAD rather than crash-loop the container.
        var config = RunEntrypoint(("STT_BACKEND", "cpu"));

        var whisperArgs = WhisperArgs(config);
        whisperArgs.ShouldNotContain("--vad");
        whisperArgs.ShouldContain("--beam-size 5");
        whisperArgs.ShouldContain("--prompt \"Asistente de voz");
    }

    [SkippableFact]
    public void Entrypoint_GpuBackend_MapsToVulkan()
    {
        SeedVadModel();

        var config = RunEntrypoint(("STT_BACKEND", "gpu"));

        config["whispercpp"]!["backend"]!.GetValue<string>().ShouldBe("vulkan");
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "agent.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("agent.sln not found above test directory");
    }

    private static (int Exit, string StdOut, string StdErr) Run(string command, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(60_000).ShouldBeTrue($"{command} timed out");
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}