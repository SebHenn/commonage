# Running an archive

This guide is for the people who run a Commonage archive with Docker Compose. The full deployment guide
(HTTPS, a server on the internet) follows with v0.1.

## Accounts

Admins manage accounts under "Manage archive" → "Accounts".

- **Curators** review contributions, describe, publish and delete items, and manage collections and contribution
  links.
- **Admins** can also manage accounts, export the archive and read the activity log.

Commonage sends no emails. A new account gets a **setup link** instead of a password; the admin passes it on (by
their own email, a message, or in person), and the person chooses their own password with it. A link works once
and for seven days. If someone forgets their password, an admin creates a new setup link; earlier links stop
working then.

Disabling an account ends its logins within a minute. Admins cannot change, disable or delete their own account, so
there is always at least one admin left. Everyone changes their own password under "Change password".

The first admin comes from `COMMONAGE_ADMIN_EMAIL` and `COMMONAGE_ADMIN_PASSWORD` in `.env`, only while the archive
has no accounts yet.

## Activity log

"Manage archive" → "Activity log" (admins) lists who did what and when: items created, changed, published,
unpublished and deleted, photos added and deleted, contributions received, accepted, rejected and withdrawn,
contributor details removed, collections and links changed, accounts managed, exports. Each item's edit page shows
its own history to all curators.

The log keeps the title of an item even after the item is deleted, and the account name even after the account is
deleted. It holds no contributor names or email addresses. The app never changes or deletes entries.

## Personal data

Contributors may ask what the archive holds about them, or ask for it to be changed or removed. "Manage archive" →
"Contributors" finds everything a person sent in, by part of their name or email address. On each item, curators can:

- **Remove name and email address.** The material stays, with its rights statement, photographer or author and
  consent text; the contributor is no longer credited.
- **Mark as withdrawn.** For a contributor who takes back their consent. The item is hidden at once and can never
  be published again; it would have to be sent in anew.
- **Delete the item**, with all its photos and original files.

Backups still contain removed data until they are replaced. Delete backups you no longer need.

## Export

"Manage archive" → "Export" (admins) downloads the whole archive as one ZIP file. It needs no Commonage to read, so
the material can always move to another system, or be handed to a public archive:

| Path | Contents |
|---|---|
| `archive.json` | All collections and items, complete: titles, descriptions, dates as written and as a range, tags, status, contribution details, and for each file its original name, type, size and SHA-256 checksum. Times are UTC. |
| `items.csv` | The items as a table for spreadsheet programs (UTF-8). Text that starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet never runs it as a formula. |
| `files/<item id>/<file id>-<name>` | The original files, exactly as uploaded. |
| `README.txt` | A short description of the above. |

`archive.json` has `"format": "commonage-export"` and `"version": 1`. While exporting, every file is checked against
its checksum. A file entry with `"problem": "missing"` was not found in storage, `"checksumMismatch"` means it has
changed since the upload; both need looking into.

Contributors' names and email addresses are only included when asked for (`"includesPersonalData": true`). Such a
file needs the same care as the archive itself. Every export is recorded in the activity log.

## Backup and restore

A backup is the database (everything except the files) plus the uploaded files. Run from the folder with
`compose.yaml`, on Linux, macOS or Windows (Git Bash):

```sh
scripts/backup.sh                    # writes backups/commonage-<date>-<time>/
scripts/backup.sh /mnt/usb-disk      # or into another folder
```

Each backup folder holds `database.dump` (`pg_dump`), `media.tar` (all originals and previews) and `SHA256SUMS`.
The app can keep running during a backup. The database is saved first, so a photo uploaded meanwhile may be in
`media.tar` without being in the database, which does no harm.

**Keep backups somewhere else.** A backup on the same disk is lost with the disk. Copy each one to another disk or
another place, keep several, and delete old ones you no longer need: they contain everything, including
contributors' personal data. A simple schedule is a daily backup (for example with cron) that keeps the last seven,
and a weekly copy to an external disk.

To restore, for example on a new server with a fresh checkout and your `.env`:

```sh
scripts/restore.sh backups/commonage-20261101-143532
```

It checks the checksums, asks you to type `restore`, stops the app, replaces the database and all files, and starts
the app again. Logins and setup links from before the backup keep working. Restore into the same or a newer version
of Commonage; the app upgrades the database when it starts.

`scripts/test-backup-restore.sh` checks the whole cycle: it creates an archive through the web interface, backs it
up, deletes all containers and volumes, restores and compares. It runs in CI on every pull request, as its own
Compose project on ports 18080 and 15432, so it can also run next to your own archive.

## Ports

The app listens on `127.0.0.1:8080` and PostgreSQL on `127.0.0.1:5432`, reachable only from the server itself. Set
`COMMONAGE_PORT` or `COMMONAGE_DB_PORT` in `.env` to use other ports.
