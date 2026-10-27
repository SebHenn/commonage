# Roadmap to v0.1

Estimates assume about 10 hours a week. Every milestone ends with something demonstrable.

| Milestone | Weeks | Done when |
|---|---|---|
| **M0 Setup** ✅ | 1 | A fresh clone runs with `docker compose up`; CI builds, tests and scans for secrets. |
| **M1 Text records** ✅ | 2–4 | A curator logs in, creates and publishes a text-only record, and visitors see it. Drafts stay hidden. UI text is localisable (German and English). |
| **M2 Photos** | 5–8 | Curators upload photos; originals stay private; previews are generated in the background without EXIF/GPS data. Files of unpublished items return 404. Show it to a partner group. |
| **M3 Search and browse** | 9–11 | Tags, uncertain dates ("circa 1950"), PostgreSQL full-text search, filters, pagination, mobile layout. |
| **M4 Contributions** | 12–15 | Anyone with the link can submit material with consent and rights details; curators approve or reject it. Spam protection; email optional. |
| **M5 Trust** | 16–19 | Export (metadata + files), tested backup and restore, audit log, curator management, deletion of personal data. |
| **M6 Release v0.1** | 20–22 | HTTPS deployment guide, documentation, demo content, accessibility and security review, five-task test with a real group. |

## Decisions

- One web application (Razor Pages) plus one test project. Split into more projects only when it hurts.
- A contribution creates an `ArchiveItem` with status `Submitted`; publishing only changes its status.
- Item status: `Draft` and `Published` so far; `Submitted`, `Rejected` and `Withdrawn` arrive with contributions (M4). Files will have their own `IsPublic` flag.
- Every public page queries `CommonageDbContext.PublishedItems`, never `ArchiveItems` directly.
- Migrations are applied automatically at startup (single-instance self-hosting).
- UI text is English-keyed and translated through `Resources/SharedResource.{culture}.resx`.
- Uncertain dates: `DateFrom` / `DateTo` for filtering, `DateText` for display.
- Search uses PostgreSQL full-text search; no separate search server.
- Original uploads are never modified. Files are stored under generated names and served through an
  endpoint that checks permissions.

## Open decisions

- License (AGPL-3.0 is the current favourite). Must be chosen before the repository is made public.
