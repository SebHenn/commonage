# Commonage

Commonage is a self-hosted web application for community archives. Local history groups,
volunteer organisations and communities use it to collect, preserve and share photographs,
documents, recordings and stories.

- **Contributors** submit material from their phone, without an account.
- **Curators** review submissions and manage descriptions, rights and attribution.
- **Visitors** browse and search the published archive.

> **Status:** early development. Nothing here is ready for real archives yet.
> See [docs/roadmap.md](docs/roadmap.md).

## Quick start

Requires Docker.

```sh
cp .env.example .env    # then set your own POSTGRES_PASSWORD
docker compose up --build
```

Open http://localhost:8080. The health check is at http://localhost:8080/health.

Running an archive (accounts, export, backup and restore): [docs/operations.md](docs/operations.md).
For working on the code, see [docs/development.md](docs/development.md).

## Tech stack

C# / ASP.NET Core (.NET 10) with Razor Pages, PostgreSQL with EF Core, Docker Compose.
