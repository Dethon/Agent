using System.Text;
using Domain.Contracts;
using Domain.DTOs.Printing;
using SharpIpp;
using SharpIpp.Models.Requests;
using SharpIpp.Protocol.Models;

namespace Infrastructure.Clients.Printer;

// Talks IPP over HTTP to a single configured printer (a CUPS server or a direct-IPP printer).
// The document is submitted with documentFormat (default "application/octet-stream", the IPP
// auto-sense type) rather than the spooled content type, which printers often reject.
public sealed class IppPrinterClient(ISharpIppClient client, Uri printerUri, string documentFormat) : IPrinterClient
{
    public async Task<PrintJobHandle> SubmitAsync(string jobName, string contentType, ReadOnlyMemory<byte> document, CancellationToken ct)
    {
        await using var stream = new MemoryStream(PreparePayload(contentType, document), writable: false);
        var request = new PrintJobRequest
        {
            Document = stream,
            OperationAttributes = new PrintJobOperationAttributes
            {
                PrinterUri = printerUri,
                JobName = jobName,
                DocumentName = jobName,
                DocumentFormat = documentFormat
            }
        };

        var response = await client.PrintJobAsync(request, ct);
        return new PrintJobHandle(response.JobAttributes?.JobId ?? 0);
    }

    public async Task<IReadOnlyList<PrintJobStatus>> GetActiveJobsAsync(CancellationToken ct)
    {
        var request = new GetJobsRequest
        {
            OperationAttributes = new GetJobsOperationAttributes
            {
                PrinterUri = printerUri,
                WhichJobs = WhichJobs.NotCompleted
            }
        };

        var response = await client.GetJobsAsync(request, ct);
        return (response.JobsAttributes ?? [])
            .Where(j => j.JobState is not null && IppJobStateMapper.IsActive(j.JobState.Value))
            .Select(j => new PrintJobStatus(
                j.JobId ?? 0,
                j.JobName ?? string.Empty,
                IppJobStateMapper.Map(j.JobState!.Value)))
            .ToList();
    }

    public async Task CancelAsync(int jobId, CancellationToken ct)
    {
        var request = new CancelJobRequest
        {
            OperationAttributes = new CancelJobOperationAttributes
            {
                PrinterUri = printerUri,
                JobId = jobId
            }
        };

        await client.CancelJobAsync(request, ct);
    }

    // Raw text sent to a printer staircases when lines end in bare LF: the printer feeds a line
    // but does not return the carriage. Normalize text payloads to CRLF; binary payloads are passed
    // through untouched so their bytes are never rewritten.
    internal static byte[] PreparePayload(string contentType, ReadOnlyMemory<byte> document)
    {
        if (!IsTextPayload(contentType, document))
        {
            return document.ToArray();
        }

        var text = Encoding.UTF8.GetString(document.Span);
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        return Encoding.UTF8.GetBytes(normalized);
    }

    // text/* is text. The catch-all "application/octet-stream" (used by cross-backend copies, which
    // lose the real content type) is content-sniffed: text iff it has no NUL byte and is valid UTF-8.
    // Any other explicit type (image/*, application/pdf, ...) is treated as binary.
    internal static bool IsTextPayload(string contentType, ReadOnlyMemory<byte> document)
    {
        var type = contentType ?? "";
        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (type.Length > 0 && !type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var span = document.Span;
        if (span.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(span);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}