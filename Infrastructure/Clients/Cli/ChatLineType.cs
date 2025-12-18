namespace Infrastructure.Clients.Cli;

internal enum ChatLineType
{
    Blank,
    System,
    UserHeader,
    UserContent,
    AgentHeader,
    AgentContent,
    ToolHeader,
    ToolContent
}