namespace Domain.DTOs.WebChat;

public enum TopicChangeType
{
    Created,
    Updated,
    Deleted
}

public enum StreamChangeType
{
    Started,
    Cancelled,
    Completed
}

public record TopicChangedNotification(
    TopicChangeType ChangeType,
    string TopicId,
    TopicMetadata? Topic = null);

public record StreamChangedNotification(
    StreamChangeType ChangeType,
    string TopicId);

public record ApprovalResolvedNotification(
    string TopicId,
    string ApprovalId,
    string? ToolCalls = null);

public record ToolCallsNotification(
    string TopicId,
    string ToolCalls);

public record UserMessageNotification(
    string TopicId,
    string Content,
    string? SenderId);