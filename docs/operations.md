# Running an archive

This guide is for the people who run a Commonage archive with Docker Compose. The full deployment guide
(HTTPS, a server on the internet) follows with v0.1.

## Accounts

Admins manage accounts under "Manage archive" → "Accounts".

- **Curators** review contributions, describe, publish and delete items, empty the bin, and manage collections and
  contribution links.
- **Admins** can also manage accounts, export the archive, read the activity log and open the System page.

Commonage sends no emails. A new account gets a **setup link** instead of a password; the admin passes it on (by
their own email, a message, or in person), and the person chooses their own password with it. A link works once
and for seven days. If someone forgets their password, an admin creates a new setup link; earlier links stop
working then.

Disabling an account ends its logins within a minute. Admins cannot change, disable or delete their own account, so
there is always at least one admin left. Everyone changes their own password under "Change password".

The first admin comes from `COMMONAGE_ADMIN_EMAIL` and `COMMONAGE_ADMIN_PASSWORD` in `.env`, only while the archive
has no accounts yet.

### Authenticator apps

Under "Change password" → "Log in with an authenticator app", each person can add a code from an app on their phone to
their login, such as Aegis, 2FAS, Google Authenticator or Microsoft Authenticator. They scan a QR code, type one code to show it
works, and get ten recovery codes, each of which logs them in once without the phone. With the app, a password alone is no
longer enough to log in. "Settings" → "Logging in" can require it of everyone who manages the archive; each of them then sets
it up the next time they open those pages, and nothing else opens for them until they have, original files included.
"Accounts" shows who uses one.

Someone who loses their phone and their recovery codes asks an admin, who opens their account and chooses "Turn off the
authenticator app". Commonage sends no emails, so this is the way back in: make sure it is really them asking. They then log
in with their password and set up the app again. An admin cannot do this for their own account, so for the only admin, or for
everyone after the login key was lost, it is done on the server:

```sh
docker compose run --rm web reset-two-factor admin@example.org
```

Every change to an authenticator app, and every login with a recovery code, is in the activity log.

### Login keys

Logins, setup links and form tokens are signed with keys that are kept in the database, so that they survive a new container
and a restore. The authenticator apps' secrets and the recovery codes are encrypted with them too. These keys are encrypted
with `COMMONAGE_LOGIN_KEY` from `.env`, which is not in the database or in the backups, so a stolen backup alone cannot be used
to log in. Without that setting, admins see a notice. `scripts/update.sh` adds one to `.env` when it updates an archive that
has none.

The first start with a key removes the keys that were stored without one. **Everyone logs in again once**, and setup links
created before stop working.

**Keep a copy of `.env` apart from the backups**, for example in a password manager. If the key is lost, or a restore runs
with another one, nothing in the archive is lost, but everyone is logged out, setup links stop working, and nobody who uses an
authenticator app can log in until their app is turned off: by an admin who does not use one, or with `reset-two-factor` on
the server for each of them. Then they set it up again.

## Activity log

"Manage archive" → "Activity log" (admins) lists who did what and when: items created, changed, published,
unpublished, moved to the bin, restored and deleted, photos added and deleted, contributions received, accepted, rejected and withdrawn,
contributor details removed, collections and links changed, accounts managed, exports. Each item's edit page shows
its own history to all curators. It can be narrowed down by who did something (an account, a contributor, or "Automatic"),
by what it was about (items, photos, contributions and links, collections, accounts and logins, or export and settings),
and by a title or name.

The log keeps the title of an item even after the item is deleted, and the account name even after the account is
deleted. It holds no contributor names or email addresses. The app never changes entries, and only deletes them once they
are older than the archive keeps them.

## How long records are kept

"Settings" → "How long records are kept" (admins). Both start as "Always", so an update deletes nothing.

- **The activity log**: always, or for 1, 2, 5 or 10 years. It names the accounts that changed something, which is personal
  data too. Older entries are deleted a few times a day, and the log notes each time with an "Automatic" entry, which is
  then the newest one. The shortest time is a year, so changing the setting cannot hide what happened recently.
