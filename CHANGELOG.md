# Changelog

All notable changes to Commonage. Versions follow [Semantic Versioning](https://semver.org/); before 1.0, a minor
version may change things that need attention when updating, and these notes say so.

## 0.1.0 (unreleased)

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
