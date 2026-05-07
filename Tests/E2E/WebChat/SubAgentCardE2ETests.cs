using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class SubAgentCardE2ETests(WebChatE2EFixture fixture)
{
    // This test requires the configured LLM to deterministically invoke run_subagent
    // with run_in_background=true in response to the prompt. In the E2E environment the
    // agent uses a real OpenRouter-backed LLM (google/gemini-2.5-flash), and LLM tool-call
    // behavior is non-deterministic — the model may paraphrase instead of calling the tool.
    // The test is skipped until a stub-agent or a deterministic tool-call fixture is
    // available that guarantees the subagent card will appear.
    [Fact(Skip = "Requires deterministic LLM tool invocation: LLM may not reliably call run_subagent(run_in_background=true) in E2E environment")]
    public async Task SendMessage_TriggeringBackgroundSubagent_ShowsSubagentCard()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Prompt designed to trigger a background subagent call.
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync(
            "Use run_subagent with run_in_background=true to do a quick research task. " +
            "Use the researcher subagent to find information about the number 42.");
        await chatInput.PressAsync("Enter");

        // The subagent card is rendered inside the message stream once the background
        // subagent session is announced via the channel's SignalR push.
        // The card has the CSS class "subagent-card".
        var subagentCard = page.Locator(".subagent-card");
        await subagentCard.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        (await subagentCard.CountAsync()).ShouldBeGreaterThan(0);

        // The card should show a handle and the subagent name.
        var cardHandle = subagentCard.First.Locator(".subagent-handle");
        var handleText = await cardHandle.TextContentAsync();
        handleText.ShouldNotBeNullOrWhiteSpace();
    }

    [SkippableFact]
    public async Task LoadPage_WebChatStackAvailable()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        // Smoke test: the WebChat stack is reachable.
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var chatInput = page.Locator("textarea.chat-input");
        (await chatInput.IsVisibleAsync()).ShouldBeTrue();
    }
}
