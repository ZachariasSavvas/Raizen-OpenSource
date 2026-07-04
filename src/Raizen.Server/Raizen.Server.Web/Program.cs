using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Licensing;
using Raizen.Server.Core.Services;
using Raizen.Server.Web.Auth;
using Raizen.Server.Web.Services;
using Serilog;
using System.Security.Claims;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10_485_760); // 10 MB

builder.Host.UseWindowsService();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/raizen-web-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ── Safety guard: placeholder secrets must not reach production ──────────────
if (!builder.Environment.IsDevelopment())
{
    var connStr = builder.Configuration.GetConnectionString("Default") ?? "";
    if (connStr.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "FATAL: Database connection string still contains the placeholder password 'CHANGE_ME'. " +
            "Run the setup wizard or update appsettings.Production.json with real credentials.");

    var encKey = builder.Configuration["Security:EncryptionKey"] ?? "";
    if (encKey.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "FATAL: Security:EncryptionKey still contains the placeholder value. " +
            "Run the setup wizard or set a strong random key in appsettings.Production.json.");
}

// ── AuditHmacKey migration: auto-generate if missing from existing installs ──
if (string.IsNullOrWhiteSpace(builder.Configuration["Security:AuditHmacKey"]))
{
    var newKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    builder.Configuration.AddInMemoryCollection([new("Security:AuditHmacKey", newKey)]);
    var settingsPath = Path.Combine(builder.Environment.ContentRootPath, "appsettings.Production.json");
    if (File.Exists(settingsPath))
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath))!;
            var sec  = (node["Security"] as System.Text.Json.Nodes.JsonObject) ?? new System.Text.Json.Nodes.JsonObject();
            sec["AuditHmacKey"] = newKey;
            node["Security"] = sec;
            File.WriteAllText(settingsPath, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log.Warning("Security:AuditHmacKey was missing — auto-generated and saved to {Path}.", settingsPath);
        }
        catch (Exception ex) { Log.Warning(ex, "Security:AuditHmacKey was missing — generated for this session but could not persist to {Path}.", settingsPath); }
    }
}

// ── Database ─────────────────────────────────────────────────────────────────
// Use AddDbContextFactory so Blazor Server components can safely share a circuit
// without concurrent-access exceptions on a single scoped DbContext instance.
builder.Services.AddDbContextFactory<RaizenDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ── Authentication — local cookie ────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(opts =>
    {
        opts.Cookie.Name         = "raizen-admin";
        opts.Cookie.HttpOnly     = true;
        opts.Cookie.SameSite     = SameSiteMode.Strict;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        opts.LoginPath           = "/Auth/Login";
        opts.LogoutPath          = "/auth/logout";
        opts.AccessDeniedPath    = "/Auth/Login";
        var timeoutMinutes = builder.Configuration.GetValue("Security:SessionTimeoutMinutes", 720);
        opts.ExpireTimeSpan      = TimeSpan.FromMinutes(timeoutMinutes);
        opts.SlidingExpiration   = true;
    })
    .AddCookie("raizen-totp-pending", opts =>
    {
        opts.Cookie.Name         = "raizen-totp-pending";
        opts.Cookie.HttpOnly     = true;
        opts.Cookie.SameSite     = SameSiteMode.Strict;
        opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        opts.ExpireTimeSpan      = TimeSpan.FromMinutes(5);
        opts.SlidingExpiration   = false;
    });

// ── Authorization ─────────────────────────────────────────────────────────────
builder.Services.AddAuthorization(opts =>
{
    opts.FallbackPolicy = opts.DefaultPolicy;
    opts.AddPolicy("Auditor",  p => p.RequireRole("Raizen.Auditor",  "Raizen.Operator", "Raizen.Approver", "Raizen.Admin"));
    opts.AddPolicy("Operator", p => p.RequireRole("Raizen.Operator", "Raizen.Approver", "Raizen.Admin"));
    opts.AddPolicy("Approver", p => p.RequireRole("Raizen.Approver", "Raizen.Admin"));
    opts.AddPolicy("Admin",    p => p.RequireRole("Raizen.Admin"));
});

