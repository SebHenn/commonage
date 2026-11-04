# Changelog

All notable changes to Commonage. Versions follow [Semantic Versioning](https://semver.org/); before 1.0, a minor
version may change things that need attention when updating, and these notes say so.

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
