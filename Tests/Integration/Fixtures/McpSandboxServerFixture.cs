using System.Net;
using System.Runtime.InteropServices;
using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using Infrastructure.Clients.Bash;
using Infrastructure.Utils;
using McpServerSandbox.McpResources;
using McpServerSandbox.McpTools;
using McpServerSandbox.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpSandboxServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;
    public string SandboxRoot { get; private set; } = null!;
    public string HomeDir { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Sandbox integration tests require Linux bash");

        SandboxRoot = "/";
        HomeDir = Path.Combine(Path.GetTempPath(), $"mcp-sandbox-{Guid.NewGuid()}");
        Directory.CreateDirectory(HomeDir);

        var port = TestPort.GetAvailable();
        var settings = new McpSettings
        {
            ContainerRoot = SandboxRoot,
            HomeDir = HomeDir,
            DefaultTimeoutSeconds = 30,
            MaxTimeoutSeconds = 120,
            OutputCapBytes = 65536,
            AllowedExtensions = [".md", ".txt", ".py", ".sh", ".json"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        builder.Services
            .AddSingleton(settings)
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.ContainerRoot))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(new BashRunnerOptions
            {
                ContainerRoot = settings.ContainerRoot,
                HomeDir = settings.HomeDir,
                DefaultTimeoutSeconds = settings.DefaultTimeoutSeconds,
                MaxTimeoutSeconds = settings.MaxTimeoutSeconds,
                OutputCapBytes = settings.OutputCapBytes
            })
            .AddSingleton<ICommandRunner, BashRunner>()
            .AddMcpServer()
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    return ToolResponse.Create(ex);
                }
            }))
            .WithTools<FsReadTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsExecTool>()
            .WithResources<FileSystemResource>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try
        {
            if (HomeDir is not null && Directory.Exists(HomeDir))
            {
                Directory.Delete(HomeDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