// ── MVC / Razor Pages / Blazor ───────────────────────────────────────────────
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor()
    .AddCircuitOptions(opts =>
    {
        opts.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(30);
    });

// ── HttpContextAccessor ───────────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();

// ── Poll-response signing (reads the same key file as the API) ────────────────
builder.Services.AddSingleton<IPollResponseSigner, PollResponseSigner>();

// ── Licensing ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<ILicenseService, LicenseService>();

// ── Syslog / SIEM forwarding ──────────────────────────────────────────────────
builder.Services.Configure<SyslogOptions>(builder.Configuration.GetSection(SyslogOptions.Section));
builder.Services.AddSingleton<ISyslogSender, SyslogSender>();

// ── Health checks ────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")!, name: "database");

// ── Domain services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<IAdminAuthService, AdminAuthService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IActionCatalogService, ActionCatalogService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IRequestService, RequestService>();
builder.Services.AddScoped<IBulkOperationService, BulkOperationService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IExportService, ExportService>();
builder.Services.AddScoped<IEndpointService, EndpointService>();
builder.Services.AddScoped<IRegistrationTokenService, RegistrationTokenService>();
builder.Services.AddScoped<IAutoApprovalService, AutoApprovalService>();
builder.Services.AddScoped<IAuditExportService, AuditExportService>();
builder.Services.AddSingleton<ILoginLockoutService, LoginLockoutService>();
builder.Services.AddHostedService<ExpiryBackgroundService>();

// ── Real-time approval toast notifications (Enterprise) ─────────────────────
builder.Services.AddSingleton<IApprovalToastNotifier, ApprovalToastNotifier>();
builder.Services.AddHostedService<PgNotifyListenerService>();

var app = builder.Build();

// ── server_settings table (grace period persistence) ─────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS server_settings (
            key        TEXT        NOT NULL PRIMARY KEY,
            value      TEXT        NOT NULL,
            updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        GRANT ALL ON TABLE server_settings TO CURRENT_USER;
        """);
}

// ── login_lockouts table ──────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS login_lockouts (
            ip           TEXT        NOT NULL PRIMARY KEY,
            count        INTEGER     NOT NULL DEFAULT 0,
            locked_until TIMESTAMPTZ NOT NULL
        );
        GRANT ALL ON TABLE login_lockouts TO CURRENT_USER;
        """);
}

// Load persisted lockouts into memory
{
    var lockoutSvc = app.Services.GetRequiredService<ILoginLockoutService>();
    await lockoutSvc.InitializeAsync();
}

// ── Syslog: load DB-persisted settings (overrides appsettings.json) ──────────
{
    var syslog = app.Services.GetRequiredService<ISyslogSender>();
    await syslog.LoadFromDatabaseAsync();
}

// ── Add LastSeenAt index if missing ───────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS "IX_endpoint_registrations_LastSeenAt"
        ON endpoint_registrations ("LastSeenAt");

        ALTER TABLE endpoint_registrations
            ADD COLUMN IF NOT EXISTS "UpdatePending" boolean NOT NULL DEFAULT false,
            ADD COLUMN IF NOT EXISTS "UpdateRequestedAt" timestamp with time zone,
            ADD COLUMN IF NOT EXISTS "PollSigningConfigured" boolean NOT NULL DEFAULT false,
            ADD COLUMN IF NOT EXISTS "LastPollSucceededAt" timestamp with time zone,
            ADD COLUMN IF NOT EXISTS "LastPollError" character varying(1000),
            ADD COLUMN IF NOT EXISTS "LastUpdateCheckAt" timestamp with time zone,
            ADD COLUMN IF NOT EXISTS "LastUpdateStatus" character varying(64),
            ADD COLUMN IF NOT EXISTS "LastUpdateError" character varying(1000),
            ADD COLUMN IF NOT EXISTS "LastSuccessfulUpdateAt" timestamp with time zone;
        """);
}

// ── Performance indexes ───────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS "IX_elevation_requests_ExpiresAt"
            ON elevation_requests ("ExpiresAt");
        CREATE INDEX IF NOT EXISTS "IX_elevation_requests_Status_ReviewedAt"
            ON elevation_requests ("Status", "ReviewedAt");
        CREATE INDEX IF NOT EXISTS "IX_request_approvals_RequestId_Approved"
            ON request_approvals ("RequestId", "Approved");
        """);
}

