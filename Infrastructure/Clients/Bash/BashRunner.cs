using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Infrastructure.Clients.Bash;

public class BashRunner(BashRunnerOptions options) : ICommandRunner
{
    public async Task<JsonNode> RunAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var cwd = ResolveCwd(path);
        if (!Directory.Exists(cwd))
        {
            return new JsonObject
            {
                ["error"] = true,
                ["message"] = $"Working directory '{cwd}' does not exist or is not a directory."
            };
        }

        var effectiveTimeout = TimeSpan.FromSeconds(
            Math.Clamp(timeoutSeconds ?? options.DefaultTimeoutSeconds, 1, options.MaxTimeoutSeconds));

        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList = { "-lc", command },
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, options.OutputCapBytes, ct);
        var stderrTask = ReadCappedAsync(process.StandardError, options.OutputCapBytes, ct);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { /* best-effort */ }
            try { await stdoutTask; } catch { /* drain reader before process disposal */ }
            try { await stderrTask; } catch { /* drain reader before process disposal */ }
            throw;
        }

        var stdoutResult = await stdoutTask;
        var stderrResult = await stderrTask;

        return new JsonObject
        {
            ["stdout"] = stdoutResult.Text,
            ["stderr"] = stderrResult.Text,
            ["exitCode"] = timedOut ? -1 : process.ExitCode,
            ["timedOut"] = timedOut,
            ["truncated"] = stdoutResult.Truncated || stderrResult.Truncated,
            ["durationMs"] = sw.ElapsedMilliseconds,
            ["cwd"] = cwd
        };
    }

    private string ResolveCwd(string path)
    {
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return options.HomeDir;
        }

        var combined = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(options.ContainerRoot, path));

        return combined;
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        StreamReader reader, int capBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var byteCount = 0;
        var truncated = false;
        var buffer = new char[4096];

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (read == 0)
            {
                break;
            }

            if (truncated)
            {
                continue;
            }

            var chunkBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (byteCount + chunkBytes <= capBytes)
            {
                sb.Append(buffer, 0, read);
                byteCount += chunkBytes;
                continue;
            }

            var remaining = capBytes - byteCount;
            var taken = 0;
            var fitting = buffer.Take(read)
                .Select(c => (Char: c, Bytes: Encoding.UTF8.GetByteCount(new[] { c })))
                .TakeWhile(x =>
                {
                    if (taken + x.Bytes > remaining)
                    {
                        return false;
                    }
                    taken += x.Bytes;
                    return true;
                })
                .Select(x => x.Char)
                .ToArray();
            sb.Append(fitting);
            byteCount += taken;
            truncated = true;
        }

        return (sb.ToString(), truncated);
    }
}
