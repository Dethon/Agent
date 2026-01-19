namespace WebChat.Client.State;

/// <summary>
/// Marker interface for all state actions.
/// Actions are defined as record types implementing this interface.
/// Example: public record TopicsLoaded(IReadOnlyList&lt;Topic&gt; Topics) : IAction;
/// </summary>
public interface IAction;
