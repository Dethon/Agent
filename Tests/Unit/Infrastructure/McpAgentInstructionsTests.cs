using Domain.Prompts;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentInstructionsTests
{
    [Fact]
    public void BuildInstructions_IncludesCurrentDateAsFirstLine()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 10, 30, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            customInstructions: null,
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.ShouldStartWith("Today is Friday, 2026-05-15.");
    }

    [Fact]
    public void BuildInstructions_KeepsBasePromptAfterDate()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            customInstructions: null,
            domainPrompts: [],
            fileSystemPrompts: [],
            clientPrompts: [],
            now: fixedTime);

        result.ShouldContain(BasePrompt.Instructions);
        result.IndexOf("Today is").ShouldBeLessThan(result.IndexOf(BasePrompt.Instructions));
    }

    [Fact]
    public void BuildInstructions_AppendsAllPromptSections()
    {
        var fixedTime = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero);

        var result = McpAgent.BuildInstructions(
            customInstructions: "CUSTOM",
            domainPrompts: ["DOMAIN"],
            fileSystemPrompts: ["FS"],
            clientPrompts: ["CLIENT"],
            now: fixedTime);

        result.ShouldContain("CUSTOM");
        result.ShouldContain("DOMAIN");
        result.ShouldContain("FS");
        result.ShouldContain("CLIENT");
        result.ShouldContain("Today is");
    }
}
