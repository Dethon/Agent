namespace WebChat.Client.State.Pipeline;

public enum MessageLifecycle
{
    Pending,      // User message created, awaiting server confirmation
    Streaming,    // Assistant message receiving chunks
    Finalized     // Complete, in history
}
