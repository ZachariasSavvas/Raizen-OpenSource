using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class UninstallMsiHandlerTests
{
    private readonly UninstallMsiHandler _handler = new(NullLogger<UninstallMsiHandler>.Instance);

    private static ElevationRequestDto MakeRequest(Dictionary<string, string> parameters) => new()
    {
        Id = Guid.NewGuid(),
        ActionDefinitionId = Guid.NewGuid(),
        ActionType = ActionType.UninstallMsi,
        Parameters = parameters,
    };

    [Fact]
    public void HandledType_IsUninstallMsi()
    {
        Assert.Equal(ActionType.UninstallMsi, _handler.HandledType);
    }

    [Fact]
    public void Validate_MissingProductCode_ReturnsError()
    {
        var request = MakeRequest(new Dictionary<string, string>());
        var error = _handler.Validate(request);
        Assert.NotNull(error);
        Assert.Contains("ProductCode", error);
    }

    [Fact]
    public void Validate_EmptyProductCode_ReturnsError()
    {
        var request = MakeRequest(new Dictionary<string, string> { ["ProductCode"] = "" });
        var error = _handler.Validate(request);
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_InvalidGuid_ReturnsError()
    {
        var request = MakeRequest(new Dictionary<string, string> { ["ProductCode"] = "not-a-guid" });
        var error = _handler.Validate(request);
        Assert.NotNull(error);
        Assert.Contains("valid MSI product code", error);
    }

    [Fact]
    public void Validate_ValidProductCode_ReturnsNull()
    {
        var request = MakeRequest(new Dictionary<string, string>
        {
            ["ProductCode"] = "{12345678-1234-1234-1234-123456789ABC}"
        });
        var error = _handler.Validate(request);
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ProductCodeWithoutBraces_ReturnsError()
    {
        var request = MakeRequest(new Dictionary<string, string>
        {
            ["ProductCode"] = "12345678-1234-1234-1234-123456789ABC"
        });
        var error = _handler.Validate(request);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Execute_InvalidProductCode_ReturnsFailure()
    {
        var request = MakeRequest(new Dictionary<string, string>
        {
            ["ProductCode"] = "invalid",
            ["ProductName"] = "Test",
        });
        var result = await _handler.ExecuteAsync(request, CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Contains("not a valid", result.ErrorMessage);
    }
}
