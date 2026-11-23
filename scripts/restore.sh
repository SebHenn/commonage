#!/bin/sh
# Restores a backup made with scripts/backup.sh. Everything currently in the archive is replaced.
#
#   scripts/restore.sh [--yes] [--database-only] <backup-folder>
#
# --database-only leaves the uploaded files as they are. scripts/update.sh uses it when it moves the archive to a new
# PostgreSQL version, which changes nothing in the files.
#
# Restore into the same or a newer version of Commonage; the app upgrades the database when it starts.
# See docs/operations.md.
set -eu

export MSYS_NO_PATHCONV=1

confirmed=no
database_only=no
while [ $# -gt 0 ]; do
    case "$1" in
        --yes) confirmed=yes ;;
        --database-only) database_only=yes ;;
        -*) echo "Usage: scripts/restore.sh [--yes] [--database-only] <backup-folder>" >&2; exit 2 ;;
        *) break ;;
    esac
    shift
done
if [ $# -ne 1 ]; then
    echo "Usage: scripts/restore.sh [--yes] [--database-only] <backup-folder>" >&2
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
    scope="database and files"
    [ "$database_only" != yes ] || scope="database"
    printf 'This replaces the whole archive (%s) with the backup in %s.\nType "restore" to continue: ' "$scope" "$folder"
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

if [ "$database_only" = yes ]; then
    echo "Leaving the uploaded files as they are."
else
    echo "Restoring the uploaded files..."
    docker compose run --rm --no-deps -T --entrypoint sh web -c \
        'find /app/App_Data/media -mindepth 1 -delete && tar -C /app/App_Data -xf -' < "$folder/media.tar"
fi

echo "Starting the app..."
docker compose up -d web >/dev/null
echo "Restored from $folder"
