namespace Dashboard.Client.State.Health;

public record UpdateHealth(IReadOnlyList<ServiceHealth> Services) : IAction;

public sealed class HealthStore : Store<HealthState>
{
    public HealthStore() : base(new HealthState()) { }

    public void UpdateHealth(IReadOnlyList<ServiceHealth> services) =>
        Dispatch(new UpdateHealth(services), static (_, a) => new HealthState { Services = a.Services });
}