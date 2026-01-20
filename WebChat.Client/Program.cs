using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebChat.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.Services.Handlers;
using WebChat.Client.Services.State;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Topics;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Hub;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// Hub event dispatching (Phase 4)
builder.Services.AddScoped<IHubEventDispatcher, HubEventDispatcher>();

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
builder.Services.AddScoped<IChatStateManager, ChatStateManager>();
builder.Services.AddScoped<ILocalStorageService, LocalStorageService>();

// State infrastructure (Phase 1)
builder.Services.AddScoped<Dispatcher>();
builder.Services.AddScoped<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());

// State stores (Phase 2)
builder.Services.AddScoped<TopicsStore>();
builder.Services.AddScoped<MessagesStore>();
builder.Services.AddScoped<StreamingStore>();
builder.Services.AddScoped<ConnectionStore>();
builder.Services.AddScoped<ApprovalStore>();

// State coordination (Phase 3)
builder.Services.AddScoped<RenderCoordinator>();

// State effects (Phase 4)
builder.Services.AddScoped<ReconnectionEffect>();

// Streaming services
builder.Services.AddScoped<IStreamingCoordinator, StreamingCoordinator>();
builder.Services.AddScoped<StreamResumeService>();
builder.Services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());

// Notification handling
builder.Services.AddScoped<IChatNotificationHandler, ChatNotificationHandler>();
builder.Services.AddScoped<ISignalREventSubscriber, SignalREventSubscriber>();

var app = builder.Build();

// Activate effects that need to run at startup
_ = app.Services.GetRequiredService<ReconnectionEffect>();

await app.RunAsync();