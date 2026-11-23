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

is_number() {
    case "$1" in
        "" | *[!0-9]*) return 1 ;;
    esac
}

# The old and the new major are only ever used in commands when both are plain numbers and the new one is higher.
valid_majors() {
    is_number "$1" && is_number "$2" && [ "$2" -gt "$1" ]
}

# Runs a script in the database volume, with the database stopped. $1 is the script, $2 the old major, $3 the new one;
# the script sees them as $OLD and $NEW. The image of the database refuses to start while the volume holds the data of
# another major version in /var/lib/postgresql/<major>/docker, so the old data is renamed to
# /var/lib/postgresql/<major>/kept: nothing is deleted, the new version no longer sees it, and the previous release
# gets it back by renaming it again.
in_database_volume() {
    valid_majors "$2" "$3" || return 1
    docker compose stop db >/dev/null 2>&1 || true
    docker compose run --rm --no-deps -T -e OLD="$2" -e NEW="$3" --entrypoint sh db -c "$1" >/dev/null
}

# Renames the old data. Refuses when a "kept" is there already: it is the data of an earlier, unfinished move.
set_old_database_aside() {
    in_database_volume '
        old=/var/lib/postgresql/$OLD
        if [ -e "$old/kept" ]; then echo "$old/kept exists already, from an earlier update. Nothing was moved." >&2; exit 2; fi
        [ -s "$old/docker/PG_VERSION" ] || { echo "There is no database in $old/docker. Nothing was moved." >&2; exit 3; }
        mv "$old/docker" "$old/kept"
    ' "$1" "$2"
}

