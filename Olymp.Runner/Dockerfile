FROM mcr.microsoft.com/dotnet/framework/runtime:4.8 as buildtools

SHELL ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12;"]

RUN Invoke-WebRequest https://aka.ms/vs/18/stable/vs_buildtools.exe -UseBasicParsing -OutFile C:\vs_buildtools.exe; \
    $install = Start-Process C:\vs_buildtools.exe -Wait -PassThru -ArgumentList '\
        --quiet --wait --norestart --nocache --installPath C:\BuildTools \
        --add Microsoft.VisualStudio.Workload.VCTools \
        --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 \
        --add Microsoft.VisualStudio.Component.Windows11SDK.26100 \
        --add Microsoft.NetCore.Component.SDK \
        --add Microsoft.NetCore.Component.Runtime.10.0'; \
    if ($install.ExitCode -notin @(0, 3010)) { exit $install.ExitCode }; \
    Remove-Item C:\vs_buildtools.exe

RUN Invoke-WebRequest https://get.scoop.sh -UseBasicParsing -OutFile C:\install-scoop.ps1; \
    C:\install-scoop.ps1 -RunAsAdmin; \
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; \
    Remove-Item C:\install-scoop.ps1
RUN scoop install git --global; \
    if ($LASTEXITCODE -eq 0) { scoop bucket add java }; \
    if ($LASTEXITCODE -eq 0) { scoop install freepascal mingw java/openjdk python pypy3 kotlin --global }; \
    exit $LASTEXITCODE

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
RUN cmd /D /C 'call C:\BuildTools\Common7\Tools\VsDevCmd.bat -no_logo -arch=x86 -host_arch=x86 >nul && set' \
        | Set-Content -Encoding UTF8 ./envs/vs2026.env; \
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
ENV RunnerClient__WorkingDirectory=/temp

ENTRYPOINT ["Olymp.Runner.exe"]
