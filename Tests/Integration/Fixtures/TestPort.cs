using System.Net;
using System.Net.Sockets;

namespace Tests.Integration.Fixtures;

public static class TestPort
{
    public static int GetAvailable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}