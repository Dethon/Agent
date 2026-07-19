namespace Tests.Unit.McpChannelVoice;

// Hands out clients over one stub handler, mirroring the factory-per-call shape of the
// production Lemonade clients. disposeHandler: false — the SUT disposes each client per call
// and the handler must survive for subsequent calls and assertions.
internal sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) =>
        new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
}