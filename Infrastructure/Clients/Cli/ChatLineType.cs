namespace Infrastructure.Clients.Cli;

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
    AutoApprovedHeader,
    AutoApprovedContent
}