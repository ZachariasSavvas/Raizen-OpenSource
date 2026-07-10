using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raizen.Server.Core.Services;

namespace Raizen.Server.Core.Data;

/// <summary>
/// Coordinates the small amount of database bootstrap work shared by the API and Web
/// processes. PostgreSQL advisory locks make clean startup and audit writes safe across
/// both services, rather than only within one process.
/// </summary>
public static class DatabaseBootstrapper
{
    private const long SchemaLockId = 7_249_316_011;
    private const string AuditFingerprintSetting = "audit_hmac_key_sha256";

    public static async Task EnsureModelCreatedAsync(
        RaizenDbContext db,
        CancellationToken ct = default)
    {
        if (!db.Database.IsNpgsql())
        {
            await db.Database.EnsureCreatedAsync(ct);
            return;
        }

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_lock({SchemaLockId})", ct);
            try
            {
                await db.Database.EnsureCreatedAsync(ct);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_unlock({SchemaLockId})", ct);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Applies the additive schema used by endpoint health, approved diagnostic bundles,
    /// and monitoring. This remains idempotent because Raizen installations do not yet
    /// use an EF migration history for upgrades.
    /// </summary>
    public static Task EnsureRmmFeaturesAsync(RaizenDbContext db, CancellationToken ct = default)
    {
        var sql = """
            ALTER TABLE endpoint_registrations
                ADD COLUMN IF NOT EXISTS "HealthReportedAt" TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS "UptimeSeconds" BIGINT,
                ADD COLUMN IF NOT EXISTS "CpuLoadPercent" DOUBLE PRECISION,
                ADD COLUMN IF NOT EXISTS "MemoryUsedPercent" DOUBLE PRECISION,
                ADD COLUMN IF NOT EXISTS "SystemDriveFreePercent" DOUBLE PRECISION,
                ADD COLUMN IF NOT EXISTS "SystemDriveFreeBytes" BIGINT,
                ADD COLUMN IF NOT EXISTS "LoggedOnUser" VARCHAR(320),
                ADD COLUMN IF NOT EXISTS "IpAddressesJson" JSONB NOT NULL DEFAULT '[]'::jsonb,
                ADD COLUMN IF NOT EXISTS "PendingReboot" BOOLEAN NOT NULL DEFAULT FALSE,
                ADD COLUMN IF NOT EXISTS "DefenderEnabled" BOOLEAN,
                ADD COLUMN IF NOT EXISTS "DefenderSignatureAgeDays" INTEGER,
                ADD COLUMN IF NOT EXISTS "BitLockerProtected" BOOLEAN,
                ADD COLUMN IF NOT EXISTS "HealthCollectionError" VARCHAR(1000),
                ADD COLUMN IF NOT EXISTS "ProcessInventoryReportedAt" TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS "ServiceInventoryReportedAt" TIMESTAMPTZ,
                ADD COLUMN IF NOT EXISTS "ProcessesJson" JSONB NOT NULL DEFAULT '[]'::jsonb,
                ADD COLUMN IF NOT EXISTS "ServicesJson" JSONB NOT NULL DEFAULT '[]'::jsonb;

            CREATE TABLE IF NOT EXISTS diagnostic_bundles (
                "Id" UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
                "EndpointRegistrationId" UUID NOT NULL REFERENCES endpoint_registrations ("Id") ON DELETE CASCADE,
                "RequestId" UUID NOT NULL REFERENCES elevation_requests ("Id") ON DELETE CASCADE,
                "FileName" VARCHAR(260) NOT NULL,
                "ContentType" VARCHAR(100) NOT NULL DEFAULT 'application/zip',
                "Content" BYTEA NOT NULL,
                "Sha256" VARCHAR(64) NOT NULL,
                "SizeBytes" INTEGER NOT NULL,
                "EventCount" INTEGER NOT NULL,
                "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT now(),
                "ExpiresAt" TIMESTAMPTZ NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_diagnostic_bundles_EndpointRegistrationId"
                ON diagnostic_bundles ("EndpointRegistrationId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_diagnostic_bundles_RequestId"
                ON diagnostic_bundles ("RequestId");
            CREATE INDEX IF NOT EXISTS "IX_diagnostic_bundles_ExpiresAt"
                ON diagnostic_bundles ("ExpiresAt");

            CREATE TABLE IF NOT EXISTS monitoring_rules (
                "Id" UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
                "RuleType" INTEGER NOT NULL,
                "Name" VARCHAR(200) NOT NULL,
                "Description" VARCHAR(1000) NOT NULL DEFAULT '',
                "Threshold" DOUBLE PRECISION NOT NULL,
                "Severity" INTEGER NOT NULL,
                "IsEnabled" BOOLEAN NOT NULL DEFAULT TRUE,
                "IsDeleted" BOOLEAN NOT NULL DEFAULT FALSE,
                "EndpointRegistrationId" UUID REFERENCES endpoint_registrations ("Id") ON DELETE CASCADE,
                "TargetServiceName" VARCHAR(256),
                "NotifyByEmail" BOOLEAN NOT NULL DEFAULT FALSE,
                "NotificationRecipientsJson" JSONB NOT NULL DEFAULT '[]'::jsonb,
                "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT now(),
                "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            ALTER TABLE monitoring_rules
                ADD COLUMN IF NOT EXISTS "EndpointRegistrationId" UUID REFERENCES endpoint_registrations ("Id") ON DELETE CASCADE,
                ADD COLUMN IF NOT EXISTS "TargetServiceName" VARCHAR(256),
                ADD COLUMN IF NOT EXISTS "NotifyByEmail" BOOLEAN NOT NULL DEFAULT FALSE,
                ADD COLUMN IF NOT EXISTS "NotificationRecipientsJson" JSONB NOT NULL DEFAULT '[]'::jsonb,
                ADD COLUMN IF NOT EXISTS "IsDeleted" BOOLEAN NOT NULL DEFAULT FALSE;
            DROP INDEX IF EXISTS "IX_monitoring_rules_RuleType";
            CREATE INDEX IF NOT EXISTS "IX_monitoring_rules_RuleType_Multi" ON monitoring_rules ("RuleType");
            CREATE INDEX IF NOT EXISTS "IX_monitoring_rules_EndpointRegistrationId"
                ON monitoring_rules ("EndpointRegistrationId");

            -- The incorrect type-7 health rule could have produced semantically invalid alerts;
            -- remove that seed row before shifting the remaining built-ins to their real enum values.
            DELETE FROM monitoring_rules
            WHERE "Name" = 'Health collection failed' AND "RuleType" = 7;
            UPDATE monitoring_rules SET "RuleType" = CASE "Name"
                WHEN 'Endpoint offline' THEN 1
                WHEN 'Low system drive space' THEN 2
                WHEN 'High memory usage' THEN 3
                WHEN 'Defender disabled' THEN 4
                WHEN 'Defender signatures stale' THEN 5
                WHEN 'BitLocker not protected' THEN 6
                WHEN 'Pending reboot' THEN 7
                WHEN 'Health collection failed' THEN 8
                ELSE "RuleType" END
            WHERE "Name" IN ('Endpoint offline','Low system drive space','High memory usage','Defender disabled',
                'Defender signatures stale','BitLocker not protected','Pending reboot','Health collection failed');

            WITH ranked AS (
                SELECT "Id",
                       first_value("Id") OVER (PARTITION BY "RuleType" ORDER BY "CreatedAt", "Id") AS keeper,
                       row_number() OVER (PARTITION BY "RuleType" ORDER BY "CreatedAt", "Id") AS rn
                FROM monitoring_rules WHERE "RuleType" BETWEEN 1 AND 8
            ), duplicates AS (SELECT "Id", keeper FROM ranked WHERE rn > 1)
            UPDATE monitoring_alerts a SET "MonitoringRuleId" = d.keeper
            FROM duplicates d WHERE a."MonitoringRuleId" = d."Id";

            WITH ranked_alerts AS (
                SELECT "Id", row_number() OVER (
                    PARTITION BY "EndpointRegistrationId", "MonitoringRuleId"
                    ORDER BY "LastObservedAt" DESC, "Id") AS rn
                FROM monitoring_alerts WHERE "IsActive" = TRUE
            )
            DELETE FROM monitoring_alerts a USING ranked_alerts r
            WHERE a."Id" = r."Id" AND r.rn > 1;

            WITH ranked AS (
                SELECT "Id", row_number() OVER (
                    PARTITION BY "RuleType" ORDER BY "CreatedAt", "Id") AS rn
                FROM monitoring_rules WHERE "RuleType" BETWEEN 1 AND 8
            )
            DELETE FROM monitoring_rules r USING ranked x
            WHERE r."Id" = x."Id" AND x.rn > 1;

            INSERT INTO monitoring_rules
                ("Id","RuleType","Name","Description","Threshold","Severity","IsEnabled","CreatedAt","UpdatedAt")
            SELECT gen_random_uuid(), v."RuleType", v."Name", v."Description", v."Threshold", v."Severity", TRUE, now(), now()
            FROM (VALUES
                (1,'Endpoint offline','Alert when an enabled endpoint misses heartbeats.',10::double precision,2),
                (2,'Low system drive space','Alert when free space on the Windows system drive falls below this percentage.',15::double precision,1),
                (3,'High memory usage','Alert when physical memory usage exceeds this percentage.',90::double precision,1),
                (4,'Defender disabled','Alert when Microsoft Defender antivirus or real-time protection is disabled.',0::double precision,2),
                (5,'Defender signatures stale','Alert when Defender signatures are older than this many days.',3::double precision,1),
                (6,'BitLocker not protected','Alert when the system volume reports BitLocker protection off.',0::double precision,1),
                (7,'Pending reboot','Alert when Windows reports that a reboot is required.',0::double precision,0),
                (8,'Health collection failed','Alert when core endpoint health metrics cannot be collected.',0::double precision,1)
            ) AS v("RuleType","Name","Description","Threshold","Severity")
            WHERE NOT EXISTS (SELECT 1 FROM monitoring_rules r WHERE r."Name" = v."Name");

            CREATE TABLE IF NOT EXISTS monitoring_alerts (
                "Id" UUID NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
                "MonitoringRuleId" UUID NOT NULL REFERENCES monitoring_rules ("Id") ON DELETE CASCADE,
                "EndpointRegistrationId" UUID NOT NULL REFERENCES endpoint_registrations ("Id") ON DELETE CASCADE,
                "Message" VARCHAR(1000) NOT NULL,
                "ObservedValue" DOUBLE PRECISION,
                "IsActive" BOOLEAN NOT NULL DEFAULT TRUE,
                "TriggeredAt" TIMESTAMPTZ NOT NULL DEFAULT now(),
                "LastObservedAt" TIMESTAMPTZ NOT NULL DEFAULT now(),
                "ResolvedAt" TIMESTAMPTZ,
                "AcknowledgedAt" TIMESTAMPTZ,
                "AcknowledgedBy" VARCHAR(320)
            );
            CREATE INDEX IF NOT EXISTS "IX_monitoring_alerts_EndpointRegistrationId_MonitoringRuleId_IsActive"
                ON monitoring_alerts ("EndpointRegistrationId", "MonitoringRuleId", "IsActive");
            CREATE INDEX IF NOT EXISTS "IX_monitoring_alerts_LastObservedAt"
                ON monitoring_alerts ("LastObservedAt");

            INSERT INTO action_definitions
                ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
                 "AutoApprove","ApprovalWindowMinutes","MinApprovers","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
            SELECT gen_random_uuid(),
                   'Collect Event Log Diagnostics',
                   'Collect a bounded, approved ZIP of recent Windows event log entries.',
                   110,
                   '[{"key":"Channels","displayName":"Channels","description":"Comma-separated allowlist: System, Application, Defender, WindowsUpdate","type":0,"required":true,"validationPattern":"^(System|Application|Defender|WindowsUpdate)(,(System|Application|Defender|WindowsUpdate)){0,3}$","defaultValue":"System,Application"},{"key":"Hours","displayName":"Lookback Hours","description":"Number of hours to collect (1-72)","type":1,"required":true,"validationPattern":"^([1-9]|[1-6][0-9]|7[0-2])$","defaultValue":"24"},{"key":"MaxEvents","displayName":"Maximum Events","description":"Maximum number of events (1-1000)","type":1,"required":true,"validationPattern":"^([1-9]|[1-9][0-9]{1,2}|1000)$","defaultValue":"500"},{"key":"IncludeInformation","displayName":"Include Information Events","description":"Include informational events in addition to warnings and errors","type":2,"required":true,"validationPattern":"^(true|false)$","defaultValue":"false"}]'::jsonb,
                   '[]'::jsonb, FALSE, 60, 1, TRUE, now(), now(), 'system:upgrade'
            WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 110);
            """;

        // ExecuteSqlRaw routes through string.Format even when there are no parameters;
        // escape JSON object/regex braces so the literal SQL reaches PostgreSQL intact.
        return db.Database.ExecuteSqlRawAsync(
            sql.Replace("{", "{{", StringComparison.Ordinal)
               .Replace("}", "}}", StringComparison.Ordinal),
            ct);
    }

    public static string RequireAuditHmacKey(IConfiguration config)
    {
        var key = config["Security:AuditHmacKey"];
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "FATAL: Security:AuditHmacKey is not configured. Configure the same dedicated key " +
                "for both RaizenApi and RaizenWeb; startup will not generate one automatically.");
        if (key.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "FATAL: Security:AuditHmacKey still contains a placeholder value. " +
                "Run setup or configure the same strong random key for RaizenApi and RaizenWeb.");

        var encryptionKey = config["Security:EncryptionKey"];
        if (!string.IsNullOrEmpty(encryptionKey)
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(encryptionKey)))
        {
            throw new InvalidOperationException(
                "FATAL: Security:AuditHmacKey must not equal Security:EncryptionKey.");
        }

        return key;
    }

