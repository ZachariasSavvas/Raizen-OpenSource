#!/usr/bin/env bash
# Generates a self-signed TLS certificate for local testing.
# For production, replace nginx/certs/raizen.crt + raizen.key with a
# CA-signed certificate (Let's Encrypt, corporate CA, or purchased cert).

set -euo pipefail

CERT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../nginx/certs" && pwd)"
mkdir -p "$CERT_DIR"

DOMAIN="${1:-raizen.local}"

echo "[Raizen] Generating self-signed certificate for: $DOMAIN"

openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
    -keyout "$CERT_DIR/raizen.key" \
    -out    "$CERT_DIR/raizen.crt" \
    -subj   "/C=US/ST=State/L=City/O=Raizen Security/CN=$DOMAIN" \
    -addext "subjectAltName=DNS:$DOMAIN,DNS:localhost,IP:127.0.0.1"

chmod 600 "$CERT_DIR/raizen.key"

echo "[Raizen] Certificate written to:"
echo "  $CERT_DIR/raizen.crt"
echo "  $CERT_DIR/raizen.key"
echo ""
echo "NOTE: This is a self-signed cert. For production, replace these files"
echo "      with a certificate signed by a trusted CA."
