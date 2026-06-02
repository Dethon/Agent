using Domain.DTOs.Printing;
using SharpIpp.Protocol.Models;

namespace Infrastructure.Clients.Printer;

public static class IppJobStateMapper
{
    public static PrintJobState Map(JobState state) => state switch
    {
        JobState.Pending or JobState.PendingHeld => PrintJobState.Pending,
        JobState.Processing or JobState.ProcessingStopped => PrintJobState.Processing,
        JobState.Completed => PrintJobState.Completed,
        JobState.Canceled => PrintJobState.Canceled,
        JobState.Aborted => PrintJobState.Aborted,
        _ => PrintJobState.Unknown
    };

    public static bool IsActive(JobState state) =>
        Map(state) is PrintJobState.Pending or PrintJobState.Processing;
}