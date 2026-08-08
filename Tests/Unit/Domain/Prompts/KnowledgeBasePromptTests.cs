using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// mcp-vault's system_prompt is the only tool-server prompt that used to open by telling the model
// who it was ("You help the user manage a personal knowledge vault...") and close by telling it how
// to write. Both are the agent's own business: identity comes from IdentityPrompt and the agent's
// customInstructions, and those are appended last precisely so they outrank everything a server
// says. A voice agent inherited a scribe's persona and a written-reply style it then had to be
// argued out of. What stays is the part only this server knows: how its two mounts differ and when
// to use which.
public class KnowledgeBasePromptTests
{
    [Fact]
    public void AgentSystemPrompt_ClaimsNoRole()
    {
        KnowledgeBasePrompt.AgentSystemPrompt.ShouldNotContain("Your Role");
        KnowledgeBasePrompt.AgentSystemPrompt.ShouldNotContain("You help the user");
    }

    [Fact]
    public void AgentSystemPrompt_DictatesNoResponseStyle()
    {
        KnowledgeBasePrompt.AgentSystemPrompt.ShouldNotContain("Response style");
        KnowledgeBasePrompt.AgentSystemPrompt.ShouldNotContain("as few words as the request allows");
    }

    // The two mounts describe themselves in vault_prompt and sandbox_prompt; restating them here
    // is what made this prompt a duplicate. Choosing between them is stated nowhere else.
    [Fact]
    public void AgentSystemPrompt_DescribesNeitherMount()
    {
        KnowledgeBasePrompt.AgentSystemPrompt.ShouldNotContain("an Obsidian-managed directory");
        KnowledgeBasePrompt.AgentSystemPrompt.ShouldNotContain("a Linux container where you can run");
    }

    [Fact]
    public void AgentSystemPrompt_KeepsTheSurfaceChoiceGuidance()
    {
        var prompt = KnowledgeBasePrompt.AgentSystemPrompt;

        prompt.ShouldContain("### Working in the vault");
        prompt.ShouldContain("### Working in the sandbox");
        prompt.ShouldContain("### Choosing between them");
        // The rule a caller cannot derive from either mount on its own.
        prompt.ShouldContain("cannot reach `/vault` directly");
        prompt.ShouldContain("read before you edit");
    }
}