namespace WebChat.Client.State.Toast;

public static class NotLiveToast
{
    // One text for every user action that could not be made. The toast store suppresses a
    // repeat of the same message, so a resume in which several calls failed shows one toast
    // rather than burying the screen.
    public const string Message = "Could not reach the server, so that did not go through. Try again in a moment.";
}