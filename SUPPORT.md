# Support

> **Confirmed by the maintainer, Seb H., on 2026-11-23.** Which versions get security fixes is decided.
> The rest of this page is written as defaults a single maintainer can keep.
> See [docs/release-1.0.md](docs/release-1.0.md). [SECURITY.md](SECURITY.md) says the same about fixes.

## Reporting a security problem

Follow [SECURITY.md](SECURITY.md): GitHub's private vulnerability reporting, and the contact address in that file if you have
no GitHub account. Please do not report security problems in public issues.

## Other problems and questions

Open an issue in the repository, `SebHenn/commonage`. Never put an archive's material, a contributor's details or a password in
an issue: describe what you did and what happened, and say which version you run. A group's export, backup or `.env`
does not belong in a public place.

## Which versions get security fixes

| Version | Security fixes | For how long |
|---|---|---|
| The newest minor (today 0.5; after 1.0, the newest 1.x) | Yes: Commonage, and relevant fixes in .NET, libvips and the base images, as a rule within a week of a fix being available | As long as it is the newest |
| Any older minor | No | Update to the newest one |

"Fix" means a bug-fix release of the newest minor (for example 0.5.1). Fixes are not made for older minors, and there is
no extra period after 1.0: someone on an older version updates through the newest minor. This is the rule in SECURITY.md.

Updating is done with `scripts/update.sh`; see [docs/operations.md](docs/operations.md). Keep a backup before updating.
Updating to a newer minor before 1.0 may need attention; the notes in [CHANGELOG.md](CHANGELOG.md) say so.

## What is supported

- Running the published image the way [docs/deployment.md](docs/deployment.md) describes, on x86 or ARM.
- The export, backup and restore described in [docs/operations.md](docs/operations.md).
- Updating to the newest release. The update test jumps from each earlier minor straight to the current version, so that is the path that is tested.

## What is not supported

- Modified copies, or images built from a changed source.
- A server without HTTPS, or one that exposes the database or the files folder.
- A version that is not the newest minor.
- Reading another system's export; only Commonage's own export is read.
- Recovering an archive without a backup. Commonage stores files in a folder and data in PostgreSQL; the backup is the group's job.
- Help with a group's own server, domain, mail or network.
- Response times. This is a project run by a volunteer: a security report gets an answer within a week, and other issues
  when there is time.

## If the project stops

See "If the project stops" in [docs/handover.md](docs/handover.md): a running archive keeps running, and the export and backups are
open formats.
