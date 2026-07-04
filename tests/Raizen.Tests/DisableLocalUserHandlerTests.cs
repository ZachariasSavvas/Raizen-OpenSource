using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class DisableLocalUserHandlerTests
{
    private readonly DisableLocalUserHandler _handler = new(NullLogger<DisableLocalUserHandler>.Instance);

    private static ElevationRequestDto MakeRequest(string username) => new()
    {
        Id = Guid.NewGuid(),
        ActionType = ActionType.DisableLocalUser,
        Parameters = new Dictionary<string, string> { ["Username"] = username },
    };

    [Fact]
    public void HandledType_IsDisableLocalUser()
    {
        Assert.Equal(ActionType.DisableLocalUser, _handler.HandledType);
    }

    [Fact]
    public void Validate_MissingUsername_ReturnsError()
    {
        var request = new ElevationRequestDto
        {
            Id = Guid.NewGuid(),
            ActionType = ActionType.DisableLocalUser,
            Parameters = [],
        };
        var error = _handler.Validate(request);
        Assert.NotNull(error);
        Assert.Contains("Username", error);
    }

    [Fact]
    public void Validate_ValidUsername_ReturnsNull()
    {
        var error = _handler.Validate(MakeRequest("testuser"));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ProtectedAccount_Administrator_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest("Administrator"));
        Assert.NotNull(error);
        Assert.Contains("protected", error);
    }

    [Fact]
    public void Validate_ProtectedAccount_Guest_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest("Guest"));
        Assert.NotNull(error);
        Assert.Contains("protected", error);
    }

    [Fact]
    public void Validate_ProtectedAccount_SYSTEM_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest("SYSTEM"));
        Assert.NotNull(error);
        Assert.Contains("protected", error);
    }

    [Fact]
    public void Validate_InvalidChars_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest("bad user!"));
        Assert.NotNull(error);
        Assert.Contains("invalid", error);
    }
}
