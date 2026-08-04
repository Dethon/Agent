using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeSessionRecovery : ISessionRecovery
{
    private readonly TaskCompletionSource _gate = new();

    public int RecoverCalls { get; private set; }

    public bool Completed { get; private set; }

    public bool BlockUntilReleased { get; set; }

    public void Release() => _gate.TrySetResult();

    public async Task RecoverAsync()
    {
        RecoverCalls++;

        if (BlockUntilReleased)
        {
            await _gate.Task;
        }

        Completed = true;
    }
}