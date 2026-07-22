using System.Net;
using McpChannelVoice.Services.Tse;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TseExtractorClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            // WaitAsync makes this mock observe cancellation the way a real transport (SocketsHttpHandler)
            // would; respond() itself may be an uncancelable Task.Delay(Timeout.Infinite), simulating a
            // sidecar that never replies. Without this, the deadline/caller-cancellation tests hang forever
            // instead of exercising TseExtractorClient's cancellation handling.
            return await respond(request).WaitAsync(ct);
        }
    }

    private static TseExtractorClient Client(StubHandler handler, int timeoutMs = 5000) =>
        new(new HttpClient(handler), new TseSettings { Endpoint = "http://tse-extractor:9098", TimeoutMs = timeoutMs },
            NullLogger<TseExtractorClient>.Instance);

    [Fact]
    public async Task SuccessReturnsBodyAndTargetsExtractRoute()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([9, 8, 7])
        }));
        var result = await Client(handler).ExtractAsync([1, 2], "Dethon", CancellationToken.None);
        result.ShouldBe(new byte[] { 9, 8, 7 });
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://tse-extractor:9098/extract?speaker=Dethon");
        handler.LastRequest.Method.ShouldBe(HttpMethod.Post);
    }

    [Fact]
    public async Task UnknownSpeaker404ReturnsNull()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        (await Client(handler).ExtractAsync([1], "ghost", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task TransportErrorReturnsNull()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("boom"));
        (await Client(handler).ExtractAsync([1], "Dethon", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task DeadlineExpiryReturnsNull()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        (await Client(handler, timeoutMs: 50).ExtractAsync([1], "Dethon", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new StubHandler(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var cts = new CancellationTokenSource(50);
        await Should.ThrowAsync<OperationCanceledException>(
            () => Client(handler).ExtractAsync([1], "Dethon", cts.Token));
    }
}