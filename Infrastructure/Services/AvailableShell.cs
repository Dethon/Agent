using System.Diagnostics;
using System.Runtime.InteropServices;
using Domain.Contracts;
using Microsoft.Win32;

namespace Infrastructure.Services;

public class AvailableShell : IAvailableShell
{
    public async Task<string> Get(CancellationToken ct)
    {
        return await GetShell(ct) switch
        {
            Shell.Bash => "bash",
            Shell.Sh => "sh",
            Shell.PowerShell => "pwsh",
            Shell.Cmd => "cmd",
            _ => throw new PlatformNotSupportedException("Unsupported shell")
        };
    }

    public static async Task<Shell> GetShell(CancellationToken ct)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return IsPowerShellInstalledOnWindows() ? Shell.PowerShell : Shell.Cmd;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return await IsBashInstalledOnLinux(ct) ? Shell.Bash : Shell.Sh;
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

public enum Shell
{
    Bash,
    Sh,
    PowerShell,
    Cmd
}