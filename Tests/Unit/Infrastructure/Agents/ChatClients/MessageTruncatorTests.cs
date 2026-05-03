using System.Text.Json;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class MessageTruncatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("abcdefgh", 2)]
    [InlineData("abcdefghi", 3)]
    public void EstimateTokens_ReturnsCeilingOfCharsDividedByFour(string input, int expected)
    {
        MessageTruncator.EstimateTokens(input).ShouldBe(expected);
    }

    [Fact]
    public void EstimateMessageTokens_TextContent_CountsTextPlusOverhead()
    {
        var msg = new ChatMessage(ChatRole.User, "abcdefgh"); // 8 chars => 2 tokens
        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(2 + 4); // + per-message overhead
    }

    [Fact]
    public void EstimateMessageTokens_FunctionCall_CountsSerializedJsonPlusOverhead()
    {
        var call = new FunctionCallContent("call-1", "doStuff",
            new Dictionary<string, object?> { ["x"] = 1 });
        var msg = new ChatMessage(ChatRole.Assistant, [call]);

        var expectedJson = JsonSerializer.Serialize(new { name = "doStuff", arguments = call.Arguments });
        var expectedTokens = MessageTruncator.EstimateTokens(expectedJson) + 4;

        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(expectedTokens);
    }

    [Fact]
    public void EstimateMessageTokens_FunctionResult_CountsSerializedResultPlusOverhead()
    {
        var result = new FunctionResultContent("call-1", "ok-result");
        var msg = new ChatMessage(ChatRole.Tool, [result]);

        var expectedJson = JsonSerializer.Serialize(result.Result);
        var expectedTokens = MessageTruncator.EstimateTokens(expectedJson) + 4;

        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(expectedTokens);
    }

    [Fact]
    public void EstimateMessageTokens_MultipleContents_SumsAllPlusSingleOverhead()
    {
        var msg = new ChatMessage(ChatRole.User,
            [new TextContent("abcd"), new TextContent("efgh")]); // 1 + 1 = 2 tokens
        MessageTruncator.EstimateMessageTokens(msg).ShouldBe(2 + 4);
    }
}
