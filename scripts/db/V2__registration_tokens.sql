-- Raizen V2: Registration tokens for agent self-registration
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
