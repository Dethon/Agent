using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Clients;
using Infrastructure.Utils;
using Mcp.Hosting;
using McpServerVault.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;

namespace Tests.Integration.Fixtures;

[CollectionDefinition("MultiFileSystem")]
public class MultiFileSystemCollection : ICollectionFixture<MultiFileSystemFixture> { }

public class MultiFileSystemFixture : IAsyncLifetime
{
    private IHost _libraryHost = null!;
    private IHost _notesHost = null!;

    public string LibraryEndpoint { get; private set; } = null!;
    public string NotesEndpoint { get; private set; } = null!;
    public string LibraryPath { get; private set; } = null!;
    public string NotesPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        LibraryPath = Path.Combine(Path.GetTempPath(), $"mcp-library-{Guid.NewGuid()}");
        NotesPath = Path.Combine(Path.GetTempPath(), $"mcp-notes-{Guid.NewGuid()}");
        Directory.CreateDirectory(LibraryPath);
        Directory.CreateDirectory(NotesPath);

        var libraryPort = TestPort.GetAvailable();
        var notesPort = TestPort.GetAvailable();

        _libraryHost = BuildVaultHost(libraryPort, LibraryPath, builder => builder
            .WithResources<LibraryFileSystemResource>());

        _notesHost = BuildVaultHost(notesPort, NotesPath, builder => builder
            .WithResources<NotesFileSystemResource>());

        await Task.WhenAll(_libraryHost.StartAsync(), _notesHost.StartAsync());

        LibraryEndpoint = $"http://localhost:{libraryPort}/mcp";
        NotesEndpoint = $"http://localhost:{notesPort}/mcp";
    }

    private static IHost BuildVaultHost(int port, string vaultPath, Func<IMcpServerBuilder, IMcpServerBuilder> addResources)
    {
        var settings = new McpSettings
        {
            VaultPath = vaultPath,
            AllowedExtensions = [".md", ".txt", ".json"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        var mcpBuilder = builder.Services
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.VaultPath))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton(sp => new TextDiskFileSystem(
                "vault",
                "Personal Obsidian vault.",
                sp.GetRequiredService<IFileSystemClient>(),
                new LibraryPathConfig(settings.VaultPath),
                settings.AllowedExtensions))
            .AddToolServer(settings, ToolResponse.Create)
            .AddFileSystemTools<TextDiskFileSystem>();

        addResources(mcpBuilder);

        var app = builder.Build();
        app.MapMcp("/mcp");
        return app;
    }

    public void CreateLibraryFile(string relativePath, string content = "test content")
    {
        CreateFile(LibraryPath, relativePath, content);
    }

    public void CreateNotesFile(string relativePath, string content = "test content")
    {
        CreateFile(NotesPath, relativePath, content);
    }

    private static void CreateFile(string basePath, string relativePath, string content)
    {
        var fullPath = Path.Combine(basePath, relativePath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory != null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(fullPath, content);
    }

    public async Task DisposeAsync()
    {
        await Task.WhenAll(_libraryHost.StopAsync(), _notesHost.StopAsync());
        _libraryHost.Dispose();
        _notesHost.Dispose();

        try
        {
            if (Directory.Exists(LibraryPath))
            {
                Directory.Delete(LibraryPath, true);
            }

            if (Directory.Exists(NotesPath))
            {
                Directory.Delete(NotesPath, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}

[McpServerResourceType]
public class LibraryFileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://library",
        Name = "Library Filesystem",
        MimeType = "application/json")]
    [Description("Personal document library filesystem")]
    public string GetLibraryInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "library",
            mountPoint = "/library",
            description = $"Personal document library ({settings.VaultPath})"
        });
    }
}

[McpServerResourceType]
public class NotesFileSystemResource(McpSettings settings)
{
    [McpServerResource(
        UriTemplate = "filesystem://notes",
        Name = "Notes Filesystem",
        MimeType = "application/json")]
    [Description("Personal notes filesystem")]
    public string GetNotesInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "notes",
            mountPoint = "/notes",
            description = $"Personal notes ({settings.VaultPath})"
        });
    }
}