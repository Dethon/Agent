namespace WebChat.Client.Contracts;

// The two things a hub call can come back with: the server's answer, or not live. Not live
// never means the server said no — a server that answers false is live and has answered, so a
// caller of a boolean-valued call has three outcomes to tell apart and not two.
public readonly record struct HubResult<T>(bool IsLive, T? Value)
{
    public static HubResult<T> NotLive => new(false, default);

    public static HubResult<T> Answered(T? value) => new(true, value);
}

// Stands in for the payload of a hub call that returns none, so those calls answer with the
// same result type as the rest instead of inventing a second convention.
public readonly record struct Nothing;