using WebChat.Client.Services;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.Extensions;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddWebChatStores()
        {
            // State infrastructure
            services.AddScoped<Dispatcher>();
            services.AddScoped<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());

            // Feature stores
            services.AddScoped<TopicsStore>();
            services.AddScoped<MessagesStore>();
            services.AddScoped<StreamingStore>();
            services.AddScoped<ConnectionStore>();
            services.AddScoped<ApprovalStore>();
            services.AddScoped<UserIdentityStore>();

            // State coordination
            services.AddScoped<RenderCoordinator>();

            // Services
            services.AddSingleton<ConfigService>();

            return services;
        }

        public IServiceCollection AddWebChatEffects()
        {
            services.AddScoped<ReconnectionEffect>();
            services.AddScoped<SendMessageEffect>();
            services.AddScoped<TopicSelectionEffect>();
            services.AddScoped<TopicDeleteEffect>();
            services.AddScoped<InitializationEffect>();
            services.AddScoped<AgentSelectionEffect>();
            services.AddScoped<UserIdentityEffect>();

            return services;
        }
    }
}