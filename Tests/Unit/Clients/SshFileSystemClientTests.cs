using Infrastructure.Clients;
using Infrastructure.Wrappers;
using Moq;
using Renci.SshNet.Common;
using Shouldly;

namespace Tests.Unit.Clients;

public class SshFileSystemClientTests
{
    private readonly Mock<ISshClientWrapper> _sshClientMock;
    private readonly SshFileSystemClient _sut;

    public SshFileSystemClientTests()
    {
        _sshClientMock = new Mock<ISshClientWrapper>();
        var commandMock = new Mock<ICommandWrapper>();
        _sut = new SshFileSystemClient(_sshClientMock.Object);

        _sshClientMock.Setup(x => x.CreateCommand(It.IsAny<string>()))
            .Returns(commandMock.Object);
    }

    [Fact]
    public async Task DescribeDirectory_WhenDirectoryExists_ReturnsFiles()
    {
        // given
        const string path = "/test/path";
        SetupFolderExists(path, exists: true);
        SetupCommandResult($"find \"{path}\" -type f", "/test/path/file1\n/test/path/file2");

        // when
        var result = await _sut.DescribeDirectory(path);

        // then
        result.ShouldNotBeEmpty();
        result.Count.ShouldBe(1);
        result.ShouldContainKey("/test/path");
        result["/test/path"].ShouldBe(["file1", "file2"]);
    }

    [Fact]
    public async Task DescribeDirectory_WhenDirectoryDoesNotExist_ThrowsException()
    {
        // given
        const string path = "/test/path";
        SetupFolderExists(path, exists: false);

        // when/then
        await Should.ThrowAsync<DirectoryNotFoundException>(() => _sut.DescribeDirectory(path));
    }

    [Fact]
    public async Task Move_WhenSourceFileExists_MovesSuccessfully()
    {
        // given
        const string sourcePath = "/source/file.txt";
        const string destinationPath = "/dest/path/file.txt";
        const string parentPath = "/dest/path";

        SetupFileExists(sourcePath, exists: true);
        SetupFolderExists(sourcePath, exists: false);
        SetupFolderExists(parentPath, exists: false);
        SetupFileExists(parentPath, exists: false);

        var mkdirCommand = SetupCommand($"umask 002 && mkdir -p \"{parentPath}\" && umask 022");
        var moveCommand = SetupCommand($"mv -T \"{sourcePath}\" \"{destinationPath}\"");

        // when
        await _sut.Move(sourcePath, destinationPath);

        // then
        _sshClientMock.Verify(x => x.CreateCommand($"umask 002 && mkdir -p \"{parentPath}\" && umask 022"), Times.Once);
        mkdirCommand.Verify(x => x.Execute(), Times.Once);
        _sshClientMock.Verify(x => x.CreateCommand($"mv -T \"{sourcePath}\" \"{destinationPath}\""), Times.Once);
        moveCommand.Verify(x => x.Execute(), Times.Once);
    }

    [Fact]
    public async Task RemoveDirectory_ExecutesCommand()
    {
        // given
        const string path = "/test/path";
        var removeCommand = SetupCommand($"rm -rf \"{path}\"");

        // when
        await _sut.RemoveDirectory(path);

        // then
        _sshClientMock.Verify(x => x.CreateCommand($"rm -rf \"{path}\""), Times.Once);
        removeCommand.Verify(x => x.Execute(), Times.Once);
    }

    [Fact]
    public async Task RemoveFile_ExecutesCommand()
    {
        // given
        const string path = "/test/file.txt";
        var removeCommand = SetupCommand($"rm -f \"{path}\"");

        // when
        await _sut.RemoveFile(path);

        // then
        _sshClientMock.Verify(x => x.CreateCommand($"rm -f \"{path}\""), Times.Once);
        removeCommand.Verify(x => x.Execute(), Times.Once);
    }

    [Fact]
    public async Task Move_WhenSourceDoesNotExist_ThrowsException()
    {
        // given
        const string sourcePath = "/source/path";
        const string destinationPath = "/dest/path";

        SetupFileExists(sourcePath, exists: false);
        SetupFolderExists(sourcePath, exists: false);

        // when/then
        await Should.ThrowAsync<IOException>(() => _sut.Move(sourcePath, destinationPath));
    }

    [Fact]
    public async Task CommandError_ThrowsSshException()
    {
        // given
        const string path = "/test/path";
        SetupCommandWithError($"rm -rf \"{path}\"", "Command failed");

        // when/then
        await Should.ThrowAsync<SshException>(() => _sut.RemoveDirectory(path));
    }

    [Fact]
    public async Task ConnectionManagement_ConnectsAndDisconnectsCorrectly()
    {
        // given
        const string path = "/test/path";
        SetupConnectionBehavior();
        SetupCommand($"rm -rf \"{path}\"");

        // when
        await _sut.RemoveDirectory(path);

        // then
        _sshClientMock.Verify(x => x.Connect(), Times.Once);
        _sshClientMock.Verify(x => x.Disconnect(), Times.Once);
    }

    #region Helper Methods

    private void SetupFileExists(string path, bool exists)
    {
        SetupPathExistsCheck(path, 'f', exists);
    }

    private void SetupFolderExists(string path, bool exists)
    {
        SetupPathExistsCheck(path, 'd', exists);
    }

    private void SetupPathExistsCheck(string path, char type, bool exists)
    {
        var command = new Mock<ICommandWrapper>();
        command.Setup(x => x.Execute());
        command.Setup(x => x.Result).Returns(exists ? "EXISTS" : "NOT_EXISTS");
        command.Setup(x => x.Error).Returns(string.Empty);

        _sshClientMock
            .Setup(x => x.RunCommand($"[ -{type} \"{path}\" ] && echo \"EXISTS\" || echo \"NOT_EXISTS\""))
            .Returns(command.Object);
    }

    private void SetupCommandResult(string commandText, string result)
    {
        var command = new Mock<ICommandWrapper>();
        command.Setup(x => x.Execute());
        command.Setup(x => x.Result).Returns(result);
        command.Setup(x => x.Error).Returns(string.Empty);

        _sshClientMock.Setup(x => x.RunCommand(commandText)).Returns(command.Object);
    }

    private Mock<ICommandWrapper> SetupCommand(string commandText)
    {
        var command = new Mock<ICommandWrapper>();
        command.Setup(x => x.Execute());
        command.Setup(x => x.Error).Returns(string.Empty);

        _sshClientMock.Setup(x => x.CreateCommand(commandText)).Returns(command.Object);
        return command;
    }

    private void SetupCommandWithError(string commandText, string errorMessage)
    {
        var command = new Mock<ICommandWrapper>();
        command.Setup(x => x.Execute());
        command.Setup(x => x.Error).Returns(errorMessage);

        _sshClientMock.Setup(x => x.CreateCommand(commandText)).Returns(command.Object);
    }

    private void SetupConnectionBehavior()
    {
        var isConnected = false;

        _sshClientMock.Setup(x => x.IsConnected).Returns(() => isConnected);
        _sshClientMock.Setup(x => x.Connect()).Callback(() => isConnected = true);
        _sshClientMock.Setup(x => x.Disconnect()).Callback(() => isConnected = false);
    }

    #endregion
}