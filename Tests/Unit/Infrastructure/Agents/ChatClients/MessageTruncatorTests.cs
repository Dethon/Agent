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

    [Fact]
    public void Truncate_OverThreshold_DropsOldestNonPinnedMessage()
    {
        // Build 4 messages so per-message text dominates.
        // Each "x" * 80 -> 20 tokens + 4 overhead = 24 tokens per message.
        var sys  = new ChatMessage(ChatRole.System,    new string('s', 80));
        var u1   = new ChatMessage(ChatRole.User,      new string('a', 80));
        var a1   = new ChatMessage(ChatRole.Assistant, new string('b', 80));
        var u2   = new ChatMessage(ChatRole.User,      new string('c', 80)); // last user (pinned)
        var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

        // total = 96. Threshold at 95% of 80 = 76. Need to drop until <= 76.
        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 80,
            out var dropped, out var before, out var after);

        dropped.ShouldBeGreaterThanOrEqualTo(1);
        result.ShouldContain(sys);                // system pinned
        result.ShouldContain(u2);                 // last user pinned
        result.ShouldNotContain(u1);              // oldest non-pinned dropped first
        after.ShouldBeLessThanOrEqualTo(76);
        before.ShouldBe(96);
    }

    [Fact]
    public void Truncate_AlwaysPreservesAllSystemMessages()
    {
        var sys1 = new ChatMessage(ChatRole.System, new string('a', 400));
        var sys2 = new ChatMessage(ChatRole.System, new string('b', 400));
        var u1   = new ChatMessage(ChatRole.User,   new string('c', 80));
        var msgs = new List<ChatMessage> { sys1, sys2, u1 };

        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 50,
            out _, out _, out _);

        result.ShouldContain(sys1);
        result.ShouldContain(sys2);
        result.ShouldContain(u1); // last user always preserved
    }

    [Fact]
    public void Truncate_StopsDroppingOnceUnderThreshold()
    {
        var sys = new ChatMessage(ChatRole.System,    new string('s', 4));
        var u1  = new ChatMessage(ChatRole.User,      new string('a', 80));
        var a1  = new ChatMessage(ChatRole.Assistant, new string('b', 80));
        var u2  = new ChatMessage(ChatRole.User,      new string('c', 4));
        var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

        // Totals: sys=5, u1=24, a1=24, u2=5 → 58. Threshold floor(40*0.95)=38.
        // 58 > 38, drop u1 (oldest non-pinned) → 34, which is ≤ 38, stop.
        var result = MessageTruncator.Truncate(
            msgs, maxContextTokens: 40,
            out var dropped, out _, out _);

        dropped.ShouldBe(1);
        result.ShouldContain(a1); // not dropped — already under threshold
        result.ShouldNotContain(u1);
    }
}
