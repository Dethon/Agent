using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// Two rules arrived here from mcp-vault's deleted agent-level system_prompt. Both are about
// running commands, so they belong to the only mount that runs them — and both are phrased
// without naming another mount, because this server does not know which others are mounted.
public class SandboxPromptTests
{
    [Fact]
    public void Prompt_SaysToPersistResultsRatherThanSteps()
    {
        SandboxPrompt.Prompt.ShouldContain("Persist results, not steps");
    }

    [Fact]
    public void Prompt_RequiresHonestyAboutWhatWasRun()
    {
        SandboxPrompt.Prompt.ShouldContain("Never claim you ran");
    }

    [Fact]
    public void Prompt_NamesNoOtherMount()
    {
        SandboxPrompt.Prompt.ShouldNotContain("/vault");
        SandboxPrompt.Prompt.ShouldNotContain("into a note");
    }
}