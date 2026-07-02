namespace Tests.Unit.McpChannelVoice;

internal sealed class RecordingHandler : HttpMessageHandler
{
    private readonly List<(Uri? Uri, string Body)> _requests = [];
    public IReadOnlyList<(Uri? Uri, string Body)> Requests
    {
        get { lock (_requests) { return _requests.ToList(); } }
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_requests)
        { _requests.Add((request.RequestUri, body)); }
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}