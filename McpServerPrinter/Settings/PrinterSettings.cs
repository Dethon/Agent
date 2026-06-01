namespace McpServerPrinter.Settings;

public record PrinterSettings
{
    public required string PrinterUri { get; init; }
    public string SpoolPath { get; init; } = "/spool";
    public int SubmitDebounceMilliseconds { get; init; } = 750;
    public int TickIntervalMilliseconds { get; init; } = 500;

    // IPP document-format sent to the printer. "application/octet-stream" is the IPP
    // auto-sense default supported by virtually all printers (the printer detects the real
    // format from the bytes); sending a specific type like "text/plain" is rejected by
    // printers that do not advertise it.
    public string DocumentFormat { get; init; } = "application/octet-stream";
}