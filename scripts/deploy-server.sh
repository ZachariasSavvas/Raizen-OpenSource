#!/usr/bin/env bash
# Raizen Server deployment script
# Builds images and starts the server stack using docker-compose.
# Run from the repository root.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE="${REPO_ROOT}/.env"

log()  { echo "[Raizen] $*"; }
fail() { echo "[ERROR]  $*" >&2; exit 1; }

# ── Check prerequisites ────────────────────────────────────────────────────────
command -v docker     >/dev/null || fail "docker is not installed."
command -v docker     >/dev/null && docker compose version >/dev/null 2>&1 || \
    command -v docker-compose >/dev/null || fail "docker compose is not installed."

# ── Ensure .env exists ─────────────────────────────────────────────────────────
if [ ! -f "$ENV_FILE" ]; then
    log "No .env file found. Creating from template..."
    # Generate random encryption key (32 bytes → 64 hex chars)
    ENC_KEY=$(openssl rand -hex 32 2>/dev/null || cat /dev/urandom | tr -dc 'a-f0-9' | head -c64)
    cat > "$ENV_FILE" <<EOF
# Raizen Security — server environment variables
# DO NOT commit this file to source control.

POSTGRES_PASSWORD=CHANGE_ME_STRONG_PASSWORD

# Auto-generated encryption key — keep this secret and back it up.
# Losing it means existing admin passwords cannot be decrypted.
RAIZEN_ENCRYPTION_KEY=${ENC_KEY}

# RSA private key PEM for poll response signing (single-line, \\n-escaped).
# Generate with: openssl genrsa -out poll-signing.key 2048
# Then: awk 'NF{printf "%s\\\\n",$0}' poll-signing.key
RAIZEN_POLL_SIGNING_KEY=

AZURE_TENANT_ID=YOUR_TENANT_ID
AZURE_CLIENT_ID_API=YOUR_API_APP_REGISTRATION_CLIENT_ID
AZURE_CLIENT_ID_WEB=YOUR_WEB_APP_REGISTRATION_CLIENT_ID
AZURE_CLIENT_SECRET_WEB=YOUR_WEB_CLIENT_SECRET
EOF
    fail ".env file created at $ENV_FILE — fill in the placeholder values and rerun."
fi

# ── Load env and validate required vars ───────────────────────────────────────
set -a; source "$ENV_FILE"; set +a

required_vars=(POSTGRES_PASSWORD RAIZEN_ENCRYPTION_KEY AZURE_TENANT_ID AZURE_CLIENT_ID_API AZURE_CLIENT_ID_WEB AZURE_CLIENT_SECRET_WEB)
for var in "${required_vars[@]}"; do
    val="${!var:-}"
    if [ -z "$val" ] || [[ "$val" == CHANGE_ME* ]] || [[ "$val" == YOUR_* ]]; then
        fail "Required .env variable $var is not set or still has a placeholder value."
    fi
done

# ── Ensure TLS certificate exists ─────────────────────────────────────────────
CERT_DIR="${REPO_ROOT}/nginx/certs"
if [ ! -f "${CERT_DIR}/raizen.crt" ] || [ ! -f "${CERT_DIR}/raizen.key" ]; then
    log "No TLS certificate found — generating self-signed cert for testing..."
    log "For production, replace nginx/certs/raizen.crt and raizen.key with a CA-signed cert."
    bash "${REPO_ROOT}/scripts/gen-self-signed-cert.sh"
fi

# ── Build and deploy ───────────────────────────────────────────────────────────
cd "$REPO_ROOT"

log "Building Docker images..."
docker compose build --no-cache

log "Starting Raizen server stack..."
docker compose up -d

log "Waiting for PostgreSQL to be healthy..."
timeout 60 bash -c 'until docker compose exec postgres pg_isready -U raizen -d raizen 2>/dev/null; do sleep 2; done'

log ""
log "╔══════════════════════════════════════════════════════════╗"
log "  Raizen Security server stack is up!"
log "  Admin portal : https://localhost"
log "  Endpoint API  : https://localhost:5001"
log "╚══════════════════════════════════════════════════════════╝"
log ""
log "Tail logs:   docker compose logs -f"
log "Stop stack:  docker compose down"
log ""
log "IMPORTANT: If using a self-signed cert, endpoints need the cert"
log "           imported as a trusted CA, or set AllowInsecure in raizen-config.json."
