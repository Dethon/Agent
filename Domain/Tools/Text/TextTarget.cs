namespace Domain.Tools.Text;

public abstract record TextTarget;

public record LinesTarget(int Start, int End) : TextTarget;

public record HeadingTarget(string Text, bool IncludeChildren = true) : TextTarget;

public record CodeBlockTarget(int Index) : TextTarget;

public record AnchorTarget(string Id) : TextTarget;

public record SectionTarget(string Marker, string? Key = null) : TextTarget;

public record SearchTarget(string Query, int ContextLines = 5, bool IsRegex = false) : TextTarget;

public record TextMatchTarget(string Text) : TextTarget;

public record PatternTarget(string Pattern, string? Flags = null) : TextTarget;

public record AfterHeadingTarget(string Heading) : TextTarget;

public record BeforeHeadingTarget(string Heading) : TextTarget;