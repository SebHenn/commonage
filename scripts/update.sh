#!/bin/sh
# Updates a production archive to a new release. Backs up first, and puts everything back if the new version does not
# come up.
#
#   scripts/update.sh                    the newest release
#   scripts/update.sh 0.3                the newest 0.3.x, and later bug-fix releases of 0.3
#   scripts/update.sh 0.3.1              exactly this release
#   scripts/update.sh 0.3 /srv/backups   with the backup in another folder (default: ./backups)
#
# See docs/deployment.md.
set -eu
# The copy of .env made while changing it holds the passwords, and so does the backup.
umask 077

export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/.."

die() {
    echo "Update stopped: $*" >&2
    exit 1
}

env_value() {
    sed -n "s/^$1=//p" .env | tail -n 1 | tr -d '\r"'
}

# Changes or adds one line of .env and leaves the rest alone, including its passwords.
set_version() {
    if grep -q '^COMMONAGE_VERSION=' .env; then
        sed "s/^COMMONAGE_VERSION=.*/COMMONAGE_VERSION=$1/" .env > .env.updating
    else
        cp .env .env.updating
        echo "COMMONAGE_VERSION=$1" >> .env.updating
    fi
    cat .env.updating > .env
    rm -f .env.updating
}

# Archives from before 0.4 have no login key. The new version encrypts its login keys with it from the first start.
add_login_key() {
    [ -z "$(env_value COMMONAGE_LOGIN_KEY)" ] || return 0
    key=$(head -c 32 /dev/urandom | base64 | tr -d '\n')
    grep -v '^COMMONAGE_LOGIN_KEY=' .env > .env.updating || true
    printf '\n# Encrypts the login keys in the database. Keep a copy of this file apart from the backups. See docs/operations.md.\nCOMMONAGE_LOGIN_KEY=%s\n' "$key" >> .env.updating
    cat .env.updating > .env
    rm -f .env.updating
    echo "Added COMMONAGE_LOGIN_KEY to .env. Everyone logs in again once. Keep a copy of .env somewhere safe, apart from the backups."
}

pull() {
    [ "${COMMONAGE_UPDATE_SKIP_PULL:-}" = 1 ] || docker compose pull --quiet web
}

# Asks from inside the Compose network, because in production the app has no open port. The app answers only after it
# has upgraded the database, so a healthy answer means the upgrade went through.
is_healthy() {
    docker compose exec -T caddy wget -q -O /dev/null --header "Host: $(env_value COMMONAGE_DOMAIN)" http://web:8080/health 2>/dev/null
}

wait_until_healthy() {
    waited=0
    while [ "$waited" -lt "${COMMONAGE_UPDATE_TIMEOUT:-300}" ]; do
        if is_healthy; then return 0; fi
        sleep 5
        waited=$((waited + 5))
    done
    return 1
}

# Stage 2 runs from the new release's copy of this script, because the checkout may have replaced the running one.
if [ "${1:-}" = --after-checkout ]; then
    new_ref=$2 setting=$3 previous_ref=$4 backups=$5

    go_back_without_restoring() {
        git checkout -q "$previous_ref"
        set_version "${previous_ref#v}"
    }

    echo "Backing up..."
    if ! sh scripts/backup.sh "$backups"; then
        go_back_without_restoring
        die "the backup failed. Nothing was changed."
    fi
    backup=$(ls -dt "$backups"/commonage-* | head -n 1)

    echo "Getting $new_ref..."
    set_version "$setting"
    if ! pull; then
        go_back_without_restoring
        die "could not download $new_ref. Nothing was changed."
    fi

    # After the backup, which keeps the archive as it was in case the update has to be undone.
    add_login_key

    echo "Starting $new_ref. The app upgrades the database when it starts, which can take a while."
    if docker compose up -d --remove-orphans web && wait_until_healthy; then
        docker compose up -d --remove-orphans >/dev/null
        echo "Updated to $new_ref. The backup from just before is in $backup"
        exit 0
    fi

    echo "$new_ref did not come up. Its log:" >&2
    docker compose logs --tail 40 web >&2 || true
    echo "Going back to $previous_ref and restoring the backup..." >&2
    # The exact version, because a version such as 0.3 may now point to the release that just failed.
    git checkout -q "$previous_ref"
    set_version "${previous_ref#v}"
    if pull && sh scripts/restore.sh --yes "$backup" && wait_until_healthy; then
        docker compose up -d --remove-orphans >/dev/null
        die "the archive is back on $previous_ref as it was before. Nothing was lost, unless something changed during the update attempt. Please report the problem."
    fi
    die "going back failed too. The archive may be down. Your data is safe in $backup: follow \"Restore\" in docs/operations.md, with COMMONAGE_VERSION=${previous_ref#v} in .env."
