# Raizen Database Migration Convention

## Schema management

Raizen uses `EnsureCreated` + `CREATE TABLE IF NOT EXISTS` for the initial schema.
For schema changes between releases, versioned SQL scripts are used.

## Upgrade procedure

Before deploying a new server version:

1. Back up the database:
   ```bash
   docker compose exec postgres pg_dump -U raizen raizen > backup-$(date +%Y%m%d).sql
   ```

2. Run any new migration scripts in order:
   ```bash
   docker compose exec -T postgres psql -U raizen -d raizen < scripts/db/upgrades/V2__description.sql
   ```

3. Deploy the new server images:
   ```bash
   bash scripts/deploy-server.sh
   ```

## Migration file naming

`V{version}__{description}.sql` — two underscores between version and description.

Example: `V2__add_request_tags.sql`

## upgrades/

Contains one SQL file per schema version change, applied in version order.
The base schema (V1) is defined by the application at startup via `EnsureCreated`
and the inline `CREATE TABLE IF NOT EXISTS` statements in `Program.cs`.
