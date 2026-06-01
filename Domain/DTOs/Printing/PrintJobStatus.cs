namespace Domain.DTOs.Printing;

public sealed record PrintJobStatus(int JobId, string JobName, PrintJobState State);