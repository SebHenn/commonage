#!/bin/sh
# Tests updating a real archive: starts an earlier published release in the production setup, makes a backup with that
# release's own scripts, tries an update that fails after upgrading the database, checks that everything went back,
# updates to the current code (which moves the archive to a new PostgreSQL major version when the code uses another one
# than the release, and keeps the old database in the volume), and finally restores the old release's backup into the new version.
#
#   scripts/test-update.sh          from the newest earlier release that has a published image
#   scripts/test-update.sh 0.3      from the newest 0.3.x release that has a published image
#
# CI runs it for each earlier minor (job "update-from" in ci.yml), and from the newest one on every pull request.
# Works on its own clone and as its own Compose project, so it never touches your archive, but it needs ports 80 and
# 443 free. CI runs it on every pull request.
set -eu

export MSYS_NO_PATHCONV=1
repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$repository"
# On Windows (Git Bash), native programs such as git need C:/... rather than /c/... paths.
if command -v cygpath >/dev/null 2>&1; then repository=$(cygpath -m "$repository"); fi

head=$(git rev-parse HEAD)
minor=${1:-}
case "$minor" in
    "" | [0-9]*.[0-9]*) case "$minor" in *[!0-9.]*) echo "Usage: scripts/test-update.sh [minor, such as 0.3]" >&2; exit 2 ;; esac ;;
    *) echo "Usage: scripts/test-update.sh [minor, such as 0.3]" >&2; exit 2 ;;
