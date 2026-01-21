namespace WebChat.Client.State.Connection;

public static class ConnectionReducers
{
    public static ConnectionState Reduce(ConnectionState state, IAction action)
    {
        return action switch
        {
            ConnectionStatusChanged a => state with
            {
                Status = a.Status
            },

            ConnectionConnecting => state with
            {
                Status = ConnectionStatus.Connecting
            },

            ConnectionConnected => state with
            {
                Status = ConnectionStatus.Connected,
                LastConnected = DateTime.UtcNow,
                ReconnectAttempts = 0,
                Error = null
            },

            ConnectionReconnecting => state with
            {
                Status = ConnectionStatus.Reconnecting,
                ReconnectAttempts = state.ReconnectAttempts + 1
            },

            ConnectionReconnected => state with
            {
                Status = ConnectionStatus.Connected,
                LastConnected = DateTime.UtcNow,
                ReconnectAttempts = 0,
                Error = null
            },

            ConnectionClosed a => state with
            {
                Status = ConnectionStatus.Disconnected,
                Error = a.Error
            },

            ConnectionError a => state with
            {
                Error = a.Error
            },

            _ => state
        };
    }
}