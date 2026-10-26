# OlympSystem

Web-based system for programming contests used on the site https://olymp.nstu.ru

## Dev Container

Requires Docker Compose with `!reset` support and the VS Code Dev Containers extension.

Select **Dev Containers: Reopen in Container**, then run in the container terminal:

```shell
dotnet ef database update --project Olymp.Domain --startup-project Olymp.Site
dotnet watch --project Olymp.Site/Olymp.Site.csproj --no-launch-profile
```

VS Code opens the site in your browser when it starts.

Database files are in `data/`, backups in `backup/`.

`Olymp.Manager`, `Olymp.QReply`, and `Olymp.Runner` require Windows.
Checking submissions requires a separate runner and a trusted HTTPS endpoint for the site.
