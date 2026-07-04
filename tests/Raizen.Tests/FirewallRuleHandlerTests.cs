using Microsoft.Extensions.Logging.Abstractions;
using Raizen.Endpoint.Service.Actions;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

public sealed class FirewallRuleHandlerTests
{
    private readonly FirewallRuleHandler _handler = new(NullLogger<FirewallRuleHandler>.Instance);

    private static ElevationRequestDto MakeRequest(Dictionary<string, string> parameters) => new()
    {
        Id = Guid.NewGuid(),
        ActionType = ActionType.SetFirewallRule,
        Parameters = parameters,
    };

    private static Dictionary<string, string> ValidParams() => new()
    {
        ["RuleName"] = "Allow-SQL",
        ["Action"] = "Allow",
        ["Direction"] = "Inbound",
        ["Protocol"] = "TCP",
        ["LocalPort"] = "1433",
    };

    [Fact]
    public void HandledType_IsSetFirewallRule()
    {
        Assert.Equal(ActionType.SetFirewallRule, _handler.HandledType);
    }

    [Fact]
    public void Validate_ValidParams_ReturnsNull()
    {
        var error = _handler.Validate(MakeRequest(ValidParams()));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_MissingRuleName_ReturnsError()
    {
        var p = ValidParams();
        p.Remove("RuleName");
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("RuleName", error);
    }

    [Fact]
    public void Validate_InvalidAction_ReturnsError()
    {
        var p = ValidParams();
        p["Action"] = "Permit";
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("Allow", error);
    }

    [Fact]
    public void Validate_InvalidDirection_ReturnsError()
    {
        var p = ValidParams();
        p["Direction"] = "Both";
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("Inbound", error);
    }

    [Fact]
    public void Validate_InvalidProtocol_ReturnsError()
    {
        var p = ValidParams();
        p["Protocol"] = "ICMP";
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("TCP", error);
    }

    [Fact]
    public void Validate_PortWithAnyProtocol_ReturnsError()
    {
        var p = ValidParams();
        p["Protocol"] = "Any";
        p["LocalPort"] = "80";
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("TCP or UDP", error);
    }

    [Fact]
    public void Validate_InvalidPortRange_ReturnsError()
    {
        var p = ValidParams();
        p["LocalPort"] = "99999";
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("valid port", error);
    }

    [Fact]
    public void Validate_InvalidPortOrder_ReturnsError()
    {
        var p = ValidParams();
        p["LocalPort"] = "8100-8000"; // reversed range
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_ValidPortRange_ReturnsNull()
    {
        var p = ValidParams();
        p["LocalPort"] = "8000-8100";
        var error = _handler.Validate(MakeRequest(p));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_InvalidProgram_ReturnsError()
    {
        var p = ValidParams();
        p["Program"] = @"C:\test\..\evil.bat";
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("Program path", error);
    }

    [Fact]
    public void Validate_ValidProgram_ReturnsNull()
    {
        var p = ValidParams();
        p["Program"] = @"C:\Program Files\App\app.exe";
        var error = _handler.Validate(MakeRequest(p));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_NoOptionalParams_ReturnsNull()
    {
        var p = new Dictionary<string, string>
        {
            ["RuleName"] = "Block-All-Outbound",
            ["Action"] = "Block",
            ["Direction"] = "Outbound",
        };
        var error = _handler.Validate(MakeRequest(p));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_RuleNameTooLong_ReturnsError()
    {
        var p = ValidParams();
        p["RuleName"] = new string('A', 257);
        var error = _handler.Validate(MakeRequest(p));
        Assert.NotNull(error);
        Assert.Contains("invalid characters", error);
    }
}
