namespace Tests.E2E.Fixtures;

// WatchedDirs drives the staleness check in TestHelpers.EnsureImageAsync: a source file newer
// than the image forces a rebuild, so the list must cover every directory the Dockerfile copies in.
internal sealed record E2EImageSpec(string Dockerfile, string ImageName, IReadOnlyList<string> WatchedDirs);

internal static class E2EImages
{
    internal static E2EImageSpec BaseSdk { get; } = new(
        "Dockerfile.base-sdk", "base-sdk:latest", ["Domain", "Infrastructure", "Channels.Hosting"]);

    internal static E2EImageSpec McpVault { get; } = new(
        "McpServerVault/Dockerfile", "mcp-vault:latest", ["Domain", "Infrastructure", "McpServerVault"]);

    // Channels.Hosting is compiled into base-sdk, not copied by this Dockerfile, but the leaf's
    // freshness is stamp-based rather than layer-based: without it here, a change to the channel
    // contract would rebuild base-sdk and leave this image pinned to the old one.
    internal static E2EImageSpec ChannelSignalR { get; } = new(
        "McpChannelSignalR/Dockerfile", "mcp-channel-signalr:latest",
        ["Domain", "Infrastructure", "Channels.Hosting", "McpChannelSignalR"]);

    internal static E2EImageSpec Agent { get; } = new(
        "Agent/Dockerfile", "agent:latest", ["Domain", "Infrastructure", "Agent"]);

    internal static E2EImageSpec WebUi { get; } = new(
        "WebChat/Dockerfile", "webui:latest", ["Domain", "WebChat", "WebChat.Client"]);

    internal static E2EImageSpec Observability { get; } = new(
        "Observability/Dockerfile", "observability:latest",
        ["Domain", "Infrastructure", "Dashboard.Client", "Observability"]);

    internal static IReadOnlyList<E2EImageSpec> All { get; } =
        [BaseSdk, McpVault, ChannelSignalR, Agent, WebUi, Observability];
}