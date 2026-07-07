using AspNetCoreRateLimit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.OpenApi.Models;
using Raizen.Server.Api.Auth;
using Raizen.Server.Core.Data;
using Raizen.Server.Core.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 10_485_760); // 10 MB

builder.Host.UseWindowsService();

// ── Serilog ──────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/raizen-api-.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

var isDev = builder.Environment.IsDevelopment();
var devModeEnabled = isDev && builder.Configuration.GetValue<bool>("DevMode:Enabled");

// ── Safety guard: DevMode must never reach production ────────────────────────
if (!isDev && builder.Configuration.GetValue<bool>("DevMode:Enabled"))
    throw new InvalidOperationException(
        "FATAL: DevMode:Enabled=true is not permitted outside the Development environment. " +
        "Remove DevMode:Enabled from appsettings.Production.json and restart.");

// ── Safety guard: placeholder secrets must not reach production ──────────────
if (!isDev)
{
    var connStr = builder.Configuration.GetConnectionString("Default") ?? "";
    if (connStr.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "FATAL: Database connection string still contains the placeholder password 'CHANGE_ME'. " +
            "Run the setup wizard or update appsettings.Production.json with real credentials.");
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
// AddDbContextFactory registers both the factory (singleton) and RaizenDbContext (scoped).
// Singleton lifetime allows singletons (LoginLockoutService, ExpiryBackgroundService) to inject the factory.
builder.Services.AddDbContextFactory<RaizenDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// ── Authentication ────────────────────────────────────────────────────────────
//   Scheme 1: ApiKey   — endpoint agents (X-Raizen-MachineId / X-Raizen-ApiKey)
//   Scheme 2: Bearer   — Entra ID JWT for admins / approvers
//   Scheme 3: DevAdmin — only in Development, accepts X-Raizen-Dev-Token header

var authBuilder = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = "MultiScheme";
    options.DefaultChallengeScheme = "MultiScheme";
});

authBuilder.AddScheme<ApiKeyAuthOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, _ => { });

if (devModeEnabled)
{
    authBuilder.AddScheme<DevAdminAuthOptions, DevAdminAuthHandler>(DevAdminAuthHandler.SchemeName, opts =>
    {
        opts.DevToken = builder.Configuration.GetValue<string>("DevMode:AdminToken") ?? "dev-admin-token";
    });
}

// Only register the full Microsoft Identity Web JWT handler when AzureAd is actually
// configured with real tenant/client IDs.  On air-gapped deployments the TenantId is
// left as the placeholder value; calling AddMicrosoftIdentityWebApi in that case causes
// a startup crash because MSAL eagerly fetches OIDC metadata from the internet.
var azureTenantId = builder.Configuration["AzureAd:TenantId"] ?? string.Empty;
var azureAdConfigured = !string.IsNullOrWhiteSpace(azureTenantId)
    && azureTenantId != "YOUR_TENANT_ID"
    && azureTenantId != "not-configured";

if (azureAdConfigured)
{
    authBuilder.AddMicrosoftIdentityWebApi(builder.Configuration, "AzureAd", JwtBearerDefaults.AuthenticationScheme);
}
else
{
    // Stub JWT bearer — no outbound calls, rejects every token.
    // Keeps the MultiScheme selector valid; ApiKey auth (the only scheme used in
    // air-gapped deployments) still works normally.
    authBuilder.AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, opts =>
    {
        opts.RequireHttpsMetadata = false;
        opts.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer   = true,
            ValidateAudience = true,
            ValidIssuer      = "urn:raizen:azure-not-configured",
            ValidAudience    = "urn:raizen:azure-not-configured",
        };
    });
}

authBuilder.AddPolicyScheme("MultiScheme", "Bearer or ApiKey", opts =>
{
    opts.ForwardDefaultSelector = ctx =>
    {
        if (ctx.Request.Headers.ContainsKey("X-Raizen-ApiKey"))
            return ApiKeyAuthHandler.SchemeName;
        if (devModeEnabled && ctx.Request.Headers.ContainsKey("X-Raizen-Dev-Token"))
            return DevAdminAuthHandler.SchemeName;
        return JwtBearerDefaults.AuthenticationScheme;
    };
});

