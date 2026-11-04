#!/bin/sh
# Tests backup and restore end to end: fills a new archive through the web interface, backs it up, deletes
# everything (containers and volumes), restores the backup and checks that the archive is back as it was.
#
#   scripts/test-backup-restore.sh
#
# Runs as its own Compose project on ports 18080 and 15432, so it never touches your own archive. CI runs it on
# every pull request.
set -eu

export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."

export COMPOSE_PROJECT_NAME=commonage-backup-test
# The development setup only, even on a server whose .env switches on compose.production.yaml.
export COMPOSE_FILE=compose.yaml
export COMMONAGE_PORT=18080
export COMMONAGE_DB_PORT=15432
export POSTGRES_PASSWORD="backup-test-$(date +%s)"
export COMMONAGE_ADMIN_EMAIL=admin@example.org
export COMMONAGE_ADMIN_PASSWORD=Backup-test-password-1

base="http://127.0.0.1:$COMMONAGE_PORT"
work=$(mktemp -d)
# On Windows (Git Bash), native programs such as curl need C:/... rather than /tmp/... paths.
if command -v cygpath >/dev/null 2>&1; then work=$(cygpath -m "$work"); fi
jar="$work/cookies.txt"
photo=tests/Commonage.Tests/Fixtures/photo-with-gps.jpg

cleanup() {
    docker compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT

fail() {
    echo "FAILED: $*" >&2
    docker compose logs web >&2 || true
    exit 1
}

sha256() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1"; else shasum -a 256 "$1"; fi | cut -d ' ' -f 1
}

wait_for_app() {
    tries=0
    until curl -fsS "$base/health" >/dev/null 2>&1; do
        tries=$((tries + 1))
        [ "$tries" -lt 90 ] || fail "the app did not start"
        sleep 2
    done
}

# The antiforgery token of the first form on a page.
token() {
    curl -fsS -b "$jar" -c "$jar" "$base$1" |
        sed -n 's/.*name="__RequestVerificationToken" type="hidden" value="\([^"]*\)".*/\1/p' | head -n 1
}

# Sends a form like a browser and prints where the app redirected to.
post() {
    page=$1
    url=$2
    shift 2
    curl -fsS -b "$jar" -c "$jar" -o "$work/response.html" -w '%{redirect_url}' \
        --data-urlencode "__RequestVerificationToken=$(token "$page")" "$@" "$base$url"
}

log_in() {
    post /Account/Login /Account/Login \
        --data-urlencode "Input.Email=$COMMONAGE_ADMIN_EMAIL" --data-urlencode "Input.Password=$COMMONAGE_ADMIN_PASSWORD"
}

echo "Starting a new archive..."
docker compose up -d --build >/dev/null
wait_for_app

echo "Adding a collection, a published item and a photo..."
case "$(log_in)" in */Admin) ;; *) fail "could not log in" ;; esac
post /Admin/Collections/Edit /Admin/Collections/Edit --data-urlencode "Input.Title=Backup test collection" >/dev/null
collection=$(curl -fsS -b "$jar" "$base/Admin/Items/Edit" | sed -n 's/.*<option value="\([0-9]*\)">Backup test collection<.*/\1/p')
[ -n "$collection" ] || fail "the collection was not created"
item_url=$(post /Admin/Items/Edit /Admin/Items/Edit \
    --data-urlencode "Input.Title=The mill in the flood" --data-urlencode "Input.CollectionId=$collection" \
    --data-urlencode "command=publish")
item=${item_url##*/}
case "$item" in '' | *[!0-9]*) fail "the item was not created ($item_url)" ;; esac
curl -fsS -b "$jar" -c "$jar" -o "$work/response.html" -F "__RequestVerificationToken=$(token "/Admin/Items/Edit/$item")" \
    -F "files=@$photo;type=image/jpeg" "$base/Admin/Items/Edit/$item?handler=Upload"
file=$(curl -fsS -b "$jar" "$base/Admin/Items/Edit/$item" | sed -n 's|.*href="/admin/media/\([0-9]*\)/original".*|\1|p' | head -n 1)
[ -n "$file" ] || fail "the photo was not uploaded"

tries=0
until curl -fsS -o "$work/preview-before.jpg" "$base/media/$file/preview" 2>/dev/null; do
    tries=$((tries + 1))
    [ "$tries" -lt 30 ] || fail "no preview was created"
    sleep 1
done
cp "$jar" "$work/cookies-before.txt"

echo "Backing up..."
sh scripts/backup.sh "$work/backups"
backup=$(ls -d "$work"/backups/commonage-*)
MSYS_NO_PATHCONV=1 docker compose run --rm --no-deps -T --entrypoint cat web /app/App_Data/last-backup | grep -Eq '^[0-9]{4}-[0-9]{2}-[0-9]{2}T' \
    || fail "the backup did not record its date for the app"

echo "Deleting the archive completely..."
docker compose down --volumes >/dev/null 2>&1
if docker volume ls --format '{{.Name}}' | grep -q "^${COMPOSE_PROJECT_NAME}_"; then
    fail "the volumes were not deleted"
fi

echo "Restoring..."
sh scripts/restore.sh --yes "$backup"
wait_for_app

echo "Checking the restored archive..."
curl -fsS "$base/items/$item" | grep -q "The mill in the flood" || fail "the item page is missing"
curl -fsS -o "$work/preview-after.jpg" "$base/media/$file/preview" || fail "the preview is missing"
[ "$(sha256 "$work/preview-after.jpg")" = "$(sha256 "$work/preview-before.jpg")" ] || fail "the preview changed"
# A login from before the backup still works: the keys that sign login cookies are in the database too.
status=$(curl -sS -b "$work/cookies-before.txt" -o "$work/original.jpg" -w '%{http_code}' "$base/admin/media/$file/original")
[ "$status" = 200 ] || fail "the login from before the backup no longer works ($status)"
[ "$(sha256 "$work/original.jpg")" = "$(sha256 "$photo")" ] || fail "the original file changed"
rm -f "$jar"
case "$(log_in)" in */Admin) ;; *) fail "the admin account was not restored" ;; esac

echo "Backup and restore work."
