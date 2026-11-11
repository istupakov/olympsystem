#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ( $# -eq 2 && $2 != --force ) ]]; then
    echo "Usage: bash scripts/prepare-production.sh <server-ip> [--force]" >&2
    exit 2
fi

server_ip=$1
if [[ -z "$server_ip" || "$server_ip" == *,* || "$server_ip" == *$'\n'* ]]; then
    echo "Provide one application server IP address." >&2
    exit 2
fi

cd "$(dirname "$0")/.."
host_user=${SUDO_UID:-$(id -u)}
host_group=${SUDO_GID:-$(id -g)}
umask 077

privileged=()
if (( EUID != 0 )); then
    privileged=(sudo)
fi

"${privileged[@]}" install -d -o "$host_user" -g "$host_group" -m 751 tls

if [[ ${2:-} != --force && ( -e tls/Olymp.WebApp.pfx || -e tls/Olymp.WebApp.crt ) ]]; then
    if [[ ! -f tls/Olymp.WebApp.pfx || ! -f tls/Olymp.WebApp.crt ]]; then
        echo "Incomplete certificate files in ./tls. Use --force to replace them." >&2
        exit 1
    fi
    if ! openssl verify -CAfile tls/Olymp.WebApp.crt -verify_ip "$server_ip" tls/Olymp.WebApp.crt; then
        echo "Existing certificate is invalid for this IP. Use --force to replace it." >&2
        exit 1
    fi
    echo "Keeping the existing certificate."
else
    temporary_directory=$(mktemp -d "$PWD/tls/.production-certificate.XXXXXX")
    trap 'rm -rf -- "$temporary_directory"' EXIT

    openssl req -x509 -newkey rsa:2048 -sha256 -noenc \
        -days 3650 \
        -subj "/CN=OlympSystem" \
        -addext "subjectAltName=IP:$server_ip,DNS:localhost" \
        -addext "basicConstraints=critical,CA:FALSE" \
        -addext "keyUsage=critical,digitalSignature,keyEncipherment" \
        -addext "extendedKeyUsage=serverAuth" \
        -keyout "$temporary_directory/Olymp.WebApp.key" \
        -out "$temporary_directory/Olymp.WebApp.crt"

    openssl pkcs12 -export \
        -inkey "$temporary_directory/Olymp.WebApp.key" \
        -in "$temporary_directory/Olymp.WebApp.crt" \
        -name OlympSystem \
        -passout pass: \
        -out "$temporary_directory/Olymp.WebApp.pfx"

    mv -f -- "$temporary_directory/Olymp.WebApp.pfx" tls/Olymp.WebApp.pfx
    mv -f -- "$temporary_directory/Olymp.WebApp.crt" tls/Olymp.WebApp.crt

    echo "Created ./tls/Olymp.WebApp.pfx and ./tls/Olymp.WebApp.crt (3650 days)."
fi

bash scripts/prepare-sql-directories.sh

# The app user writes keys; the host user's group can list and read existing keys.
"${privileged[@]}" install -d -o 1654 -g "$host_group" -m 2750 keys
"${privileged[@]}" chown -R "1654:$host_group" keys
"${privileged[@]}" find keys -type d -exec chmod 2750 {} +
"${privileged[@]}" find keys -type f -exec chmod 640 {} +
"${privileged[@]}" chown "1654:$host_group" tls/Olymp.WebApp.pfx
"${privileged[@]}" chmod 640 tls/Olymp.WebApp.pfx
"${privileged[@]}" chmod 644 tls/Olymp.WebApp.crt

echo "Prepared SQL directories, TLS certificate and Data Protection keys."
echo "Copy only ./tls/Olymp.WebApp.crt to the Caddy server."
