using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsInfoTool(LibraryPathConfig libraryPath) : FileInfoTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_info")]
    [Description(Description)]
    public CallToolResult McpRun(string path)
    {
        return ToolResponse.Create(Run(path));
    }
}