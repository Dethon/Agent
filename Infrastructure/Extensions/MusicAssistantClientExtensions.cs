using Domain.Contracts;
using Infrastructure.Clients.MusicAssistant;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

public static class MusicAssistantClientExtensions
{
    // A connection is opened per call, so there is no shared socket state to scope: singleton.
    public static IServiceCollection AddMusicAssistantClient(
        this IServiceCollection services, string baseUrl, string token)
    {
        services.AddSingleton<IMusicAssistantClient>(_ => new MusicAssistantClient(baseUrl, token));
        return services;
    }
}