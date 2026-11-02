# Putting an archive online

This guide sets up Commonage on a server of your own, reachable at an address such as `https://archive.example.org`,
with HTTPS certificates that renew themselves. It takes about an hour and needs someone who is comfortable typing
commands into a server. Day-to-day tasks (accounts, backups, export) are in [operations.md](operations.md).

## What you need

- **A server** running Linux, such as a small virtual server from a hosting company: 2 GB of memory, 2 processor
  cores and enough disk for your material (photos at full resolution add up; start with 50 GB and grow it). Debian 12
  or Ubuntu 24.04 work well.
- **A domain name** you control, for example `archive.example.org`. A subdomain of your group's website is fine.
- **Docker Engine** with the Compose plugin (Compose 2.24 or newer). Install it from
  [docs.docker.com/engine/install](https://docs.docker.com/engine/install/).
- **Somewhere else for backups**, such as a second server, a storage service or a disk someone takes home.

## 1. Point the domain at the server

At your domain provider, create an `A` record (and an `AAAA` record if the server has IPv6) for
`archive.example.org` pointing to the server's address. Wait until `ping archive.example.org` answers from the
server's address.

## 2. Open the firewall

Only three ports need to be reachable from the internet: 22 (SSH, for you), 80 and 443 (the archive). On Ubuntu:

```sh
sudo ufw allow OpenSSH
sudo ufw allow 80,443/tcp
sudo ufw allow 443/udp
sudo ufw enable
```

The database and the app itself are never exposed; only Caddy, the HTTPS front, is.

## 3. Get Commonage

```sh
git clone https://github.com/SebHenn/commonage.git
cd commonage
git checkout v0.1.1        # a released version, not the development branch
```

This gives you the Compose files and the backup scripts. The app itself comes as a ready-made Docker image,
`ghcr.io/sebhenn/commonage`, for x86 and ARM servers, so nothing is compiled on your server.

## 4. Configure

```sh
cp .env.example .env
chmod 600 .env
openssl rand -base64 24     # run twice: one database password, one first admin password
nano .env
```

Fill in `.env`:

```sh
POSTGRES_PASSWORD=<the first random value>
COMMONAGE_ADMIN_EMAIL=you@example.org
COMMONAGE_ADMIN_PASSWORD=<the second random value, or a long passphrase>

COMMONAGE_DOMAIN=archive.example.org
COMPOSE_FILE=compose.yaml:compose.production.yaml
COMMONAGE_VERSION=0.1
```

`COMMONAGE_VERSION=0.1` follows the bug-fix releases of 0.1 (0.1.1, 0.1.2, ...) whenever you update; a new minor
version such as 0.2 only comes when you change this line.

The `COMPOSE_FILE` line adds `compose.production.yaml`, so every `docker compose` command, and the backup scripts,
use the production setup: HTTPS through Caddy, automatic restarts, and no ports open except 80 and 443. (On Windows
the separator is `;` instead of `:`.)

## 5. Start

```sh
docker compose up -d
docker compose ps
```

To check that the image really was built from this repository by its release workflow:

```sh
gh attestation verify oci://ghcr.io/sebhenn/commonage:0.1 --owner SebHenn
```

Within a minute Caddy has a certificate from Let's Encrypt. Open `https://archive.example.org`, log in with
"Curator login" at the bottom of the page, and change the admin password under "Change password". You can now delete
the two `COMMONAGE_ADMIN_` lines from `.env`; they are only used while the archive has no accounts.

Then, under "Manage archive":

1. "Accounts": create accounts for the other curators and pass on their setup links.
2. "Collections": create the first collection.
3. "Contribution links": create a link for people to send in material.

## 6. Back up every night

Backups (see [operations.md](operations.md#backup-and-restore)) are written by `scripts/backup.sh`. Run it every
night with cron, keep a week of them, and copy them off the server. For example, `crontab -e` and add:

```cron
# 03:00 every night: back up, keep the newest 7, copy to the backup server.
0 3 * * * cd /home/archive/commonage && scripts/backup.sh /srv/backups >> /srv/backups/backup.log 2>&1 && ls -1d /srv/backups/commonage-* | head -n -7 | xargs -r rm -rf && rsync -a --delete /srv/backups/ backup@backup.example.org:commonage/
```

**Test a restore** on another machine before you rely on the backups, and again every few months:
`scripts/restore.sh` with a copied backup folder, `.env` and the same `COMMONAGE_DOMAIN` (or `localhost` for a test).

## Updating

Read the release notes in `CHANGELOG.md` first. Then:

A bug-fix release of your version (for example 0.1.2 when you run 0.1):

```sh
scripts/backup.sh
git fetch --tags && git checkout v0.1.2
docker compose pull
docker compose up -d
```

A new minor version (for example 0.2): the same, but also set `COMMONAGE_VERSION=0.2` in `.env` before
`docker compose pull`.

The app upgrades the database when it starts. If something goes wrong, check out the previous version, set
`COMMONAGE_VERSION` to the exact previous version (for example `0.1.1`), run `docker compose pull`, and restore the
backup you just made.

## Keeping an eye on it

- `https://archive.example.org/health` answers "Healthy" while the app and the database work. An uptime monitor can
  check it every few minutes.
- `docker compose logs --tail 100 web` shows the app's log; `docker compose logs caddy` shows certificate problems.
- Keep the server's own updates switched on (`unattended-upgrades` on Debian and Ubuntu).

## A changed version of Commonage

If you change the code, build and publish your own image (for example with this repository's release workflow in your
fork), set `COMMONAGE_IMAGE` to it in `.env`, and set `COMMONAGE_SOURCE_URL` to your fork, as the AGPL requires.
Without `compose.production.yaml`, `docker compose up -d --build` builds the image from the checked-out code.

## Behind an existing web server

If the server already runs nginx, Apache or Traefik for other sites, leave out `compose.production.yaml` and let that
web server forward `archive.example.org` to the app instead. It must send the `X-Forwarded-For` and
`X-Forwarded-Proto` headers, and the app needs these settings in a Compose override of your own:

```yaml
services:
  web:
    environment:
      ASPNETCORE_FORWARDEDHEADERS_ENABLED: "true"
      AllowedHosts: archive.example.org
```

`ASPNETCORE_FORWARDEDHEADERS_ENABLED` trusts whoever sends these headers, so the app must only be reachable through
your web server: keep the `127.0.0.1:8080` port binding from `compose.yaml`, or put both on a private Docker network.
Without forwarded headers, every visitor would count as the same address for the contribution limit, and links the
app creates (setup links) would start with `http://`.

## Troubleshooting

| What you see | What to check |
|---|---|
| The browser warns about the certificate | The domain does not point to this server yet, or ports 80/443 are blocked. `docker compose logs caddy` says which. |
| "Bad Request - Invalid Hostname" | `COMMONAGE_DOMAIN` in `.env` does not match the address you typed. |
| `docker compose` says `Set COMMONAGE_DOMAIN in .env` | The `COMMONAGE_DOMAIN` line is missing in `.env`. |
| The admin login does not work on a new archive | `COMMONAGE_ADMIN_*` must be set on the very first start; `docker compose logs web` says if the password was refused (at least 12 characters). |
| The log shows `fail: ... Failed executing DbCommand ... "__EFMigrationsHistory"` on the very first start | Harmless: the database is still empty, and the app creates its tables right after. |
| `docker compose pull` says the image cannot be found | `COMMONAGE_VERSION` names a version that has not been released; see the releases page. |
| Uploads stop at a certain size | Each file may be up to 200 MB (`Uploads__MaxFileSizeMegabytes` in the `web` environment). |
