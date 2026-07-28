using System.Text;
using System.Text.Json.Nodes;
using Domain.DTOs;
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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

        // Assert
        // The messages array is untouched; the request now also carries usage:{include:true},
        // which is what makes OpenRouter report prompt_tokens_details.cached_tokens.
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = System.Text.Json.Nodes.JsonNode.Parse(resultJson)!.AsObject();
        obj["messages"]!.ToJsonString().ShouldBe(
            System.Text.Json.Nodes.JsonNode.Parse(json)!["messages"]!.ToJsonString());
        obj["usage"]!["include"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task PrepareRequestBody_WithSessionId_AddsTopLevelSessionId()
    {
        // Arrange
        var json = "{\"model\":\"anthropic/claude-sonnet-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";
        var request = CreateRequest(json);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "jack:123:456", null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "  ", null, CancellationToken.None);

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
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, "jack:123:456", null, CancellationToken.None);

        // Assert
        var resultJson = await request.Content!.ReadAsStringAsync();
        var obj = JsonNode.Parse(resultJson);

        obj!["session_id"]!.GetValue<string>().ShouldBe("jack:123:456");
        obj["messages"]![0]!["content"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithFullProviderRouting_MapsEveryFieldToItsOpenRouterKey()
    {
        // Arrange
        var request = CreateRequest(BodyJson);
        var routing = new ProviderRouting
        {
            Sort = ProviderSort.Throughput,
            Order = ["deepinfra", "novita"],
            Only = ["deepinfra"],
            Ignore = ["chutes"],
            AllowFallbacks = false
        };

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, routing, CancellationToken.None);

        // Assert
        var provider = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!;

        provider["sort"]!.GetValue<string>().ShouldBe("throughput");
        provider["order"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["deepinfra", "novita"]);
        provider["only"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["deepinfra"]);
        provider["ignore"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["chutes"]);
        provider["allow_fallbacks"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [InlineData(ProviderSort.Price, "price")]
    [InlineData(ProviderSort.Throughput, "throughput")]
    [InlineData(ProviderSort.Latency, "latency")]
    public async Task PrepareRequestBody_WithSort_SerializesLowercased(ProviderSort sort, string expected)
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, null, new ProviderRouting { Sort = sort }, CancellationToken.None);

        // Assert
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!["sort"]!
            .GetValue<string>().ShouldBe(expected);
    }

    [Fact]
    public async Task PrepareRequestBody_WithPartialProviderRouting_OmitsUnsetFields()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, null, new ProviderRouting { Sort = ProviderSort.Latency }, CancellationToken.None);

        // Assert
        var provider = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!.AsObject();

        provider.Count.ShouldBe(1);
        provider["order"].ShouldBeNull();
        provider["only"].ShouldBeNull();
        provider["ignore"].ShouldBeNull();
        provider["allow_fallbacks"].ShouldBeNull();
    }

    // Balanced load balancing is only available by sending no `sort` and no `order`, so the
    // absence of the whole `provider` key is a behaviour, not an optimisation. This is also the
    // regression guard that today's traffic is unchanged for agents that configure nothing.
    [Fact]
    public async Task PrepareRequestBody_WithNullProviderRouting_OmitsProviderKey()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, null, CancellationToken.None);

        // Assert
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithEmptyProviderRouting_OmitsProviderKey()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, null, new ProviderRouting(), CancellationToken.None);

        // Assert
        JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"].ShouldBeNull();
    }

    [Fact]
    public async Task PrepareRequestBody_WithEmptyArrays_OmitsThoseKeys()
    {
        // Arrange
        var request = CreateRequest(BodyJson);
        var routing = new ProviderRouting { Sort = ProviderSort.Price, Order = [], Only = [], Ignore = [] };

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(request, null, routing, CancellationToken.None);

        // Assert
        var provider = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["provider"]!.AsObject();

        provider.Count.ShouldBe(1);
        provider["sort"]!.GetValue<string>().ShouldBe("price");
    }

    // sort coexists with sticky routing -- only `order` disables it -- so both fields must
    // survive on the same request.
    [Fact]
    public async Task PrepareRequestBody_WithProviderRoutingAndSessionId_KeepsBoth()
    {
        // Arrange
        var request = CreateRequest(BodyJson);

        // Act
        await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
            request, "nabu:123:456", new ProviderRouting { Sort = ProviderSort.Latency },
            CancellationToken.None);

        // Assert
        var obj = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;

        obj["session_id"]!.GetValue<string>().ShouldBe("nabu:123:456");
        obj["provider"]!["sort"]!.GetValue<string>().ShouldBe("latency");
        obj["usage"]!["include"]!.GetValue<bool>().ShouldBeTrue();
    }

    private const string BodyJson =
        "{\"model\":\"anthropic/claude-sonnet-4\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}";

    private static HttpRequestMessage CreateRequest(string jsonContent)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost");
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        return request;
    }
}