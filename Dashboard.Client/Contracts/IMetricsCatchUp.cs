namespace Dashboard.Client.Contracts;

// Re-reading whatever arrived while the dashboard was not listening. The metrics hub pushes what
// happens while somebody is attached and never replays a gap, so this is the only way what is on
// screen becomes true again after an interruption.
public interface IMetricsCatchUp
{
    Task CatchUpAsync();
}