# Undoes the above after a failed or interrupted update, and can be run again. The database of the new major in the
# volume is only a copy of the backup. An empty or new "docker" folder of the old major is moved aside, never over
# "kept", and a "docker" folder that holds a database is never replaced.
bring_old_database_back() {
    in_database_volume '
        old=/var/lib/postgresql/$OLD
        rm -rf "/var/lib/postgresql/$NEW/docker"
        if [ -d "$old/kept" ]; then
            if [ -e "$old/docker" ]; then
                [ ! -s "$old/docker/PG_VERSION" ] || { echo "$old/docker holds a database and $old/kept does too. Nothing was changed." >&2; exit 4; }
                mv "$old/docker" "$old/docker.unused.$(date +%s)"
            fi
            mv "$old/kept" "$old/docker"
        elif [ ! -s "$old/docker/PG_VERSION" ]; then
            echo "The old database is neither in $old/kept nor in $old/docker." >&2
            exit 5
        fi
    ' "$1" "$2"
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

# While the old database is set aside, this file says so. An update that stopped halfway (the connection dropped, the
# power went) would otherwise look like a finished one, and the app could start on an empty database. Nothing else
# runs while it exists.
marker=.update-in-progress

write_marker() {
    printf 'backup=%s\nold_major=%s\nnew_major=%s\nprevious_ref=%s\nnew_ref=%s\n' "$1" "$2" "$3" "$4" "$5" > "$marker.tmp"
    mv "$marker.tmp" "$marker"
}

marker_value() {
    sed -n "s/^$1=//p" "$marker" | head -n 1 | tr -d '\r'
}

print_recovery() {
    m_backup=$(marker_value backup)
    m_old=$(marker_value old_major)
    m_new=$(marker_value new_major)
    m_previous=$(marker_value previous_ref)
    cat >&2 <<EOF
An update to PostgreSQL $m_new did not finish, and the archive may not be usable. Nothing is lost:
  - the old database (PostgreSQL $m_old) is in the database volume, as /var/lib/postgresql/$m_old/kept
    (or /var/lib/postgresql/$m_old/docker, if it was put back already),
  - the backup is in ${m_backup:-(none was made yet)}.
Do not start the app by hand and do not run the update again before this is cleared up.

To go back to $m_previous with the old database as it was before the update, run:
    sh scripts/update.sh --recover

By hand, this is what it does:
    git checkout $m_previous        (and set COMMONAGE_VERSION=${m_previous#v} in .env)
    docker compose stop web db
    docker compose run --rm --no-deps --entrypoint sh db -c 'rm -rf /var/lib/postgresql/$m_new/docker; mv /var/lib/postgresql/$m_old/kept /var/lib/postgresql/$m_old/docker'
    docker compose up -d
    rm $marker
If the "mv" says kept is missing, the old database is in /var/lib/postgresql/$m_old/docker already: leave it there.
EOF
}

# scripts/update.sh --recover: puts the archive back on the release it was on, with its own database.
if [ "${1:-}" = --recover ]; then
    [ -f "$marker" ] || die "there is nothing to recover: no update was left unfinished."
    r_old=$(marker_value old_major)
    r_new=$(marker_value new_major)
    r_previous=$(marker_value previous_ref)
    valid_majors "$r_old" "$r_new" || die "$marker is damaged. Follow the steps that the update printed, or ask for help."
    case "$r_previous" in
        v[0-9]*.[0-9]*.[0-9]*) ;;
        *) die "$marker is damaged. Follow the steps that the update printed, or ask for help." ;;
    esac
    echo "Putting the archive back on $r_previous with its old database..."
    docker compose stop web >/dev/null 2>&1 || true
    git checkout -q "$r_previous"
    set_version "${r_previous#v}"
    bring_old_database_back "$r_old" "$r_new" || die "the old database could not be put back. $marker stays; the old database is untouched."
    docker compose up -d --remove-orphans >/dev/null
    wait_until_healthy || die "the app does not answer. $marker stays. Look at: docker compose logs web"
    rm -f "$marker"
    echo "The archive runs on $r_previous again, on its old database."
    exit 0
fi

if [ "${1:-}" != --after-checkout ] && [ -f "$marker" ]; then
    print_recovery
    die "an earlier update did not finish."
fi

# Stage 2 runs from the new release's copy of this script, because the checkout may have replaced the running one.
if [ "${1:-}" = --after-checkout ]; then
    new_ref=$2 setting=$3 previous_ref=$4 backups=$5
    # A dropped connection must not stop the update halfway; other signals and failures go back (see below).
    trap '' HUP

    backup=
    old_major=
    new_major=
    major_change=0
    # While 1, an unexpected end of the script puts the archive back on the previous release.
    guard=0

    go_back_without_restoring() {
        git checkout -q "$previous_ref"
        set_version "${previous_ref#v}"
    }

    # Nothing was changed on disk yet, besides the version, and the app only has to be started again.
    go_back_without_restoring_and_restart() {
        go_back_without_restoring
        docker compose up -d web >/dev/null 2>&1 || true
        rm -f "$marker"
    }

    stop_update() {
        guard=0
        die "$@"
    }

    on_unexpected_end() {
        code=$?
        trap - EXIT INT TERM
        if [ "$guard" = 1 ] && [ "$code" -ne 0 ]; then
            guard=0
            echo "The update stopped unexpectedly. Going back to $previous_ref..." >&2
            git checkout -q "$previous_ref" 2>/dev/null || true
            set_version "${previous_ref#v}" 2>/dev/null || true
            if [ "$major_change" = 1 ]; then
                if bring_old_database_back "$old_major" "$new_major" && docker compose up -d --remove-orphans >/dev/null 2>&1; then
                    rm -f "$marker"
                    echo "The archive is back on $previous_ref with its old database." >&2
                else
                    print_recovery
                fi
            else
                docker compose up -d --remove-orphans >/dev/null 2>&1 || true
            fi
        fi
        exit "$code"
    }
    trap on_unexpected_end EXIT
    trap 'exit 130' INT TERM

    # A new PostgreSQL major version cannot open the data of the old one. The database that is running still belongs to
    # the previous release, which is what the backup has to read, so this is asked before anything is changed.
    old_major=$(running_database_major)
    new_major=$(configured_database_major)
    if ! is_number "$old_major" || ! is_number "$new_major"; then
        go_back_without_restoring
        stop_update "could not tell which PostgreSQL version the database runs ('$old_major') and which $new_ref needs ('$new_major'). Nothing was changed."
    fi
    if [ "$new_major" -lt "$old_major" ]; then
        go_back_without_restoring
        stop_update "$new_ref needs PostgreSQL $new_major, older than the $old_major that the archive runs. To go back to an older version, restore a backup (docs/operations.md). Nothing was changed."
    fi
    [ "$old_major" = "$new_major" ] || major_change=1

    if [ "$major_change" = 1 ]; then
        # The login key goes into .env while the app still runs, so that no step later in the move can fail on it.
        add_login_key || { go_back_without_restoring; stop_update "could not write .env. Nothing was changed."; }
        # The app stops before the backup, so that nothing is added between the backup and the move to the new database.
        echo "$new_ref uses PostgreSQL $new_major, the archive is on PostgreSQL $old_major. The archive is moved with the backup."
        write_marker "" "$old_major" "$new_major" "$previous_ref" "$new_ref"
        guard=1
        docker compose stop web >/dev/null 2>&1 || true
    else
        guard=1
    fi

    echo "Backing up..."
    if ! sh scripts/backup.sh "$backups"; then
        guard=0
        go_back_without_restoring_and_restart
        stop_update "the backup failed. Nothing was changed."
    fi
    backup=$(ls -dt "$backups"/commonage-* | head -n 1)
    [ "$major_change" != 1 ] || write_marker "$backup" "$old_major" "$new_major" "$previous_ref" "$new_ref"

    echo "Getting $new_ref..."
    set_version "$setting"
    if [ "$major_change" = 1 ]; then
        # Nothing is replaced unless the backup is whole and the new database image is here.
        backup_is_intact "$backup" || { go_back_without_restoring_and_restart; stop_update "the backup did not pass its checksums. Nothing was changed."; }
        pull db || { go_back_without_restoring_and_restart; stop_update "could not download $new_ref. Nothing was changed."; }
    elif ! pull; then
        go_back_without_restoring
        stop_update "could not download $new_ref. Nothing was changed."
    fi

    # After the backup, which keeps the archive as it was in case the update has to be undone.
    add_login_key

    # From here on, a same-major update has started the new app, and only the backup can undo that.
    [ "$major_change" = 1 ] || guard=0
    echo "Starting $new_ref. The app upgrades the database when it starts, which can take a while."
    # With a new PostgreSQL major, the old data is set aside in the volume (not deleted), the new database starts empty
    # and the backup's database is restored into it; the uploaded files stay as they are. The app starts at the end.
    moved=1
    if [ "$major_change" = 1 ]; then
        echo "Moving the archive to PostgreSQL $new_major..."
        if set_old_database_aside "$old_major" "$new_major"; then
            # Only scripts/test-update.sh sets this: it stops the update right here, as a power cut would.
            [ "${COMMONAGE_UPDATE_TEST_CRASH:-}" != after-rename ] || kill -9 $$
            sh scripts/restore.sh --yes --database-only "$backup" || moved=0
        else
            moved=0
        fi
    fi
    if [ "$moved" = 1 ] && docker compose up -d --remove-orphans web && wait_until_healthy; then
        docker compose up -d --remove-orphans >/dev/null
        guard=0
        rm -f "$marker"
        echo "Updated to $new_ref. The backup from just before is in $backup"
        if [ "$major_change" = 1 ]; then
            echo "The archive now runs on PostgreSQL $new_major. The old database is still in the volume, set aside, so that going back stays possible."
            echo "It holds the archive's data, including personal data, until it is removed. Once the archive has run well for a while, free its disk space with:"
            echo "  docker compose run --rm --no-deps --entrypoint rm db -rf /var/lib/postgresql/$old_major"
        fi
        exit 0
    fi

    guard=0
    echo "$new_ref did not come up. Its log:" >&2
    docker compose logs --tail 40 web >&2 || true
    echo "Going back to $previous_ref..." >&2
    # The exact version, because a version such as 0.3 may now point to the release that just failed.
    git checkout -q "$previous_ref"
    set_version "${previous_ref#v}"
    if [ "$major_change" = 1 ]; then
        docker compose stop web >/dev/null 2>&1 || true
        if bring_old_database_back "$old_major" "$new_major"; then
            # The old database is back as it was, and nothing wrote to it: the backup is not needed.
            if pull && docker compose up -d --remove-orphans web && wait_until_healthy; then
                docker compose up -d --remove-orphans >/dev/null
                rm -f "$marker"
                die "the archive is back on $previous_ref, on its old database, as it was before. Nothing was lost, unless something changed during the update attempt. Please report the problem."
            fi
        fi
    else
        echo "Restoring the backup..." >&2
        if pull && sh scripts/restore.sh --yes "$backup" && wait_until_healthy; then
            docker compose up -d --remove-orphans >/dev/null
            die "the archive is back on $previous_ref as it was before. Nothing was lost, unless something changed during the update attempt. Please report the problem."
        fi
    fi
    if [ "$major_change" = 1 ]; then
        print_recovery
        die "going back failed too. The archive may be down. The old database is untouched in /var/lib/postgresql/$old_major/kept or /var/lib/postgresql/$old_major/docker of the database volume, and $marker stays until it is cleared up: follow the steps above. The backup is $backup."
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

# A new PostgreSQL major version needs room for a second copy of the database in the database volume (the old one is kept).
running_major=$(running_database_major)
next_major=$(git show "$new_ref:compose.yaml" 2>/dev/null | sed -n 's/^ *image: *postgres:\([0-9][0-9]*\).*/\1/p' | head -n 1)
if is_number "$running_major" && is_number "$next_major" && [ "$next_major" != "$running_major" ]; then
    [ "$next_major" -gt "$running_major" ] ||
        die "$new_ref needs PostgreSQL $next_major, older than the $running_major that the archive runs. To go back to an older version, restore a backup (docs/operations.md)."
    volume_free_kilobytes=$(docker compose run --rm --no-deps -T --entrypoint df db -Pk /var/lib/postgresql 2>/dev/null | awk 'NR == 2 { print $4 }' | tr -d '\r')
    # The restored database, with its indexes, plus a little for the new server to work in.
    volume_needed_kilobytes=$(( database_bytes / 1024 + database_bytes / 2048 + 262144 ))
    if is_number "$volume_free_kilobytes"; then
        [ "$volume_free_kilobytes" -ge "$volume_needed_kilobytes" ] ||
            die "$new_ref moves the archive to PostgreSQL $next_major, which keeps the old database and needs about $(( volume_needed_kilobytes / 1024 )) MB more in the database volume, and only $(( volume_free_kilobytes / 1024 )) MB are free. Make room first (docs/operations.md, \"A new PostgreSQL version\")."
    else
        echo "Warning: could not see how much room the database volume has. $new_ref moves the archive to PostgreSQL $next_major, which needs about $(( volume_needed_kilobytes / 1024 )) MB more there." >&2
    fi
fi

git checkout -q "$new_ref"
exec sh scripts/update.sh --after-checkout "$new_ref" "$setting" "$previous_ref" "$backups"
