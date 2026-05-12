FROM mcr.microsoft.com/dotnet/framework/runtime:4.8 as buildtools

SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]
RUN Set-ExecutionPolicy Bypass -Scope Process -Force; \
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol \
    -bor 3072; iex ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))

RUN choco install visualstudio2026buildtools --package-parameters '\
    --add Microsoft.VisualStudio.Workload.VCTool \
    --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 \
    --add Microsoft.VisualStudio.Component.Windows11SDK.26100 \
    --add Microsoft.NetCore.Component.SDK \
    --add Microsoft.NetCore.Component.Runtime.10.0 \
    --quiet --wait --norestart --nocache --installPath C:\BuildTools' -y

RUN choco install freepascal mingw openjdk python -y

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

# Build Olymp.Runner
FROM buildtools AS build
WORKDIR /src

COPY ./Olymp.Runner/*.csproj ./Olymp.Runner/
RUN dotnet restore ./Olymp.Runner/Olymp.Runner.csproj --no-cache

COPY ./Olymp.Runner ./Olymp.Runner/
COPY ./Olymp.Site/Protos ./Olymp.Site/Protos/
RUN dotnet publish ./Olymp.Runner/Olymp.Runner.csproj --no-restore -c Release -o ./app

# Create final image
FROM buildtools
WORKDIR /runner

USER ContainerUser
COPY --from=build /src/app ./
RUN cmd /C set > ./envs/base.env
ENV RunnerClient__WorkingDirectory=/temp

ENTRYPOINT ["Olymp.Runner.exe"]
