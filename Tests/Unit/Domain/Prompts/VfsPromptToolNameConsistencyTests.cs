using System.Text.RegularExpressions;
using Domain.Prompts;
using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// The LLM-facing VFS prompts must reference the tool names that are actually exposed
// (domain__filesystem__<leaf>, e.g. text_create / remove / glob). No tool is exposed under
// an `fs_` prefix — those are the raw MCP tools, which are filtered out when the domain
// filesystem tools are active — so a prompt mentioning `fs_create` / `fs_delete` teaches a
// name the model can never call.
public class VfsPromptToolNameConsistencyTests
{
    public static IEnumerable<object[]> VfsPrompts =>
    [
        ["scheduling_prompt", SchedulingPrompt.Prompt],
        ["printing_prompt", PrintingPrompt.Build("text,jpeg")],
        ["downloader_prompt", DownloaderPrompt.AgentSystemPrompt]
    ];

    [Theory]
    [MemberData(nameof(VfsPrompts))]
    public void Prompt_DoesNotReferenceNonexistentFsPrefixedToolNames(string name, string prompt)
    {
        var phantomNames = Regex.Matches(prompt, @"\bfs_[a-z_]+")
            .Select(m => m.Value)
            .Distinct()
            .ToList();

        phantomNames.ShouldBeEmpty(
            $"{name} references tool names that are not exposed to the model: {string.Join(", ", phantomNames)}");
    }

    [Fact]
    public void SchedulingPrompt_ReferencesActualExposedToolLeafNames()
    {
        var prompt = SchedulingPrompt.Prompt;

        prompt.ShouldContain($"`{VfsTextCreateTool.Name}`");
        prompt.ShouldContain($"`{VfsGlobFilesTool.Name}`");
        prompt.ShouldContain($"`{VfsTextEditTool.Name}`");
        prompt.ShouldContain($"`{VfsMoveTool.Name}`");
        prompt.ShouldContain($"`{VfsRemoveTool.Name}`");
        prompt.ShouldContain($"`{VfsExecTool.Name}`");
    }
}