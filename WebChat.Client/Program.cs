using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using WebChat.Client;
using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.Services.Handlers;
using WebChat.Client.Services.State;
using WebChat.Client.Services.Streaming;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

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

// Streaming services
builder.Services.AddScoped<IStreamingCoordinator, StreamingCoordinator>();
builder.Services.AddScoped<StreamResumeService>();
builder.Services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());

// Notification handling
builder.Services.AddScoped<IChatNotificationHandler, ChatNotificationHandler>();
builder.Services.AddScoped<SignalREventSubscriber>();

await builder.Build().RunAsync();