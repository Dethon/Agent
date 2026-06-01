using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.Printing;
using Infrastructure.Utils;
using McpServerPrinter.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerPrinter.McpTools;

[McpServerToolType]
public class FsBlobWriteTool(IPrintSpool spool, PrinterSettings settings)
{
    [McpServerTool(Name = "fs_blob_write")]
    [Description("Write a chunk of raw bytes (base64) to a queued document. offset=0 starts it; the document prints once writes go quiet.")]
    public async Task<CallToolResult> McpRun(
        string path, string contentBase64, long offset = 0, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default)
    {
        var fileName = path.TrimStart('/');
        var bytes = Convert.FromBase64String(contentBase64);

        // The first chunk carries the file header; reject formats the printer cannot render before
        // anything is spooled, so unsupported files fail the copy instead of printing as gibberish.
        if (offset == 0)
        {
            var format = PrintableContent.DetectFormat(bytes);
            if (!PrintableContent.IsSupported(format, settings.SupportedFormats))
            {
                return ToolResponse.Create(new ToolErrorResult
                {
                    ErrorCode = ToolError.Codes.UnsupportedOperation,
                    Message = $"'{fileName}' looks like '{format}', which this printer cannot render. Supported formats: {settings.SupportedFormats}.",
                    Retryable = false,
                    Hint = "Convert it to a supported format first (e.g. export an image as JPEG)."
                }.ToNode());
            }
        }

        await spool.WriteBytesAsync(fileName, "application/octet-stream", bytes, offset, overwrite, ct);
        var entry = await spool.GetAsync(fileName, ct);
        return ToolResponse.Create(FsResultContract.ToNode(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = entry?.SizeBytes ?? bytes.Length
        }));
    }
}