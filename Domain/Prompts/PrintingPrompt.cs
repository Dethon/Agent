namespace Domain.Prompts;

public static class PrintingPrompt
{
    public const string Name = "printing_prompt";

    public const string Description =
        "Explains how to print documents via the /print-queue filesystem (copy to print, remove to cancel).";

    public const string Prompt =
        """
        ## Printing

        The `/print-queue` filesystem is a printer. To print a document, copy or create it into
        `/print-queue/<filename>` (e.g. copy `/vault/report.pdf` to `/print-queue/report.pdf`, or
        `fs_create` a text file there). It is sent to the configured printer automatically.

        - To **cancel** a job that has not finished printing yet, remove it with `fs_delete`.
          If it has already finished, it is gone from the queue and removal is a no-op.
        - Read `/print-queue/status.json` to see every queued job and its state
          (queued / pending / processing).
        - Finished jobs disappear from the listing automatically.
        - `move` and `exec` are not supported on this filesystem. Re-printing an edited document:
          use `fs_edit` (text only); it cancels the old job and queues the new version.
        """;
}