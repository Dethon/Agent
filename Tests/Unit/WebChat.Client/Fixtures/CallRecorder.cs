namespace Tests.Unit.WebChat.Client.Fixtures;

// Fakes append what they were asked to do here, so a test can assert the order of a
// sequence instead of only that each step happened. Detached work (push subscription,
// stream resume) is deliberately not recorded — its position is not deterministic.
public sealed class CallRecorder
{
    private readonly List<string> _calls = [];
    private readonly Lock _gate = new();

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public void Record(string call)
    {
        lock (_gate)
        {
            _calls.Add(call);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _calls.Clear();
        }
    }
}