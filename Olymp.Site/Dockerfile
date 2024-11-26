FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ./Olymp.Checker/*.csproj ./Olymp.Checker/
COPY ./Olymp.Domain/*.csproj ./Olymp.Domain/
COPY ./Olymp.Site/*.csproj ./Olymp.Site/
COPY ./Olymp.Site.IdentityUI/*.csproj ./Olymp.Site.IdentityUI/
RUN dotnet restore --no-cache ./Olymp.Site/Olymp.Site.csproj

COPY ./Olymp.Checker ./Olymp.Checker/
COPY ./Olymp.Domain ./Olymp.Domain/
COPY ./Olymp.Site ./Olymp.Site/
COPY ./Olymp.Site.IdentityUI ./Olymp.Site.IdentityUI/
RUN dotnet publish --no-restore ./Olymp.Site/Olymp.Site.csproj -c Release -o ./app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /src/app ./

ENTRYPOINT ["./Olymp.Site"]
