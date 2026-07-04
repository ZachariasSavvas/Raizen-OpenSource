using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class CopyFileHandlerTests : IDisposable
{
    private readonly string _sourceDir = Path.Combine(Path.GetTempPath(), "RaizenTest_Src_" + Guid.NewGuid().ToString("N"));
    private readonly string _destDir   = Path.Combine(Path.GetTempPath(), "RaizenTest_Dst_" + Guid.NewGuid().ToString("N"));

    public CopyFileHandlerTests()
    {
        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sourceDir)) Directory.Delete(_sourceDir, recursive: true);
        if (Directory.Exists(_destDir))   Directory.Delete(_destDir,   recursive: true);
    }

    private static ElevationRequestDto MakeRequest(string src, string dest, string operation) => new()
    {
        Id                 = Guid.NewGuid(),
        ActionDefinitionId = Guid.NewGuid(),
        ActionType         = ActionType.CopyFile,
        Parameters         = new Dictionary<string, string>
        {
            ["SourcePath"]      = src,
            ["DestinationPath"] = dest,
            ["Operation"]       = operation,
        },
    };

    [Fact]
    public async Task Copy_LeavesSourceIntact()
    {
        var src  = Path.Combine(_sourceDir, "test.ps1");
        var dest = _destDir;
        File.WriteAllText(src, "# hello");

        var handler = new CopyFileHandler(NullLogger<CopyFileHandler>.Instance);
        var result  = await handler.ExecuteAsync(MakeRequest(src, dest, "Copy"), CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(File.Exists(src),                        "Source should still exist after Copy.");
        Assert.True(File.Exists(Path.Combine(dest, "test.ps1")), "Destination file should exist.");
    }

    [Fact]
    public async Task Move_DeletesSourceAndCreatesDestination()
    {
        var src  = Path.Combine(_sourceDir, "test.ps1");
        var dest = _destDir;
        File.WriteAllText(src, "# hello");

        var handler = new CopyFileHandler(NullLogger<CopyFileHandler>.Instance);
        var result  = await handler.ExecuteAsync(MakeRequest(src, dest, "Move"), CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(File.Exists(src),                           "Source must be DELETED after Move.");
        Assert.True(File.Exists(Path.Combine(dest, "test.ps1")), "Destination file should exist.");
    }

    [Fact]
    public async Task Move_CaseInsensitive_DeletesSource()
    {
        var src  = Path.Combine(_sourceDir, "test.ps1");
        var dest = _destDir;
        File.WriteAllText(src, "# hello");

        var handler = new CopyFileHandler(NullLogger<CopyFileHandler>.Instance);
        var result  = await handler.ExecuteAsync(MakeRequest(src, dest, "move"), CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(File.Exists(src), "Source must be deleted for lowercase 'move' operation.");
    }

    [Fact]
    public async Task Move_MissingSource_ReturnsFailure()
    {
        var src  = Path.Combine(_sourceDir, "nonexistent.ps1");
        var dest = _destDir;

        var handler = new CopyFileHandler(NullLogger<CopyFileHandler>.Instance);
        var result  = await handler.ExecuteAsync(MakeRequest(src, dest, "Move"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NoOperationParam_DefaultsToCopy_LeavesSource()
    {
        var src  = Path.Combine(_sourceDir, "test.ps1");
        var dest = _destDir;
        File.WriteAllText(src, "# hello");

        var request = new ElevationRequestDto
        {
            Id         = Guid.NewGuid(),
            ActionType = ActionType.CopyFile,
            Parameters = new Dictionary<string, string>
            {
                ["SourcePath"]      = src,
                ["DestinationPath"] = dest,
                // No "Operation" key — should default to Copy
            },
        };

        var handler = new CopyFileHandler(NullLogger<CopyFileHandler>.Instance);
        var result  = await handler.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(File.Exists(src), "Source should still exist when Operation defaults to Copy.");
    }
}
