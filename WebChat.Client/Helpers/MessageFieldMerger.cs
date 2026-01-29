namespace WebChat.Client.Helpers;

internal static class MessageFieldMerger
{
    public const string ReasoningSeparator = "\n-----\n";
    public const string ToolCallsSeparator = "\n";

    public static string? Merge(string? existing, string? incoming, string separator)
    {
        return (existing, incoming) switch
        {
            (null or "", null or "") => null,
            (null or "", _) => incoming,
            (_, null or "") => existing,
            _ => existing + separator + incoming
        };
    }
}