namespace Domain.Memory;

// The point in a conversation's persisted history that an extraction window is cut at.
public readonly record struct MemoryAnchor
{
    private MemoryAnchor(int persistedMessageCount) => PersistedMessageCount = persistedMessageCount;

    public int PersistedMessageCount { get; }

    // The way to make one from a count, and its name is the precondition: the count has to be read
    // while the turn is being built and before the agent persists it. An anchor taken after
    // would point past the current message, so the window would take that message out of the
    // persisted history and append the fallback copy as well — the extractor would see the
    // same turn twice, with the real one labelled as context.
    public static MemoryAnchor TakenBeforeCurrentTurnIsPersisted(long persistedMessageCount) =>
        new((int)persistedMessageCount);
}