#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
sql_group=${SUDO_GID:-$(id -g)}
privileged=()
if (( EUID != 0 )); then
    privileged=(sudo)
fi

"${privileged[@]}" install -d -o 10001 -g "$sql_group" -m 2770 data backup
"${privileged[@]}" chown -R "10001:$sql_group" data backup
"${privileged[@]}" chmod -R g+rwX data backup
"${privileged[@]}" find data backup -type d -exec chmod g+s {} +
