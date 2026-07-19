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

// Blocks inside SendAsync until Release() is called, so tests can observe "the POST has started but
// not finished" — the window a hung/slow escalation webhook leaves open in production.
internal sealed class BlockingHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Entered => _entered.Task;

    public void Release() => _release.TrySetResult();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        _entered.TrySetResult();
        await _release.Task;
        return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
    }
}