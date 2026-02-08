using System.Threading.Channels;
using Agent.App;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Domain.Tools.Scheduling;
using Infrastructure.Agents;
using Infrastructure.StateManagers;
using Infrastructure.Validation;

namespace Agent.Modules;

public static class SchedulingModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddScheduling()
        {
            services.AddSingleton(Channel.CreateUnbounded<Schedule>(
                new UnboundedChannelOptions { SingleReader = true }));

            services.AddSingleton<IScheduleStore, RedisScheduleStore>();
            services.AddSingleton<ICronValidator, CronValidator>();
            services.AddSingleton<IAgentDefinitionProvider, AgentDefinitionProvider>();

            services.AddTransient<ScheduleCreateTool>();
            services.AddTransient<ScheduleListTool>();
            services.AddTransient<ScheduleDeleteTool>();

            services.AddTransient<IDomainToolFeature, SchedulingToolFeature>();

            services.AddSingleton<ScheduleDispatcher>();
            services.AddSingleton<ScheduleExecutor>();

            services.AddHostedService<ScheduleMonitoring>();

            return services;
        }
    }
}
