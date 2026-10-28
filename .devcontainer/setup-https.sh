#!/usr/bin/env bash
set -eu

cd "$(dirname "$0")/.."
umask 077
mkdir -p tls

certificate_password='local-development'

if [ -f tls/Olymp.Site.pfx ]; then
    dotnet dev-certs https --clean --import "$PWD/tls/Olymp.Site.pfx" \
        --password "$certificate_password"
else
    dotnet dev-certs https --export-path "$PWD/tls/Olymp.Site.pfx" \
        --password "$certificate_password"
fi

dotnet dev-certs https --trust --export-path "$PWD/tls/Olymp.Site.crt" --format PEM
