using Microsoft.Extensions.Configuration;
using Cli.Settings;

namespace Cli.Modules;

public static class ConfigModule
{
    public static AgentConfiguration GetSettings(this ConfigurationManager configuration)
    {
        configuration
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>();

        var settings = configuration.Get<AgentConfiguration>();
        if (settings == null)
        {
            throw new InvalidOperationException("Settings not found");
        }

        return settings;
    }
}