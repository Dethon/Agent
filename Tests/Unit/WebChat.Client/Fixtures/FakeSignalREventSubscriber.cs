using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeSignalREventSubscriber(CallRecorder? recorder = null) : ISignalREventSubscriber
{
    public bool IsSubscribed { get; private set; }

    public void Subscribe()
    {
        IsSubscribed = true;
        recorder?.Record("subscribe");
    }

    public void Unsubscribe() => IsSubscribed = false;

    public void Dispose() => Unsubscribe();
}