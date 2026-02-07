using Domain.DTOs;

namespace Domain.Contracts;

public interface IMessageSourceRouter
{
    IEnumerable<IChatMessengerClient> GetClientsForSource(
        IReadOnlyList<IChatMessengerClient> clients,
        MessageSource source);
}
