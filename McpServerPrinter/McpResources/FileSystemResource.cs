using System.ComponentModel;
using System.Text.Json;
using Domain.Prompts;
using McpServerPrinter.Settings;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpResources;

[McpServerResourceType]
public class FileSystemResource(PrinterSettings settings)
{
    [McpServerResource(UriTemplate = "filesystem://print-queue", Name = "Print Queue Filesystem", MimeType = "application/json")]
    [Description("Printer queue exposed as a filesystem")]
    public string GetInfo() => JsonSerializer.Serialize(new
    {
        name = "print-queue",
        mountPoint = "/print-queue",
        description =
            "A printer exposed as a flat filesystem. Copy or create a document at /print-queue/<filename> " +
            "to print it on the configured printer; the document is submitted automatically. Accepted formats: " +
            $"{PrintingPrompt.DescribeFormats(settings.SupportedFormats)} - any other format is rejected on copy-in, " +
            "so first convert whatever you want to print into an accepted format (typically text or JPEG, e.g. render " +
            "a PDF or PNG to a JPEG) and copy that in. Remove a file with fs_delete to cancel it if it has not " +
            "finished printing yet. Read /print-queue/status.json for the state of every queued job " +
            "(queued/pending/processing). Finished jobs disappear from the listing automatically. Supported: " +
            "read, create, edit (text only), copy, glob, search, delete, and binary copy-in. Not supported: move and exec."
    });
}