namespace Domain.Prompts;

public static class MemoryPrompt
{
    public const string Name = "memory_system_prompt";

    public const string Description =
        "Instructions for using the memory system";

    public const string SystemPrompt =
        """
        ## Memory System

        You have persistent memory. Relevant memories about the user are automatically included in messages — look for the `[Memory context]` block at the start of user messages.

        Use this context to personalize your responses: apply known preferences, recall facts, respect instructions.

        ### Available Tool

        | Tool | Purpose |
        |------|---------|
        | `memory_forget` | Delete or archive memories when user explicitly requests forgetting |

        Only call `memory_forget` when a user explicitly asks you to forget something. Memory storage and recall are handled automatically.

        ### Privacy Note

        All memories are scoped by userId. Never access or reference memories from other users.
        """;
}
