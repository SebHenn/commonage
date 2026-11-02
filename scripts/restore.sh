#!/bin/sh
# Restores a backup made with scripts/backup.sh. Everything currently in the archive is replaced.
#
#   scripts/restore.sh [--yes] <backup-folder>
#
# Restore into the same or a newer version of Commonage; the app upgrades the database when it starts.
# See docs/operations.md.
set -eu

export MSYS_NO_PATHCONV=1

confirmed=no
if [ "${1:-}" = "--yes" ]; then
    confirmed=yes
    shift
fi
if [ $# -ne 1 ]; then
    echo "Usage: scripts/restore.sh [--yes] <backup-folder>" >&2
    exit 2
fi

folder=$(cd "$1" && pwd)
cd "$(dirname "$0")/.."

for file in database.dump media.tar SHA256SUMS; do
    [ -f "$folder/$file" ] || { echo "$folder/$file is missing. Is this a Commonage backup folder?" >&2; exit 1; }
done

echo "Checking the backup..."
if command -v sha256sum >/dev/null 2>&1; then
    (cd "$folder" && sha256sum -c --quiet SHA256SUMS)
else
    (cd "$folder" && shasum -a 256 -c --quiet SHA256SUMS)
fi

if [ "$confirmed" != yes ]; then
    printf 'This replaces the whole archive (database and files) with the backup in %s.\nType "restore" to continue: ' "$folder"
    read -r answer
    [ "$answer" = restore ] || { echo "Nothing was changed."; exit 1; }
fi

echo "Stopping the app..."
docker compose stop web >/dev/null 2>&1 || true
docker compose up -d --wait db >/dev/null

echo "Restoring the database..."
docker compose exec -T db dropdb --username=commonage --if-exists --force commonage
docker compose exec -T db createdb --username=commonage commonage
docker compose exec -T db pg_restore --username=commonage --dbname=commonage --no-owner --exit-on-error < "$folder/database.dump"

echo "Restoring the uploaded files..."
docker compose run --rm --no-deps -T --entrypoint sh web -c \
    'find /app/App_Data/media -mindepth 1 -delete && tar -C /app/App_Data -xf -' < "$folder/media.tar"

echo "Starting the app..."
docker compose up -d web >/dev/null
echo "Restored from $folder"
