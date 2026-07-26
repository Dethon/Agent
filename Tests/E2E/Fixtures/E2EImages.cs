namespace Tests.E2E.Fixtures;

/// <summary>
/// A Docker image an E2E fixture builds. <paramref name="WatchedDirs"/> drives the staleness
/// check in <see cref="TestHelpers.EnsureImageAsync"/>: a source file newer than the image
/// forces a rebuild, so the list must cover every directory the Dockerfile copies in.
/// </summary>
internal sealed record E2EImageSpec(string Dockerfile, string ImageName, IReadOnlyList<string> WatchedDirs);

internal static class E2EImages
{
    internal static E2EImageSpec BaseSdk { get; } = new(
        "Dockerfile.base-sdk", "base-sdk:latest", ["Domain", "Infrastructure"]);

    internal static E2EImageSpec McpVault { get; } = new(
        "McpServerVault/Dockerfile", "mcp-vault:latest", ["Domain", "Infrastructure", "McpServerVault"]);

    internal static E2EImageSpec ChannelSignalR { get; } = new(
        "McpChannelSignalR/Dockerfile", "mcp-channel-signalr:latest", ["Domain", "Infrastructure", "McpChannelSignalR"]);

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