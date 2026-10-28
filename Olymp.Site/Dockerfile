FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY --link ./.config/dotnet-tools.json ./.config/
RUN dotnet tool restore

COPY --link ./Olymp.Checker/*.csproj ./Olymp.Checker/
COPY --link ./Olymp.Domain/*.csproj ./Olymp.Domain/
COPY --link ./Olymp.Site/*.csproj ./Olymp.Site/
COPY --link ./Olymp.Site.IdentityUI/*.csproj ./Olymp.Site.IdentityUI/
RUN dotnet restore --no-cache ./Olymp.Site/Olymp.Site.csproj

COPY --link ./Olymp.Checker ./Olymp.Checker/
COPY --link ./Olymp.Domain ./Olymp.Domain/
COPY --link ./Olymp.Site ./Olymp.Site/
COPY --link ./Olymp.Site.IdentityUI ./Olymp.Site.IdentityUI/
RUN dotnet publish --no-restore ./Olymp.Site/Olymp.Site.csproj -c Release -o ./app

ENV ConnectionStrings__DefaultConnection="Server=localhost;Database=olympdb"
RUN dotnet ef migrations bundle --project ./Olymp.Domain --startup-project ./Olymp.Site \
        --configuration Release --no-build --output ./app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --link --from=build /src/app ./

USER app
ENTRYPOINT ["./Olymp.Site"]
