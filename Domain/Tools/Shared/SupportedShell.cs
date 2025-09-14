using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Domain.Tools.Shared;

public static class SupportedShell
{
    public static async Task<(string shell, string commandOption)> GetShellAndCommandOption(CancellationToken ct)
    {
        var shell = await GetShell(ct);
        return shell switch
        {
            "pwsh" => (shell, "-Command"),
            "cmd" => (shell, "/C"),
            "bash" or "sh" => (shell, "-c"),
            _ => throw new PlatformNotSupportedException("Unsupported shell")
        };
    }

    public static async Task<string> GetShell(CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return IsPowerShellInstalledOnWindows() ? "pwsh" : "cmd";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await IsBashInstalledOnLinux(ct) ? "bash" : "sh";
        }

        throw new PlatformNotSupportedException("Unsupported OS");
    }

    private static bool IsPowerShellInstalledOnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return false;
        }

        var val = Registry
            .GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PowerShell\1", "Install", "0")
            ?.ToString();

        return val == "1";
    }

    private static async Task<bool> IsBashInstalledOnLinux(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "bash",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var result = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return !string.IsNullOrWhiteSpace(result);
    }
}