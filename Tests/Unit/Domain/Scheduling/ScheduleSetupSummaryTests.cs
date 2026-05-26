using Domain.Agents;
using Domain.DTOs.Channel;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling;

public class ScheduleSetupSummaryTests
{
    private static MutableAgentCatalog Catalog(params AgentCatalogEntry[] agents)
    {
        var cat = new MutableAgentCatalog();
        cat.Replace(agents);
        return cat;
    }

    [Fact]
    public void Get_EmptyCatalog_ReturnsEmpty()
    {
        var summary = new ScheduleSetupSummary(Catalog());

        summary.Get().ShouldBeEmpty();
    }

    [Fact]
    public void Get_SingleAgentWithDescription_RendersPathAndDescription()
    {
        var summary = new ScheduleSetupSummary(Catalog(
            new AgentCatalogEntry("assistant", "Jonas", "General-purpose home assistant.")));

        var text = summary.Get();

        text.ShouldContain("## Current scheduling setup");
        text.ShouldContain("Mounted at `/schedules`");
        text.ShouldContain("/schedules/assistant");
        text.ShouldContain("### Agent descriptions");
        text.ShouldContain("- `assistant` (Jonas) — General-purpose home assistant.");
    }

    [Fact]
    public void Get_MultipleAgents_SortsAlphabeticallyInBothSections()
    {
        var summary = new ScheduleSetupSummary(Catalog(
            new AgentCatalogEntry("downloader", "Captain", "Media acquisition."),
            new AgentCatalogEntry("assistant", "Jonas", "Home assistant."),
            new AgentCatalogEntry("librarian", "Books", "Knowledge curator.")));

        var text = summary.Get();

        var pathIdxAssistant = text.IndexOf("/schedules/assistant", StringComparison.Ordinal);
        var pathIdxDownloader = text.IndexOf("/schedules/downloader", StringComparison.Ordinal);
        var pathIdxLibrarian = text.IndexOf("/schedules/librarian", StringComparison.Ordinal);
        pathIdxAssistant.ShouldBeLessThan(pathIdxDownloader);
        pathIdxDownloader.ShouldBeLessThan(pathIdxLibrarian);

        var bulletIdxAssistant = text.IndexOf("- `assistant`", StringComparison.Ordinal);
        var bulletIdxDownloader = text.IndexOf("- `downloader`", StringComparison.Ordinal);
        var bulletIdxLibrarian = text.IndexOf("- `librarian`", StringComparison.Ordinal);
        bulletIdxAssistant.ShouldBeLessThan(bulletIdxDownloader);
        bulletIdxDownloader.ShouldBeLessThan(bulletIdxLibrarian);
    }

    [Fact]
    public void Get_AgentWithoutDescription_DropsEmDashTail()
    {
        var summary = new ScheduleSetupSummary(Catalog(
            new AgentCatalogEntry("solo", "Solo", null),
            new AgentCatalogEntry("blank", "Blank", "   ")));

        var text = summary.Get();

        text.ShouldContain("- `solo` (Solo)\n");
        text.ShouldNotContain("- `solo` (Solo) —");
        text.ShouldContain("- `blank` (Blank)\n");
        text.ShouldNotContain("- `blank` (Blank) —");
    }
}