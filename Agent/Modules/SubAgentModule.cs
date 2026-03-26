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

            services.AddSingleton(sp =>
                new Lazy<IAgentFactory>(sp.GetRequiredService<IAgentFactory>));
            services.AddTransient<IDomainToolFeature, SubAgentToolFeature>();

            return services;
        }
    }
}
