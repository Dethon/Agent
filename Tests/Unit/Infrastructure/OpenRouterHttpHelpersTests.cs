using System.Text;
using System.Text.Json.Nodes;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class OpenRouterHttpHelpersTests
{
    [Fact]
    public async Task FixEmptyAssistantContent_WithEmptyString_RemovesContent()
    {
        // Arrange
        var json = "{\"messages\":[{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[]}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);
        var msg = obj!["messages"]![0]!;

        msg["content"].ShouldBeNull();
        msg["tool_calls"].ShouldNotBeNull();
    }

    [Fact]
    public async Task FixEmptyAssistantContent_WithArrayAndEmptyText_RemovesEmptyText()
    {
        // Arrange
        var json =
            "{\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"\"},{\"type\":\"text\",\"text\":\"valid\"}],\"tool_calls\":[]}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);
        var content = obj!["messages"]![0]!["content"]!.AsArray();

        content.Count.ShouldBe(1);
        content[0]!["text"]!.GetValue<string>().ShouldBe("valid");
    }

    [Fact]
    public async Task FixEmptyAssistantContent_WithArrayOnlyEmptyText_RemovesContent()
    {
        // Arrange
        var json =
            "{\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"\"}],\"tool_calls\":[]}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);
        var msg = obj!["messages"]![0]!;

        msg["content"].ShouldBeNull();
    }

    [Fact]
    public async Task FixEmptyAssistantContent_WithNoToolCalls_RemovesEmptyContent()
    {
        // Arrange
        var json = "{\"messages\":[{\"role\":\"assistant\",\"content\":\"\"}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);
        var msg = obj!["messages"]![0]!;

        msg["content"].ShouldBeNull();
    }

    [Fact]
    public async Task FixEmptyAssistantContent_WithValidContent_DoesNothing()
    {
        // Arrange
        var json = "{\"messages\":[{\"role\":\"assistant\",\"content\":\"valid content\",\"tool_calls\":[]}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        // Should be unchanged
        resultJson.ShouldBe(json);
    }

    [Fact]
    public async Task PrepareRequestBody_WithSessionId_AddsTopLevelSessionId()
    {
        // Arrange
        var json = "{\"model\":\"anthropic/claude-sonnet-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "jack:123:456", CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);

        obj!["session_id"]!.GetValue<string>().ShouldBe("jack:123:456");
    }

    [Fact]
    public async Task PrepareRequestBody_WithNullSessionId_OmitsSessionId()
    {
        // Arrange
        var json = "{\"model\":\"anthropic/claude-sonnet-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);

        obj!["session_id"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithEmptySessionId_OmitsSessionId()
    {
        // Arrange
        var json = "{\"model\":\"anthropic/claude-sonnet-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "  ", CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);

        obj!["session_id"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithSessionId_StillFixesEmptyAssistantContent()
    {
        // Arrange
        var json =
            "{\"messages\":[{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[]}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "jack:123:456", CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);

        obj!["session_id"]!.GetValue<string>().ShouldBe("jack:123:456");
        obj["messages"]![0]!["content"].ShouldBeNull();
    }

    private static HttpRequestMessage CreateRequest(string jsonContent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost");
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        return request;
    }
}