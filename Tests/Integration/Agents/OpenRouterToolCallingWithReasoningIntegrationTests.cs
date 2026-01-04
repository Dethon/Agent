using System.ClientModel;
using System.Text.Json.Nodes;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Integration.Agents;

public class OpenRouterToolCallingWithReasoningIntegrationTests
{
    private static readonly IConfiguration _configuration = new ConfigurationBuilder()
        .AddUserSecrets<OpenRouterToolCallingWithReasoningIntegrationTests>()
        .Build();

    private static (string apiUrl, string apiKey, string model) GetConfig()
    {
        var apiKey = _configuration["openRouter:apiKey"]
                     ?? throw new SkipException("openRouter:apiKey not set in user secrets");
        var apiUrl = _configuration["openRouter:apiUrl"] ?? "https://openrouter.ai/api/v1/";
        var model = _configuration["openRouter:reasoningModel"] ?? "z-ai/glm-4.7";
        return (apiUrl, apiKey, model);
    }

    [SkippableFact]
    public async Task GetStreamingResponseAsync_ToolCallingPlusReasoning_DoesNot400()
    {
        var (apiUrl, apiKey, model) = GetConfig();

        using var baseClient = new OpenAiClient(apiUrl, apiKey, [model], useFunctionInvocation: false);
        using var client = new OpenRouterReasoningChatClient(baseClient);
        using var invoking = new FunctionInvokingChatClient(client)
        {
            IncludeDetailedErrors = true,
            MaximumIterationsPerRequest = 5
        };

        var tool = AIFunctionFactory.Create(() => "ok", "TestTool", "Returns ok");
        var options = new ChatOptions
        {
            Tools = [tool],
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning"] = new JsonObject { ["effort"] = "low" }
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        try
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in invoking.GetStreamingResponseAsync(
                               [new ChatMessage(ChatRole.User, "Call TestTool, then say DONE")],
                               options,
                               cts.Token))
            {
                updates.Add(update);
            }

            updates.ShouldNotBeEmpty();
        }
        catch (ClientResultException ex)
        {
            var raw = ex.GetRawResponse();
            var body = raw?.Content.ToString();
            throw new Exception($"OpenRouter returned {raw?.Status} {raw?.ReasonPhrase}. Body: {body}", ex);
        }
    }
}