# Commonage

Commonage is a self-hosted web application for community archives. Local history groups, volunteer organisations
and communities use it to collect, preserve and share photographs and stories. Scans of documents work as images today;
PDFs and sound recordings are planned.

- **Contributors** send in material from their phone through a link, without an account, and say who made it and
  what may be done with it.
- **Curators** review what comes in, describe it with uncertain dates ("circa 1950", "1950s") and tags, and publish
  it. Originals stay private; visitors see previews without location data.
- **Visitors** browse collections and search the published archive, in English or German.
- **The archive carries the group's own name**, logo and colour, with its own About, Contact and Privacy pages.
- **The group stays in control**: it runs the archive on its own server, can export everything in an open format at
  any time, handles contributors' requests to remove their data, and sees who changed what.

> **Status:** v0.4, an early version for real community archives. See [CHANGELOG.md](CHANGELOG.md) and
> [docs/roadmap.md](docs/roadmap.md).

## Try it

Requires Docker.

```sh
cp .env.example .env    # set your own passwords
docker compose up --build
```

To look around in a made-up archive with photos, contributions and a waiting review, uncomment `COMMONAGE_DEMO=en`
(or `de`) in `.env` before the first start.

Open http://localhost:8080 and log in with "Curator login" at the bottom of the page, using the admin email address
and password from `.env`. The health check is at http://localhost:8080/health.

## Documentation

- [Putting an archive online](docs/deployment.md): a server, a domain and HTTPS.
- [Running an archive](docs/operations.md): accounts, activity log, personal data, export, backup and restore.
- [Handbook for curators](docs/curator-handbook.md) ([deutsch](docs/handbuch-kuratoren.md)): describing, reviewing and
  handling removal requests.
- [Security](docs/security.md) and [accessibility](docs/accessibility.md): what is in place and what is still open.
  Report vulnerabilities as described in [SECURITY.md](SECURITY.md).
- [Development](docs/development.md): working on the code, and how it fits together.
- [Handing the project over](docs/handover.md): what a new maintainer needs to know.

If this project ever stops, archives keep working: a running archive calls no service of this project, and exports, backups and the
published images stay usable. See [what that means in practice](docs/handover.md#if-the-project-stops).

## License

Commonage is free software under the [GNU Affero General Public License v3.0](LICENSE). You may run, study, change
and share it. If you run a changed version for others, you must offer them its source code; set
`COMMONAGE_SOURCE_URL` so the "Source code" link at the bottom of every page points to it.

The license covers the software only. The photos, documents and stories in an archive belong to the community and
the people who contributed them, under the terms they agreed to.

Contributions are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md). Translations need no Git: see
[docs/translating.md](docs/translating.md).

## Tech stack

C# / ASP.NET Core (.NET 10) with Razor Pages, PostgreSQL with EF Core, libvips for images, Docker Compose, Caddy for
HTTPS.
