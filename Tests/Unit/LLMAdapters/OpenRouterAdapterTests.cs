using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Domain.DTOs;
using Infrastructure.LLMAdapters.OpenRouter;
using JetBrains.Annotations;
using Moq;
using Moq.Protected;
using Shouldly;

namespace Tests.Unit.LLMAdapters;

public class OpenRouterAdapterTests
{
    private const string ModelName = "test-model";
    private const string BaseUrl = "https://api.openrouter.ai/api/v1/";

    private readonly Mock<HttpMessageHandler> _handlerMock;
    private readonly OpenRouterAdapter _adapter;
    private readonly JsonSerializerOptions _jsonOptions;

    public OpenRouterAdapterTests()
    {
        _handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var httpClient = new HttpClient(_handlerMock.Object)
        {
            BaseAddress = new Uri(BaseUrl)
        };
        _adapter = new OpenRouterAdapter(httpClient, ModelName);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            Converters =
            {
                new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower)
            }
        };
    }

    [Fact]
    public async Task Prompt_ShouldReturnCorrectResponse_WhenApiCallSucceeds()
    {
        // given
        var messages = CreateUserMessage("Hello");
        var response = CreateSuccessResponse("I'm an AI assistant", null);

        SetupMockResponse(HttpStatusCode.OK, response);

        // when
        var result = await InvokePrompt(messages, []);

        // then
        AssertSingleResponseWithContent(result, "I'm an AI assistant");
        AssertStopReason(result, StopReason.Stop);
    }

    [Fact]
    public async Task Prompt_ShouldReturnToolCallResponse_WhenApiReturnsToolCall()
    {
        // given
        var messages = CreateUserMessage("Use a tool");
        var tools = CreateTools("test_tool", "A test tool", typeof(TestToolParams));

        const string toolCallId = "call_123456";
        const string toolName = "test_tool";
        var toolArgs = new JsonObject
        {
            ["param1"] = "value1"
        };

        var response = CreateToolCallResponse(toolCallId, toolName, toolArgs.ToJsonString());

        SetupMockResponse(HttpStatusCode.OK, response);

        // when
        var result = await InvokePrompt(messages, tools);

        // then
        AssertStopReason(result, StopReason.ToolCalls);
        AssertToolCall(result, toolCallId, toolName);
    }

    [Fact]
    public async Task Prompt_ShouldRetry_WhenApiReturnsError()
    {
        // given
        var messages = CreateUserMessage("Hello");

        var errorResponse = CreateErrorResponse();
        var successResponse = CreateSuccessResponse("I'm an AI assistant", null);

        SetupSequentialResponses(errorResponse, successResponse);

        // when
        var result = await InvokePrompt(messages, []);

        // then
        AssertSingleResponseWithContent(result, "I'm an AI assistant");
        VerifyHttpCallCount(2);
    }

    [Fact]
    public async Task Prompt_ShouldThrowException_AfterMaxRetries()
    {
        // given
        var messages = CreateUserMessage("Hello");
        var errorResponse = CreateErrorResponse();

        SetupAlwaysErrorResponse(errorResponse);

        // when/then
        await Should.ThrowAsync<Exception>(async () =>
            await InvokePrompt(messages, []));

        VerifyHttpCallCount(3);
    }

    [Fact]
    public async Task Prompt_ShouldRespectCancellationToken_WhenCancelled()
    {
        // given
        var messages = CreateUserMessage("Hello");
        var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        // when/then
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await _adapter.Prompt(messages, [], cancellationToken: cts.Token));

        VerifyHttpCallCount(0);
    }

    [Fact]
    public async Task Prompt_ShouldIncludeTemperature_WhenProvided()
    {
        // given
        var messages = CreateUserMessage("Hello");
        const float temperature = 0.7f;

        var response = CreateSuccessResponse("Response", null);
        string? capturedRequest = null;

        SetupMockResponseWithRequestCapture(response, request => capturedRequest = request);

        // when
        await _adapter.Prompt(messages, [], temperature);

        // then
        AssertRequestContainsTemperature(capturedRequest, temperature);
    }

    #region Helper Methods for Tests

    private static Message[] CreateUserMessage(string content)
    {
        return
        [
            new Message
            {
                Role = Role.User,
                Content = content
            }
        ];
    }

    private static ToolDefinition[] CreateTools(string name, string description, Type paramsType)
    {
        return
        [
            new ToolDefinition<TestToolParams>
            {
                Name = name,
                Description = description,
                ParamsType = paramsType
            }
        ];
    }

    private async Task<AgentResponse[]> InvokePrompt(
        Message[] messages,
        ToolDefinition[] tools,
        float? temperature = null)
    {
        return await _adapter.Prompt(messages, tools, temperature);
    }

    private static void AssertSingleResponseWithContent(AgentResponse[] response, string expectedContent)
    {
        response.ShouldNotBeNull();
        response.Length.ShouldBe(1);
        response[0].Content.ShouldBe(expectedContent);
    }

    private static void AssertStopReason(AgentResponse[] response, StopReason expectedReason)
    {
        response.ShouldNotBeNull();
        response.Length.ShouldBe(1);
        response[0].StopReason.ShouldBe(expectedReason);
    }

    private static void AssertToolCall(AgentResponse[] response, string expectedToolCallId, string expectedToolName)
    {
        response.ShouldNotBeNull();
        response.Length.ShouldBe(1);
        response[0].ToolCalls.ShouldNotBeEmpty();
        response[0].ToolCalls[0].Id.ShouldBe(expectedToolCallId);
        response[0].ToolCalls[0].Name.ShouldBe(expectedToolName);
        response[0].ToolCalls[0].Parameters.ShouldNotBeNull();
    }

    private void VerifyHttpCallCount(int count)
    {
        _handlerMock.Protected().Verify(
            "SendAsync",
            Times.Exactly(count),
            ItExpr.IsAny<HttpRequestMessage>(),
            ItExpr.IsAny<CancellationToken>());
    }

    private static void AssertRequestContainsTemperature(string? request, float expectedTemperature)
    {
        request.ShouldNotBeNull();
        request.ShouldNotBeEmpty();
        var requestObject = JsonSerializer.Deserialize<JsonObject>(request);
        requestObject.ShouldNotBeNull();
        requestObject.TryGetPropertyValue("temperature", out var tempNode).ShouldBeTrue();
        tempNode?.GetValue<float>().ShouldBe(expectedTemperature);
    }

    private void SetupSequentialResponses(OpenRouterResponse firstResponse, OpenRouterResponse secondResponse)
    {
        _handlerMock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(CreateHttpResponse(firstResponse))
            .ReturnsAsync(CreateHttpResponse(secondResponse));
    }

    private HttpResponseMessage CreateHttpResponse(object content)
    {
        return new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(
                JsonSerializer.Serialize(content, _jsonOptions),
                Encoding.UTF8,
                "application/json")
        };
    }

    private void SetupAlwaysErrorResponse(OpenRouterResponse errorResponse)
    {
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => CreateHttpResponse(errorResponse));
    }

    private void SetupMockResponseWithRequestCapture(
        OpenRouterResponse response,
        Action<string> requestCapture)
    {
        string requestContent;
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            // ReSharper disable once AsyncVoidMethod
            .Callback<HttpRequestMessage, CancellationToken>(async void (request, cancellationToken) =>
            {
                if (request.Content == null)
                {
                    return;
                }

                requestContent = await request.Content.ReadAsStringAsync(cancellationToken);
                requestCapture(requestContent);
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(response, _jsonOptions))
            });
    }

    private void SetupMockResponse(HttpStatusCode statusCode, object content)
    {
        var serializedContent = JsonSerializer.Serialize(content, _jsonOptions);
        _handlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.Method == HttpMethod.Post &&
                    req.RequestUri != null &&
                    req.RequestUri.PathAndQuery.EndsWith("chat/completions")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(serializedContent, Encoding.UTF8, "application/json")
            })
            .Verifiable();
    }

    private static OpenRouterResponse CreateSuccessResponse(string content, string? reasoning)
    {
        return new OpenRouterResponse
        {
            Choices =
            [
                new OpenRouterResponseChoice
                {
                    FinishReason = FinishReason.Stop,
                    Message = new OpenRouterMessage
                    {
                        Role = "assistant",
                        Content = content,
                        Reasoning = reasoning
                    }
                }
            ]
        };
    }

    private static OpenRouterResponse CreateToolCallResponse(string toolCallId, string toolName, string arguments)
    {
        return new OpenRouterResponse
        {
            Choices =
            [
                new OpenRouterResponseChoice
                {
                    FinishReason = FinishReason.ToolCalls,
                    Message = new OpenRouterMessage
                    {
                        Role = "assistant",
                        Content = "",
                        ToolCalls =
                        [
                            new OpenRouterToolCall
                            {
                                Id = toolCallId,
                                Type = "function",
                                Function = new OpenRouterFunctionCall
                                {
                                    Name = toolName,
                                    Arguments = arguments
                                }
                            }
                        ]
                    }
                }
            ]
        };
    }

    private static OpenRouterResponse CreateErrorResponse()
    {
        return new OpenRouterResponse
        {
            Choices =
            [
                new OpenRouterResponseChoice
                {
                    FinishReason = FinishReason.Error,
                    Message = new OpenRouterMessage
                    {
                        Role = "assistant",
                        Content = "An error occurred"
                    }
                }
            ]
        };
    }

    #endregion
}

public record TestToolParams
{
    [UsedImplicitly] public string Param1 { get; set; } = string.Empty;
}