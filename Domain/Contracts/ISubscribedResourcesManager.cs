using ModelContextProtocol.Server;

namespace Domain.Contracts;

public interface ISubscribedResourcesManager
{
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, IMcpServer>> Get();
    void Add(string sessionId, string uri, IMcpServer server);
    void Remove(string sessionId, string uri);
}