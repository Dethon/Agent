using System.ComponentModel;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsCopyTool(LibraryPathConfig libraryPath) : CopyTool(libraryPath.BaseLibraryPath)
{
    [McpServerTool(Name = "fs_copy")]
    [Description(Description)]
    public CallToolResult McpRun(
        string sourcePath,
        string destinationPath,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(sourcePath, destinationPath, overwrite, createDirectories));
    }
}
