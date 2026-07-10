using Npgsql;

namespace Raizen.Server.Setup.Installer;

public static class DbSetup
{
    public static async Task TestConnectionAsync(string host, int port, string user, string pass)
    {
        var cs = SuperConnStr(host, port, user, pass);
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        // simple ping
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        await cmd.ExecuteScalarAsync();
    }

    public static async Task SetupAsync(WizardState s, CancellationToken ct)
    {
        var superCs = SuperConnStr(s.PgHost, s.PgPort, s.PgSuperUser, s.PgSuperPass);

        // ── Connect as superuser ──────────────────────────────────────────────
        await using var conn = new NpgsqlConnection(superCs);
        await conn.OpenAsync(ct);

        // ── Create database (cannot be in a transaction block) ────────────────
        await using (var checkCmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = 'raizen'", conn))
        {
            var exists = await checkCmd.ExecuteScalarAsync(ct) != null;
            if (!exists)
            {
                await using var createCmd = new NpgsqlCommand("CREATE DATABASE raizen", conn);
                await createCmd.ExecuteNonQueryAsync(ct);
            }
        }

        // ── Create / update app user ──────────────────────────────────────────
        // Use PostgreSQL's format('%L') to safely quote the password — avoids manual
        // escaping which is vulnerable to single-quote and dollar-quoting injection.
        await using var fmtCmd = new NpgsqlCommand("SELECT format('%L', @pass::text)", conn);
        fmtCmd.Parameters.AddWithValue("pass", s.DbPass);
        var quotedPass = (string)(await fmtCmd.ExecuteScalarAsync(ct))!;

        await using (var userCmd = new NpgsqlCommand($"""
            DO $RAIZEN$
            BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'raizen') THEN
                CREATE USER raizen WITH PASSWORD {quotedPass};
              ELSE
                ALTER USER raizen WITH PASSWORD {quotedPass};
              END IF;
            END $RAIZEN$;
            GRANT ALL PRIVILEGES ON DATABASE raizen TO raizen;
            """, conn))
        {
            await userCmd.ExecuteNonQueryAsync(ct);
        }

        // ── Connect to raizen DB as superuser for schema setup ────────────────
        var raizenCs = SuperConnStr(s.PgHost, s.PgPort, s.PgSuperUser, s.PgSuperPass, "raizen");
        await using var raizenConn = new NpgsqlConnection(raizenCs);
        await raizenConn.OpenAsync(ct);

        await using var schemaCmd = new NpgsqlCommand(
            "GRANT ALL ON SCHEMA public TO raizen;", raizenConn);
        await schemaCmd.ExecuteNonQueryAsync(ct);

        // ── Create all tables (idempotent — safe to re-run on upgrade) ────────
        await using var tablesCmd = new NpgsqlCommand(InitialSchema, raizenConn);
        await tablesCmd.ExecuteNonQueryAsync(ct);

        // ── Grant table/sequence permissions to the app user ──────────────────
        await using var grantCmd = new NpgsqlCommand(
            "GRANT ALL ON ALL TABLES IN SCHEMA public TO raizen;" +
            "GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO raizen;", raizenConn);
        await grantCmd.ExecuteNonQueryAsync(ct);

        // ── Reassign ownership of all public tables to raizen ─────────────────
        // Handles upgrades over existing installs where tables were created by the
        // superuser; raizen must own tables to create indexes at API/Web startup.
        await using var reassignCmd = new NpgsqlCommand("""
            DO $$
            DECLARE r RECORD;
            BEGIN
                FOR r IN
                    SELECT tablename FROM pg_tables
                    WHERE schemaname = 'public' AND tableowner <> 'raizen'
                LOOP
                    EXECUTE format('ALTER TABLE public.%I OWNER TO raizen', r.tablename);
                END LOOP;
            END $$;
            """, raizenConn);
        await reassignCmd.ExecuteNonQueryAsync(ct);

        // Runtime code never deletes audit history. The trigger also blocks ordinary
        // updates; AuditService enables its narrowly scoped repair mode in a transaction.
        await using var auditAclCmd = new NpgsqlCommand("""
            ALTER FUNCTION raizen_guard_audit_mutation() OWNER TO raizen;
            REVOKE DELETE ON TABLE audit_logs FROM raizen;
            """, raizenConn);
        await auditAclCmd.ExecuteNonQueryAsync(ct);

        // ── Seed / repair default action definitions ──────────────────────────
        // INSERT ... WHERE NOT EXISTS creates definitions on a fresh install.
        // UPDATE ... WHERE schema = '[]' repairs existing definitions whose
        // parameter schema was left empty by an earlier install run.
        // Definitions with a non-empty custom schema are never overwritten.
        await using var seedCmd = new NpgsqlCommand(DefaultActionsSeed, raizenConn);
        await seedCmd.ExecuteNonQueryAsync(ct);
    }

    private const string InitialSchema = """
        CREATE EXTENSION IF NOT EXISTS "pgcrypto";

        CREATE TABLE IF NOT EXISTS action_definitions (
            "Id"                              UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "DisplayName"                     VARCHAR(200) NOT NULL,
            "Description"                     VARCHAR(2000),
            "ActionType"                      INTEGER      NOT NULL,
            "ParametersSchemaJson"            JSONB        NOT NULL DEFAULT '[]',
            "ApproverGroupIdsJson"            JSONB        NOT NULL DEFAULT '[]',
            "AutoApprove"                     BOOLEAN      NOT NULL DEFAULT FALSE,
            "AutoApproveConditionDescription" VARCHAR(1000),
            "ApprovalWindowMinutes"           INTEGER      NOT NULL DEFAULT 60,
            "MinApprovers"                    INTEGER      NOT NULL DEFAULT 1,
            "IsEnabled"                       BOOLEAN      NOT NULL DEFAULT TRUE,
            "CreatedAt"                       TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "UpdatedAt"                       TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "CreatedByUpn"                    VARCHAR(320) NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS idx_action_definitions_type    ON action_definitions ("ActionType");
        CREATE INDEX IF NOT EXISTS idx_action_definitions_enabled ON action_definitions ("IsEnabled");

        CREATE TABLE IF NOT EXISTS endpoint_registrations (
            "Id"           UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "MachineId"    VARCHAR(256) NOT NULL UNIQUE,
            "MachineName"  VARCHAR(256) NOT NULL,
            "Description"  VARCHAR(1000),
            "IsEnabled"    BOOLEAN      NOT NULL DEFAULT TRUE,
            "ApiKeyHash"   VARCHAR(512) NOT NULL,
            "RegisteredAt" TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "LastSeenAt"   TIMESTAMPTZ,
            "OsVersion"    VARCHAR(256),
            "AgentVersion" VARCHAR(64),
            "TagsJson"     JSONB        NOT NULL DEFAULT '[]',
            "DormantSince" TIMESTAMPTZ,
            "UpdatePending" BOOLEAN     NOT NULL DEFAULT FALSE,
            "UpdateRequestedAt" TIMESTAMPTZ,
            "PollSigningConfigured" BOOLEAN NOT NULL DEFAULT FALSE,
            "LastPollSucceededAt" TIMESTAMPTZ,
            "LastPollError" VARCHAR(1000),
            "LastUpdateCheckAt" TIMESTAMPTZ,
            "LastUpdateStatus" VARCHAR(64),
            "LastUpdateError" VARCHAR(1000),
            "LastSuccessfulUpdateAt" TIMESTAMPTZ,
            "PreviousApiKeyHash" TEXT,
            "PreviousKeyExpiresAt" TIMESTAMPTZ,
            "HealthReportedAt" TIMESTAMPTZ,
            "UptimeSeconds" BIGINT,
            "CpuLoadPercent" DOUBLE PRECISION,
            "MemoryUsedPercent" DOUBLE PRECISION,
            "SystemDriveFreePercent" DOUBLE PRECISION,
            "SystemDriveFreeBytes" BIGINT,
            "LoggedOnUser" VARCHAR(320),
            "IpAddressesJson" JSONB NOT NULL DEFAULT '[]',
            "PendingReboot" BOOLEAN NOT NULL DEFAULT FALSE,
            "DefenderEnabled" BOOLEAN,
            "DefenderSignatureAgeDays" INTEGER,
            "BitLockerProtected" BOOLEAN,
            "HealthCollectionError" VARCHAR(1000),
            "ProcessInventoryReportedAt" TIMESTAMPTZ,
            "ServiceInventoryReportedAt" TIMESTAMPTZ,
            "ProcessesJson" JSONB NOT NULL DEFAULT '[]',
            "ServicesJson" JSONB NOT NULL DEFAULT '[]'
        );

        CREATE TABLE IF NOT EXISTS elevation_requests (
            "Id"                     UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "ActionDefinitionId"     UUID         NOT NULL REFERENCES action_definitions ("Id") ON DELETE RESTRICT,
            "EndpointRegistrationId" UUID         NOT NULL REFERENCES endpoint_registrations ("Id") ON DELETE RESTRICT,
            "RequesterUpn"           VARCHAR(320) NOT NULL,
            "RequesterDisplayName"   VARCHAR(256),
            "Justification"          VARCHAR(2000) NOT NULL,
            "TicketReference"        VARCHAR(200),
            "ParametersJson"         JSONB        NOT NULL DEFAULT '{}',
            "Status"                 INTEGER      NOT NULL DEFAULT 0,
            "SubmittedAt"            TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "ExpiresAt"              TIMESTAMPTZ  NOT NULL,
            "ReviewedAt"             TIMESTAMPTZ,
            "ExecutedAt"             TIMESTAMPTZ,
            "ReviewerUpn"            VARCHAR(320),
            "ReviewerNote"           VARCHAR(1000),
            "ExecutionResult"        VARCHAR(4000),
            "ExecutionError"         VARCHAR(4000)
        );
        CREATE INDEX IF NOT EXISTS idx_requests_status    ON elevation_requests ("Status");
        CREATE INDEX IF NOT EXISTS idx_requests_requester ON elevation_requests ("RequesterUpn");
        CREATE INDEX IF NOT EXISTS idx_requests_submitted ON elevation_requests ("SubmittedAt" DESC);
        CREATE INDEX IF NOT EXISTS idx_requests_ep_status ON elevation_requests ("EndpointRegistrationId", "Status");

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
        CREATE INDEX IF NOT EXISTS "IX_diagnostic_bundles_EndpointRegistrationId" ON diagnostic_bundles ("EndpointRegistrationId");
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_diagnostic_bundles_RequestId" ON diagnostic_bundles ("RequestId");
        CREATE INDEX IF NOT EXISTS "IX_diagnostic_bundles_ExpiresAt" ON diagnostic_bundles ("ExpiresAt");

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
            "NotificationRecipientsJson" JSONB NOT NULL DEFAULT '[]',
            "CreatedAt" TIMESTAMPTZ NOT NULL DEFAULT now(),
            "UpdatedAt" TIMESTAMPTZ NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS "IX_monitoring_rules_RuleType_Multi" ON monitoring_rules ("RuleType");
        CREATE INDEX IF NOT EXISTS "IX_monitoring_rules_EndpointRegistrationId" ON monitoring_rules ("EndpointRegistrationId");
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
        CREATE INDEX IF NOT EXISTS "IX_monitoring_alerts_LastObservedAt" ON monitoring_alerts ("LastObservedAt");

        CREATE TABLE IF NOT EXISTS audit_logs (
            "Id"            UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "RequestId"     UUID         REFERENCES elevation_requests ("Id") ON DELETE SET NULL,
            "Event"         VARCHAR(100) NOT NULL,
            "ActorUpn"      VARCHAR(320) NOT NULL,
            "TargetMachine" VARCHAR(256),
            "Detail"        VARCHAR(4000),
            "IpAddress"     VARCHAR(64),
            "OccurredAt"    TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "PreviousHash"  VARCHAR(128),
            "RowHash"       VARCHAR(128)
        );
        CREATE INDEX IF NOT EXISTS idx_audit_occurred ON audit_logs ("OccurredAt" DESC);
        CREATE INDEX IF NOT EXISTS idx_audit_actor    ON audit_logs ("ActorUpn");
        CREATE INDEX IF NOT EXISTS idx_audit_event    ON audit_logs ("Event");
        CREATE INDEX IF NOT EXISTS idx_audit_request  ON audit_logs ("RequestId");

        CREATE OR REPLACE FUNCTION raizen_guard_audit_mutation()
        RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF TG_OP = 'DELETE' THEN
                RAISE EXCEPTION 'audit_logs is append-only';
            END IF;
            IF current_setting('raizen.audit_repair', true) IS DISTINCT FROM 'on' THEN
                RAISE EXCEPTION 'audit_logs may only be updated by the audited repair workflow';
            END IF;
            RETURN NEW;
        END;
        $$;
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_trigger
                WHERE tgname = 'TR_audit_logs_append_only'
                  AND tgrelid = 'audit_logs'::regclass
            ) THEN
                CREATE TRIGGER "TR_audit_logs_append_only"
                    BEFORE UPDATE OR DELETE ON audit_logs
                    FOR EACH ROW EXECUTE FUNCTION raizen_guard_audit_mutation();
            END IF;
        END $$;

        CREATE INDEX IF NOT EXISTS "IX_endpoint_registrations_LastSeenAt" ON endpoint_registrations ("LastSeenAt");

        CREATE TABLE IF NOT EXISTS registration_tokens (
            "Id"        UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "TokenHash" VARCHAR(128) NOT NULL,
            "Label"     VARCHAR(200) NOT NULL DEFAULT '',
            "CreatedAt" TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "ExpiresAt" TIMESTAMPTZ  NOT NULL,
            "MaxUses"   INTEGER      NOT NULL DEFAULT 100,
            "UseCount"  INTEGER      NOT NULL DEFAULT 0,
            "IsActive"  BOOLEAN      NOT NULL DEFAULT TRUE,
            "CreatedBy" VARCHAR(320) NOT NULL DEFAULT ''
        );
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'IX_registration_tokens_TokenHash'
                  AND indexdef LIKE 'CREATE UNIQUE INDEX%'
            ) THEN
                EXECUTE 'DROP INDEX IF EXISTS "IX_registration_tokens_TokenHash"';
                EXECUTE 'CREATE UNIQUE INDEX "IX_registration_tokens_TokenHash" '
                     || 'ON registration_tokens ("TokenHash")';
            END IF;
        END $$;
        CREATE INDEX IF NOT EXISTS "IX_registration_tokens_IsActive"  ON registration_tokens ("IsActive");
        CREATE INDEX IF NOT EXISTS "IX_registration_tokens_ExpiresAt" ON registration_tokens ("ExpiresAt");

        CREATE TABLE IF NOT EXISTS auto_approval_rules (
            "Id"                  UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "Name"                VARCHAR(200) NOT NULL DEFAULT '',
            "ActionType"          INTEGER,
            "ActionDefinitionId"  UUID,
            "RequesterUpnPattern" VARCHAR(320),
            "IsEnabled"           BOOLEAN      NOT NULL DEFAULT TRUE,
            "CreatedBy"           VARCHAR(320) NOT NULL DEFAULT '',
            "CreatedAt"           TIMESTAMPTZ  NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS "IX_auto_approval_rules_IsEnabled" ON auto_approval_rules ("IsEnabled");

        CREATE TABLE IF NOT EXISTS request_comments (
            "Id"                UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "RequestId"         UUID         NOT NULL REFERENCES elevation_requests ("Id") ON DELETE CASCADE,
            "AuthorUpn"         VARCHAR(320) NOT NULL DEFAULT '',
            "AuthorDisplayName" VARCHAR(256) NOT NULL DEFAULT '',
            "IsAdmin"           BOOLEAN      NOT NULL DEFAULT FALSE,
            "Body"              VARCHAR(4000) NOT NULL DEFAULT '',
            "CreatedAt"         TIMESTAMPTZ  NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS "IX_request_comments_RequestId" ON request_comments ("RequestId");
        CREATE INDEX IF NOT EXISTS "IX_request_comments_CreatedAt"  ON request_comments ("CreatedAt");

        CREATE TABLE IF NOT EXISTS request_approvals (
            "Id"          UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "RequestId"   UUID         NOT NULL REFERENCES elevation_requests ("Id") ON DELETE CASCADE,
            "ApproverUpn" VARCHAR(320) NOT NULL,
            "Note"        VARCHAR(1000),
            "Approved"    BOOLEAN      NOT NULL DEFAULT TRUE,
            "OccurredAt"  TIMESTAMPTZ  NOT NULL DEFAULT now()
        );
        CREATE INDEX IF NOT EXISTS "IX_request_approvals_RequestId"
            ON request_approvals ("RequestId");
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'IX_request_approvals_RequestId_ApproverUpn'
                  AND indexdef LIKE 'CREATE UNIQUE INDEX%'
                  AND indexdef LIKE '%lower(%'
            ) THEN
                IF EXISTS (
                    SELECT 1 FROM request_approvals
                    GROUP BY "RequestId", lower("ApproverUpn")
                    HAVING count(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Duplicate approver votes must be resolved before applying the unique approval index';
                END IF;
                EXECUTE 'DROP INDEX IF EXISTS "IX_request_approvals_RequestId_ApproverUpn"';
                EXECUTE 'CREATE UNIQUE INDEX "IX_request_approvals_RequestId_ApproverUpn" '
                     || 'ON request_approvals ("RequestId", lower("ApproverUpn"))';
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS notification_settings (
            "Id"               UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "Enabled"          BOOLEAN      NOT NULL DEFAULT FALSE,
            "SmtpHost"         VARCHAR(256) NOT NULL DEFAULT '',
            "SmtpPort"         INTEGER      NOT NULL DEFAULT 587,
            "SmtpUseTls"       BOOLEAN      NOT NULL DEFAULT TRUE,
            "SmtpUsername"     VARCHAR(256) NOT NULL DEFAULT '',
            "SmtpPassword"     VARCHAR(512) NOT NULL DEFAULT '',
            "FromAddress"      VARCHAR(320) NOT NULL DEFAULT '',
            "FromDisplayName"  VARCHAR(200) NOT NULL DEFAULT 'Raizen',
            "RecipientsJson"   JSONB        NOT NULL DEFAULT '[]',
            "NotifyOnSubmit"   BOOLEAN      NOT NULL DEFAULT TRUE,
            "NotifyOnApproved" BOOLEAN      NOT NULL DEFAULT TRUE,
            "NotifyOnDenied"   BOOLEAN      NOT NULL DEFAULT TRUE,
            "NotifyOnCompleted" BOOLEAN     NOT NULL DEFAULT TRUE,
            "UpdatedAt"        TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "UpdatedBy"        VARCHAR(320) NOT NULL DEFAULT ''
        );
        ALTER TABLE notification_settings
            ADD COLUMN IF NOT EXISTS "NotifyOnCompleted" BOOLEAN NOT NULL DEFAULT TRUE;

        CREATE TABLE IF NOT EXISTS admin_users (
            "Id"                 UUID         NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
            "Username"           VARCHAR(100) NOT NULL,
            "PasswordHash"       VARCHAR(512) NOT NULL,
            "MustChangePassword" BOOLEAN      NOT NULL DEFAULT TRUE,
            "IsActive"           BOOLEAN      NOT NULL DEFAULT TRUE,
            "TotpEnabled"        BOOLEAN      NOT NULL DEFAULT FALSE,
            "TotpSecret"         VARCHAR(128),
            "RequiresMfa"        BOOLEAN      NOT NULL DEFAULT FALSE,
            "MfaDeadline"        TIMESTAMPTZ,
            "CreatedAt"          TIMESTAMPTZ  NOT NULL DEFAULT now(),
            "LastLoginAt"        TIMESTAMPTZ
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_admin_users_Username" ON admin_users ("Username");
        """;

    // Seeds default action definitions and repairs any existing definitions whose
    // ParametersSchemaJson was left as '[]' by an earlier install.
    // Rules:
    //   • INSERT ... WHERE NOT EXISTS  → creates the definition on a fresh install
    //   • UPDATE ... WHERE schema='[]' → fixes an empty schema left by a prior install
    //   • Definitions with a non-empty custom schema are never touched by the UPDATE
    private const string DefaultActionsSeed = """
        -- ── RunAsAdmin (100) ─────────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Run as Administrator',
               'Run an approved executable with elevated privileges.',
               100,
               '[{"key":"ExecutablePath","displayName":"Executable Path","description":"Full path to the executable (.exe/.msi/.msc)","type":3,"required":true,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 100);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"ExecutablePath","displayName":"Executable Path","description":"Full path to the executable (.exe/.msi/.msc)","type":3,"required":true,"validationPattern":null,"defaultValue":null}]',
            "DisplayName" = 'Run as Administrator',
            "UpdatedAt" = now()
        WHERE "ActionType" = 100 AND "ParametersSchemaJson"::text = '[]';

        -- ── StartService (30) ─────────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Start Service',
               'Start a Windows service.',
               30,
               '[{"key":"ServiceName","displayName":"Service Name","description":"Windows service name (not display name)","type":4,"required":true,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 30);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"ServiceName","displayName":"Service Name","description":"Windows service name (not display name)","type":4,"required":true,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 30 AND "ParametersSchemaJson"::text = '[]';

        -- ── StopService (31) ──────────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Stop Service',
               'Stop a Windows service.',
               31,
               '[{"key":"ServiceName","displayName":"Service Name","description":"Windows service name (not display name)","type":4,"required":true,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 31);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"ServiceName","displayName":"Service Name","description":"Windows service name (not display name)","type":4,"required":true,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 31 AND "ParametersSchemaJson"::text = '[]';

        -- ── RestartService (32) ───────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Restart Service',
               'Restart a Windows service.',
               32,
               '[{"key":"ServiceName","displayName":"Service Name","description":"Windows service name (not display name)","type":4,"required":true,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 32);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"ServiceName","displayName":"Service Name","description":"Windows service name (not display name)","type":4,"required":true,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 32 AND "ParametersSchemaJson"::text = '[]';

        -- ── InstallMsi (10) ───────────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Install MSI Package',
               'Install a software package via Windows Installer.',
               10,
               '[{"key":"PackagePath","displayName":"Package Path","description":"Full path to the MSI file","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ProductName","displayName":"Product Name","description":"Display name of the product","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"MsiProperties","displayName":"MSI Properties","description":"Additional MSI property string (e.g. PROPERTY=VALUE)","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 10);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"PackagePath","displayName":"Package Path","description":"Full path to the MSI file","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ProductName","displayName":"Product Name","description":"Display name of the product","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"MsiProperties","displayName":"MSI Properties","description":"Additional MSI property string (e.g. PROPERTY=VALUE)","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 10 AND "ParametersSchemaJson"::text = '[]';

        -- ── CopyFile (40) ─────────────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Copy / Move File',
               'Copy or move a file to a protected location.',
               40,
               '[{"key":"SourcePath","displayName":"Source Path","description":"Full path of the source file","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"DestinationPath","displayName":"Destination Path","description":"Full path of the destination","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Operation","displayName":"Operation","description":"Copy or Move","type":0,"required":false,"validationPattern":null,"defaultValue":"Copy"}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 40);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"SourcePath","displayName":"Source Path","description":"Full path of the source file","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"DestinationPath","displayName":"Destination Path","description":"Full path of the destination","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Operation","displayName":"Operation","description":"Copy or Move","type":0,"required":false,"validationPattern":null,"defaultValue":"Copy"}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 40 AND "ParametersSchemaJson"::text = '[]';

        -- ── AddLocalGroupMember (20) ──────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Add Local Group Member',
               'Add a user or group to a local Windows group.',
               20,
               '[{"key":"GroupName","displayName":"Group Name","description":"Local group name (e.g. Administrators)","type":5,"required":true,"validationPattern":null,"defaultValue":null},{"key":"UserName","displayName":"User Name","description":"DOMAIN\\username or local username","type":0,"required":true,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 20);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"GroupName","displayName":"Group Name","description":"Local group name (e.g. Administrators)","type":5,"required":true,"validationPattern":null,"defaultValue":null},{"key":"UserName","displayName":"User Name","description":"DOMAIN\\username or local username","type":0,"required":true,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 20 AND "ParametersSchemaJson"::text = '[]';

        -- ── RemoveLocalGroupMember (21) ───────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Remove Local Group Member',
               'Remove a user or group from a local Windows group.',
               21,
               '[{"key":"GroupName","displayName":"Group Name","description":"Local group name","type":5,"required":true,"validationPattern":null,"defaultValue":null},{"key":"UserName","displayName":"User Name","description":"DOMAIN\\username or local username","type":0,"required":true,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 21);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"GroupName","displayName":"Group Name","description":"Local group name","type":5,"required":true,"validationPattern":null,"defaultValue":null},{"key":"UserName","displayName":"User Name","description":"DOMAIN\\username or local username","type":0,"required":true,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 21 AND "ParametersSchemaJson"::text = '[]';

        -- ── SetRegistryValue (50) ─────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Set Registry Value',
               'Create or modify a registry value.',
               50,
               '[{"key":"Hive","displayName":"Hive","description":"HKLM, HKCU, HKCR, HKU, or HKCC","type":0,"required":false,"validationPattern":null,"defaultValue":"HKLM"},{"key":"SubKey","displayName":"Sub Key","description":"Registry key path","type":6,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ValueName","displayName":"Value Name","description":"Registry value name","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ValueData","displayName":"Value Data","description":"Data to write","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ValueKind","displayName":"Value Kind","description":"String, DWord, QWord, Binary, MultiString, ExpandString","type":0,"required":false,"validationPattern":null,"defaultValue":"String"}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 50);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"Hive","displayName":"Hive","description":"HKLM, HKCU, HKCR, HKU, or HKCC","type":0,"required":false,"validationPattern":null,"defaultValue":"HKLM"},{"key":"SubKey","displayName":"Sub Key","description":"Registry key path","type":6,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ValueName","displayName":"Value Name","description":"Registry value name","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ValueData","displayName":"Value Data","description":"Data to write","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ValueKind","displayName":"Value Kind","description":"String, DWord, QWord, Binary, MultiString, ExpandString","type":0,"required":false,"validationPattern":null,"defaultValue":"String"}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 50 AND "ParametersSchemaJson"::text = '[]';

        -- ── RunApprovedScript (60) ────────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Run Approved Script',
               'Execute a hash-verified script from the approved scripts directory.',
               60,
               '[{"key":"ScriptFileName","displayName":"Script File Name","description":"Name of the script file (not full path)","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ExpectedSha256","displayName":"Expected SHA-256","description":"SHA-256 hash of the script for integrity verification","type":7,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ScriptArgs","displayName":"Script Arguments","description":"Optional arguments to pass to the script","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 60);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"ScriptFileName","displayName":"Script File Name","description":"Name of the script file (not full path)","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ExpectedSha256","displayName":"Expected SHA-256","description":"SHA-256 hash of the script for integrity verification","type":7,"required":true,"validationPattern":null,"defaultValue":null},{"key":"ScriptArgs","displayName":"Script Arguments","description":"Optional arguments to pass to the script","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 60 AND "ParametersSchemaJson"::text = '[]';

        -- ── OpenFileProperties (95) ───────────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Set File / Folder Permissions',
               'Grant or modify NTFS permissions on a file or folder.',
               95,
               '[{"key":"FilePath","displayName":"File/Folder Path","description":"Full path to the file or folder","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Account","displayName":"Account","description":"Username or group to configure permissions for","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"PermissionLevel","displayName":"Permission Level","description":"Read, ReadAndExecute, FullControl, or Remove","type":0,"required":false,"validationPattern":null,"defaultValue":"FullControl"},{"key":"ApplyTo","displayName":"Apply To","description":"FolderOnly, FolderSubfoldersAndFiles, or FilesOnly","type":0,"required":false,"validationPattern":null,"defaultValue":"FolderSubfoldersAndFiles"}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 95);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"FilePath","displayName":"File/Folder Path","description":"Full path to the file or folder","type":3,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Account","displayName":"Account","description":"Username or group to configure permissions for","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"PermissionLevel","displayName":"Permission Level","description":"Read, ReadAndExecute, FullControl, or Remove","type":0,"required":false,"validationPattern":null,"defaultValue":"FullControl"},{"key":"ApplyTo","displayName":"Apply To","description":"FolderOnly, FolderSubfoldersAndFiles, or FilesOnly","type":0,"required":false,"validationPattern":null,"defaultValue":"FolderSubfoldersAndFiles"}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 95 AND "ParametersSchemaJson"::text = '[]';

        -- ── SetNetworkConfiguration (91) ──────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Set Network Configuration',
               'Change IP address, subnet mask, gateway, or DNS settings for a network adapter.',
               91,
               '[{"key":"AdapterName","displayName":"Adapter Name","description":"Name of the network adapter","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"AdapterGuid","displayName":"Adapter GUID","description":"WMI SettingID of the adapter (auto-populated by the tray app)","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Mode","displayName":"Mode","description":"Static or DHCP","type":0,"required":true,"validationPattern":null,"defaultValue":"Static"},{"key":"IpAddress","displayName":"IP Address","description":"Static IP address (leave empty for DHCP)","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"SubnetMask","displayName":"Subnet Mask","description":"Subnet mask for static configuration","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"DefaultGateway","displayName":"Default Gateway","description":"Default gateway IP address","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"DnsMode","displayName":"DNS Mode","description":"Auto or Custom","type":0,"required":false,"validationPattern":null,"defaultValue":"Auto"},{"key":"PrimaryDns","displayName":"Primary DNS","description":"Primary DNS server IP","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"SecondaryDns","displayName":"Secondary DNS","description":"Secondary DNS server IP","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 91);
        -- Repair any existing record missing AdapterGuid/Mode (added in 1.0.8).
        -- Skips records with '[]' (handled above) and custom schemas that already contain AdapterGuid.
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"AdapterName","displayName":"Adapter Name","description":"Name of the network adapter","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"AdapterGuid","displayName":"Adapter GUID","description":"WMI SettingID of the adapter (auto-populated by the tray app)","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Mode","displayName":"Mode","description":"Static or DHCP","type":0,"required":true,"validationPattern":null,"defaultValue":"Static"},{"key":"IpAddress","displayName":"IP Address","description":"Static IP address (leave empty for DHCP)","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"SubnetMask","displayName":"Subnet Mask","description":"Subnet mask for static configuration","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"DefaultGateway","displayName":"Default Gateway","description":"Default gateway IP address","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"DnsMode","displayName":"DNS Mode","description":"Auto or Custom","type":0,"required":false,"validationPattern":null,"defaultValue":"Auto"},{"key":"PrimaryDns","displayName":"Primary DNS","description":"Primary DNS server IP","type":0,"required":false,"validationPattern":null,"defaultValue":null},{"key":"SecondaryDns","displayName":"Secondary DNS","description":"Secondary DNS server IP","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 91 AND "ParametersSchemaJson"::text NOT LIKE '%AdapterGuid%';

        -- ── SetEnvironmentVariable (92) ───────────────────────────────────────────
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Set System Environment Variable',
               'Create, update, or remove a system-wide environment variable.',
               92,
               '[{"key":"VariableName","displayName":"Variable Name","description":"Name of the environment variable","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Operation","displayName":"Operation","description":"Set, Append, or Delete","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Value","displayName":"Value","description":"Value to assign (leave empty for Delete)","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
               '[]', false, 60, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 92);
        UPDATE action_definitions
        SET "ParametersSchemaJson" = '[{"key":"VariableName","displayName":"Variable Name","description":"Name of the environment variable","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Operation","displayName":"Operation","description":"Set, Append, or Delete","type":0,"required":true,"validationPattern":null,"defaultValue":null},{"key":"Value","displayName":"Value","description":"Value to assign (leave empty for Delete)","type":0,"required":false,"validationPattern":null,"defaultValue":null}]',
            "UpdatedAt" = now()
        WHERE "ActionType" = 92 AND "ParametersSchemaJson"::text = '[]';

        -- CollectEventLogs (110). The endpoint handler accepts only this bounded allowlist.
        INSERT INTO action_definitions
            ("Id","DisplayName","Description","ActionType","ParametersSchemaJson","ApproverGroupIdsJson",
             "AutoApprove","ApprovalWindowMinutes","MinApprovers","IsEnabled","CreatedAt","UpdatedAt","CreatedByUpn")
        SELECT gen_random_uuid(),
               'Collect Event Log Diagnostics',
               'Collect a bounded, approved ZIP of recent Windows event log entries.',
               110,
               '[{"key":"Channels","displayName":"Channels","description":"Comma-separated allowlist: System, Application, Defender, WindowsUpdate","type":0,"required":true,"validationPattern":"^(System|Application|Defender|WindowsUpdate)(,(System|Application|Defender|WindowsUpdate)){0,3}$","defaultValue":"System,Application"},{"key":"Hours","displayName":"Lookback Hours","description":"Number of hours to collect (1-72)","type":1,"required":true,"validationPattern":"^([1-9]|[1-6][0-9]|7[0-2])$","defaultValue":"24"},{"key":"MaxEvents","displayName":"Maximum Events","description":"Maximum number of events (1-1000)","type":1,"required":true,"validationPattern":"^([1-9]|[1-9][0-9]{1,2}|1000)$","defaultValue":"500"},{"key":"IncludeInformation","displayName":"Include Information Events","description":"Include informational events in addition to warnings and errors","type":2,"required":true,"validationPattern":"^(true|false)$","defaultValue":"false"}]',
               '[]', false, 60, 1, true, now(), now(), 'setup'
        WHERE NOT EXISTS (SELECT 1 FROM action_definitions WHERE "ActionType" = 110);
        """;

    private static string SuperConnStr(string host, int port, string user, string pass,
        string db = "postgres") =>
        $"Host={host};Port={port};Database={db};Username={user};Password={pass};Pooling=false;Timeout=15;CommandTimeout=30";
}