// ── Authorization policies ────────────────────────────────────────────────────
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("EndpointOnly", p =>
        p.RequireRole(ApiKeyAuthHandler.RoleEndpoint));

    if (devModeEnabled)
    {
        // In dev mode, AdminOnly and ApproverOrAdmin also accept the DevAdmin scheme
        opts.AddPolicy("AdminOnly", p => p
            .RequireAuthenticatedUser()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, DevAdminAuthHandler.SchemeName));
        opts.AddPolicy("ApproverOrAdmin", p => p
            .RequireAuthenticatedUser()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, DevAdminAuthHandler.SchemeName));
    }
    else
    {
        opts.AddPolicy("AdminOnly", p => p
            .RequireAuthenticatedUser()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme));
        opts.AddPolicy("ApproverOrAdmin", p => p
            .RequireAuthenticatedUser()
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme));
    }
});

// ── Rate limiting ─────────────────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.Configure<IpRateLimitOptions>(builder.Configuration.GetSection("IpRateLimiting"));
builder.Services.AddSingleton<IIpPolicyStore, MemoryCacheIpPolicyStore>();
builder.Services.AddSingleton<IRateLimitCounterStore, MemoryCacheRateLimitCounterStore>();
builder.Services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
builder.Services.AddSingleton<IProcessingStrategy, AsyncKeyLockProcessingStrategy>();
builder.Services.AddInMemoryRateLimiting();

// ── Poll-response signing (MITM protection) ───────────────────────────────────
builder.Services.AddSingleton<IPollResponseSigner, PollResponseSigner>();
builder.Services.AddSingleton<Raizen.Server.Api.Services.MsiHashCache>();

// ── Syslog / SIEM forwarding ──────────────────────────────────────────────────
builder.Services.Configure<SyslogOptions>(builder.Configuration.GetSection(SyslogOptions.Section));
builder.Services.AddSingleton<ISyslogSender, SyslogSender>();

// ── Domain services ───────────────────────────────────────────────────────────
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IActionCatalogService, ActionCatalogService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<IRequestService, RequestService>();
builder.Services.AddScoped<IBulkOperationService, BulkOperationService>();
builder.Services.AddScoped<IEndpointService, EndpointService>();
builder.Services.AddScoped<IRegistrationTokenService, RegistrationTokenService>();
builder.Services.AddScoped<IAutoApprovalService, AutoApprovalService>();
builder.Services.AddScoped<IAuditExportService, AuditExportService>();
builder.Services.AddSingleton<ILoginLockoutService, LoginLockoutService>();
builder.Services.AddHostedService<ExpiryBackgroundService>();

// ── Health checks ────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("Default")!, name: "database");

// ── Controllers / Swagger ─────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Raizen API", Version = "v1" });

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Description = "Entra ID JWT (admin operations)"
    });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header,
        Name = "X-Raizen-ApiKey", Description = "Endpoint API key"
    });
    if (devModeEnabled)
    {
        c.AddSecurityDefinition("DevAdmin", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header,
            Name = "X-Raizen-Dev-Token", Description = "Dev-mode admin token (Development environment only)"
        });
    }

    var securityRequirement = new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, [] },
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" } }, [] },
    };
    if (devModeEnabled)
    {
        securityRequirement.Add(
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "DevAdmin" } },
            []);
    }
    c.AddSecurityRequirement(securityRequirement);
});

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

// ── Syslog: load DB-persisted settings (overrides appsettings.json) ──────────
{
    var syslog = app.Services.GetRequiredService<ISyslogSender>();
    await syslog.LoadFromDatabaseAsync();
}

