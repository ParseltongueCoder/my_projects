#!/usr/bin/env bash
# Generates a local CA and a server certificate for the simulator's RabbitMQ (dev only).
set -euo pipefail
cd "$(dirname "$0")/.."
out=config/tls
if [[ -f "$out/server.pem" && "${1:-}" != "--force" ]]; then
  echo "$out already exists (use --force to regenerate)"
  exit 0
fi
mkdir -p "$out"
openssl req -x509 -newkey rsa:2048 -nodes -days 825 -subj "/CN=UofSim Dev CA" \
  -keyout "$out/ca.key" -out "$out/ca.pem" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj "/CN=rabbitmq" \
  -keyout "$out/server.key" -out "$out/server.csr" 2>/dev/null
openssl x509 -req -in "$out/server.csr" -CA "$out/ca.pem" -CAkey "$out/ca.key" -CAcreateserial \
  -days 825 -out "$out/server.pem" \
  -extfile <(printf "subjectAltName=DNS:rabbitmq,DNS:localhost,IP:127.0.0.1") 2>/dev/null
rm -f "$out/server.csr" "$out/ca.srl"
# The broker runs as a non-root user inside the container and must be able to read the key.
chmod 644 "$out"/*.key
echo "TLS material written to $out"
