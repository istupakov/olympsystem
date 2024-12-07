FROM mcr.microsoft.com/windows/servercore:ltsc2019 as buildtools

SHELL ["powershell", "-Command", "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue';"]

# Download and install Build Tools for Visual Studio 2022 for VCTools workload
RUN Invoke-WebRequest -Uri https://aka.ms/vs/17/release/channel -OutFile $env:TEMP/VisualStudio.chman; \
    Invoke-WebRequest -Uri https://aka.ms/vs/17/release/vs_buildtools.exe -OutFile vs_buildtools.exe; \
    Write-Host 'Installing ...'; \
    $exitCode = (Start-Process vs_buildtools.exe -Wait -NoNewWindow -PassThru \
        -ArgumentList @( \
            '--quiet', '--wait', '--norestart', '--nocache', \
			'--channelUri', ($env:TEMP + '/VisualStudio.chman'), \
            '--installChannelUri', ($env:TEMP + '/VisualStudio.chman'), \
			'--add', 'Microsoft.VisualStudio.Workload.VCTools', \
			'--add', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', \
			'--add', 'Microsoft.VisualStudio.Component.Windows11SDK.22621', \
			'--installPath', 'C:\BuildTools' \
		) \
    ).ExitCode; \
    if ($exitCode -ne 0) { \
		Write-Host ('Running vs_buildtools installer failed with exit code: {0}' -f $exitCode); \
		exit $exitCode; \
	} \
    Write-Host 'Removing ...'; \
    Remove-Item $env:TEMP/VisualStudio.chman -Force; \
	Remove-Item vs_buildtools.exe -Force; \
    Remove-Item $env:TEMP/*.log -Force; \
    Write-Host 'Complete.'

# Download and install Python 3.12.7
ENV PYTHONIOENCODING UTF-8
ENV PYTHON_VERSION 3.12.7
RUN $url = ('https://www.python.org/ftp/python/{0}/python-{1}.exe' -f ($env:PYTHON_VERSION -replace '[a-z]+[0-9]*$', ''), $env:PYTHON_VERSION); \
	Write-Host ('Downloading {0} ...' -f $url); \
	[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; \
	Invoke-WebRequest -Uri $url -OutFile 'python.exe'; \
	Write-Host 'Installing ...'; \
	$exitCode = (Start-Process python.exe -Wait -NoNewWindow -PassThru \
		-ArgumentList @( \
			'/quiet', \
			'InstallAllUsers=1', \
			'TargetDir=C:\Python', \
			'PrependPath=1', \
			'Shortcuts=0', \
			'Include_doc=0', \
			'Include_pip=0', \
			'Include_test=0' \
		) \
	).ExitCode; \
	if ($exitCode -ne 0) { \
		Write-Host ('Running python installer failed with exit code: {0}' -f $exitCode); \
		Get-ChildItem $env:TEMP | Sort-Object -Descending -Property LastWriteTime | Select-Object -First 1 | Get-Content; \
		exit $exitCode; \
	} \
	$env:PATH = [Environment]::GetEnvironmentVariable('PATH', [EnvironmentVariableTarget]::Machine); \
	Write-Host 'Verifying install ...'; \
	Write-Host 'python --version'; python --version; \
	Write-Host 'Removing ...'; \
	Remove-Item python.exe -Force; \
	Remove-Item $env:TEMP/Python*.log -Force; \
	Write-Host 'Complete.'

# Download and install OpenJDK 21.0.5
ENV JAVA_VERSION 21.0.5
RUN $url = ('https://aka.ms/download-jdk/microsoft-jdk-{0}-windows-x64.msi' -f $env:JAVA_VERSION); \
	Write-Host ('Downloading {0} ...' -f $url); \
	Invoke-WebRequest -Uri $url -OutFile 'openjdk.msi'; \
	Write-Host 'Installing ...'; \
    $exitCode = (Start-Process msiexec.exe -Wait -NoNewWindow -PassThru \
		-ArgumentList /i, openjdk.msi, /qn, '/L*V', ($env:TEMP + '/OpenJDK.log'), 'ADDLOCAL=FeatureEnvironment', 'INSTALLDIR=C:\openjdk-21' \
	).ExitCode; \
    if ($exitCode -ne 0) { \
		Write-Host ('Running java installer failed with exit code: {0}' -f $exitCode); \
		Get-ChildItem $env:TEMP/OpenJDK.log | Get-Content; \
		exit $exitCode; \
	} \
	$env:PATH = [Environment]::GetEnvironmentVariable('PATH', [EnvironmentVariableTarget]::Machine); \
    Write-Host 'Verifying install ...'; \
    Write-Host 'javac --version'; javac --version; \
    Write-Host 'java --version'; java --version; \
    Write-Host 'Removing ...'; \
    Remove-Item $env:TEMP/OpenJDK.log -Force; \
    Remove-Item openjdk.msi -Force ; \
    Write-Host 'Complete.'

# Download and install FreePascal 3.2.2
RUN $url = 'https://deac-riga.dl.sourceforge.net/project/freepascal/Win32/3.2.2/fpc-3.2.2.i386-win32.exe?viasf=1'; \
	Write-Host ('Downloading {0} ...' -f $url); \
	Invoke-WebRequest -Uri $url -OutFile 'fpc.exe'; \
	Write-Host 'Installing ...'; \
    $exitCode = (Start-Process fpc.exe -Wait -NoNewWindow -PassThru -ArgumentList /VERYSILENT, /NORESTART).ExitCode; \
    if ($exitCode -ne 0) { \
		Write-Host ('Running pascal installer failed with exit code: {0}' -f $exitCode); \
		exit $exitCode; \
	} \
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', [EnvironmentVariableTarget]::Machine); \
    Write-Host 'Verifying install ...'; \
    Write-Host 'fpc -l'; fpc -l; \
    Write-Host 'Removing ...'; \
    Remove-Item fpc.exe -Force ; \
    Write-Host 'Complete.'

# Download and install msys2
RUN $url = 'https://github.com/msys2/msys2-installer/releases/download/nightly-x86_64/msys2-base-x86_64-latest.sfx.exe'; \
	Write-Host ('Downloading {0} ...' -f $url); \
	[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; \
	Invoke-WebRequest -Uri $url -OutFile 'msys2.exe'; \
	Write-Host 'Installing ...'; \
    .\msys2.exe -y -oC:\; \
    Remove-Item msys2.exe ; \
    function msys() { C:\msys64\usr\bin\bash.exe @('-lc') + @Args; } \
    msys ' '; \
    msys 'pacman --noconfirm -Syuu'; \
    msys 'pacman --noconfirm -Syuu'; \
    msys 'pacman --noconfirm -Scc'; \
    msys 'pacman --noconfirm -S mingw-w64-ucrt-x86_64-gcc';

# Enable detection of running in a container
ENV DOTNET_RUNNING_IN_CONTAINER true
# .NET Runtime version
ENV DOTNET_VERSION=9.0.0
# .NET SDK version
ENV DOTNET_SDK_VERSION=9.0.101
# Install .NET Runtime and SDK
RUN Invoke-WebRequest -OutFile dotnet.zip https://dotnetcli.azureedge.net/dotnet/Runtime/$Env:DOTNET_VERSION/dotnet-runtime-$Env:DOTNET_VERSION-win-x64.zip; \
	$dotnet_sha512 = '23ae6ce34fe1271a5a48675a9cb7ed728af4be4014a7ee4a6a60a84fc23e55b50a5cafd7ec20197bd73ee47901e4239e0c4cd8fd0f5deeb34cc3da1de3960e46'; \
	if ((Get-FileHash dotnet.zip -Algorithm sha512).Hash -ne $dotnet_sha512) { \
		Write-Host 'CHECKSUM VERIFICATION FAILED!'; \
		exit 1; \
	}; \
	mkdir $Env:ProgramFiles\dotnet; \
	tar -oxzf dotnet.zip -C $Env:ProgramFiles\dotnet; \
	Remove-Item -Force dotnet.zip; \
	Invoke-WebRequest -OutFile dotnet.zip https://dotnetcli.azureedge.net/dotnet/Sdk/$Env:DOTNET_SDK_VERSION/dotnet-sdk-$Env:DOTNET_SDK_VERSION-win-x64.zip; \
	$dotnet_sha512 = '53f16be2079ed85d230a6c98fa9220046930ca0eaaf1f928b63cfae9fd9a0a5ad87c60c07833ee16dedfa582ce5d9ae68b5b4292aec56fd44203fe9e7bcfba92'; \
	if ((Get-FileHash dotnet.zip -Algorithm sha512).Hash -ne $dotnet_sha512) { \
		Write-Host 'CHECKSUM VERIFICATION FAILED!'; \
		exit 1; \
	}; \
	tar -oxzf dotnet.zip -C $Env:ProgramFiles\dotnet ./LICENSE.txt ./ThirdPartyNotices.txt ./packs ./sdk ./sdk-manifests ./templates ./shared/Microsoft.WindowsDesktop.App; \
	$env:Path += ';' + $Env:ProgramFiles + '\dotnet'; \
	Write-Host 'Verifying install ...'; \
	Write-Host 'dotnet help'; dotnet help; \
	Write-Host 'Removing ...'; \
	Remove-Item -Force dotnet.zip ; \
	setx /M PATH ($env:Path + ';' + $Env:ProgramFiles + '\dotnet')

# Build Olymp.Runner
FROM mcr.microsoft.com/dotnet/sdk:9.0-windowsservercore-ltsc2019 AS build
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
