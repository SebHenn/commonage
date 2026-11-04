#!/bin/sh
# Backs up a Commonage archive that runs with Docker Compose: the database and all uploaded files.
#
#   scripts/backup.sh [target-directory]      (default: ./backups)
#
# Writes a new folder commonage-<date>-<time> with database.dump, media.tar and SHA256SUMS. Copy it to another
# disk or place; a backup on the same server is lost with the server. The app can keep running meanwhile.
# Backups contain everything in the archive, including contributors' personal data: keep them safe, and delete
# old ones you no longer need. See docs/operations.md.
set -eu

# Git Bash on Windows would otherwise turn paths such as /app/App_Data into Windows paths.
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."

target=${1:-backups}
folder="$target/commonage-$(date -u +%Y%m%d-%H%M%S)"
mkdir -p "$folder"

sha256() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$@"; else shasum -a 256 "$@"; fi
}

docker compose up -d --wait db >/dev/null

# The database first, then the files: an upload in between leaves an extra file, which does no harm.
echo "Backing up the database..."
docker compose exec -T db pg_dump --username=commonage --dbname=commonage --format=custom > "$folder/database.dump"

echo "Backing up the uploaded files..."
docker compose run --rm --no-deps -T --entrypoint tar web -C /app/App_Data -cf - media > "$folder/media.tar"

(cd "$folder" && sha256 database.dump media.tar > SHA256SUMS)
echo "Backup written to $folder"

# Admins see this date in the app. It sits next to the media folder, so restoring an old backup does not bring back an
# old date. Failing to write it leaves the backup as good as it is, so it only gets a warning.
if ! date -u +%Y-%m-%dT%H:%M:%SZ | docker compose run --rm --no-deps -T --entrypoint sh web -c 'cat > /app/App_Data/last-backup'; then
    echo "Warning: could not tell the app about this backup, so it may show the last backup as overdue." >&2
fi
