namespace WebChat.Client.State.Toast;

public record ShowError(string Message) : IAction;

public record DismissToast(Guid Id) : IAction;
