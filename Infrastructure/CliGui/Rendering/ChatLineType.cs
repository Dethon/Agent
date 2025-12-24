namespace Infrastructure.CliGui.Rendering;

public enum ChatLineType
{
    Blank,
    System,
    UserHeader,
    UserContent,
    AgentHeader,
    AgentContent,
    ToolHeader,
    ToolContent,
    ToolApprovedHeader,
    ToolApprovedContent,
    ToolRejectedHeader,
    ToolRejectedContent
}