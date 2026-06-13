using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.Mcp;

public class McpFileSystemCapabilitiesTests
{
    [Fact]
    public void DeriveCapabilities_MapsAdvertisedFsToolsToDomainLeafNames_InCanonicalOrder()
    {
        // Home Assistant advertises only read/info/glob/search/exec.
        var caps = McpFileSystemDiscovery.DeriveCapabilities(
            ["fs_glob", "fs_info", "fs_read", "fs_search", "fs_exec"]);

        caps.ShouldBe(["text_read", "glob", "text_search", "file_info", "exec"]);
    }

    [Fact]
    public void DeriveCapabilities_OmitsOperationsTheServerDoesNotExpose()
    {
        // Printer omits fs_move and fs_exec; it does expose create/edit/copy/delete.
        var caps = McpFileSystemDiscovery.DeriveCapabilities(
            ["fs_read", "fs_info", "fs_glob", "fs_search", "fs_create", "fs_edit", "fs_delete", "fs_copy",
             "fs_blob_read", "fs_blob_write"]);

        caps.ShouldNotContain("move");
        caps.ShouldNotContain("exec");
        caps.ShouldContain("text_create");
        caps.ShouldContain("copy");
        caps.ShouldContain("remove");
    }

    [Fact]
    public void DeriveCapabilities_IgnoresBlobAndNonFilesystemTools()
    {
        var caps = McpFileSystemDiscovery.DeriveCapabilities(
            ["fs_read", "fs_blob_read", "fs_blob_write", "send_reply", "some_other_tool"]);

        caps.ShouldBe(["text_read"]);
    }

    [Fact]
    public void DeriveCapabilities_MatchesPrefixedToolNames()
    {
        // Aggregated agent-side names are namespaced (mcp__server__fs_glob); derivation must still match.
        var caps = McpFileSystemDiscovery.DeriveCapabilities(
            ["mcp__mcp-homeassistant__fs_glob", "mcp__mcp-homeassistant__fs_exec"]);

        caps.ShouldBe(["glob", "exec"]);
    }
}