using System.Collections.Immutable;
using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Effects;

public sealed class AgentActivityEffect : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _topicsStore;
    private readonly AgentActivityStore _activityStore;
    private readonly ITopicService _topicService;
    private readonly SpaceStore _spaceStore;
    private readonly IDisposable _streamingSubscription;
    private readonly IDisposable _setAgentsRegistration;
    private readonly IDisposable _selectAgentRegistration;
    private ImmutableHashSet<string> _previousStreamingTopics = [];

    public AgentActivityEffect(
        Dispatcher dispatcher,
        TopicsStore topicsStore,
        StreamingStore streamingStore,
        AgentActivityStore activityStore,
        ITopicService topicService,
        SpaceStore spaceStore)
    {
        _dispatcher = dispatcher;
        _topicsStore = topicsStore;
        _activityStore = activityStore;
        _topicService = topicService;
        _spaceStore = spaceStore;

        _setAgentsRegistration = dispatcher.RegisterHandler<SetAgents>(HandleSetAgents);
        _selectAgentRegistration = dispatcher.RegisterHandler<SelectAgent>(
            action => _dispatcher.Dispatch(new ClearAgentUnseenActivity(action.AgentId)));
        _streamingSubscription = streamingStore.StateObservable.Subscribe(HandleStreamingChange);
    }

    private void HandleSetAgents(SetAgents action) => _ = MapAllAgentTopicsAsync(action.Agents);

    private async Task MapAllAgentTopicsAsync(IReadOnlyList<AgentCatalogEntry> agents)
    {
        var slug = _spaceStore.State.CurrentSlug;
        var map = new Dictionary<string, string>();
        foreach (var agent in agents)
        {
            var topics = await _topicService.GetAllTopicsAsync(agent.Id, slug);
            foreach (var topic in topics)
            {
                map[topic.TopicId] = agent.Id;
            }
        }
        _dispatcher.Dispatch(new AllAgentsTopicsMapped(map));
    }

    private void HandleStreamingChange(StreamingState state)
    {
        var completed = _previousStreamingTopics.Except(state.StreamingTopics);
        var selectedAgent = _topicsStore.State.SelectedAgentId;
        var map = _activityStore.State.TopicToAgent;

        foreach (var topicId in completed.Where(t => map.TryGetValue(t, out var a) && a != selectedAgent))
        {
            _dispatcher.Dispatch(new MarkAgentUnseenActivity(map[topicId]));
        }

        _previousStreamingTopics = state.StreamingTopics;
    }

    public void Dispose()
    {
        _streamingSubscription.Dispose();
        _setAgentsRegistration.Dispose();
        _selectAgentRegistration.Dispose();
    }
}