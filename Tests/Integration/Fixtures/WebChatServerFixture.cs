using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Agent.Hubs;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Infrastructure.Agents;
using Infrastructure.Clients.Messaging.WebChat;
using Infrastructure.Clients.ToolApproval;
using Infrastructure.StateManagers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Tests.Integration.Fixtures;

public sealed class WebChatServerFixture : IAsyncLifetime
{
    private IHost _host = null!;
    private int _port;
    private CancellationTokenSource _monitorCts = null!;
    private Task _monitorTask = null!;

    public FakeAgentFactory FakeAgentFactory { get; } = new();
    private RedisFixture RedisFixture { get; } = new();

    private string BaseUrl => $"http://localhost:{_port}";
    private string HubUrl => $"{BaseUrl}/hubs/chat";

    public async Task InitializeAsync()
    {
        await RedisFixture.InitializeAsync();

        _port = GetAvailablePort();

        var testAgents = new[]
        {
            new AgentDefinition
            {
                Id = "test-agent",
                Name = "Test Agent",
                Description = "A test agent for integration tests",
                Model = "test-model",
                McpServerEndpoints = []
            },
            new AgentDefinition
            {
                Id = "second-agent",
                Name = "Second Agent",
                Description = "A second test agent",
                Model = "test-model",
                McpServerEndpoints = []
            }
        };

        FakeAgentFactory.ConfigureAgents(testAgents);

        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Spaces:0:Slug"] = "default",
            ["Spaces:0:Name"] = "Main",
            ["Spaces:0:AccentColor"] = "#e94560",
            ["Spaces:1:Slug"] = "secret-room",
            ["Spaces:1:Name"] = "Secret Room",
            ["Spaces:1:AccentColor"] = "#6366f1",
        });
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, _port));

        // Configure services
        builder.Services
            .AddLogging()
            .AddCors()
            .AddSignalR(options => options.EnableDetailedErrors = true);

        // Add Redis connection
        builder.Services.AddSingleton(RedisFixture.Connection);
        builder.Services.AddSingleton<IThreadStateStore>(sp =>
            new RedisThreadStateStore(
                sp.GetRequiredService<IConnectionMultiplexer>(),
                TimeSpan.FromMinutes(10)));

        // Configure agent registry
        builder.Services.Configure<AgentRegistryOptions>(options => options.Agents = testAgents);

        // Add fake agent factory
        builder.Services.AddSingleton<IAgentFactory>(FakeAgentFactory);

        // Add web chat components
        builder.Services.AddSingleton<IHubNotificationSender, HubNotificationAdapter>();
        builder.Services.AddSingleton<INotifier, HubNotifier>();
        builder.Services.AddSingleton<WebChatSessionManager>();
        builder.Services.AddSingleton<WebChatStreamManager>();
        builder.Services.AddSingleton<WebChatApprovalManager>();
        builder.Services.AddSingleton<WebChatMessengerClient>();
        builder.Services.AddSingleton<IChatMessengerClient>(sp =>
            sp.GetRequiredService<WebChatMessengerClient>());

        // Add tool approval (auto-approve for tests)
        builder.Services.AddSingleton<IToolApprovalHandlerFactory>(new AutoApproveToolHandlerFactory());

        // Add chat thread resolver and monitor (but NOT as hosted service - we manage it manually)
        builder.Services.AddSingleton<ChatThreadResolver>();
        builder.Services.AddSingleton<ChatMonitor>();

        var app = builder.Build();

        app.UseCors(policy =>
        {
            policy.SetIsOriginAllowed(_ => true)
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials();
        });

        app.MapHub<ChatHub>("/hubs/chat");

        _host = app;
        await _host.StartAsync();

        // Start ChatMonitor manually with our own cancellation token so we can control its lifecycle
        _monitorCts = new CancellationTokenSource();
        var monitor = _host.Services.GetRequiredService<ChatMonitor>();
        _monitorTask = Task.Run(async () =>
        {
            try
            {
                while (!_monitorCts.Token.IsCancellationRequested)
                {
                    await monitor.Monitor(_monitorCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
            catch (ChannelClosedException)
            {
                // Expected during shutdown
            }
            catch (Exception)
            {
                // Suppress any other exceptions during shutdown
            }
        }); // Note: No cancellation token here - we handle cancellation ourselves
    }

    public HubConnection CreateHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl(HubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    public async Task DisposeAsync()
    {
        // Cancel the monitor task first - this allows it to exit gracefully
        await _monitorCts.CancelAsync();

        // Wait for monitor task to complete (with timeout)
        try
        {
            await _monitorTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // Monitor task didn't complete in time
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Dispose the messenger client to complete channels
        try
        {
            var messengerClient = _host.Services.GetRequiredService<WebChatMessengerClient>();
            messengerClient.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        // Dispose the thread resolver
        try
        {
            var threadResolver = _host.Services.GetRequiredService<ChatThreadResolver>();
            threadResolver.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        // Stop the host
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await _host.StopAsync(cts.Token);
        }
        catch
        {
            // Ignore host stop errors
        }

        try
        {
            _monitorCts.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        try
        {
            _host.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        try
        {
            await RedisFixture.DisposeAsync();
        }
        catch
        {
            // Ignore disposal errors
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}