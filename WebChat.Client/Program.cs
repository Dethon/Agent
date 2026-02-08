using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebChat.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Pipeline;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Hub event dispatching (Phase 4)
builder.Services.AddScoped<IHubEventDispatcher, HubEventDispatcher>();
builder.Services.AddScoped<ConnectionEventDispatcher>();

// Connection services (ChatConnectionService is the concrete type needed by dependent services)
builder.Services.AddScoped<ChatConnectionService>();
builder.Services.AddScoped<IChatConnectionService>(sp => sp.GetRequiredService<ChatConnectionService>());

// Core services
builder.Services.AddScoped<IChatSessionService, ChatSessionService>();
builder.Services.AddScoped<IChatMessagingService, ChatMessagingService>();
builder.Services.AddScoped<ITopicService, TopicService>();
builder.Services.AddScoped<IAgentService, AgentService>();
builder.Services.AddScoped<IApprovalService, ApprovalService>();

// State management
builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();

// State stores and effects (Phase 1-5)
builder.Services.AddWebChatStores();
builder.Services.AddWebChatEffects();

// Streaming services
builder.Services.AddScoped<IStreamingService, StreamingService>();
builder.Services.AddScoped<StreamResumeService>();
builder.Services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());
builder.Services.AddScoped<IMessagePipeline, MessagePipeline>();

// Notification handling
builder.Services.AddScoped<ISignalREventSubscriber, SignalREventSubscriber>();

var app = builder.Build();

// Activate effects that need to run at startup
_ = app.Services.GetRequiredService<ReconnectionEffect>();
_ = app.Services.GetRequiredService<SendMessageEffect>();
_ = app.Services.GetRequiredService<TopicSelectionEffect>();
_ = app.Services.GetRequiredService<TopicDeleteEffect>();
_ = app.Services.GetRequiredService<InitializationEffect>();
_ = app.Services.GetRequiredService<AgentSelectionEffect>();
_ = app.Services.GetRequiredService<UserIdentityEffect>();

await app.RunAsync();