// ── Add OriginalParametersJson column + backfill ─────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        ALTER TABLE elevation_requests ADD COLUMN IF NOT EXISTS "OriginalParametersJson" jsonb;
        UPDATE elevation_requests SET "OriginalParametersJson" = "ParametersJson"
            WHERE "OriginalParametersJson" IS NULL;
        """);
}

// ── Ensure tables + seed default admin user ────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx  = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    var auth = scope.ServiceProvider.GetRequiredService<IAdminAuthService>();

    await ctx.Database.EnsureCreatedAsync();

    // Create admin_users table if it doesn't exist (EnsureCreated skips on existing DBs)
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS notification_settings (
            "Id"               uuid                     NOT NULL DEFAULT gen_random_uuid(),
            "Enabled"          boolean                  NOT NULL DEFAULT false,
            "SmtpHost"         character varying(256)   NOT NULL DEFAULT '',
            "SmtpPort"         integer                  NOT NULL DEFAULT 587,
            "SmtpUseTls"       boolean                  NOT NULL DEFAULT true,
            "SmtpUsername"     character varying(256)   NOT NULL DEFAULT '',
            "SmtpPassword"     character varying(512)   NOT NULL DEFAULT '',
            "FromAddress"      character varying(320)   NOT NULL DEFAULT '',
            "FromDisplayName"  character varying(200)   NOT NULL DEFAULT 'Raizen',
            "RecipientsJson"   jsonb                    NOT NULL DEFAULT '[]',
            "NotifyOnSubmit"   boolean                  NOT NULL DEFAULT true,
            "NotifyOnApproved" boolean                  NOT NULL DEFAULT true,
            "NotifyOnDenied"   boolean                  NOT NULL DEFAULT true,
            "UpdatedAt"        timestamp with time zone NOT NULL DEFAULT now(),
            "UpdatedBy"        character varying(320)   NOT NULL DEFAULT '',
            CONSTRAINT "PK_notification_settings" PRIMARY KEY ("Id")
        );
        GRANT ALL ON TABLE notification_settings TO CURRENT_USER;
        """);

    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS admin_users (
            "Id"                 uuid                     NOT NULL DEFAULT gen_random_uuid(),
            "Username"           character varying(100)   NOT NULL,
            "PasswordHash"       character varying(512)   NOT NULL,
            "MustChangePassword" boolean                  NOT NULL DEFAULT true,
            "IsActive"           boolean                  NOT NULL DEFAULT true,
            "CreatedAt"          timestamp with time zone NOT NULL DEFAULT now(),
            "LastLoginAt"        timestamp with time zone,
            CONSTRAINT "PK_admin_users" PRIMARY KEY ("Id")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_admin_users_Username" ON admin_users ("Username");
        GRANT ALL ON TABLE admin_users TO CURRENT_USER;
        """);

    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS registration_tokens (
            "Id"        uuid                     NOT NULL DEFAULT gen_random_uuid(),
            "TokenHash" character varying(128)   NOT NULL,
            "Label"     character varying(200)   NOT NULL DEFAULT '',
            "CreatedAt" timestamp with time zone NOT NULL DEFAULT now(),
            "ExpiresAt" timestamp with time zone NOT NULL,
            "MaxUses"   integer                  NOT NULL DEFAULT 100,
            "UseCount"  integer                  NOT NULL DEFAULT 0,
            "IsActive"  boolean                  NOT NULL DEFAULT true,
            "CreatedBy" character varying(320)   NOT NULL DEFAULT '',
            CONSTRAINT "PK_registration_tokens" PRIMARY KEY ("Id")
        );
        CREATE INDEX IF NOT EXISTS "IX_registration_tokens_TokenHash" ON registration_tokens ("TokenHash");
        CREATE INDEX IF NOT EXISTS "IX_registration_tokens_IsActive"  ON registration_tokens ("IsActive");
        CREATE INDEX IF NOT EXISTS "IX_registration_tokens_ExpiresAt" ON registration_tokens ("ExpiresAt");
        GRANT ALL ON TABLE registration_tokens TO CURRENT_USER;
        """);

    // Add TOTP and MFA enforcement columns to admin_users if they don't exist yet
    await ctx.Database.ExecuteSqlRawAsync("""
        ALTER TABLE admin_users ADD COLUMN IF NOT EXISTS "TotpEnabled"       boolean                  NOT NULL DEFAULT false;
        ALTER TABLE admin_users ADD COLUMN IF NOT EXISTS "TotpSecret"        character varying(128);
        ALTER TABLE admin_users ADD COLUMN IF NOT EXISTS "RequiresMfa"       boolean                  NOT NULL DEFAULT false;
        ALTER TABLE admin_users ADD COLUMN IF NOT EXISTS "MfaDeadline"       timestamp with time zone;
        ALTER TABLE admin_users ADD COLUMN IF NOT EXISTS "PasswordChangedAt" timestamp with time zone;
        ALTER TABLE admin_users ADD COLUMN IF NOT EXISTS "Role"              character varying(32) NOT NULL DEFAULT 'Admin';
        """);

    // API key rotation grace period columns
    await ctx.Database.ExecuteSqlRawAsync("""
        ALTER TABLE endpoint_registrations ADD COLUMN IF NOT EXISTS "PreviousApiKeyHash"   text;
        ALTER TABLE endpoint_registrations ADD COLUMN IF NOT EXISTS "PreviousKeyExpiresAt"  timestamp with time zone;
        """);

    // Password history table for reuse prevention
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS password_history (
            "Id"            bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            "AdminUserId"   uuid                     NOT NULL,
            "PasswordHash"  character varying(512)    NOT NULL,
            "CreatedAt"     timestamp with time zone  NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS "IX_password_history_AdminUserId" ON password_history ("AdminUserId");
        GRANT ALL ON TABLE password_history TO CURRENT_USER;
        """);

    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS auto_approval_rules (
            "Id"                  uuid                     NOT NULL DEFAULT gen_random_uuid(),
            "Name"                character varying(200)   NOT NULL DEFAULT '',
            "ActionType"          integer,
            "ActionDefinitionId"  uuid,
            "RequesterUpnPattern" character varying(320),
            "IsEnabled"           boolean                  NOT NULL DEFAULT true,
            "CreatedBy"           character varying(320)   NOT NULL DEFAULT '',
            "CreatedAt"           timestamp with time zone NOT NULL DEFAULT now(),
            CONSTRAINT "PK_auto_approval_rules" PRIMARY KEY ("Id")
        );
        CREATE INDEX IF NOT EXISTS "IX_auto_approval_rules_IsEnabled" ON auto_approval_rules ("IsEnabled");
        GRANT ALL ON TABLE auto_approval_rules TO CURRENT_USER;
        """);

    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS request_comments (
            "Id"                uuid                     NOT NULL DEFAULT gen_random_uuid(),
            "RequestId"         uuid                     NOT NULL,
            "AuthorUpn"         character varying(320)   NOT NULL DEFAULT '',
            "AuthorDisplayName" character varying(256)   NOT NULL DEFAULT '',
            "IsAdmin"           boolean                  NOT NULL DEFAULT false,
            "Body"              character varying(4000)  NOT NULL DEFAULT '',
            "CreatedAt"         timestamp with time zone NOT NULL DEFAULT now(),
            CONSTRAINT "PK_request_comments" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_request_comments_RequestId" FOREIGN KEY ("RequestId")
                REFERENCES elevation_requests ("Id") ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS "IX_request_comments_RequestId" ON request_comments ("RequestId");
        CREATE INDEX IF NOT EXISTS "IX_request_comments_CreatedAt"  ON request_comments ("CreatedAt");
        GRANT ALL ON TABLE request_comments TO CURRENT_USER;

        -- Scheduled execution: deferred execution time on requests
        ALTER TABLE elevation_requests ADD COLUMN IF NOT EXISTS "ScheduledForUtc" timestamp with time zone;
        CREATE INDEX IF NOT EXISTS "IX_elevation_requests_scheduled" ON elevation_requests ("Status", "ScheduledForUtc");

        -- Bulk operations
        CREATE TABLE IF NOT EXISTS bulk_operations (
            "Id"                  uuid                     NOT NULL DEFAULT gen_random_uuid(),
            "ActionDefinitionId"  uuid                     NOT NULL,
            "CreatedByUpn"        character varying(320)   NOT NULL DEFAULT '',
            "Justification"       character varying(2000)  NOT NULL DEFAULT '',
            "TicketReference"     character varying(200),
            "ParametersJson"      jsonb                    NOT NULL DEFAULT '{{}}'::jsonb,
            "TotalCount"          integer                  NOT NULL DEFAULT 0,
            "SucceededCount"      integer                  NOT NULL DEFAULT 0,
            "FailedCount"         integer                  NOT NULL DEFAULT 0,
            "CreatedAt"           timestamp with time zone NOT NULL DEFAULT now(),
            "CompletedAt"         timestamp with time zone,
            CONSTRAINT "PK_bulk_operations" PRIMARY KEY ("Id"),
            CONSTRAINT "FK_bulk_operations_ActionDefinitionId" FOREIGN KEY ("ActionDefinitionId")
                REFERENCES action_definitions ("Id") ON DELETE RESTRICT
        );
        GRANT ALL ON TABLE bulk_operations TO CURRENT_USER;

        ALTER TABLE elevation_requests ADD COLUMN IF NOT EXISTS "BulkOperationId" uuid
            REFERENCES bulk_operations ("Id") ON DELETE SET NULL;
        CREATE INDEX IF NOT EXISTS "IX_elevation_requests_BulkOperationId"
            ON elevation_requests ("BulkOperationId") WHERE "BulkOperationId" IS NOT NULL;
        """);

    if (!ctx.AdminUsers.Any())
    {
        Log.Information("No admin users found — seeding default Admin:Admin account (must change password on first login).");
        await auth.CreateAsync("Admin", "Admin", mustChangePassword: true);
    }
}

// ── Poll signing key: load from DB for stability across server restarts ───────
if (string.IsNullOrWhiteSpace(app.Configuration["Security:PollSigningPrivateKeyPem"]))
{
    using var pollKeyScope = app.Services.CreateScope();
    var pollKeyFactory = pollKeyScope.ServiceProvider
        .GetRequiredService<IDbContextFactory<RaizenDbContext>>();
    await using var pollKeyCtx = await pollKeyFactory.CreateDbContextAsync();
    var pollKeyConn = pollKeyCtx.Database.GetDbConnection();
    if (pollKeyConn.State != System.Data.ConnectionState.Open)
        await pollKeyConn.OpenAsync();

    string? existingPem;
    using (var cmd = pollKeyConn.CreateCommand())
    {
        cmd.CommandText = "SELECT value FROM server_settings WHERE key = 'poll_signing_key_pem'";
        existingPem = await cmd.ExecuteScalarAsync() as string;
    }

    if (!string.IsNullOrEmpty(existingPem))
    {
        app.Configuration["Security:PollSigningPrivateKeyPem"] = existingPem;
        Log.Information("Poll signing key loaded from database.");
    }
    else
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        using (var cmd = pollKeyConn.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO server_settings (key, value, updated_at) " +
                "VALUES ('poll_signing_key_pem', @pem, now()) " +
                "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now()";
            var param = cmd.CreateParameter();
            param.ParameterName = "@pem";
            param.Value = pem;
            cmd.Parameters.Add(param);
            await cmd.ExecuteNonQueryAsync();
        }
        app.Configuration["Security:PollSigningPrivateKeyPem"] = pem;
        Log.Warning(
            "Poll signing key generated and stored in database. " +
            "This is normal on first startup. The public key will be logged by PollResponseSigner at startup. " +
            "All new deployment packages automatically include it.");
    }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseStaticFiles();
app.UseRouting();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"]        = "DENY";
    ctx.Response.Headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-eval'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "font-src 'self'; " +
        "img-src 'self' data:; " +
        "connect-src 'self' ws: wss:;";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapBlazorHub();
app.MapRazorPages();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapFallbackToPage("/_Host");

app.Run();
