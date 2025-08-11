using Domain.Contracts;
using Infrastructure.Clients;
using Infrastructure.Wrappers;
using McpServer.Organize.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace McpServer.Organize.Modules;

public static class InjectorModule
{
    public static IServiceCollection AddFileSystemClient(
        this IServiceCollection services, McpSettings settings, bool sshMode)
    {
        if (!sshMode)
        {
            return services.AddTransient<IFileSystemClient, LocalFileSystemClient>();
        }

        var sshClient = new SshClientWrapper(
            settings.Ssh.Host, settings.Ssh.UserName, settings.Ssh.KeyPath, settings.Ssh.KeyPass);
        return services
            .AddSingleton(sshClient)
            .AddTransient<IFileSystemClient, SshFileSystemClient>(_ => new SshFileSystemClient(sshClient));
    }
}