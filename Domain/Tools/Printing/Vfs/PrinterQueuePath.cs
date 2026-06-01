namespace Domain.Tools.Printing.Vfs;

public enum PrinterNodeKind
{
    Root,
    DocumentFile,
    StatusFile,
    Unknown
}

public sealed record PrinterQueueNode(PrinterNodeKind Kind, string? FileName);

public static class PrinterQueuePath
{
    public const string StatusFileName = "status.json";

    public static PrinterQueueNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (Array.Exists(segments, s => s is "." or ".."))
        {
            return new PrinterQueueNode(PrinterNodeKind.Unknown, null);
        }

        return segments switch
        {
            [] => new PrinterQueueNode(PrinterNodeKind.Root, null),
            [StatusFileName] => new PrinterQueueNode(PrinterNodeKind.StatusFile, null),
            [var file] => new PrinterQueueNode(PrinterNodeKind.DocumentFile, file),
            _ => new PrinterQueueNode(PrinterNodeKind.Unknown, null)
        };
    }
}