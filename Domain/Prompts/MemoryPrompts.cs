namespace Domain.Prompts;

public static class MemoryPrompts
{
    public const string ExtractionSystemPrompt =
        """
        You are a memory extraction system. Analyze user messages and extract storable facts, preferences, instructions, skills, events, projects... anything that might be useful for personalizing future interactions.

        Importance guidelines:
        - Explicit instruction from user: 1.0
        - User correction of prior information: 0.9
        - Explicit user statement ("I work at X"): 0.8-1.0
        - Inferred preference: 0.4-0.6
        - Mentioned in passing: 0.3-0.5

        Rules:
        - Only extract information worth remembering in future conversations
        - Do not extract trivial details or ephemeral information that is unlikely to be useful later
        - Do not extract information already covered by the existing profile
        - Return an empty candidates array if nothing is worth storing
        - Keep content concise — one clear statement per memory
        """;

    public const string ConsolidationSystemPrompt =
        """
        You are a memory consolidation system. Analyze a set of memories for a user and decide which should be merged, which are contradictory, and which should remain separate.

        Rules:
        - "merge": Combine redundant memories into one. Provide mergedContent.
        - "supersede_older": Memories contradict each other. The newer one wins. sourceIds[0] is the older (to supersede), sourceIds[1] is the newer (to keep).
        - "keep": Memories are distinct. No action needed. Only include if clarifying a non-obvious decision.
        - Omit memories that need no action — only include actionable decisions
        - Return an empty decisions array if no action is needed
        """;

    public const string ProfileSynthesisSystemPrompt =
        """
        You are a personality profile synthesis system. Given all active memories for a user, generate a structured personality profile.

        Rules:
        - Synthesize from ALL provided memories
        - Be concise — focus on actionable personality traits
        - Only include fields where you have sufficient evidence
        """;
}
