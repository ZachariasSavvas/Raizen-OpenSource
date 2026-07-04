using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class CreateLocalUserHandlerTests
{
    private readonly CreateLocalUserHandler _handler = new(NullLogger<CreateLocalUserHandler>.Instance);

    private static ElevationRequestDto MakeRequest(Dictionary<string, string> parameters) => new()
    {
        Id = Guid.NewGuid(),
        ActionType = ActionType.CreateLocalUser,
        Parameters = parameters,
    };

    [Fact]
    public void HandledType_IsCreateLocalUser()
    {
        Assert.Equal(ActionType.CreateLocalUser, _handler.HandledType);
    }

    [Fact]
    public void Validate_MissingUsername_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Password"] = "Str0ng!Pass",
        }));
        Assert.NotNull(error);
        Assert.Contains("Username", error);
    }

    [Fact]
    public void Validate_MissingPassword_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = "testuser",
        }));
        Assert.NotNull(error);
        Assert.Contains("Password", error);
    }

    [Fact]
    public void Validate_ShortPassword_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = "testuser",
            ["Password"] = "short",
        }));
        Assert.NotNull(error);
        Assert.Contains("8 characters", error);
    }

    [Fact]
    public void Validate_ReservedUsername_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = "Administrator",
            ["Password"] = "Str0ng!Pass123",
        }));
        Assert.NotNull(error);
        Assert.Contains("reserved", error);
    }

    [Fact]
    public void Validate_InvalidUsernameChars_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = "bad user!@#",
            ["Password"] = "Str0ng!Pass123",
        }));
        Assert.NotNull(error);
        Assert.Contains("invalid characters", error);
    }

    [Fact]
    public void Validate_UsernameTooLong_ReturnsError()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = new string('a', 21),
            ["Password"] = "Str0ng!Pass123",
        }));
        Assert.NotNull(error);
        Assert.Contains("20 characters", error);
    }

    [Fact]
    public void Validate_ValidParams_ReturnsNull()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = "testuser01",
            ["Password"] = "Str0ng!Pass123",
        }));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_ValidWithOptionalFields_ReturnsNull()
    {
        var error = _handler.Validate(MakeRequest(new Dictionary<string, string>
        {
            ["Username"] = "svc.account",
            ["Password"] = "Str0ng!Pass123",
            ["FullName"] = "Service Account",
            ["Description"] = "Used for automation",
        }));
        Assert.Null(error);
    }
}
