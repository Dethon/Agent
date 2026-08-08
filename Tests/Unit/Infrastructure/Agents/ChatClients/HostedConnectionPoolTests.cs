using System.Net;
using System.Net.Sockets;
using Agent.Modules;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class HostedConnectionPoolTests
{
    // Real traffic is about 35 turns a day, so an ordinary gap between two turns is tens of
    // minutes. Anything shorter than this leaves the connection dead before the next turn.
    private static readonly TimeSpan _ordinaryGapBetweenTurns = TimeSpan.FromMinutes(3);

    [Fact]
    public void SharedHandler_OutlivesAnOrdinaryGapBetweenTurns()
    {
        var handler = OpenRouterChatClient.SharedHandler;

        handler.PooledConnectionLifetime.ShouldBe(HostedConnectionPool.ConnectionLifetime);
        handler.PooledConnectionIdleTimeout.ShouldBe(HostedConnectionPool.IdleTimeout);
        handler.PooledConnectionIdleTimeout.ShouldBeGreaterThan(_ordinaryGapBetweenTurns);
        handler.PooledConnectionLifetime.ShouldBeGreaterThan(handler.PooledConnectionIdleTimeout);
    }

    [Fact]
    public void EmbeddingClient_GetsTheSameConnectionPoolTreatmentAsTheChatClients()
    {
        var services = new ServiceCollection();
        services.AddMemory(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(nameof(IEmbeddingService));

        var primary = PrimaryHandlerOf(handler);
        primary.PooledConnectionLifetime.ShouldBe(HostedConnectionPool.ConnectionLifetime);
        primary.PooledConnectionIdleTimeout.ShouldBe(HostedConnectionPool.IdleTimeout);
    }

    [Fact]
    public async Task TwoCallsSeparatedByAGap_ReuseTheSameConnection()
    {
        using var server = new ConnectionCountingServer();
        using var invoker = new HttpMessageInvoker(HostedConnectionPool.CreateHandler());

        await SendAsync(invoker, server.Address);
        await Task.Delay(TimeSpan.FromMilliseconds(250));
        await SendAsync(invoker, server.Address);

        server.AcceptedConnections.ShouldBe(1);
    }

    private static async Task SendAsync(HttpMessageInvoker invoker, Uri address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        using var response = await invoker.SendAsync(request, CancellationToken.None);
        await response.Content.ReadAsStringAsync();
    }

    private static SocketsHttpHandler PrimaryHandlerOf(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler
                ?? throw new InvalidOperationException("Handler chain ended without a primary handler");
        }

        return handler as SocketsHttpHandler
            ?? throw new InvalidOperationException($"Primary handler is {handler.GetType().Name}, not SocketsHttpHandler");
    }

    private sealed class ConnectionCountingServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private int _accepted;

        public ConnectionCountingServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");
            _ = Task.Run(AcceptLoopAsync);
        }

        public Uri Address { get; }

        public int AcceptedConnections => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    Interlocked.Increment(ref _accepted);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException)
            {
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: keep-alive\r\n\r\nok"u8.ToArray();
                try
                {
                    while (await stream.ReadAsync(buffer, _cts.Token) > 0)
                    {
                        await stream.WriteAsync(response, _cts.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Dispose();
            _cts.Dispose();
        }
    }
}