// ── Dual-approval + audit hash chain + syslog columns (idempotent) ───────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        ALTER TABLE action_definitions
            ADD COLUMN IF NOT EXISTS "MinApprovers" INTEGER NOT NULL DEFAULT 1;

        ALTER TABLE audit_logs
            ADD COLUMN IF NOT EXISTS "PreviousHash" VARCHAR(128),
            ADD COLUMN IF NOT EXISTS "RowHash"      VARCHAR(128);

        CREATE TABLE IF NOT EXISTS request_approvals (
            "Id"          UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "RequestId"   UUID         NOT NULL REFERENCES elevation_requests ("Id") ON DELETE CASCADE,
            "ApproverUpn" VARCHAR(320) NOT NULL,
            "Note"        VARCHAR(1000),
            "Approved"    BOOLEAN      NOT NULL DEFAULT TRUE,
            "OccurredAt"  TIMESTAMPTZ  NOT NULL DEFAULT now()
        );
        GRANT ALL ON TABLE request_approvals TO CURRENT_USER;
        CREATE INDEX IF NOT EXISTS "IX_request_approvals_RequestId"
            ON request_approvals ("RequestId");
        CREATE INDEX IF NOT EXISTS "IX_request_approvals_RequestId_ApproverUpn"
            ON request_approvals ("RequestId", "ApproverUpn");
        """);
}

// ── Add LastSeenAt index + DormantSince column if missing (idempotent) ────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    await ctx.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS "IX_endpoint_registrations_LastSeenAt"
        ON endpoint_registrations ("LastSeenAt");

        ALTER TABLE endpoint_registrations
            ADD COLUMN IF NOT EXISTS "DormantSince" TIMESTAMPTZ;
        CREATE INDEX IF NOT EXISTS "IX_endpoint_registrations_DormantSince"
        ON endpoint_registrations ("DormantSince");

        ALTER TABLE endpoint_registrations
            ADD COLUMN IF NOT EXISTS "UpdatePending" boolean NOT NULL DEFAULT false,
            ADD COLUMN IF NOT EXISTS "UpdateRequestedAt" TIMESTAMPTZ;

        ALTER TABLE endpoint_registrations
            ADD COLUMN IF NOT EXISTS "PollSigningConfigured" boolean NOT NULL DEFAULT false,
            ADD COLUMN IF NOT EXISTS "LastPollSucceededAt" TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS "LastPollError" VARCHAR(1000),
            ADD COLUMN IF NOT EXISTS "LastUpdateCheckAt" TIMESTAMPTZ,
            ADD COLUMN IF NOT EXISTS "LastUpdateStatus" VARCHAR(64),
            ADD COLUMN IF NOT EXISTS "LastUpdateError" VARCHAR(1000),
            ADD COLUMN IF NOT EXISTS "LastSuccessfulUpdateAt" TIMESTAMPTZ;

        ALTER TABLE endpoint_registrations
            ADD COLUMN IF NOT EXISTS "PreviousApiKeyHash" text,
            ADD COLUMN IF NOT EXISTS "PreviousKeyExpiresAt" TIMESTAMPTZ;
        """);
}

// ── Create / migrate database on startup ──────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<RaizenDbContext>();
    // EnsureCreatedAsync creates tables from the model without requiring EF migrations.
    // Switch to MigrateAsync() once you run: dotnet ef migrations add InitialCreate
    await ctx.Database.EnsureCreatedAsync();

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

        ALTER TABLE elevation_requests ADD COLUMN IF NOT EXISTS "BulkOperationId" uuid
            REFERENCES bulk_operations ("Id") ON DELETE SET NULL;
        CREATE INDEX IF NOT EXISTS "IX_elevation_requests_BulkOperationId"
            ON elevation_requests ("BulkOperationId") WHERE "BulkOperationId" IS NOT NULL;
        """);
}

// ── Poll signing key: load from DB for stability across server restarts ───────
// Priority: 1. Security:PollSigningPrivateKeyPem in appsettings (manual override)
//           2. server_settings table in DB (survives restarts + reimages)
//           3. Generate new RSA-2048, store in DB (first-ever startup only)
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

// ── Middleware ────────────────────────────────────────────────────────────────

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseIpRateLimiting();
app.UseSerilogRequestLogging();

// Only redirect to HTTPS in non-dev environments where a certificate is configured
if (!app.Environment.IsDevelopment())
    app.UseHttpsRedirection();

app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"]        = "DENY";
    ctx.Response.Headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none';";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
