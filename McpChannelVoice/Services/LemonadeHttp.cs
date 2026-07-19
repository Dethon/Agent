namespace McpChannelVoice.Services;

// Named registration for the Lemonade OpenAI-compatible endpoints. Clients are created per call
// (never cached in the singleton services) so IHttpClientFactory handler rotation keeps working.
public static class LemonadeHttp
{
    public const string ClientName = "lemonade";
}