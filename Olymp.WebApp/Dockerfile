FROM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute AS build
WORKDIR /src

COPY --link ./.config/dotnet-tools.json ./.config/
RUN dotnet tool restore

COPY --link ./global.json ./
COPY --link ./Olymp.Checker/*.csproj ./Olymp.Checker/
COPY --link ./Olymp.Domain/*.csproj ./Olymp.Domain/
COPY --link ./Olymp.Icpc/*.csproj ./Olymp.Icpc/
COPY --link ./Olymp.WebApp/*.csproj ./Olymp.WebApp/
RUN dotnet restore --no-cache ./Olymp.WebApp/Olymp.WebApp.csproj

COPY --link ./Protos ./Protos/
COPY --link ./Olymp.Checker ./Olymp.Checker/
COPY --link ./Olymp.Domain ./Olymp.Domain/
COPY --link ./Olymp.Icpc ./Olymp.Icpc/
COPY --link ./Olymp.WebApp ./Olymp.WebApp/
RUN dotnet publish --no-restore ./Olymp.WebApp/Olymp.WebApp.csproj -c Release -o ./app

ENV ConnectionStrings__DefaultConnection="Server=localhost;Database=olympdb"
RUN dotnet ef migrations bundle --project ./Olymp.Domain --startup-project ./Olymp.WebApp \
        --configuration Release --no-build --output ./app/efbundle

FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --link --from=build /src/app ./

USER app
ENTRYPOINT ["./Olymp.WebApp"]
