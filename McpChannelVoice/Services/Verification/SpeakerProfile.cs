namespace McpChannelVoice.Services.Verification;

// One enrolled identity as a set of prototype embeddings: one per enrollment take plus the
// re-normalized mean of the takes. Multi-condition enrollment (facing the mic, facing away,
// across the room) keeps each acoustic condition as its own prototype instead of diluting
// them into a single centroid that matches none of them well; scoring takes the best one.
public sealed record SpeakerProfile(string Name, IReadOnlyList<float[]> Prototypes);