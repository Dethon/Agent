using System.Collections.Concurrent;

namespace Tests.Unit.Dashboard.Client.Fixtures;

public sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly Queue<(object Data, TimeSpan Delay)> _responses = new();

    public string? LastRequestUri { get; private set; }

    // Concurrent bag, not a List<T>: DataLoadEffect fires ~19 requests via Task.WhenAll, so
    // multiple SendAsync calls can race on this collection.
    public ConcurrentBag<string?> Requests { get; } = [];

    public void EnqueueResponse<T>(T data, TimeSpan delay)
    {
        _responses.Enqueue((data!, delay));
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        Requests.Add(LastRequestUri);

        if (_responses.TryDequeue(out var entry))
        {
            if (entry.Delay > TimeSpan.Zero)
            {
                await Task.Delay(entry.Delay, cancellationToken);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(entry.Data);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }
}