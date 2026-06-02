namespace Domain.DTOs.Printing;

public enum PrintJobState
{
    Queued,
    Pending,
    Processing,
    Completed,
    Canceled,
    Aborted,
    Unknown
}