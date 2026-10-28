#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
sql_group=$(id -g)

sudo install -d -o 10001 -g "$sql_group" -m 2770 data backup
sudo chown -R "10001:$sql_group" data backup
sudo chmod -R g+rwX data backup
sudo find data backup -type d -exec chmod g+s {} +
