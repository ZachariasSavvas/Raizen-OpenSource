using Microsoft.EntityFrameworkCore;
using Raizen.Server.Core.Models;

namespace Raizen.Server.Core.Data;

public sealed class RaizenDbContext(DbContextOptions<RaizenDbContext> options) : DbContext(options)
{
    public DbSet<ElevationRequest>     ElevationRequests     => Set<ElevationRequest>();
    public DbSet<ActionDefinition>     ActionDefinitions     => Set<ActionDefinition>();
    public DbSet<EndpointRegistration> EndpointRegistrations => Set<EndpointRegistration>();
    public DbSet<AuditLog>             AuditLogs             => Set<AuditLog>();
    public DbSet<AdminUser>            AdminUsers            => Set<AdminUser>();
    public DbSet<NotificationSettings> NotificationSettings  => Set<NotificationSettings>();
    public DbSet<RegistrationToken>    RegistrationTokens    => Set<RegistrationToken>();
    public DbSet<AutoApprovalRule>     AutoApprovalRules     => Set<AutoApprovalRule>();
    public DbSet<RequestComment>       RequestComments       => Set<RequestComment>();
    public DbSet<RequestApproval>      RequestApprovals      => Set<RequestApproval>();
    public DbSet<PasswordHistory>     PasswordHistories     => Set<PasswordHistory>();
    public DbSet<BulkOperation>       BulkOperations        => Set<BulkOperation>();
    public DbSet<DiagnosticBundle>    DiagnosticBundles     => Set<DiagnosticBundle>();
    public DbSet<MonitoringRule>      MonitoringRules       => Set<MonitoringRule>();
    public DbSet<MonitoringAlert>     MonitoringAlerts      => Set<MonitoringAlert>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        base.OnModelCreating(model);