    /// <summary>
    /// Verifies that this process has the key used for the newest hashed audit entry,
    /// then atomically registers/compares a non-secret fingerprint shared by API and Web.
    /// </summary>
    public static async Task ValidateAuditKeyConsistencyAsync(
        RaizenDbContext db,
        IConfiguration config,
        CancellationToken ct = default)
    {
        var key = RequireAuditHmacKey(config);

        var newestHashed = await db.AuditLogs
            .AsNoTracking()
            .Where(x => x.RowHash != null && x.RowHash != "")
            .OrderByDescending(x => x.OccurredAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(ct);

        if (newestHashed is not null)
        {
            var expected = AuditService.ComputeHash(newestHashed, key);
            if (!FixedTimeEqualsHex(expected, newestHashed.RowHash!))
                throw new InvalidOperationException(
                    "FATAL: Security:AuditHmacKey does not validate the newest audit entry. " +
                    "RaizenApi and RaizenWeb must use the same existing audit key before startup.");
        }

        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        var stored = await RegisterOrReadFingerprintAsync(db, fingerprint, ct);

        if (!FixedTimeEqualsHex(fingerprint, stored))
            throw new InvalidOperationException(
                "FATAL: Security:AuditHmacKey differs from the key registered by another " +
                "Raizen service. Configure the same key for RaizenApi and RaizenWeb.");
    }

    private static async Task<string> RegisterOrReadFingerprintAsync(
        RaizenDbContext db,
        string fingerprint,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO server_settings (key, value, updated_at)
                VALUES ('audit_hmac_key_sha256', @fingerprint, now())
                ON CONFLICT (key) DO UPDATE SET value = server_settings.value
                RETURNING value;
                """;
            var parameter = command.CreateParameter();
            parameter.ParameterName = "fingerprint";
            parameter.Value = fingerprint;
            command.Parameters.Add(parameter);

            return (string)(await command.ExecuteScalarAsync(ct)
                ?? throw new InvalidOperationException(
                    $"Could not register {AuditFingerprintSetting}."));
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    private static bool FixedTimeEqualsHex(string left, string right)
    {
        var leftBytes = Encoding.ASCII.GetBytes(left);
        var rightBytes = Encoding.ASCII.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
            && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