- **Rejected and withdrawn contributions**: until a curator deletes them, or for 3, 6, 12 or 24 months after the
  decision. They hold the contributor's name and email address, and material that will not be published. When their time
  is up, they go to the bin, where they can still be restored for 30 days, and the log records it as "Automatic". One that
  is restored, or rejected again, starts its time anew. For contributions rejected or withdrawn before this setting
  existed, the time starts with the update that added it, because when they were rejected is not recorded reliably.

Backups keep older copies until they are replaced, and the privacy text says so.

## Settings

"Manage archive" → "Settings" (admins) makes the archive your group's own, without touching any files on the server: its
name, logo and colour, the language it falls back to, the time zone for curators' pages, and whether search engines may
list it. A colour too light for white text is refused, with a similar one that works.

The pages About, Contact, Privacy and Imprint are written there too, as plain text. An empty page is not shown, except
Privacy, which comes with a description of what Commonage stores. **That text is a template and not legal advice.** Adapt it
to what your group does, and find out whether your country requires an imprint.

The list on "Manage archive" shows what is left to set up, and stays until everything is done.

**Language of the material** is the language most items are described in, which can be any language, not only one of the
interface. Item pages, lists and the feed mark what curators and contributors wrote with it, so that screen readers read it aloud
in the right language, also to a visitor who reads the interface in another one. "Several languages, none of them most" marks
nothing, because a wrong language is worse than none.

Dates typed into a form are read in that language, in those ticked under "Dates can also be written in", and in the language of
whoever types them. Month names work in any language that writes them as words, also without accents, such as "Marz 1953" or "12 pazdziernika 1953".
Words such as "circa" or "before" are known in English and German so far. Until an admin saves this, dates are read in English
and German, as before, and saving the settings without a change keeps that. A stored date is only the days it stands for, so
changing the languages never changes an item.

Curators have their own guide: [curator-handbook.md](curator-handbook.md) ([deutsch](handbuch-kuratoren.md)).

## Rights and catalogue details

On each item, curators can say what others may do with it, as a standard statement with a plain label. "Settings" has a
default for items without one of their own. Each item page tells visitors what applies, with the credit if there is one. Without a
statement or a default, it tells them to ask. "Who holds the rights?" stays with curators.

A contributor's answer only suggests a statement: a free licence only if they offered one themselves, and never if they do not know
who made it. Their offer is stored in the words they saw, as part of the consent. A curator decides, because a free licence cannot be
taken back from those who have already reused something.

## Beyond this website

If search engines may list the archive, it also has a feed of the latest 30 items at `/feed.xml`, a list of its public pages at
`/sitemap.xml`, named in `robots.txt`, and what a link shows when it is pasted into a messenger. Switching that setting off ends all
of them at once, though what was already copied elsewhere stays there.

Contributions sent in before this was added agreed to "this website" only, and stay under that: `consentCoversSharing` is false, so their
picture and text stay out of previews, and the whole item stays out of the feed. A link to one still shows its title. There is no
button to change this, because it would take the contributor's agreement, which the archive has no record of.

"Catalogue details" (reference number, where the original is kept, received from and when, notes) are for curators only and
stay out of public pages and public search. A reference number typed into the search on "Manage archive" finds its item.

## Picture sizes

Each photo has a preview and smaller sizes for lists, made when it is uploaded. When a new version of Commonage adds a size,
the archive makes it for older photos in the background, whenever no new upload is waiting. Nothing goes offline meanwhile,
and it continues after a restart. On a small server with many photos this can take hours, and the larger size adds about
0.1 to 0.3 MB per photo to the disk.

## The bin

Deleting an item or a photo moves it to the bin ("Manage archive" → "Bin"), so a mistake can be undone. It disappears
from the archive at once, public pages and exports included, but curators can restore it for 30 days. After that the
archive deletes it for good, with its original files; the activity log records this as "Automatic". An item comes
back as it was, so a published item is public again.

"Delete for good" in the bin does not wait for the 30 days. Use it for spam that takes up space, and when someone asks
for their material to be removed.

## The System page

"Manage archive" → "System" (admins) shows in one place what whoever runs the server should keep an eye on:

- **The checks behind `/health`**: the database, the media folder, free disk space and the last backup, each "All right",
  "Needs attention" or "Not working".