fi

[ -f .env ] || die "there is no .env here."
docker compose config --services 2>/dev/null | grep -qx caddy ||
    die "this script is for the production setup (compose.production.yaml). In development, use: docker compose up --build"
git diff --quiet HEAD || die "files of this checkout were changed. Undo that first (git status shows them), so an update cannot overwrite them."
previous_ref=$(git describe --tags --exact-match HEAD 2>/dev/null) ||
    die "this checkout is not on a release. Check out the release you are running, for example: git checkout v0.2.0"

target=${1:-}
backups=${2:-backups}
git fetch --tags --quiet || die "could not fetch the releases."

case "$target" in
    "")
        new_ref=$(git tag --list 'v[0-9]*.[0-9]*.[0-9]*' --sort=-v:refname | head -n 1)
        setting=$(echo "${new_ref#v}" | cut -d . -f 1,2)
        ;;
    [0-9]*.[0-9]*.[0-9]*)
        new_ref=v$target
        setting=$target
        ;;
    [0-9]*.[0-9]*)
        new_ref=$(git tag --list "v$target.[0-9]*" --sort=-v:refname | head -n 1)
        setting=$target
        ;;
    *)
        die "\"$target\" is not a version. Use one such as 0.3 or 0.3.1."
        ;;
esac
[ -n "$new_ref" ] && git rev-parse -q --verify "refs/tags/$new_ref" >/dev/null || die "there is no release ${target:-to update to}."

if [ "$new_ref" = "$previous_ref" ]; then
    echo "Already on $new_ref."
    exit 0
fi
# Going back needs a restore, which this script does not do by accident.
newest=$(printf '%s\n%s\n' "$previous_ref" "$new_ref" | sort -V | tail -n 1)
[ "$newest" = "$new_ref" ] || die "$new_ref is older than $previous_ref. To go back to an older version, restore a backup (docs/operations.md)."

echo "Updating from $previous_ref to $new_ref. Read its notes in CHANGELOG.md first."

# A backup that fills the disk would do more harm than the update could.
mkdir -p "$backups"
docker compose up -d --wait db >/dev/null 2>&1 || die "the database does not start."
database_bytes=$(docker compose exec -T db psql --username=commonage --dbname=commonage -Atc "select pg_database_size('commonage')" | tr -d '\r')
media_kilobytes=$(docker compose run --rm --no-deps -T --entrypoint du web -sk /app/App_Data 2>/dev/null | cut -f 1)
free_kilobytes=$(df -Pk "$backups" | awk 'NR == 2 { print $4 }')
# Room for the backup, plus a gigabyte for the new image and for the app to keep working.
needed_kilobytes=$(( database_bytes / 1024 + ${media_kilobytes:-0} + 1048576 ))
[ "$free_kilobytes" -ge "$needed_kilobytes" ] ||
    die "the backup needs about $(( needed_kilobytes / 1024 )) MB in $backups, and only $(( free_kilobytes / 1024 )) MB are free. Make room, or name another folder: scripts/update.sh ${target:-<version>} /other/folder"

git checkout -q "$new_ref"
exec sh scripts/update.sh --after-checkout "$new_ref" "$setting" "$previous_ref" "$backups"
