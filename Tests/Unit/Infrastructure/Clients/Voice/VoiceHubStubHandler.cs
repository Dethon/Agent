using System.Net;
using System.Net.Http.Json;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

// Captures the outgoing request and returns a canned response, so the voice-hub HTTP adapters can be
// asserted (path, method, token header, body) without a live hub.
internal sealed class VoiceHubStubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        return respond(request);
    }

    public static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body) };

    public static HttpClient Client(HttpMessageHandler handler) =>
        new(handler, disposeHandler: false) { BaseAddress = new Uri("http://hub.local/") };

    public static IHttpClientFactory Factory(HttpMessageHandler handler) => new StubFactory(handler);

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            // Pins the adapter↔DI contract: an adapter asking for any other name would get an
            // unconfigured client (no BaseAddress) in production.
            name.ShouldBe(VoiceHubHttp.ClientName);
            return Client(handler);
        }
    }
}