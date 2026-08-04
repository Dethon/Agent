using WebChat.Client.Contracts;

namespace Tests.Unit.WebChat.Client.Fixtures;

public sealed class FakeHubConnectionFactory : IHubConnectionFactory
{
    private readonly Queue<FakeHubConnection> _scripted = new();

    public List<FakeHubConnection> Created { get; } = [];
    public Func<FakeHubConnection> CreateBehavior { get; set; } = () => new FakeHubConnection();

    public void Enqueue(FakeHubConnection connection) => _scripted.Enqueue(connection);

    public Task<IChatHubConnection> CreateAsync()
    {
        var connection = _scripted.TryDequeue(out var scripted) ? scripted : CreateBehavior();
        Created.Add(connection);
        return Task.FromResult<IChatHubConnection>(connection);
    }
}