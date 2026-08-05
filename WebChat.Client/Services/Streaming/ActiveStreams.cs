using System.Collections.Concurrent;

namespace WebChat.Client.Services.Streaming;

// The stream in flight per topic, and the one place that decides when a topic stops having one.
public sealed class ActiveStreams
{
    private readonly ConcurrentDictionary<string, Task> _byTopic = new();

    public bool IsActive(string topicId) =>
        _byTopic.TryGetValue(topicId, out var task) && !task.IsCompleted;

    public void Track(string topicId, Task streamTask)
    {
        _byTopic[topicId] = streamTask;
        _ = streamTask.ContinueWith(_ => Forget(topicId, streamTask));
    }

    // By value, never by topic alone: a stream ends after the fact, so its cleanup can land once
    // the user has sent again and a newer stream already holds the topic. Dropping that entry
    // would let the next send open a second stream over a live one and duplicate the reply.
    public void Forget(string topicId, Task streamTask) =>
        _byTopic.TryRemove(new KeyValuePair<string, Task>(topicId, streamTask));
}