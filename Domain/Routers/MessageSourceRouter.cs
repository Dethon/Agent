using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Routers;

public sealed class MessageSourceRouter : IMessageSourceRouter
{
    public IEnumerable<IChatMessengerClient> GetClientsForSource(
        IReadOnlyList<IChatMessengerClient> clients,
        MessageSource source)
    {
        return clients.Where(c => c.Source == MessageSource.WebUi || c.Source == source);
    }
}