- **Disk**: free space, how much the media folder and the database take up, what was added in the last 30 days, and how
  long the free space lasts at the pace of the last 90 days. The media folder is measured at most every 15 minutes, because
  reading every file of a large archive takes a moment.
- **Backups and keys**: the last backup, the last restore test, and whether the login keys are encrypted.
- **Previews**: how many photos wait for their previews, and those for which none could be made, with "Try again" for one or
  all of them. A file the archive cannot read will fail again; its original stays safe and curators can download it from the item.
- **Software**: the version, PostgreSQL's version, the last change to the database, and whether search ignores accents with
  the PostgreSQL extension `unaccent` or with the built-in folding.

## Health check for uptime monitors

`/health` answers with one word, and nothing else, so visitors learn no details:

| Answer | HTTP status | When |
|---|---|---|
| `Healthy` | 200 | Everything is all right. |
| `Degraded` | 200 | The disk is filling up or uploads have stopped, or the last backup is older than `Backups__WarnAfterDays` (3 days), or there has been none. The archive itself still works. |
| `Unhealthy` | 503 | The database cannot be reached, the media folder cannot be written to, or the newest original is missing from it. |

**`Degraded` still answers with 200**, so `scripts/update.sh` does not roll back an update because of an old backup, and an
uptime monitor that only looks at the status code does not notice it. To be told about it too, set the monitor to look for the
keyword `Healthy` in the answer (most monitors, such as Uptime Kuma, call this a keyword or content check).

"The newest original is missing" usually means that the media volume is not mounted, so the app writes into an empty folder
in the container, or that a database was restored without its files. Check `docker compose config` and the restore. If only
that one file is lost, its item shows it; delete the photo there, or upload it again, and the check looks at the next one.

## Disk space and limits

A full disk also stops the database, so the archive stops taking in files before that happens:

- Below 1 GB free (`Uploads__MinimumFreeMegabytes`), uploads and contributions are refused with a friendly message.
  Curators can still edit text.
- Below 5 GB free (`Uploads__WarnFreeMegabytes`), admins see a notice on "Manage archive".
- One contribution link takes in at most 5 GB of files a day (`Contributions__MaxUploadMegabytesPerLinkPerDay`). After
  that, people can still send text. Raise it for a big event.
- One address can try to log in 100 times an hour (`Login__MaxAttemptsPerHour`), on top of the lockout of a single
  account.

These go in the `environment` of the `web` service if you need other values. What is in the bin counts towards the disk
until it is deleted.

## Personal data

Contributors may ask what the archive holds about them, or ask for it to be changed or removed. "Manage archive" →
"Contributors" finds everything a person sent in or is named as the photographer or author of, by part of their name
or email address, including items in the bin. On each item, curators can:

- **Remove name and email address.** The material stays, with its rights statement, photographer or author and
  consent text; the contributor is no longer credited.
- **Mark as withdrawn.** For a contributor who takes back their consent. The item is hidden at once and can never
  be published again; it would have to be sent in anew.
- **Move the item to the bin, then "Delete for good"** there, with all its photos and original files. Until that second
  step, the data is still in the archive.

Backups still contain removed data until they are replaced. Delete backups you no longer need.

## Export

"Manage archive" → "Export" (admins) downloads the whole archive as one ZIP file. It needs no Commonage to read, so
the material can always move to another system, or be handed to a public archive:

| Path | Contents |
|---|---|
| `archive.json` | All collections and items, complete: titles, descriptions, dates as written and as a range, tags, status, contribution details, and for each file its original name, type, size and SHA-256 checksum. Times are UTC. |
| `items.csv` | The items as a table for spreadsheet programs (UTF-8). Text that starts with `=`, `+`, `-` or `@` gets a leading apostrophe, so a spreadsheet never runs it as a formula. |
| `files/<item id>/<file id>-<name>` | The original files, exactly as uploaded. |
| `README.txt` | A short description of the above, in the language of the admin who exported. |
| `README.en.txt` | The same in English, if the other one is not. |

