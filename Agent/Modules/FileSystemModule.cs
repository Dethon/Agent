using Domain.Contracts;
using Infrastructure.Agents.Mcp;

namespace Agent.Modules;

public static class FileSystemModule
{
    public static IServiceCollection AddFileSystem(this IServiceCollection services)
    {
        services.AddSingleton<IFileSystemBackendFactory, McpFileSystemBackendFactory>();
        return services;
    }
}