        // ── ActionDefinition ────────────────────────────────────────────────
        model.Entity<ActionDefinition>(e =>
        {
            e.ToTable("action_definitions");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.ParametersSchemaJson).HasColumnType("jsonb");
            e.Property(x => x.ApproverGroupIdsJson).HasColumnType("jsonb");
            e.HasIndex(x => x.ActionType);
            e.HasIndex(x => x.IsEnabled);
        });

        // ── EndpointRegistration ────────────────────────────────────────────
        model.Entity<EndpointRegistration>(e =>
        {
            e.ToTable("endpoint_registrations");
            e.HasKey(x => x.Id);
            e.Property(x => x.MachineId).HasMaxLength(256).IsRequired();
            e.Property(x => x.MachineName).HasMaxLength(256).IsRequired();
            e.Property(x => x.ApiKeyHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.TagsJson).HasColumnType("jsonb");
            e.Property(x => x.LastPollError).HasMaxLength(1000);
            e.Property(x => x.LastUpdateStatus).HasMaxLength(64);
            e.Property(x => x.LastUpdateError).HasMaxLength(1000);
            e.Property(x => x.LoggedOnUser).HasMaxLength(320);
            e.Property(x => x.IpAddressesJson).HasColumnType("jsonb");
            e.Property(x => x.ProcessesJson).HasColumnType("jsonb");
            e.Property(x => x.ServicesJson).HasColumnType("jsonb");
            e.Property(x => x.HealthCollectionError).HasMaxLength(1000);
            e.HasIndex(x => x.MachineId).IsUnique();
            e.HasIndex(x => x.DormantSince);
        });

        // ── ElevationRequest ────────────────────────────────────────────────
        model.Entity<ElevationRequest>(e =>
        {
            e.ToTable("elevation_requests");
            e.HasKey(x => x.Id);
            e.Property(x => x.RequesterUpn).HasMaxLength(320).IsRequired();
            e.Property(x => x.RequesterDisplayName).HasMaxLength(256);
            e.Property(x => x.Justification).HasMaxLength(2000).IsRequired();
            e.Property(x => x.TicketReference).HasMaxLength(200);
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
            e.Property(x => x.OriginalParametersJson).HasColumnType("jsonb");
            e.Property(x => x.ReviewerUpn).HasMaxLength(320);
            e.Property(x => x.ReviewerNote).HasMaxLength(1000);
            e.Property(x => x.ExecutionResult).HasMaxLength(4000);
            e.Property(x => x.ExecutionError).HasMaxLength(4000);
            e.Property(x => x.RowVersion)
             .HasColumnName("xmin")
             .HasColumnType("xid")
             .IsRowVersion();

            e.HasOne(x => x.ActionDefinition)
             .WithMany(x => x.Requests)
             .HasForeignKey(x => x.ActionDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Endpoint)
             .WithMany(x => x.Requests)
             .HasForeignKey(x => x.EndpointRegistrationId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.BulkOperation)
             .WithMany(x => x.Requests)
             .HasForeignKey(x => x.BulkOperationId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.RequesterUpn);
            e.HasIndex(x => x.SubmittedAt);
            e.HasIndex(x => new { x.EndpointRegistrationId, x.Status });
            e.HasIndex(x => new { x.Status, x.ScheduledForUtc });
        });

        // ── AdminUser ────────────────────────────────────────────────────────
        model.Entity<AdminUser>(e =>
        {
            e.ToTable("admin_users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Username).HasMaxLength(100).IsRequired();
            e.HasIndex(x => x.Username).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.Property(x => x.Role).HasMaxLength(32).HasDefaultValue("Admin");
        });

        // ── PasswordHistory ────────────────────────────────────────────────────
        model.Entity<PasswordHistory>(e =>
        {
            e.ToTable("password_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            e.HasIndex(x => x.AdminUserId);
        });

        // ── NotificationSettings ─────────────────────────────────────────────
        model.Entity<NotificationSettings>(e =>
        {
            e.ToTable("notification_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.SmtpHost).HasMaxLength(256);
            e.Property(x => x.SmtpUsername).HasMaxLength(256);
            e.Property(x => x.SmtpPassword).HasMaxLength(512);
            e.Property(x => x.FromAddress).HasMaxLength(320);
            e.Property(x => x.FromDisplayName).HasMaxLength(200);
            e.Property(x => x.RecipientsJson).HasColumnType("jsonb");
            e.Property(x => x.UpdatedBy).HasMaxLength(320);
        });

        // ── RegistrationToken ───────────────────────────────────────────────
        model.Entity<RegistrationToken>(e =>
        {
            e.ToTable("registration_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.Label).HasMaxLength(200).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.IsActive);
            e.HasIndex(x => x.ExpiresAt);
        });

        // ── AutoApprovalRule ────────────────────────────────────────────────
        model.Entity<AutoApprovalRule>(e =>
        {
            e.ToTable("auto_approval_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.RequesterUpnPattern).HasMaxLength(320);
            e.Property(x => x.CreatedBy).HasMaxLength(320).IsRequired();
            e.HasIndex(x => x.IsEnabled);
        });

        // ── RequestComment ───────────────────────────────────────────────────
        model.Entity<RequestComment>(e =>
        {
            e.ToTable("request_comments");
            e.HasKey(x => x.Id);
            e.Property(x => x.AuthorUpn).HasMaxLength(320).IsRequired();
            e.Property(x => x.AuthorDisplayName).HasMaxLength(256);
            e.Property(x => x.Body).HasMaxLength(4000).IsRequired();
            e.HasOne(x => x.Request)
             .WithMany()
             .HasForeignKey(x => x.RequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => x.CreatedAt);
        });

        // ── AuditLog ────────────────────────────────────────────────────────
        model.Entity<AuditLog>(e =>
        {
            e.ToTable("audit_logs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Event).HasMaxLength(100).IsRequired();
            e.Property(x => x.ActorUpn).HasMaxLength(320).IsRequired();
            e.Property(x => x.TargetMachine).HasMaxLength(256);
            e.Property(x => x.Detail).HasMaxLength(4000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.PreviousHash).HasMaxLength(128);
            e.Property(x => x.RowHash).HasMaxLength(128);

            e.HasOne(x => x.Request)
             .WithMany(x => x.AuditLogs)
             .HasForeignKey(x => x.RequestId)
             .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.OccurredAt);
            e.HasIndex(x => x.ActorUpn);
            e.HasIndex(x => x.Event);
            e.HasIndex(x => x.RequestId);
        });

        // ── RequestApproval ─────────────────────────────────────────────────
        model.Entity<RequestApproval>(e =>
        {
            e.ToTable("request_approvals");
            e.HasKey(x => x.Id);
            e.Property(x => x.ApproverUpn).HasMaxLength(320).IsRequired();
            e.Property(x => x.Note).HasMaxLength(1000);
            e.HasOne(x => x.Request)
             .WithMany(x => x.Approvals)
             .HasForeignKey(x => x.RequestId)
             .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RequestId);
            e.HasIndex(x => new { x.RequestId, x.ApproverUpn }).IsUnique();
        });

        // ── BulkOperation ─────────────────────────────────────────────────
        model.Entity<BulkOperation>(e =>
        {
            e.ToTable("bulk_operations");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedByUpn).HasMaxLength(320).IsRequired();
            e.Property(x => x.Justification).HasMaxLength(2000).IsRequired();
            e.Property(x => x.TicketReference).HasMaxLength(200);
            e.Property(x => x.ParametersJson).HasColumnType("jsonb");
            e.HasOne(x => x.ActionDefinition)
             .WithMany()
             .HasForeignKey(x => x.ActionDefinitionId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<DiagnosticBundle>(e =>
        {
            e.ToTable("diagnostic_bundles");
            e.HasKey(x => x.Id);
            e.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            e.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Content).HasColumnType("bytea").IsRequired();
            e.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Endpoint).WithMany(x => x.DiagnosticBundles)
                .HasForeignKey(x => x.EndpointRegistrationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Request).WithMany()
                .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.EndpointRegistrationId);
            e.HasIndex(x => x.RequestId).IsUnique();
            e.HasIndex(x => x.ExpiresAt);
        });

        model.Entity<MonitoringRule>(e =>
        {
            e.ToTable("monitoring_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.TargetServiceName).HasMaxLength(256);
            e.Property(x => x.NotificationRecipientsJson).HasColumnType("jsonb");
            e.HasOne(x => x.Endpoint).WithMany(x => x.MonitoringRules)
                .HasForeignKey(x => x.EndpointRegistrationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.RuleType);
            e.HasIndex(x => x.EndpointRegistrationId);
        });

        model.Entity<MonitoringAlert>(e =>
        {
            e.ToTable("monitoring_alerts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            e.Property(x => x.AcknowledgedBy).HasMaxLength(320);
            e.HasOne(x => x.Rule).WithMany(x => x.Alerts)
                .HasForeignKey(x => x.MonitoringRuleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Endpoint).WithMany(x => x.MonitoringAlerts)
                .HasForeignKey(x => x.EndpointRegistrationId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.EndpointRegistrationId, x.MonitoringRuleId, x.IsActive });
            e.HasIndex(x => x.LastObservedAt);
        });
    }
}
