# Security

How Commonage protects an archive, what the v0.1 security review checked, and what is still open. To report a
vulnerability, see [SECURITY.md](../SECURITY.md).

## What is worth protecting

- **Unpublished material**: drafts, contributions waiting for review, rejected and withdrawn items, and every
  original file (originals can hold GPS positions and camera serial numbers in their EXIF data).
- **Contributors' personal data**: names, email addresses, and what they sent in.
- **Curator and admin accounts**, which can publish, change and delete anything.
- **The archive's availability and integrity**: material that took years to collect.

## Who could attack

- Anyone on the internet: visitors, bots, and people who found or guessed a contribution link.
- Contributors, who can send in any file and any text.
- A curator account whose password was guessed or reused elsewhere.

## Measures

| Area | Measure |
|---|---|
| Access | Public pages and public image addresses read only from published items (`PublishedItems`). Admin pages are protected by policy per folder; admin-only pages (accounts, export, activity log) by a second policy. Originals and previews of unpublished items are served only to curators. Tested in `PublicationTests`, `MediaTests`, `AccountTests`, `ExportTests`. |
| Accounts | Passwords of at least 12 characters (length over forced symbols, NIST SP 800-63B). Five wrong passwords lock an account for five minutes. Accounts are created with one-time setup links, valid seven days. Disabling an account, or a password change, ends its logins within a minute. Admins cannot lock themselves out. |
| Forms | Every form carries an antiforgery token. Redirects after login and language change only go to addresses on this site. |
| Uploads | Files are recognised by their content (magic bytes), never by name or browser type, and stored under random names outside the web root. Originals are never changed or served to visitors. Previews are made by libvips, rotated upright and stripped of all metadata. Images over 250 megapixels get no preview (decompression bombs). Size limits per file and per request. |
| Browser | Strict Content Security Policy (no inline script or style, no framing, no plugins), `nosniff`, `Referrer-Policy: same-origin` (setup links never leak to other sites), `X-Frame-Options`, `Permissions-Policy`, `Cross-Origin-Opener-Policy`. No `Server` header. `SecurityHeaderTests` checks the headers and that no page contains inline script or style, so the policy can stay strict. |
| Output | Razor encodes all text. The CSV export prefixes cells that start with `=`, `+`, `-` or `@`, so contributed text cannot become a spreadsheet formula. |
| Database | All queries go through EF Core with parameters; the only raw SQL (`AccentFolding`) contains no input. Contributor search escapes `%` and `_`. |
| Spam | Unlisted contribution links, a honeypot field, a minimum fill time and a per-address limit. Spam gets the normal thank-you page. |
| Transport | In production, Caddy terminates HTTPS with automatic certificates and redirects HTTP; the app sends HSTS. Only Caddy is reachable; the app accepts forwarded headers only because nothing else can reach it, and accepts only the archive's own host name. Login cookies are `Secure`, `HttpOnly` and `SameSite=Lax`. |
| Containers | The app runs as a non-root user. The database and the app have no ports open to the internet. |
| Personal data | Contributor details are visible to curators only, removable on request, never written to the activity log or to logs (EF Core does not log parameter values). Exports include them only on request; each export is logged. |
| Errors | Visitors see a plain page with a reference number; details go to the log. |
| Repository | CI scans the whole history for secrets (gitleaks), builds with warnings as errors, and runs all tests and the backup-and-restore test. CodeQL analyses the C# code and the workflows; dependency review refuses pull requests that add vulnerable dependencies; Dependabot proposes updates weekly, and its patch and minor updates merge only after every required check has passed; OpenSSF Scorecard rates the setup. `main` only changes through pull requests with passing checks, and release tags cannot be moved or deleted. |
| Supply chain | GitHub Actions are pinned to commits and container images to digests, so a moved tag cannot change what runs. Workflows have read-only tokens unless a job needs more. Release images are built by the release workflow only after a maintainer approves it, and carry a software bill of materials and a signed build attestation (`gh attestation verify`). |

## Known limits and open points

- **No two-factor authentication** yet. Until then, curators should use a password manager and long passwords.
- **No limit on login attempts per address**, only per account. Guessing passwords for many accounts at once would
  not be slowed down; with the small number of accounts in an archive, and email addresses that are not public,
  this is accepted for v0.1.
- **The login page reveals whether an account exists** when it has been locked after wrong passwords ("too many
  failed attempts").
- **Keys that protect login cookies and setup links are stored unencrypted in the database.** Whoever has the
  database (or a backup) could forge logins. Protect backups accordingly.
- **Images are decoded by libvips**, a large C library. Keep the Docker image up to date (`docker compose build
  --pull` with each update) to get its security fixes.
- **The activity log is kept indefinitely** and holds curators' email addresses.

## Checking for vulnerable packages

```sh
dotnet list package --vulnerable --include-transitive
```

Dependabot does this continuously on the repository; running it before a release costs nothing. Bootstrap, jQuery and
jQuery Validation are copied into `wwwroot/lib` and are not seen by Dependabot: check them by hand before a
release.
