using Agent.Services.SubAgents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.SubAgents;

namespace Agent.Modules;

public static class SubAgentModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddSubAgents(SubAgentDefinition[] subAgentDefinitions)
        {
            var hasRecursion = subAgentDefinitions.Any(d =>
                d.EnabledFeatures.Any(f => f.Equals("subagents", StringComparison.OrdinalIgnoreCase)));

            if (hasRecursion)
            {
                throw new InvalidOperationException(
                    "SubAgent definitions must not include 'subagents' in enabledFeatures. Recursive subagents are not supported.");
            }

            var registryOptions = new SubAgentRegistryOptions { SubAgents = subAgentDefinitions };
            services.AddSingleton(registryOptions);

            services.AddSingleton<SystemChannelConnection>();
            services.AddSingleton<IChannelConnection>(sp => sp.GetRequiredService<SystemChannelConnection>());
            services.AddSingleton<SubAgentSessionManagerFactory>();

            services.AddSingleton<ISubAgentSessionsRegistry>(_ =>
                new SubAgentSessionsRegistry(_ => throw new InvalidOperationException(
                    "Manager must be created via MultiAgentFactory which supplies agent + reply context.")));

            services.AddSingleton<SubAgentCancelSink>();
            services.AddSingleton<ISubAgentCancelSink>(sp => sp.GetRequiredService<SubAgentCancelSink>());

            services.AddTransient<IDomainToolFeature, SubAgentToolFeature>();

            return services;
        }
    }
}