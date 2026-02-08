using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public enum MessageSource
{
    WebUi,
    ServiceBus,
    Telegram,
    Cli
}
