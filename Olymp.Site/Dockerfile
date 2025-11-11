FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

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

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --link --from=build /src/app ./

ENTRYPOINT ["./Olymp.Site"]
