namespace Dashboard.Client.State.Connection;

// One word and one modifier per status, written once. The layout and the overview show the same
// status in two places, so a status that gained a fourth state could otherwise say two things.
public static class ConnectionStatusText
{
    extension(ConnectionStatus status)
    {
        public string Label => status switch
        {
            ConnectionStatus.Live => "Live",
            ConnectionStatus.Reconnecting => "Reconnecting",
            _ => "Connecting"
        };

        public string Modifier => status switch
        {
            ConnectionStatus.Live => "live",
            ConnectionStatus.Reconnecting => "reconnecting",
            _ => "connecting"
        };
    }
}