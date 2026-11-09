#!/bin/sh
# Tests updating a real archive: starts the previous published release in the production setup, tries an update that
# fails after upgrading the database, checks that everything went back, then updates to the current code.
#
#   scripts/test-update.sh
#
# Works on its own clone and as its own Compose project, so it never touches your archive, but it needs ports 80 and
# 443 free. CI runs it on every pull request.
set -eu

export MSYS_NO_PATHCONV=1
repository=$(cd "$(dirname "$0")/.." && pwd)
cd "$repository"
# On Windows (Git Bash), native programs such as git need C:/... rather than /c/... paths.
if command -v cygpath >/dev/null 2>&1; then repository=$(cygpath -m "$repository"); fi

head=$(git rev-parse HEAD)
# The newest earlier release with a published image. A release just tagged may still be waiting for its approval.
previous=
for tag in $(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --merged HEAD --sort=-v:refname); do
    [ "$(git rev-parse "$tag^{commit}")" != "$head" ] || continue
    if docker pull --quiet "ghcr.io/sebhenn/commonage:${tag#v}" >/dev/null 2>&1; then
        previous=$tag
        break
    fi
    echo "$tag has no published image yet, so trying the release before it."
done
[ -n "$previous" ] || { echo "FAILED: no earlier release with an image found. CI must fetch the tags." >&2; exit 1; }
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
[ ! -e .env.updating ] || fail "a temporary file stayed behind"

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
[ "$(ls -d "$work"/backups/commonage-* | wc -l)" -ge 2 ] || fail "no backup was made"

echo "Updating to the version it is already on changes nothing..."
sh scripts/update.sh 9.0.0 "$work/backups" | grep -q "Already on" || fail "it did not notice the version is current"

echo "The update works."
