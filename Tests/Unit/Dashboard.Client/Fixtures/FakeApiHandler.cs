using System.Collections.Concurrent;

namespace Tests.Unit.Dashboard.Client.Fixtures;

public sealed class FakeApiHandler : HttpMessageHandler
{
    private readonly Queue<(object Data, TimeSpan Delay)> _responses = new();
    private readonly Dictionary<string, object> _answers = [];

    public string? LastRequestUri { get; private set; }

    // Concurrent bag, not a List<T>: DataLoadEffect fires ~19 requests via Task.WhenAll, so
    // multiple SendAsync calls can race on this collection.
    public ConcurrentBag<string?> Requests { get; } = [];

    public void EnqueueResponse<T>(T data, TimeSpan delay)
    {
        _responses.Enqueue((data!, delay));
    }

    // Answers keyed by a fragment of the request, for the callers that fire a whole page's worth of
    // requests at once and cannot say in which order they will arrive.
    public void AnswerFor<T>(string uriFragment, T data)
    {
        _answers[uriFragment] = data!;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        Requests.Add(LastRequestUri);

        var answer = _answers
            .Where(pair => LastRequestUri?.Contains(pair.Key, StringComparison.Ordinal) == true)
            .Select(pair => pair.Value)
            .FirstOrDefault();

        if (answer is not null)
        {
            return Json(answer);
        }

        if (_responses.TryDequeue(out var entry))
        {
            if (entry.Delay > TimeSpan.Zero)
            {
                await Task.Delay(entry.Delay, cancellationToken);
            }

            return Json(entry.Data);
        }

        return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(object data) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(data),
                System.Text.Encoding.UTF8,
                "application/json")
        };
}