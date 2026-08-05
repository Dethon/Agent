namespace WebChat.Client.State;

// The reducers never mutate state in place: every map change is a copy with one key set or
// removed. These are the two shapes, so no reducer spells the copy out by hand.
public static class CopyOnWrite
{
    public static IReadOnlyDictionary<string, TValue> With<TValue>(
        this IReadOnlyDictionary<string, TValue> source, string key, TValue value) =>
        new Dictionary<string, TValue>(source) { [key] = value };

    public static IReadOnlyDictionary<string, TValue> Without<TValue>(
        this IReadOnlyDictionary<string, TValue> source, string key)
    {
        var copy = new Dictionary<string, TValue>(source);
        copy.Remove(key);
        return copy;
    }
}