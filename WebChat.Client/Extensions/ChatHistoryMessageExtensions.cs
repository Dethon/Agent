using Domain.DTOs.WebChat;
using WebChat.Client.Models;

namespace WebChat.Client.Extensions;

public static class ChatHistoryMessageExtensions
{
    public static ChatMessageModel ToChatMessageModel(this ChatHistoryMessage history) =>
        new()
        {
            MessageId = history.MessageId,
            Role = history.Role,
            Content = history.Content,
            SenderId = history.SenderId,
            Timestamp = history.Timestamp
        };
}
