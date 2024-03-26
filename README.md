# OlympSystem

Web-based system for programming contests used on the site https://olymp.nstu.ru

Run site in development mode:
```shell
docker compose up sqlserver
cd Olymp.Site
dotnet user-secrets set ConnectionStrings:DefaultConnection 'Server=localhost;Database=olympdb;TrustServerCertificate=true;User Id=SA;Password=YourStrong!Passw0rd'
dotnet run
```
