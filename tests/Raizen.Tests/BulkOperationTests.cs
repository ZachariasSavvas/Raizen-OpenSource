using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Models;
using Raizen.Server.Core.Services;
using Raizen.Shared.DTOs;
using Raizen.Shared.Enums;

namespace Raizen.Tests;

/// <summary>Tests for the bulk operations feature.</summary>
public sealed class BulkOperationTests : IDisposable
{
    private readonly RaizenDbContext _db;
    private readonly BulkOperationService _bulkSvc;
    private readonly RequestService _requestSvc;
    private readonly ActionDefinition _def;
    private readonly EndpointRegistration _ep1;
    private readonly EndpointRegistration _ep2;
    private readonly EndpointRegistration _ep3;

    public BulkOperationTests()
    {
        var opts = new DbContextOptionsBuilder<RaizenDbContext>()
            .UseInMemoryDatabase($"Bulk_{Guid.NewGuid()}")
            .Options;
        _db = new RaizenDbContext(opts);

        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AuditHmacKey"] = "TestKey_Bulk",
            })
            .Build();

        var factory = new TestDbContextFactory(opts);
        var syslog = new NullSyslogSender();
        var audit = new AuditService(factory, syslog, cfg);
        var catalog = new ActionCatalogService(factory, audit);

        _bulkSvc = new BulkOperationService(factory, catalog, audit);

        var notifications = new NullNotificationService();
        var autoApproval = new AutoApprovalService(factory);
        _requestSvc = new RequestService(factory, audit, catalog, notifications, autoApproval, cfg);

        // Seed action definition
        _def = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = "Restart Service",
            ActionType = ActionType.RestartService,
            IsEnabled = true,
            ApprovalWindowMinutes = 60,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
        };
        _db.ActionDefinitions.Add(_def);

        // Seed 3 endpoints
        _ep1 = MakeEndpoint("EP-001");
        _ep2 = MakeEndpoint("EP-002");
        _ep3 = MakeEndpoint("EP-003");
        _db.EndpointRegistrations.AddRange(_ep1, _ep2, _ep3);
        _db.SaveChanges();
    }

    private static EndpointRegistration MakeEndpoint(string name) => new()
    {
        Id = Guid.NewGuid(),
        MachineId = Guid.NewGuid().ToString(),
        MachineName = name,
        ApiKeyHash = new string('0', 128),
        IsEnabled = true,
    };

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Submit_CreatesChildRequests()
    {
        var dto = new SubmitBulkOperationDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "Restart all",
            EndpointIds = [_ep1.Id, _ep2.Id, _ep3.Id],
        };

        var result = await _bulkSvc.SubmitAsync(dto, "admin@test.com");

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(3, result.PendingCount);
        Assert.Equal("Restart Service", result.ActionDisplayName);
    }

    [Fact]
    public async Task Submit_ChildRequestsHaveBulkOperationId()
    {
        var dto = new SubmitBulkOperationDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "Bulk test",
            EndpointIds = [_ep1.Id, _ep2.Id],
        };

        var result = await _bulkSvc.SubmitAsync(dto, "admin@test.com");

        var children = _db.ElevationRequests
            .Where(r => r.BulkOperationId == result.Id)
            .ToList();

        Assert.Equal(2, children.Count);
        Assert.All(children, c =>
        {
            Assert.Equal(RequestStatus.Pending, c.Status);
            Assert.Equal("Bulk test", c.Justification);
            Assert.Equal(_def.Id, c.ActionDefinitionId);
        });
    }

    [Fact]
    public async Task Submit_NoEndpoints_ThrowsError()
    {
        var dto = new SubmitBulkOperationDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "Empty",
            EndpointIds = [],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _bulkSvc.SubmitAsync(dto, "admin@test.com"));
    }

    [Fact]
    public async Task Submit_DisabledAction_ThrowsError()
    {
        var disabledDef = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = "Disabled",
            ActionType = ActionType.StopService,
            IsEnabled = false,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
        };
        _db.ActionDefinitions.Add(disabledDef);
        await _db.SaveChangesAsync();

        var dto = new SubmitBulkOperationDto
        {
            ActionDefinitionId = disabledDef.Id,
            Justification = "Should fail",
            EndpointIds = [_ep1.Id],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _bulkSvc.SubmitAsync(dto, "admin@test.com"));
    }

    [Fact]
    public async Task GetById_ReturnsChildDetails()
    {
        var dto = new SubmitBulkOperationDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "Detail test",
            EndpointIds = [_ep1.Id, _ep2.Id],
        };
        var created = await _bulkSvc.SubmitAsync(dto, "admin@test.com");

        var detail = await _bulkSvc.GetByIdAsync(created.Id);

        Assert.NotNull(detail);
        Assert.Equal(2, detail!.Requests.Count);
        Assert.Contains(detail.Requests, r => r.TargetMachine == "EP-001");
        Assert.Contains(detail.Requests, r => r.TargetMachine == "EP-002");
    }

    [Fact]
    public async Task GetById_NonExistent_ReturnsNull()
    {
        var result = await _bulkSvc.GetByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task List_ReturnsBulkOperations()
    {
        // Create two bulk ops
        await _bulkSvc.SubmitAsync(new SubmitBulkOperationDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "Bulk 1",
            EndpointIds = [_ep1.Id],
        }, "admin@test.com");

        await _bulkSvc.SubmitAsync(new SubmitBulkOperationDto
        {
            ActionDefinitionId = _def.Id,
            Justification = "Bulk 2",
            EndpointIds = [_ep2.Id, _ep3.Id],
        }, "admin@test.com");

        var page = await _bulkSvc.ListAsync(1, 10);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task ExecutionResult_UpdatesBulkCounters()
    {
        // Create bulk op with auto-approve definition
        var autoApproveDef = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            DisplayName = "Auto Action",
            ActionType = ActionType.StartService,
            IsEnabled = true,
            AutoApprove = true,
            ApprovalWindowMinutes = 60,
            ParametersSchemaJson = "[]",
            ApproverGroupIdsJson = "[]",
        };
        _db.ActionDefinitions.Add(autoApproveDef);
        await _db.SaveChangesAsync();

        var bulkDto = new SubmitBulkOperationDto
        {
            ActionDefinitionId = autoApproveDef.Id,
            Justification = "Counter test",
            EndpointIds = [_ep1.Id, _ep2.Id],
        };
        var bulk = await _bulkSvc.SubmitAsync(bulkDto, "admin@test.com");

        // Get the child requests
        var children = _db.ElevationRequests
            .Where(r => r.BulkOperationId == bulk.Id)
            .ToList();

        // The requests are Pending (bulk ops don't auto-approve).
        // Manually approve them to simulate the approval flow.
        foreach (var child in children)
        {
            child.Status = RequestStatus.Approved;
            child.ReviewedAt = DateTimeOffset.UtcNow;
        }
        await _db.SaveChangesAsync();

        // Claim and report success for first request
        var claimed1 = await _requestSvc.MarkAsExecutingAsync(children[0].Id, _ep1.Id);
        Assert.NotNull(claimed1);
        await _requestSvc.ReportExecutionResultAsync(new ExecutionResultDto
        {
            RequestId = children[0].Id,
            Succeeded = true,
            ResultMessage = "OK",
            ExecutedAt = DateTimeOffset.UtcNow,
        }, _ep1.Id);

        // Verify bulk counter
        var bulkAfter1 = await _bulkSvc.GetByIdAsync(bulk.Id);
        Assert.NotNull(bulkAfter1);
        Assert.Equal(1, bulkAfter1!.SucceededCount);
        Assert.Null(bulkAfter1.CompletedAt); // Not yet complete

        // Claim and report failure for second
        var claimed2 = await _requestSvc.MarkAsExecutingAsync(children[1].Id, _ep2.Id);
        Assert.NotNull(claimed2);
        await _requestSvc.ReportExecutionResultAsync(new ExecutionResultDto
        {
            RequestId = children[1].Id,
            Succeeded = false,
            ErrorMessage = "Failed",
            ExecutedAt = DateTimeOffset.UtcNow,
        }, _ep2.Id);

        // Verify bulk is now complete
        var bulkAfter2 = await _bulkSvc.GetByIdAsync(bulk.Id);
        Assert.NotNull(bulkAfter2);
        Assert.Equal(1, bulkAfter2!.SucceededCount);
        Assert.Equal(1, bulkAfter2.FailedCount);
        Assert.NotNull(bulkAfter2.CompletedAt); // Should be marked complete
    }
}
