using Cli.Settings;
using Microsoft.Extensions.Configuration;

namespace Cli.Modules;

public static class ConfigModule
{
    public static AgentConfiguration GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<AgentConfiguration>();
        if (settings == null)
        {
            throw new InvalidOperationException("Settings not found");
        }

        return settings;
    }
}