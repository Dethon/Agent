using System.Net;
using Infrastructure.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Moq.Protected;
using Shouldly;

namespace Tests.Unit.Extensions;

public class HttpClientBuilderExtensionsTests
{
    private const string TestClientName = "TestClient";
    private const string TestUrl = "https://test.com";

    [Fact]
    public async Task AddRetryWithExponentialWaitPolicy_ShouldRetry_OnTransientErrors()
    {
        // given
        const int maxAttempts = 3;
        var mockMessageHandler = SetupTransientErrorsThenSuccessHandler(maxAttempts);
        var client = ConfigureHttpClientWithRetryPolicy(
            mockMessageHandler.Object,
            maxAttempts,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromSeconds(1));

        // when
        var response = await client.GetAsync(TestUrl);

        // then
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        VerifyHandlerCalls(mockMessageHandler, maxAttempts);
    }

    [Fact]
    public async Task AddRetryWithExponentialWaitPolicy_ShouldRetryOnTimeout_WhenAttemptTakesTooLong()
    {
        // given
        const int maxAttempts = 3;
        var attemptTimeout = TimeSpan.FromMilliseconds(200);
        var mockMessageHandler = SetupDelayedResponseHandler(attemptTimeout * 2);
        var client = ConfigureHttpClientWithRetryPolicy(
            mockMessageHandler.Object,
            maxAttempts,
            TimeSpan.FromMilliseconds(10),
            attemptTimeout);

        // when/then
        await Should.ThrowAsync<Exception>(() => client.GetAsync(TestUrl));
        VerifyHandlerCalls(mockMessageHandler, maxAttempts + 1);
    }

    private static Mock<HttpMessageHandler> SetupTransientErrorsThenSuccessHandler(int successOnAttempt)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        var requestCount = 0;

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(() =>
            {
                requestCount++;
                return requestCount < successOnAttempt
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK);
            });

        return mockHandler;
    }

    private static Mock<HttpMessageHandler> SetupDelayedResponseHandler(TimeSpan delay)
    {
        var mockHandler = new Mock<HttpMessageHandler>();

        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .Returns(async (HttpRequestMessage _, CancellationToken cancellationToken) =>
            {
                await Task.Delay(delay, cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        return mockHandler;
    }

    private static HttpClient ConfigureHttpClientWithRetryPolicy(
        HttpMessageHandler handler,
        int maxAttempts,
        TimeSpan retryDelay,
        TimeSpan timeout)
    {
        var services = new ServiceCollection();

        services.AddHttpClient(TestClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddRetryWithExponentialWaitPolicy(maxAttempts, retryDelay, timeout);

        var serviceProvider = services.BuildServiceProvider();
        var clientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
        return clientFactory.CreateClient(TestClientName);
    }

    private static void VerifyHandlerCalls(Mock<HttpMessageHandler> mockHandler, int expectedCalls)
    {
        mockHandler.Protected().Verify(
            "SendAsync",
            Times.Exactly(expectedCalls),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>()
        );
    }
}