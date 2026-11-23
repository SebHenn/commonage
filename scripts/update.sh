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
    [ "${COMMONAGE_UPDATE_SKIP_PULL:-}" = 1 ] || docker compose pull --quiet web "$@"
}

# The PostgreSQL major version of the database that is running now (18, 19, ...).
running_database_major() {
    version=$(docker compose exec -T db psql --username=commonage --dbname=commonage -Atc 'show server_version_num' 2>/dev/null | tr -d '\r') || return 0
    case "$version" in
        [0-9][0-9][0-9][0-9][0-9]*) echo $(( version / 10000 )) ;;
    esac
}

# The PostgreSQL major version that the checked-out compose files start (18 for "postgres:18-alpine@sha256:...").
configured_database_major() {
    docker compose config 2>/dev/null | sed -n 's/^ *image: *postgres:\([0-9][0-9]*\).*/\1/p' | head -n 1
}

# Runs a command in the database volume, with the database stopped. The image of the database refuses to start while
# the volume holds the data of another major version in /var/lib/postgresql/<major>/docker, so the old data is renamed
# to /var/lib/postgresql/<major>/kept: nothing is deleted, the new version no longer sees it, and the previous release
# gets it back by renaming it again.
in_database_volume() {
    docker compose stop db >/dev/null 2>&1 || true
    docker compose run --rm --no-deps -T --entrypoint sh db -c "$1" >/dev/null
}

# The majors are numbers (read from PostgreSQL and the compose file), so they are safe inside the command.
set_old_database_aside() {
    in_database_volume "[ -d /var/lib/postgresql/$1/docker ] && mv /var/lib/postgresql/$1/docker /var/lib/postgresql/$1/kept"
}

# Undoes the above after a failed update. The database of the new major in the volume is only a copy of the backup.
bring_old_database_back() {
    in_database_volume "rm -rf /var/lib/postgresql/$2/docker; if [ -d /var/lib/postgresql/$1/kept ] && [ ! -e /var/lib/postgresql/$1/docker ]; then mv /var/lib/postgresql/$1/kept /var/lib/postgresql/$1/docker; fi"
}

# Checks the backup again right before the database is replaced.
backup_is_intact() {
    [ -s "$1/database.dump" ] && [ -f "$1/media.tar" ] || return 1
    if command -v sha256sum >/dev/null 2>&1; then
        (cd "$1" && sha256sum -c --quiet SHA256SUMS)
    else
        (cd "$1" && shasum -a 256 -c --quiet SHA256SUMS)
    fi
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

    # A new PostgreSQL major version cannot open the data of the old one. The database that is running still belongs to
    # the previous release, which is what the backup has to read, so this is asked before anything is changed.
    old_major=$(running_database_major)
    new_major=$(configured_database_major)
    major_change=0
    if [ -z "$old_major" ] || [ -z "$new_major" ]; then
        go_back_without_restoring
        die "could not tell which PostgreSQL version the database runs ('$old_major') and which $new_ref needs ('$new_major'). Nothing was changed."
    fi
    [ "$old_major" = "$new_major" ] || major_change=1

    # The app stops before the backup, so that nothing is added between the backup and the move to the new database.
    go_back_without_restoring_and_restart() {
        go_back_without_restoring
        docker compose up -d web >/dev/null 2>&1 || true
    }
    if [ "$major_change" = 1 ]; then
        echo "$new_ref uses PostgreSQL $new_major, the archive is on PostgreSQL $old_major. The archive is moved with the backup."
        docker compose stop web >/dev/null 2>&1 || true
    fi

    echo "Backing up..."
    if ! sh scripts/backup.sh "$backups"; then
        go_back_without_restoring_and_restart
        die "the backup failed. Nothing was changed."
    fi
    backup=$(ls -dt "$backups"/commonage-* | head -n 1)

    echo "Getting $new_ref..."
    set_version "$setting"
    if [ "$major_change" = 1 ]; then
        # Nothing is replaced unless the backup is whole and the new database image is here.
        backup_is_intact "$backup" || { go_back_without_restoring_and_restart; die "the backup did not pass its checksums. Nothing was changed."; }
        pull db || { go_back_without_restoring_and_restart; die "could not download $new_ref. Nothing was changed."; }
    elif ! pull; then
        go_back_without_restoring
        die "could not download $new_ref. Nothing was changed."
    fi

    # After the backup, which keeps the archive as it was in case the update has to be undone.
    add_login_key

    echo "Starting $new_ref. The app upgrades the database when it starts, which can take a while."
    # With a new PostgreSQL major, the old data is set aside in the volume (not deleted), the new database starts empty
    # and the backup is restored into it. The app starts at the end of the restore.
    if [ "$major_change" = 1 ]; then
        echo "Moving the archive to PostgreSQL $new_major..."
    fi
    if { [ "$major_change" != 1 ] || { set_old_database_aside "$old_major" && sh scripts/restore.sh --yes "$backup"; }; } &&
        docker compose up -d --remove-orphans web && wait_until_healthy; then
        docker compose up -d --remove-orphans >/dev/null
        echo "Updated to $new_ref. The backup from just before is in $backup"
        if [ "$major_change" = 1 ]; then
            echo "The archive now runs on PostgreSQL $new_major. The old database is still in the volume, set aside, so that going back stays possible."
            echo "Once the archive has run well for a while, free its disk space with:"
            echo "  docker compose run --rm --no-deps --entrypoint rm db -rf /var/lib/postgresql/$old_major"
        fi
        exit 0
    fi

    echo "$new_ref did not come up. Its log:" >&2
    docker compose logs --tail 40 web >&2 || true
    echo "Going back to $previous_ref and restoring the backup..." >&2
    # The exact version, because a version such as 0.3 may now point to the release that just failed.
    git checkout -q "$previous_ref"
    set_version "${previous_ref#v}"
    if [ "$major_change" = 1 ]; then
        bring_old_database_back "$old_major" "$new_major" || echo "Could not put the old database back in the volume." >&2
    fi
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
