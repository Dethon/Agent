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

    [Fact]
    public void Truncate_NullMaxTokens_ReturnsOriginalUnchanged()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.System, "sys"),
            new(ChatRole.User, "hi")
        };

        var result = MessageTruncator.Truncate(
            msgs, null, out var dropped, out var before, out var after);

        result.ShouldBe(msgs);
        dropped.ShouldBe(0);
        before.ShouldBe(after);
    }

    [Fact]
    public void Truncate_UnderThreshold_ReturnsOriginalUnchanged()
    {
        var msgs = new List<ChatMessage>
        {
            new(ChatRole.User, "hi") // tiny
        };

        var result = MessageTruncator.Truncate(
            msgs, 10000, out var dropped, out var before, out var after);

        result.ShouldBe(msgs);
        dropped.ShouldBe(0);
        before.ShouldBe(after);
    }

    [Fact]
    public void Truncate_EmptyList_ReturnsOriginalUnchanged()
    {
        var msgs = new List<ChatMessage>();

        var result = MessageTruncator.Truncate(
            msgs, 100, out var dropped, out var before, out var after);

        result.ShouldBe(msgs);
        dropped.ShouldBe(0);
        before.ShouldBe(0);
        after.ShouldBe(0);
    }
}
