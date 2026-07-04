-- Raizen initial database schema (PostgreSQL 16)
-- Run this manually if you prefer SQL migrations over EF Core migrations.
-- The EF Core migration in RaizenDbContext covers the same schema.

CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ── action_definitions ────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS action_definitions (
    "Id"                              UUID            NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    "DisplayName"                     VARCHAR(200)    NOT NULL,
    "Description"                     VARCHAR(2000),
    "ActionType"                      INTEGER         NOT NULL,
    "ParametersSchemaJson"            JSONB           NOT NULL DEFAULT '[]',
    "ApproverGroupIdsJson"            JSONB           NOT NULL DEFAULT '[]',
    "AutoApprove"                     BOOLEAN         NOT NULL DEFAULT FALSE,
    "AutoApproveConditionDescription" VARCHAR(1000),
    "ApprovalWindowMinutes"           INTEGER         NOT NULL DEFAULT 60,
    "IsEnabled"                       BOOLEAN         NOT NULL DEFAULT TRUE,
    "CreatedAt"                       TIMESTAMPTZ     NOT NULL DEFAULT now(),
    "UpdatedAt"                       TIMESTAMPTZ     NOT NULL DEFAULT now(),
    "CreatedByUpn"                    VARCHAR(320)    NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS idx_action_definitions_type    ON action_definitions ("ActionType");
CREATE INDEX IF NOT EXISTS idx_action_definitions_enabled ON action_definitions ("IsEnabled");

-- ── endpoint_registrations ────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS endpoint_registrations (
    "Id"           UUID          NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    "MachineId"    VARCHAR(256)  NOT NULL UNIQUE,
    "MachineName"  VARCHAR(256)  NOT NULL,
    "Description"  VARCHAR(1000),
    "IsEnabled"    BOOLEAN       NOT NULL DEFAULT TRUE,
    "ApiKeyHash"   VARCHAR(512)  NOT NULL,
    "RegisteredAt" TIMESTAMPTZ   NOT NULL DEFAULT now(),
    "LastSeenAt"   TIMESTAMPTZ,
    "OsVersion"    VARCHAR(256),
    "AgentVersion" VARCHAR(64),
    "TagsJson"     JSONB         NOT NULL DEFAULT '[]'
);

-- ── elevation_requests ────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS elevation_requests (
    "Id"                    UUID          NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    "ActionDefinitionId"    UUID          NOT NULL REFERENCES action_definitions ("Id") ON DELETE RESTRICT,
    "EndpointRegistrationId" UUID         NOT NULL REFERENCES endpoint_registrations ("Id") ON DELETE RESTRICT,
    "RequesterUpn"          VARCHAR(320)  NOT NULL,
    "RequesterDisplayName"  VARCHAR(256),
    "Justification"         VARCHAR(2000) NOT NULL,
    "TicketReference"       VARCHAR(200),
    "ParametersJson"        JSONB         NOT NULL DEFAULT '{}',
    "Status"                INTEGER       NOT NULL DEFAULT 0,
    "SubmittedAt"           TIMESTAMPTZ   NOT NULL DEFAULT now(),
    "ExpiresAt"             TIMESTAMPTZ   NOT NULL,
    "ReviewedAt"            TIMESTAMPTZ,
    "ExecutedAt"            TIMESTAMPTZ,
    "ReviewerUpn"           VARCHAR(320),
    "ReviewerNote"          VARCHAR(1000),
    "ExecutionResult"       VARCHAR(4000),
    "ExecutionError"        VARCHAR(4000),
    "xmin"                  xid           NOT NULL  -- PostgreSQL row version for optimistic concurrency
);

CREATE INDEX IF NOT EXISTS idx_requests_status       ON elevation_requests ("Status");
CREATE INDEX IF NOT EXISTS idx_requests_requester    ON elevation_requests ("RequesterUpn");
CREATE INDEX IF NOT EXISTS idx_requests_submitted    ON elevation_requests ("SubmittedAt" DESC);
CREATE INDEX IF NOT EXISTS idx_requests_ep_status    ON elevation_requests ("EndpointRegistrationId", "Status");

-- ── audit_logs ────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS audit_logs (
    "Id"            UUID          NOT NULL DEFAULT gen_random_uuid() PRIMARY KEY,
    "RequestId"     UUID          REFERENCES elevation_requests ("Id") ON DELETE SET NULL,
    "Event"         VARCHAR(100)  NOT NULL,
    "ActorUpn"      VARCHAR(320)  NOT NULL,
    "TargetMachine" VARCHAR(256),
    "Detail"        VARCHAR(4000),
    "IpAddress"     VARCHAR(64),
    "OccurredAt"    TIMESTAMPTZ   NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS idx_audit_occurred ON audit_logs ("OccurredAt" DESC);
CREATE INDEX IF NOT EXISTS idx_audit_actor    ON audit_logs ("ActorUpn");
CREATE INDEX IF NOT EXISTS idx_audit_event    ON audit_logs ("Event");
CREATE INDEX IF NOT EXISTS idx_audit_request  ON audit_logs ("RequestId");

-- Audit log is append-only: revoke DELETE and UPDATE from the app user
-- (run as superuser once, substituting your actual app role)
-- REVOKE DELETE, UPDATE ON audit_logs FROM raizen_app;
