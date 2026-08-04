namespace Dashboard.Client.Metrics;

// One choice a user makes about how to see a family: which dimension to group by, which quantity to
// chart, how to reduce durations. It round-trips through strings, because a pill and a saved
// preference are both strings, and its key is the suffix the preference is saved under.
public sealed class MetricChoice(string key, Func<string> read, Action<string> apply)
{
    public string Key { get; } = key;

    public string Current => read();

    public void Apply(string value) => apply(value);

    // A value that no longer parses is a preference saved by an older build against a member that
    // has since gone. It is ignored, exactly as the typed preference read it replaces did.
    public static MetricChoice For<TValue>(string key, Func<TValue> read, Action<TValue> apply)
        where TValue : struct, Enum =>
        new(
            key,
            () => read().ToString(),
            value =>
            {
                if (Enum.TryParse<TValue>(value, out var parsed))
                {
                    apply(parsed);
                }
            });
}