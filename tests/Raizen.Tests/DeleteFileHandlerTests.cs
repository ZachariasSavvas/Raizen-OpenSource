using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class DeleteFileHandlerTests : IDisposable
{
    private readonly string _testDir = Path.Combine(Path.GetTempPath(), "RaizenTest_Del_" + Guid.NewGuid().ToString("N"));
    private readonly DeleteFileHandler _handler = new(NullLogger<DeleteFileHandler>.Instance);

    public DeleteFileHandlerTests()
    {
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir)) Directory.Delete(_testDir, recursive: true);
    }

    private static ElevationRequestDto MakeRequest(string filePath, string recursive = "false") => new()
    {
        Id = Guid.NewGuid(),
        ActionType = ActionType.DeleteFile,
        Parameters = new Dictionary<string, string>
        {
            ["FilePath"] = filePath,
            ["Recursive"] = recursive,
        },
    };

    [Fact]
    public void HandledType_IsDeleteFile()
    {
        Assert.Equal(ActionType.DeleteFile, _handler.HandledType);
    }

    [Fact]
    public void Validate_MissingFilePath_ReturnsError()
    {
        var request = new ElevationRequestDto
        {
            Id = Guid.NewGuid(),
            ActionType = ActionType.DeleteFile,
            Parameters = [],
        };
        var error = _handler.Validate(request);
        Assert.NotNull(error);
        Assert.Contains("FilePath", error);
    }

    [Fact]
    public void Validate_FileExists_ReturnsNull()
    {
        var file = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(file, "data");
        var error = _handler.Validate(MakeRequest(file));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_FileNotFound_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(Path.Combine(_testDir, "nonexistent.txt")));
        Assert.NotNull(error);
        Assert.Contains("not found", error);
    }

    [Fact]
    public async Task Execute_DeletesFile()
    {
        var file = Path.Combine(_testDir, "delete_me.txt");
        File.WriteAllText(file, "content");

        var result = await _handler.ExecuteAsync(MakeRequest(file), CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Execute_DirectoryWithoutRecursive_Fails()
    {
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);

        var result = await _handler.ExecuteAsync(MakeRequest(subDir, "false"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("Recursive", result.ErrorMessage);
        Assert.True(Directory.Exists(subDir));
    }

    [Fact]
    public async Task Execute_DirectoryWithRecursive_Succeeds()
    {
        var subDir = Path.Combine(_testDir, "subdir_r");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "file.txt"), "data");

        var result = await _handler.ExecuteAsync(MakeRequest(subDir, "true"), CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(Directory.Exists(subDir));
    }

    [Fact]
    public async Task Execute_SystemPath_BlockedByPolicy()
    {
        var result = await _handler.ExecuteAsync(MakeRequest(@"C:\Windows"), CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("protected", result.ErrorMessage);
    }

    [Fact]
    public async Task Execute_TraversalPath_Rejected()
    {
        var result = await _handler.ExecuteAsync(
            MakeRequest(Path.Combine(_testDir, "..", "evil.txt")),
            CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("traversal", result.ErrorMessage);
    }
}
