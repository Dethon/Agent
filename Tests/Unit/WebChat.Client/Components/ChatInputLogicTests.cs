using Shouldly;
using WebChat.Client.Components;

namespace Tests.Unit.WebChat.Client.Components;

public class ChatInputLogicTests
{
    [Theory]
    [InlineData(false, "hello", false, true)]  // connected, has text, not answering -> can send
    [InlineData(false, "hello", true, false)]  // agent is answering -> cannot send
    [InlineData(true, "hello", false, false)]  // composer disabled (no agent / disconnected)
    [InlineData(false, "", false, false)]      // empty input
    [InlineData(false, "   ", false, false)]   // whitespace-only input
    [InlineData(false, null, false, false)]    // null input
    public void CanSend_ReturnsExpected(bool disabled, string? inputText, bool isStreaming, bool expected)
    {
        ChatInputLogic.CanSend(disabled, inputText, isStreaming).ShouldBe(expected);
    }
}