esac
# The newest earlier release (of the given minor) with a published image. A release just tagged may still be waiting for
# its approval, and v0.1.0 never had an image.
previous=
for tag in $(git tag --list "v${minor:-[0-9]*}.[0-9]*" --merged HEAD --sort=-v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$'); do
    [ "$(git rev-parse "$tag^{commit}")" != "$head" ] || continue
    if docker pull --quiet "ghcr.io/sebhenn/commonage:${tag#v}" >/dev/null 2>&1; then
        previous=$tag
        break
    fi
    echo "$tag has no published image yet, so trying the release before it."
done
[ -n "$previous" ] || { echo "FAILED: no earlier release ${minor:+of $minor }with an image found. CI must fetch the tags." >&2; exit 1; }
echo "Testing the update from $previous to $head."

work=$(mktemp -d)
if command -v cygpath >/dev/null 2>&1; then work=$(cygpath -m "$work"); fi
image=commonage-update-test

export COMPOSE_PROJECT_NAME=commonage-update-test
export COMPOSE_FILE=compose.yaml:compose.production.yaml
export COMPOSE_PATH_SEPARATOR=:
export COMMONAGE_UPDATE_SKIP_PULL=1
export COMMONAGE_UPDATE_TIMEOUT=120

cleanup() {
    (cd "$work/archive" 2>/dev/null && docker compose down --volumes --remove-orphans >/dev/null 2>&1) || true
    docker image rm -f "$image:8.0.0" "$image:9.0.0" "$image:9.0.1" >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT

fail() {
    echo "FAILED: $*" >&2
    (cd "$work/archive" && docker compose ps --all >&2; docker compose logs --tail 60 web >&2) || true
    exit 1
}

echo "Preparing the images..."
docker tag "ghcr.io/sebhenn/commonage:${previous#v}" "$image:8.0.0"
docker build --quiet -t "$image:9.0.0" . >/dev/null
# Upgrades the database like the real thing, but never answers: the worst moment for an update to fail.
printf 'FROM %s:9.0.0\nENTRYPOINT ["dotnet", "Commonage.Web.dll", "--urls", "http://+:9999"]\n' "$image" | docker build --quiet -t "$image:9.0.1" - >/dev/null

echo "Preparing the archive..."
git clone --quiet --no-hardlinks "$repository" "$work/archive"
cd "$work/archive"
git config user.name test
git config user.email test@example.org
git checkout --quiet "$head"
git tag v9.0.0
git commit --quiet --no-verify --allow-empty -m "The broken test release"
git tag v9.0.1
# The previous release, with this update script. Older releases do not have it, and a newer one may have the same.
git checkout --quiet "$previous"
git show "$head:scripts/update.sh" > scripts/update.sh
git add scripts/update.sh
git commit --quiet --no-verify --allow-empty -m "Add the update script for the test"
git tag v8.0.0
cat > .env <<EOF
POSTGRES_PASSWORD=update-test-$(date +%s)
COMMONAGE_ADMIN_EMAIL=admin@example.org
COMMONAGE_ADMIN_PASSWORD=Update-test-password-1
COMMONAGE_DEMO=en
COMMONAGE_DOMAIN=localhost
COMMONAGE_IMAGE=$image
COMMONAGE_VERSION=8.0.0
EOF

running_image() {
    docker inspect --format '{{.Config.Image}}' "$(docker compose ps --quiet web)"
}

database_major() {
    version=$(docker compose exec -T db psql --username=commonage --dbname=commonage -Atc 'show server_version_num' | tr -d '\r')
    echo $(( version / 10000 ))
}

item_count() {
    docker compose exec -T db psql --username=commonage --dbname=commonage -Atc 'select count(*) from "ArchiveItems"' | tr -d '\r'
}

# The PostgreSQL major that the current code starts. When the previous release runs another one, the update moves the
# archive to it, and that is the path this test is about.
expected_major=$(git show "$head:compose.yaml" | sed -n 's/^ *image: *postgres:\([0-9][0-9]*\).*/\1/p' | head -n 1)
[ -n "$expected_major" ] || fail "could not read the PostgreSQL version from compose.yaml"

# Different for every database cluster: it only stays the same when the original data directory is the one that runs.
cluster_identity() {
    docker compose exec -T db psql --username=commonage --dbname=commonage -Atc 'select system_identifier from pg_control_system()' | tr -d '\r'
}

migrations() {
    docker compose exec -T db psql --username=commonage --dbname=commonage -Atc 'select count(*) from "__EFMigrationsHistory"' | tr -d '\r'
}

wait_for_page() {
    tries=0
    until curl -kfsS https://localhost/ 2>/dev/null | grep -q "Mill Street"; do
        tries=$((tries + 1))
        [ "$tries" -lt 60 ] || fail "the archive does not answer through Caddy"
        sleep 2
    done
}

echo "Starting $previous with the demo content..."
docker compose up -d --no-build >/dev/null 2>&1
wait_for_page
migrations_before=$(migrations)
major_before=$(database_major)
items_before=$(item_count)
identity_before=$(cluster_identity)
[ "$items_before" -gt 0 ] || fail "the demo content is not in the database"
echo "$previous runs PostgreSQL $major_before; the current code runs PostgreSQL $expected_major."

# The backup that release itself makes, with its own scripts. It is restored into the new version at the end.
echo "Making a backup with $previous..."
sh scripts/backup.sh "$work/old-backup" >/dev/null || fail "$previous could not make a backup"
old_backup=$(ls -dt "$work"/old-backup/commonage-* 2>/dev/null | head -n 1)
[ -n "$old_backup" ] || fail "$previous made no backup folder"

echo "Trying an update that fails after upgrading the database..."
if sh scripts/update.sh 9.0.1 "$work/backups"; then
    fail "the update to a broken version reported success"
fi
wait_for_page
[ "$(running_image)" = "$image:8.0.0" ] || fail "the old image does not run again, but $(running_image)"
[ "$(git describe --tags --exact-match HEAD)" = v8.0.0 ] || fail "the checkout did not go back"
grep -q '^COMMONAGE_VERSION=8.0.0' .env || fail ".env did not go back"
grep -q '^POSTGRES_PASSWORD=update-test' .env || fail ".env lost its other lines"
[ "$(migrations)" = "$migrations_before" ] || fail "the database was not restored ($(migrations) migrations, was $migrations_before)"
[ "$(database_major)" = "$major_before" ] || fail "the database is on PostgreSQL $(database_major) after going back, was $major_before"
[ "$(cluster_identity)" = "$identity_before" ] || fail "going back did not bring back the original database (another cluster runs)"
[ "$(item_count)" = "$items_before" ] || fail "the items were not restored after going back"
[ ! -e .env.updating ] || fail "a temporary file stayed behind"

if [ "$major_before" != "$expected_major" ]; then
    echo "Interrupting an update right after the old database was set aside..."
    if COMMONAGE_UPDATE_TEST_CRASH=after-rename sh scripts/update.sh 9.0.0 "$work/backups" >/dev/null 2>&1; then
        fail "the interrupted update reported success"
    fi
    [ -f .update-in-progress ] || fail "the interrupted update left no marker"
    if again=$(sh scripts/update.sh 9.0.0 "$work/backups" 2>&1); then fail "an update was accepted over an unfinished one"; fi
    echo "$again" | grep -q "/var/lib/postgresql/$major_before/kept" || fail "the refusal does not say where the old database is: $again"
    echo "$again" | grep -q -- "--recover" || fail "the refusal does not say how to recover: $again"
    sh scripts/update.sh --recover >/dev/null || fail "the recovery failed"
    wait_for_page
    [ ! -e .update-in-progress ] || fail "the marker stayed after the recovery"
    [ "$(running_image)" = "$image:8.0.0" ] || fail "the old image does not run after the recovery, but $(running_image)"
    [ "$(git describe --tags --exact-match HEAD)" = v8.0.0 ] || fail "the checkout did not go back in the recovery"
    grep -q '^COMMONAGE_VERSION=8.0.0' .env || fail ".env did not go back in the recovery"
    [ "$(database_major)" = "$major_before" ] || fail "the database is not on PostgreSQL $major_before after the recovery"
    [ "$(cluster_identity)" = "$identity_before" ] || fail "the recovery did not bring back the original database"
    [ "$(item_count)" = "$items_before" ] || fail "the items are not as before after the recovery"
fi

echo "Refusing what cannot work..."
if sh scripts/update.sh 7.0.0 "$work/backups" 2>/dev/null; then fail "an unknown release was accepted"; fi
if sh scripts/update.sh nonsense "$work/backups" 2>/dev/null; then fail "a wrong version was accepted"; fi
echo "changed" >> compose.yaml
if sh scripts/update.sh 9.0.0 "$work/backups" 2>/dev/null; then fail "an update over changed files was accepted"; fi
git checkout --quiet -- compose.yaml

echo "Updating to the current code..."
sh scripts/update.sh 9.0.0 "$work/backups"
wait_for_page
[ "$(running_image)" = "$image:9.0.0" ] || fail "the new image does not run, but $(running_image)"
[ "$(git describe --tags --exact-match HEAD)" = v9.0.0 ] || fail "the checkout is not on the new release"
grep -q '^COMMONAGE_VERSION=9.0.0' .env || fail ".env does not name the new version"
[ "$(grep -c '^COMMONAGE_LOGIN_KEY=.\{44\}$' .env)" = 1 ] || fail ".env did not get exactly one login key"
readable=$(docker compose exec -T db psql --username=commonage --dbname=commonage -Atc \
    "select count(*) from \"DataProtectionKeys\" where \"Xml\" like '%requiresEncryption=\"true\"%'" | tr -d '\r')
[ "$readable" = 0 ] || fail "$readable login keys in the database are still readable without the key"
curl -kfsS https://localhost/search | grep -q "Flood in Mill Street" || fail "the content did not survive the update"
[ "$(database_major)" = "$expected_major" ] || fail "the database is on PostgreSQL $(database_major), not $expected_major"
[ "$(item_count)" = "$items_before" ] || fail "the items did not survive the update ($(item_count), were $items_before)"
if [ "$major_before" != "$expected_major" ]; then
    # The old database stays in the volume, set aside, until the curator removes it with the command the update prints.
    kept=$(docker compose run --rm --no-deps -T --entrypoint ls db "/var/lib/postgresql/$major_before" | tr -d '\r')
    [ "$kept" = kept ] || fail "the old database was not kept in the volume ('$kept')"
    docker compose run --rm --no-deps -T --entrypoint rm db -rf "/var/lib/postgresql/$major_before" >/dev/null
    old_folders=$(docker compose run --rm --no-deps -T --entrypoint ls db /var/lib/postgresql | tr -d '\r')
    [ "$old_folders" = "$expected_major" ] || fail "removing the old database left '$old_folders'"
    [ "$(database_major)" = "$expected_major" ] || fail "the database does not answer after removing the old one"
fi
migrations_current=$(migrations)
[ "$(ls -d "$work"/backups/commonage-* | wc -l)" -ge 2 ] || fail "no backup was made"

echo "Updating to the version it is already on changes nothing..."
sh scripts/update.sh 9.0.0 "$work/backups" | grep -q "Already on" || fail "it did not notice the version is current"

echo "Restoring the backup made by $previous into the new version..."
sh scripts/restore.sh --yes "$old_backup" >/dev/null
wait_for_page
[ "$(running_image)" = "$image:9.0.0" ] || fail "the new image does not run after the restore, but $(running_image)"
[ "$(migrations)" = "$migrations_current" ] || fail "the database from the backup was not upgraded ($(migrations) migrations, the new version has $migrations_current)"
curl -kfsS https://localhost/search | grep -q "Flood in Mill Street" || fail "the content of the old backup did not come back"

echo "The update works."
