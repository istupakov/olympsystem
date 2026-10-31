# OlympSystem

[![.NET](https://img.shields.io/badge/.NET-10-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/istupakov/olympsystem)](LICENSE)
[![CI](https://github.com/istupakov/olympsystem/actions/workflows/ci.yml/badge.svg?branch=master&event=push)](https://github.com/istupakov/olympsystem/actions/workflows/ci.yml)

Programming contest system with automated judging and standings. Used at [olymp.nstu.ru](https://olymp.nstu.ru).

## Projects

- `Olymp.Site` — contest website.
- `Olymp.Site.IdentityUI` — authentication and account management UI.
- `Olymp.Domain` — data model and SQL Server migrations.
- `Olymp.Checker` — compiler configurations, output checkers and judging self-tests.
- `Olymp.Runner` — compilation and execution of submissions.

`Olymp.Manager` and `Olymp.QReply` are legacy Windows applications, excluded from the solution.

## Development

Requires VS Code with Dev Containers and Docker Compose.

### Setup

On the host, use the commands below or the corresponding VS Code tasks (**Tasks: Run Task**).

1. For SQL Server data on a Linux filesystem, run `Host: Prepare SQL directories` or:

   ```bash
   bash scripts/prepare-sql-directories.sh
   ```

2. **Dev Containers: Reopen in Container**. The HTTPS certificate is prepared automatically in `./tls`.
3. On Windows/WSL, run `Host: Trust HTTPS certificate` or:

   ```bash
   certutil.exe -user -addstore Root 'tls\Olymp.Site.crt'
   ```

### Run

In the container, run the `Site: Watch` task or:

```bash
dotnet watch --project Olymp.Site
```

Open https://localhost:5001.

Judging requires a separate Runner, a trusted HTTPS endpoint and matching site `ApiKeys` / Runner `ApiKey` settings. Its Compose file is [Olymp.Runner/docker-compose.yml](Olymp.Runner/docker-compose.yml).

### Lint

Check formatting and code style:

```bash
dotnet format --verify-no-changes --severity info
```

### Tests

Run all tests:

```bash
dotnet test
```

Integration tests create and delete a temporary database from the current EF model.