An item's title can be empty in a full export: a draft from "Add many photos" that nobody has named yet. Each file says its
`position` among the item's photos and its `rotation`, how far curators turned it in degrees clockwise. The file itself is the
original, which is never turned, so whoever uses the export has to apply that. Files are listed in order. `needsTranscription` is
in a full export only. A contribution says what the contributor offered (`reuse`) and whether they agreed to more than this website
(`consentCoversSharing`). `settings` has `contentLanguage`, the language of the material as a code such as `de` (`mul` for several,
none of them most, and null if never chosen), and `otherContentLanguages`, separated by commas. `activityLogKeptYears` and
`rejectedKeptMonths` are how long records are kept (null for always), and each item's `rejectedOrWithdrawnAt` is when that time
started for it.

`archive.json` has `"format": "commonage-export"` and `"version": 4`. Version 4 added `tags`, each with its `name` and `kind`
(`Topic`, `Place` or `Person`); items still list their tags by name. Each collection also has its `position` among the others,
the `sort` of its items (`RecentlyAdded`, `OldestFirst` or `NewestFirst`) and `coverFileId`, the `id` of the file chosen as its
cover, or null when none is chosen or that file is not in the export. Version 3 added each item's rights, credit and catalogue
details and each photo's description; in `items.csv`, `rights` and `rights_holder` are now the item's own, and the contributor's
answers moved to `contributor_rights` and `contributor_rights_holder`. An export of published items leaves out what only
curators see. Without personal data, it also leaves out rights holders, credits, "received from" and internal notes. Version 2 added `settings` with the archive's name, pages and colours, and the logo under `branding/`. While exporting, every file is checked against
its checksum. A file entry with `"problem": "missing"` was not found in storage, `"checksumMismatch"` means it has
changed since the upload; both need looking into.

Contributors' names and email addresses are only included when asked for (`"includesPersonalData": true`). Such a
file needs the same care as the archive itself. Every export is recorded in the activity log.

Items and photos in the bin are not part of an export.

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

A finished backup also tells the app when it ran. Admins see the date on "Manage archive", and a notice if the last
backup is more than three days old (`Backups__WarnAfterDays`). With none recorded, the "Getting started" list says so. Commonage sends no emails, so this
notice is how a failing nightly backup gets noticed.

To restore, for example on a new server with a fresh checkout and your `.env`:

```sh
scripts/restore.sh backups/commonage-20261101-143532
```

It checks the checksums, asks you to type `restore`, stops the app, replaces the database and all files, and starts
the app again. Logins and setup links from before the backup keep working, as long as `.env` has the same `COMMONAGE_LOGIN_KEY`
(see [Login keys](#login-keys)): a backup does not contain it, on purpose. Restore into the same or a newer version
of Commonage; the app upgrades the database when it starts. This is also how to move an archive to a new server: back
up, copy the folder, restore. Addresses of items, accounts and logins all stay the same. After a restore the backup date
stays as it was on that server, or none is recorded on a new one, until the next backup runs.

`scripts/test-backup-restore.sh` checks the whole cycle: it creates an archive through the web interface, backs it
up, deletes all containers and volumes, restores and compares, and checks that no login key in the database can be read
without `COMMONAGE_LOGIN_KEY`. It runs in CI on every pull request, as its own
Compose project on ports 18080 and 15432, so it can also run next to your own archive.

## Demo archive

`COMMONAGE_DEMO=en` or `COMMONAGE_DEMO=de` in `.env` fills a new, empty archive with made-up content when the app
starts: three collections in the invented town of Lindenau, eight published items with drawn pictures, a draft, an
open contribution link and a contribution waiting for review. Every page then says that it is a demo archive.
Archives that already have a collection are never touched. It is meant for trying Commonage out, showing it to a
group and the [five-task test](usability-test.md); for a real archive, start without it.

## Source code link

Every page links to the source code, as the AGPL requires. It points to the main Commonage repository. If you change
the code, publish your version (for example as a fork) and set `COMMONAGE_SOURCE_URL` in `.env` to its address.

## Ports

The app listens on `127.0.0.1:8080` and PostgreSQL on `127.0.0.1:5432`, reachable only from the server itself. Set
`COMMONAGE_PORT` or `COMMONAGE_DB_PORT` in `.env` to use other ports.
