using System.Text.Json.Nodes;
using Domain.Tools;
using Renci.SshNet;

namespace Infrastructure.ToolAdapters.FileMoveTools;

public class SshFileMoveAdapter(SshClient client) : FileMoveTool
{
    protected override Task<JsonNode> Resolve(FileMoveParams parameters, CancellationToken cancellationToken)
    {
        if (!client.IsConnected)
        {
            client.Connect();
        }

        try
        {
            if (!DoesFileExist(parameters.SourceFile))
            {
                throw new Exception("Source file does not exist");
            }

            CreateDestinationPath(parameters.DestinationPath);
            MoveFile(parameters);
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["status"] = "success",
                ["message"] = "File moved successfully",
                ["source"] = parameters.SourceFile,
                ["destination"] = parameters.DestinationPath
            });
        }
        finally
        {
            if (client.IsConnected)
            {
                client.Disconnect();
            }
        }
    }

    private void MoveFile(FileMoveParams parameters)
    {
        var moveCommand = client.CreateCommand($"mv \"{parameters.SourceFile}\" \"{parameters.DestinationPath}\"");
        moveCommand.Execute();
        if (!string.IsNullOrEmpty(moveCommand.Error))
        {
            throw new Exception($"Failed to move file: {moveCommand.Error}");
        }
    }

    private void CreateDestinationPath(string destinationPath)
    {
        if (DoesFolderExist(destinationPath))
        {
            return;
        }

        var createDirCommand = client.RunCommand($"mkdir -p \"{destinationPath}\"");
        createDirCommand.Execute();
        if (!string.IsNullOrEmpty(createDirCommand.Error))
        {
            throw new Exception($"Failed to create destination directory: {createDirCommand.Error}");
        }
    }

    private bool DoesFolderExist(string path)
    {
        return DoesPathExist(path, 'd');
    }

    private bool DoesFileExist(string path)
    {
        return DoesPathExist(path, 'f');
    }

    private bool DoesPathExist(string path, char descriptor)
    {
        var checkDirCommand = $"[ -{descriptor} \"{path}\" ] && echo \"EXISTS\" || echo \"NOT_EXISTS\"";
        var dirExists = client.RunCommand(checkDirCommand).Result.Trim();
        return dirExists == "EXISTS";
    }
}