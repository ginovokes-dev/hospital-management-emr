# Running Danphe EMR with Docker (for IT people)

`START-HERE.md` in the repository root is the plain-language guide. This page is the technical background.

## What `docker compose up -d --build` starts

| Service | Image | What it does |
|---|---|---|
| `db` | `mcr.microsoft.com/mssql/server:2022-latest` (Express edition) | SQL Server. Data lives in the volume `danphe-emr_dbdata`. |
| `db-init` | same image | One-shot job: on the very first start it restores the two sample databases shipped in `Database/` (`DEV_DanpheEMR_INT`, `DanpheAdmin`). On every later start it finds them and does nothing. |
| `app` | built from `docker/Dockerfile` | The Danphe web application (Kestrel, ASP.NET Core 2.0 compiled for .NET Framework 4.6.1, run with Mono) that also serves the Angular front-end. Listens on `127.0.0.1:8080`. Uploaded scans and documents are kept in the volume `danphe-emr_appfiles` (mounted at `/data/files`). |

All three are `linux/amd64`. On Apple-silicon Macs Docker Desktop runs them through Rosetta emulation (slower; enable *Use Rosetta for x86_64/amd64 emulation* in Docker Desktop). This was built and tested on Linux/amd64; it has not been run on a real Mac yet.

### The image, stage by stage

1. **web** – `node:12`, `npm ci` from `docker/build-support/angular-package-lock.json` (the exact versions the front-end was built with), then `ng build`.
   The Angular sources import a few files that live next to the app (`wwwroot/assets-dph/dicom-assets`, `wwwroot/themes/theme-default/loading.component.css`); the Dockerfile copies exactly those.
2. **api** – `dotnet/sdk:8.0` compiles the .NET Framework 4.6.1 projects against Mono's copy of the 4.6.1 reference assemblies (no Windows needed):
   * `restore-packages-config.sh` restores the legacy `packages.config` projects,
   * `make-refs.sh` prepares the reference folder (case-insensitive aliases),
   * `Directory.Build.targets` adds Mono's type-forwarding facades to the compile.
3. **run** – `mono:6.12`; `fix-mono.sh` swaps in Mono's own `System.Runtime.InteropServices.RuntimeInformation` (the NuGet build always reports "Windows"), `entrypoint.sh` builds the connection strings and starts the application.

### Start-up of the application

`CareTeam/DatabaseUpgrader.cs` runs SQL scripts from `Database/3. Doctor-Care-Team/` (embedded in the program) against the hospital database, waiting up to five minutes for the database:

| Script | When |
|---|---|
| `01_care_team_schema.sql`, `02_care_team_access.sql`, `05_attachments_without_filestream.sql`, `06_not_demo_mode.sql` | every start (they are idempotent) |
| `07_file_folders_on_linux.sql` | every start, when `FILES_DIR` is set: the sample database keeps its upload folders on a Windows drive (`C:\...`, `D:\...`); they are pointed at folders below `FILES_DIR` |
| `03_login_and_license.sql` | once, when `ADMIN_PASSWORD` is set: `admin` login/password, licence valid to 2099 |
| `04_fresh_start.sql` | once, when `FRESH_START=1`: sample staff logins off, sample patients hidden |

"Once" is remembered in the table `CORE_CFG_SetupLog`.

## Settings (`.env`, next to `docker-compose.yml`)

| Variable | Default | Meaning |
|---|---|---|
| `DB_PASSWORD` | `Danphe#Local2025` (`docker/start.sh` generates a private one) | SQL Server `sa` password. Set before the first start; do not change it afterwards. |
| `ADMIN_PASSWORD` | `123` | Password of the `admin` login the first time. |
| `FRESH_START` | `1` | `1` = start clean (sample staff logins off, sample patients hidden). `0` = keep the sample data. |
| `BIND_ADDRESS` | `127.0.0.1` | `0.0.0.0` lets other computers reach it. Put HTTPS in front before you do that. |
| `APP_PORT` | `8080` | Port on the host. |
| `DANPHE_DETAILED_ERRORS` | unset | `1` shows .NET error pages (never in production). |

Inside the `app` container the compose file also sets `FILES_DIR=/data/files` (where uploads are kept), `DB_HOST`, `FRESH_START` and `ADMIN_PASSWORD`; see `docker/entrypoint.sh`.

## Everyday operations

```sh
docker compose up -d            # start (add --build after changing the code)
docker compose stop             # stop, keep everything
docker compose logs -f app      # application log
docker compose down             # remove containers, KEEP the data volume
docker compose down -v          # remove containers AND ALL DATA
```

Back up the database (writes `emr.bak` into the database container, then copies it out):

```sh
docker compose exec db /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$DB_PASSWORD" \
  -Q "BACKUP DATABASE [DEV_DanpheEMR_INT] TO DISK='/var/opt/mssql/backup/emr.bak' WITH INIT, COMPRESSION"
docker compose cp db:/var/opt/mssql/backup/emr.bak ./emr.bak
```

Back up `DanpheAdmin` the same way (it holds the licence/tenant settings), and the uploaded scans / documents:

```sh
docker compose cp app:/data/files ./appfiles-backup          # restore with:  docker compose cp ./appfiles-backup/. app:/data/files
```

## Building behind a company proxy that re-signs HTTPS

If `npm` or `nuget` inside the build fail with certificate errors, put the proxy's root certificate (PEM, extension `.crt`) into `docker/build-support/ca/` and build again. Certificates in that folder are used only while building; `*.crt` files there are git-ignored on purpose.

## Memory

Building the front-end was measured to fit in 3 GB (it succeeded with a 3 GB container limit), the back-end build needs about 1.5 GB more, and SQL Server needs at least 2 GB while running. Give Docker Desktop at least **6 GB** (*Settings → Resources*); 4 GB is the very minimum. After the first build the running system needs about 3 GB (SQL Server + the application).

## Checking a running system

```sh
BASE_URL=http://localhost:8080 python3 tests/api_tests.py             # ~140 checks on sign-in, admin rules, care teams, documents, messages
BASE_URL=http://localhost:8080 node tests/e2e/browser_flow.js         # real-browser walk-through (npm i playwright)
```
Both create their own doctors and withdraw them again at the end.
