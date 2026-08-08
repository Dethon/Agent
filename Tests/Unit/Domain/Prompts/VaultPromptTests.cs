using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// The vault's editing rules used to be stated twice: here, and again in mcp-vault's separate
// agent-level system_prompt, which every agent mounting the vault also loaded. The system prompt
// is gone; the one rule it held that this file did not is the guard below.
public class VaultPromptTests
{
    [Fact]
    public void Prompt_GuardsAnIrreversibleChange()
    {
        VaultPrompt.Prompt.ShouldContain("cannot restore");
    }

    // These were the duplicated pair. They belong to the mount that owns the files.
    [Fact]
    public void Prompt_KeepsTheEditingRulesTheSystemPromptRestated()
    {
        VaultPrompt.Prompt.ShouldContain("Read before you edit");
        VaultPrompt.Prompt.ShouldContain("Prefer surgical edits");
    }
}