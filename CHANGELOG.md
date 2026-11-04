# Changelog

All notable changes to Commonage. Versions follow [Semantic Versioning](https://semver.org/); before 1.0, a minor
version may change things that need attention when updating, and these notes say so.

## Unreleased

### For visitors and contributors

- The contribution form says how it understands a date while it is typed, and notices a missing agreement before chosen
  photos are lost.
- Forms that come back with a mistake list what to correct, with links to the fields. Screen readers read the message
  when a field gets the focus.
- A form cannot be sent twice by clicking twice.
- The file picker speaks the page's language, not the browser's.
- Collections and Search are always visible on phones, and everything works without JavaScript there.
- The browser's font size setting takes effect. Language buttons and tags are easier to hit on a touch screen.
- Items without a picture show a "Text" tile instead of an empty grey box.

### For curators

- "Save and next" goes through a filtered list, and "Save and add another" starts the next item in the same collection.
- The item list shows pictures, filters by collection, tag and what is still missing, and can be sorted. Waiting
  contributions come oldest first, and after a decision the next one opens.
- A review shows the photos first. Drafts can be previewed as visitors will see them.
- Every management page has the same navigation.
- Dates write the month out, because 11/4/2026 means different days to different readers.

### For people who run an archive

- `scripts/update.sh` updates to a new release. It makes a backup first, and if the new version does not come up, it goes
  back and restores the backup. See docs/deployment.md.
- `SECURITY.md` says which versions get fixes, how soon, and how to follow releases.

### Project

- Browser tests in CI: axe on every main page at phone and desktop width, each JavaScript behaviour, and the main tasks
  without JavaScript.
- CI tests updating from the previous published release, including a failed update.
- The check for inline script and style finds all pages by itself. A new field in the data model has to be exported or be
  left out on purpose, or a test fails.

## 0.2.0 (2026-11-04)

Protection against losing material. Updating needs no steps beyond the usual ones, but set `COMMONAGE_VERSION=0.2` in
`.env` (see docs/deployment.md), and update your copy of the scripts so `scripts/backup.sh` can report its date.

### For curators

- Deleting an item or a photo moves it to the new bin, where it can be restored for 30 days. After that it is deleted
  for good by itself; "Delete for good" does it sooner, for example when someone asks for removal.
- When two people edit one item, the second one to save now sees both versions instead of silently overwriting the
  first. Nothing is lost.
- Forms warn before unsaved text is lost, for example when uploading a photo or switching language.

### For admins

- "Manage archive" shows when the last backup ran and warns when it is overdue or missing, and when the disk is
  filling up.
- "Contributors" also finds photographers and authors, and items in the bin.
- Exports without personal data now also leave out the names of photographers and authors.

### Protection

- The language cookie is only sent over HTTPS, and scripts cannot read it.
- Every package is pinned by content hash when the app is built.
- Uploads and contributions are refused before a nearly full disk can stop the database.
- One contribution link takes in at most 5 GB of files a day, and one address can try to log in 100 times an hour. Both
  can be changed.

### Project

- Dependabot's patch and minor updates merge by themselves once all checks pass; major updates still wait for
  review. GitHub Actions updates are grouped the same way as NuGet updates.

## 0.1.1 (2026-11-03)

### Running an archive

- Ready-made Docker images for x86 and ARM servers at `ghcr.io/sebhenn/commonage`, with a software bill of materials
  and a signed build attestation. `compose.production.yaml` uses them (`COMMONAGE_VERSION`), so servers no longer
  compile Commonage; updating is `docker compose pull`.
- Base images and the database and Caddy images are pinned by digest.

### Project

- Public repository setup: Dependabot, CodeQL, dependency review, OpenSSF Scorecard, a sign-off (DCO) check,
  issue and pull request templates, CODEOWNERS, and pinned GitHub Actions.
- Code of Conduct (Contributor Covenant 2.1) and a contact address for people without a GitHub account.

## 0.1.0 (2026-11-02)

The first version for real community archives.

### For visitors

- Browse collections and items with photos, dates and tags; search with accent-insensitive full-text search,
  filters by period and collection, and paging. Works on phones.
- English and German interface.

### For contributors

- Send in photos and stories through a contribution link, without an account, with rights and consent details.
  Email address optional.

### For curators

- Create, describe, publish and unpublish items; upload photos (originals stay private, previews are made without
  location data); uncertain dates such as "circa 1950" or "1950s"; tags; collections.
- Contribution links and a review workflow: publish, accept as draft, reject, delete.
- Personal data: find a contributor's contributions, remove their name and email address, mark a contribution as
  withdrawn, delete any item with its files.
- Each item shows its history.

### For admins

- Accounts for curators and admins with one-time setup links (the app sends no emails), roles, disabling and
  deleting.
- Activity log of all changes.
- Export of the whole archive as a ZIP with JSON, CSV and all original files.

### Running an archive

- Docker Compose setup with automatic database upgrades.
- HTTPS deployment with Caddy and automatic certificates (`compose.production.yaml`, docs/deployment.md).
- Backup and restore scripts, tested end to end in CI.
- Optional demo content in English or German (`COMMONAGE_DEMO`).
- Security headers with a strict Content Security Policy, length-based password rules, accessibility review
  against WCAG 2.2 AA (docs/security.md, docs/accessibility.md).
- Licensed under the GNU AGPL v3.0; every page links to the source code (`COMMONAGE_SOURCE_URL` for changed
  versions).
