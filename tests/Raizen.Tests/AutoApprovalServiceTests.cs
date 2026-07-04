using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>Tests for auto-approval rule matching logic.</summary>
public sealed class AutoApprovalServiceTests : IDisposable
{
    private readonly RaizenDbContext _db;
    private readonly AutoApprovalService _svc;

    public AutoApprovalServiceTests()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"AutoApproval_{Guid.NewGuid()}")
            .Options;
        _db  = new RaizenDbContext(opts);
        _svc = new AutoApprovalService(new TestDbContextFactory(opts));
    }

    public void Dispose() => _db.Dispose();

    private async Task AddRule(
        string name,
        ActionType? actionType      = null,
        Guid? actionDefinitionId    = null,
        string? requesterUpnPattern = null,
        bool isEnabled              = true)
    {
        _db.AutoApprovalRules.Add(new AutoApprovalRule
        {
            Name                = name,
            ActionType          = actionType,
            ActionDefinitionId  = actionDefinitionId,
            RequesterUpnPattern = requesterUpnPattern,
            IsEnabled           = isEnabled,
            CreatedBy           = "test",
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task NoRules_ReturnsFalse()
    {
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "user@corp.com");
        Assert.False(result);
    }

    [Fact]
    public async Task DisabledRule_ReturnsFalse()
    {
        await AddRule("disabled", actionType: ActionType.StartService, isEnabled: false);
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "user@corp.com");
        Assert.False(result);
    }

    [Fact]
    public async Task MatchAll_CatchAllRule_ReturnsTrue()
    {
        await AddRule("catch-all");  // no filters
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "anyone@anywhere.com");
        Assert.True(result);
    }

    [Fact]
    public async Task ActionTypeFilter_Match_ReturnsTrue()
    {
        await AddRule("start only", actionType: ActionType.StartService);
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "user@corp.com");
        Assert.True(result);
    }

    [Fact]
    public async Task ActionTypeFilter_Mismatch_ReturnsFalse()
    {
        await AddRule("start only", actionType: ActionType.StartService);
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StopService, Guid.NewGuid(), "user@corp.com");
        Assert.False(result);
    }

    [Fact]
    public async Task ActionDefinitionFilter_Match_ReturnsTrue()
    {
        var defId = Guid.NewGuid();
        await AddRule("specific def", actionDefinitionId: defId);
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, defId, "user@corp.com");
        Assert.True(result);
    }

    [Fact]
    public async Task ActionDefinitionFilter_Mismatch_ReturnsFalse()
    {
        var defId = Guid.NewGuid();
        await AddRule("specific def", actionDefinitionId: defId);
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "user@corp.com");
        Assert.False(result);
    }

    [Fact]
    public async Task UpnPattern_ExactMatch_ReturnsTrue()
    {
        await AddRule("exact upn", requesterUpnPattern: "alice@corp.com");
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "alice@corp.com");
        Assert.True(result);
    }

    [Fact]
    public async Task UpnPattern_ExactMatch_CaseInsensitive_ReturnsTrue()
    {
        await AddRule("exact upn", requesterUpnPattern: "ALICE@CORP.COM");
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "alice@corp.com");
        Assert.True(result);
    }

    [Fact]
    public async Task UpnPattern_WildcardDomain_Match_ReturnsTrue()
    {
        await AddRule("corp users", requesterUpnPattern: "*@corp.com");
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "alice@corp.com");
        Assert.True(result);
    }

    [Fact]
    public async Task UpnPattern_WildcardDomain_Mismatch_ReturnsFalse()
    {
        await AddRule("corp users", requesterUpnPattern: "*@corp.com");
        var result = await _svc.ShouldAutoApproveAsync(ActionType.StartService, Guid.NewGuid(), "alice@other.com");
        Assert.False(result);
    }

    [Fact]
    public async Task CreateRule_PersistsAndReturnsDto()
    {
        var dto = await _svc.CreateRuleAsync(new CreateAutoApprovalRuleDto
        {
            Name                = "test rule",
            ActionType          = ActionType.StartService,
            RequesterUpnPattern = "*@corp.com",
        }, "admin@corp.com");

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal("test rule", dto.Name);
        Assert.Equal(ActionType.StartService, dto.ActionType);
        Assert.True(dto.IsEnabled);
    }

    [Fact]
    public async Task ToggleRule_FlipsEnabled()
    {
        var dto = await _svc.CreateRuleAsync(new CreateAutoApprovalRuleDto { Name = "r" }, "admin");
        Assert.True(dto.IsEnabled);

        var result1 = await _svc.ToggleRuleAsync(dto.Id);
        Assert.False(result1);

        var result2 = await _svc.ToggleRuleAsync(dto.Id);
        Assert.True(result2);
    }

    [Fact]
    public async Task DeleteRule_RemovesFromDb()
    {
        var dto = await _svc.CreateRuleAsync(new CreateAutoApprovalRuleDto { Name = "r" }, "admin");
        await _svc.DeleteRuleAsync(dto.Id);
        var rules = await _svc.ListRulesAsync();
        Assert.Empty(rules);
    }
}
