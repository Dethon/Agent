namespace WebChat.Client.Services.Utilities;

public static class TopicIdGenerator
{
    public static string GenerateTopicId()
    {
        return Guid.NewGuid().ToString("N");
    }

    public static long GetChatIdForTopic(string topicId)
    {
        return GetDeterministicHash(topicId, seed: 0x1234);
    }

    public static long GetThreadIdForTopic(string topicId)
    {
        return GetDeterministicHash(topicId, seed: 0x5678) & 0x7FFFFFFF;
    }

    private static long GetDeterministicHash(string input, long seed)
    {
        const long fnvPrime = 0x100000001b3;
        var hash = unchecked((long)0xcbf29ce484222325) ^ seed;

        foreach (var c in input)
        {
            hash ^= c;
            hash = unchecked(hash * fnvPrime);
        }

        return hash & 0x7FFFFFFFFFFFFFFF;
    }
}