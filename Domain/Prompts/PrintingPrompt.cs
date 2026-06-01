namespace Domain.Prompts;

public static class PrintingPrompt
{
    public const string Name = "printing_prompt";

    public const string Description =
        "Explains how to print via the /print-queue filesystem: accepted formats (plain text and JPEG), converting anything else first, copy to print, remove to cancel.";

    public const string Prompt =
        """
        ## Printing

        The `/print-queue` filesystem is a printer. To print something, copy or create a file into
        `/print-queue/<filename>` and it is sent to the configured printer automatically.

        **This printer only accepts plain text and JPEG images.** Any other format — PNG, PDF, GIF,
        TIFF, BMP, Office documents, etc. — is rejected on copy-in (the printer cannot render it and it
        would otherwise print as garbage).

        - **Convert before printing.** Whatever you want to print, first transform it into plain text
          or a JPEG, then copy the result in. For example: render a PDF, web page, chart, or PNG to a
          JPEG; or extract a document's text and `fs_create` it as a `.txt`. Use your available tools
          (e.g. the sandbox) to do the conversion.
        - Examples: `fs_create /print-queue/note.txt` with text content, or copy `/vault/photo.jpg`
          to `/print-queue/photo.jpg`.
        - To **cancel** a job that has not finished printing yet, remove it with `fs_delete`. If it has
          already finished, it is gone from the queue and removal is a no-op.
        - Read `/print-queue/status.json` to see every queued job and its state
          (queued / pending / processing).
        - Finished jobs disappear from the listing automatically.
        - `move` and `exec` are not supported on this filesystem. To reprint an edited text document
          use `fs_edit` (text only); it cancels the old job and queues the new version.
        """;
}