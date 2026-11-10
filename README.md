# OlympSystem

[![.NET](https://img.shields.io/badge/.NET-11-512BD4)](https://dotnet.microsoft.com/)
[![License](https://img.shields.io/github/license/istupakov/olympsystem)](LICENSE)
[![CI](https://github.com/istupakov/olympsystem/actions/workflows/ci.yml/badge.svg?branch=master&event=push)](https://github.com/istupakov/olympsystem/actions/workflows/ci.yml)

Programming contest system with automated judging and standings. Used at [olymp.nstu.ru](https://olymp.nstu.ru).

## Projects

- `Olymp.WebApp` – Blazor website, account management, jury/admin tools and Runner RPC.
- `Olymp.Domain` – data model and SQL Server migrations.
- `Olymp.Checker` – compiler configurations, output checkers and judging self-tests.
- `Olymp.Runner` – compilation and execution of submissions.
- `Olymp.Tests` and `Olymp.IntegrationTests` – unit and SQL Server integration tests.

`Olymp.Manager` is a Windows contest preparation application outside the solution.
The web preparation interface is not available; see [ideas](docs/webapp-ideas.md).

## Development

The supported development setup uses VS Code with Dev Containers and Docker Compose.
The container provides the SDK pinned in [global.json](global.json) and starts SQL Server.

### Setup

Run host commands from the repository root on the Linux/WSL Docker host.
The corresponding VS Code tasks are available through **Tasks: Run Task**.

1. For SQL Server data on a Linux filesystem, run `Host: Prepare SQL directories` or:

   ```bash
   bash scripts/prepare-sql-directories.sh
   ```

2. Run **Dev Containers: Reopen in Container**. The container prepares the HTTPS
   certificate in `./tls` and restores the declared .NET tools and dependencies.
3. Trust `./tls/Olymp.WebApp.crt` in the host's root certificate store.
   If `certutil.exe` is available, run `Host: Trust HTTPS certificate` or:

   ```bash
   certutil.exe -user -addstore Root 'tls\Olymp.WebApp.crt'
   ```

4. In the container, initialize or update the development database:

   ```bash
   dotnet ef database update --project Olymp.Domain --startup-project Olymp.WebApp
   ```

   To start from a backup, first use the `Database: Restore` task with a `.bak`
   file in `./backup`, then apply migrations.

### Run

In the container, run the `Site: Watch` task or:

```bash
dotnet watch --project Olymp.WebApp
```

Open https://localhost:5001.

Judging requires a separate Runner; see [deployment](docs/deployment.md).

### Checks

Run these commands from the repository root inside the container.
After changing project dependencies, restore them:

```bash
dotnet restore
```

Build the website:

```bash
dotnet build Olymp.WebApp --no-restore
```

Check formatting and code style:

```bash
dotnet format --verify-no-changes --no-restore --severity info
```

Tests use Microsoft.Testing.Platform. Run unit tests without SQL Server:

```bash
dotnet test --project Olymp.Tests --no-restore
```

Run all tests:

```bash
dotnet test --no-restore
```

Run one integration test class:

```bash
dotnet test --project Olymp.IntegrationTests --filter-class 'Olymp.IntegrationTests.WebApp.ContestProblemTests' --no-restore
```

Integration tests use the connection supplied by Compose and create temporary
databases. The SQL Server account must have permission to create and delete them.

## Production

See [deployment](docs/deployment.md) for Docker Compose, Caddy and Runner configuration.
