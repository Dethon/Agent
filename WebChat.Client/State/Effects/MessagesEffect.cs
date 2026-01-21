using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class MessagesEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly ITopicService _topicService;

    public MessagesEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        ITopicService topicService)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _topicService = topicService;

        dispatcher.RegisterHandler<LoadMessages>(HandleLoadMessages);
    }

    private void HandleLoadMessages(LoadMessages action)
    {
        _ = HandleLoadMessagesAsync(action.TopicId);
    }

    private async Task HandleLoadMessagesAsync(string topicId)
    {
        var topic = _topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null)
        {
            return;
        }

        var history = await _topicService.GetHistoryAsync(topic.ChatId, topic.ThreadId);
        var messages = history.Select(h => new ChatMessageModel
        {
            Role = h.Role,
            Content = h.Content,
            SenderId = h.SenderId
        }).ToList();

        _dispatcher.Dispatch(new MessagesLoaded(topicId, messages));
    }

    public void Dispose()
    {
        // No subscription to dispose
    }
}