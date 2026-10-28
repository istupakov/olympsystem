# OlympSystem

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

Open the repository with **Dev Containers: Reopen in Container**, then run:

```shell
dotnet ef database update --project Olymp.Domain --startup-project Olymp.Site
dotnet watch --project Olymp.Site/Olymp.Site.csproj --no-launch-profile
```

Open the forwarded port `8080` in VS Code. Migrations do not create an administrator account.

Judging requires a separate Runner, a trusted HTTPS endpoint and matching site `ApiKeys` / Runner `ApiKey` settings. Its Compose file is [Olymp.Runner/docker-compose.yml](Olymp.Runner/docker-compose.yml).

[CI workflow](.github/workflows/ci.yml) · [License](LICENSE).
