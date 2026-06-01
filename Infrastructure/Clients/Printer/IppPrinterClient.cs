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
    // but does not return the carriage. Normalize text payloads to CRLF; binary payloads (sent as
    // octet-stream and auto-sensed) are passed through untouched so their bytes are never rewritten.
    internal static byte[] PreparePayload(string contentType, ReadOnlyMemory<byte> document)
    {
        if (string.IsNullOrEmpty(contentType) || !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return document.ToArray();
        }

        var text = Encoding.UTF8.GetString(document.Span);
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
        return Encoding.UTF8.GetBytes(normalized);
    }
}