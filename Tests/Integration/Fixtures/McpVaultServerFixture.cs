using System.Net;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Utils;
using McpServerVault.McpResources;
using McpServerVault.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpVaultServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;
    public string VaultPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        VaultPath = Path.Combine(Path.GetTempPath(), $"mcp-vault-{Guid.NewGuid()}");
        Directory.CreateDirectory(VaultPath);

        var port = TestPort.GetAvailable();
        var settings = new McpSettings
        {
            VaultPath = VaultPath,
            AllowedExtensions = [".md", ".txt", ".json"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        builder.Services
            .AddSingleton(settings)
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.VaultPath))
            .AddTransient<global::Domain.Contracts.IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(sp => new TextDiskFileSystem(
                "vault",
                sp.GetRequiredService<global::Domain.Contracts.IFileSystemClient>(),
                new LibraryPathConfig(settings.VaultPath),
                settings.AllowedExtensions))
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
            .AddFileSystemTools<TextDiskFileSystem>()
            .WithResources<FileSystemResource>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{port}/mcp";
    }

    public void CreateFile(string relativePath, string content = "test content")
    {
        var fullPath = Path.Combine(VaultPath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        try
        {
            if (Directory.Exists(VaultPath))
            {
                Directory.Delete(VaultPath, true);
            }

        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}