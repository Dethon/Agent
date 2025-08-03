using Agent.Modules;
using Domain.Agents;
using Domain.Contracts;
using Domain.Monitor;
using Domain.Tools;
using Domain.Tools.Attachments;
using Infrastructure.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Tests.Unit;

public class ProgramTests
{
    [Fact]
    public void ServiceCollection_ShouldContainRequiredServices()
    {
        // given
        var builder = Host.CreateApplicationBuilder([]);
        var settings = builder.Configuration
            .GetSettings();

        // when
        builder.Services
            .AddMemoryCache()
            .AddOpenRouterAdapter(settings)
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient(settings, false)
            .AddChatMonitoring(settings)
            .AddAttachments()
            .AddTools(settings)
            .AddSingleton<IAgentResolver, AgentResolver>();

        var provider = builder.Services.BuildServiceProvider();

        // then
        provider.GetService<IAgentResolver>().ShouldNotBeNull();
        provider.GetService<ILargeLanguageModel>().ShouldNotBeNull();
        provider.GetService<ISearchClient>().ShouldNotBeNull();
        provider.GetService<IDownloadClient>().ShouldNotBeNull();
        provider.GetService<IFileSystemClient>().ShouldNotBeNull();
        provider.GetService<ChatMonitor>().ShouldNotBeNull();

        provider.GetService<FileSearchTool>().ShouldNotBeNull();
        provider.GetService<FileDownloadTool>().ShouldNotBeNull();
        provider.GetService<WaitForDownloadTool>().ShouldNotBeNull();
        provider.GetService<ListDirectoriesTool>().ShouldNotBeNull();
        provider.GetService<ListFilesTool>().ShouldNotBeNull();
        provider.GetService<MoveTool>().ShouldNotBeNull();
        provider.GetService<CleanupTool>().ShouldNotBeNull();

        provider.GetService<SearchHistory>().ShouldNotBeNull();
    }

    [Fact]
    public void ServiceCollection_WithSshMode_ShouldUseSshFileSystemClient()
    {
        // given
        var builder = Host.CreateApplicationBuilder([]);
        var settings = builder.Configuration.GetSettings();

        // when
        builder.Services.AddFileSystemClient(settings, true); // SSH mode enabled
        var provider = builder.Services.BuildServiceProvider();

        // then
        var fileSystemClient = provider.GetService<IFileSystemClient>();
        fileSystemClient.ShouldNotBeNull();
        fileSystemClient.ShouldBeOfType<SshFileSystemClient>();
    }

    [Fact]
    public void ServiceCollection_WithoutSshMode_ShouldUseLocalFileSystemClient()
    {
        // given
        var builder = Host.CreateApplicationBuilder([]);
        var settings = builder.Configuration.GetSettings();

        // when
        builder.Services.AddFileSystemClient(settings, false); // SSH mode disabled

        var provider = builder.Services.BuildServiceProvider();

        // then
        var fileSystemClient = provider.GetService<IFileSystemClient>();
        fileSystemClient.ShouldNotBeNull();
        fileSystemClient.ShouldBeOfType<LocalFileSystemClient>();
    }
}