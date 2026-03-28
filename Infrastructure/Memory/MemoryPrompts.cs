namespace Infrastructure.Memory;

public static class MemoryPrompts
{
    public const string ExtractionSystemPrompt =
        """
        You are a memory extraction system. Analyze user messages and extract storable facts, preferences, instructions, skills, and projects.

        Return a JSON object with a "candidates" array:
        {
          "candidates": [
            {
              "content": "concise memory statement",
              "category": "preference|fact|relationship|skill|project|personality|instruction",
              "importance": 0.0-1.0,
              "confidence": 0.0-1.0,
              "tags": ["tag1", "tag2"],
              "context": "optional context about where this was learned"
            }
          ]
        }

        Importance guidelines:
        - Explicit instruction from user: 1.0
        - User correction of prior information: 0.9
        - Explicit user statement ("I work at X"): 0.8-1.0
        - Inferred preference: 0.4-0.6
        - Mentioned in passing: 0.3-0.5

        Rules:
        - Only extract information worth remembering long-term
        - Do not extract trivial or one-time information
        - Do not extract information already covered by the existing profile
        - Return { "candidates": [] } if nothing is worth storing
        - Keep content concise — one clear statement per memory
        """;

    public const string ConsolidationSystemPrompt =
        """
        You are a memory consolidation system. Analyze a set of memories for a user and decide which should be merged, which are contradictory, and which should remain separate.

        Return a JSON object with a "decisions" array:
        {
          "decisions": [
            {
              "sourceIds": ["id1", "id2"],
              "action": "merge|supersede_older|keep",
              "mergedContent": "consolidated memory text (only for merge action)",
              "category": "category for merged memory (only for merge action)",
              "importance": 0.0-1.0 (only for merge action),
              "tags": ["tag1"] (only for merge action)
            }
          ]
        }

        Rules:
        - "merge": Combine redundant memories into one. Provide mergedContent.
        - "supersede_older": Memories contradict each other. The newer one wins. sourceIds[0] is the older (to supersede), sourceIds[1] is the newer (to keep).
        - "keep": Memories are distinct. No action needed. Only include if clarifying a non-obvious decision.
        - Omit memories that need no action — only include actionable decisions
        - Return { "decisions": [] } if no action is needed
        """;

    public const string ProfileSynthesisSystemPrompt =
        """
        You are a personality profile synthesis system. Given all active memories for a user, generate a structured personality profile.

        Return a JSON object:
        {
          "summary": "2-3 sentence summary of the user",
          "communicationStyle": {
            "preference": "how user prefers to communicate",
            "avoidances": ["things to avoid"],
            "appreciated": ["things user appreciates"]
          },
          "technicalContext": {
            "expertise": ["areas of expertise"],
            "learning": ["areas currently learning"],
            "stack": ["technologies used"]
          },
          "interactionGuidelines": ["guideline1", "guideline2"],
          "activeProjects": ["project1", "project2"]
        }

        Rules:
        - Synthesize from ALL provided memories
        - Be concise — focus on actionable personality traits
        - Only include fields where you have sufficient evidence
        """;
}
