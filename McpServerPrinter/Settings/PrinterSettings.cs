namespace McpServerPrinter.Settings;

public record PrinterSettings
{
    public required string PrinterUri { get; init; }
    public string SpoolPath { get; init; } = "/spool";
    public int SubmitDebounceMilliseconds { get; init; } = 750;
    public int TickIntervalMilliseconds { get; init; } = 500;
}