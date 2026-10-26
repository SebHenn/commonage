# Development

## Prerequisites

- .NET 10 SDK
- Docker (for PostgreSQL and for the tests)

## Run everything in Docker

```sh
cp .env.example .env    # set your own POSTGRES_PASSWORD
docker compose up --build
```

The app runs at http://localhost:8080.

## Run the app from your IDE

Start only the database:

```sh
docker compose up -d db
```

Store the connection string in [user-secrets](https://learn.microsoft.com/aspnet/core/security/app-secrets).
They live in your user profile, outside the repository:

```sh
dotnet user-secrets --project src/Commonage.Web set "ConnectionStrings:Default" \
  "Host=localhost;Database=commonage;Username=commonage;Password=<your POSTGRES_PASSWORD>"
dotnet run --project src/Commonage.Web
```

To open a SQL shell: `docker compose exec db psql -U commonage`.

## Tests

```sh
dotnet test
```

Integration tests start a throwaway PostgreSQL container, so Docker must be running.

## Secrets and private data

This repository is going to be public. Never commit:

- passwords, API keys, tokens, connection strings with credentials, certificates (`.pfx`, `.pem`, `.key`)
- `.env` files (only `.env.example` with placeholder values belongs in git)
- real archive material: uploads, database dumps, backups, or anyone's personal data
- demo content you do not have the right to publish

Local configuration goes in user-secrets or `.env`, both ignored by git. CI scans the full history
with [gitleaks](https://github.com/gitleaks/gitleaks) on every push. To run the same scan locally:

```sh
docker run --rm -v "$PWD:/repo" ghcr.io/gitleaks/gitleaks:v8.24.2 git /repo --verbose --redact
```

If a secret is ever committed, deleting it in a new commit is not enough. Revoke or rotate it
first, then remove it from the history.
