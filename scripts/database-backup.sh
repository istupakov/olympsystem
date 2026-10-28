#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
backup_file="olympdb-$(date +%Y%m%d).bak"

# Extra arguments select the Compose configuration, including production.
docker compose "$@" exec -T sqlserver bash -c '
    export SQLCMDPASSWORD="$MSSQL_SA_PASSWORD"
    exec /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -d master -C -b -x
' <<SQL
BACKUP DATABASE [olympdb]
TO DISK = N'/var/opt/mssql/backup/$backup_file'
WITH COPY_ONLY, CHECKSUM, INIT, STATS = 10;
GO
SQL

printf 'Backup saved to ./backup/%s\n' "$backup_file"
