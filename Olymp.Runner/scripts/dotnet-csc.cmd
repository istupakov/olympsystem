@echo off
setlocal enabledelayedexpansion

for /f "usebackq delims=" %%i  in (`where dotnet.exe`) do set dotnethome=%%~dpi
for /f %%i  in ('dotnet --version') do set sdkver=%%i
for /f "tokens=2" %%i in ('dotnet --list-runtimes ^| find "Microsoft.NETCore.App"') do set fwkver=%%i
for /f "tokens=1,2 delims=." %%a in ("%fwkver%") do set tfm=net%%a.%%b
set dotnetlib=%dotnethome%packs\Microsoft.NETCore.App.Ref\%fwkver%\ref\%tfm%

if "%1" == "" goto :help
if exist %temp%\csc-%fwkver%.rsp goto :compile
for %%f in ("%dotnetlib%\*.dll") do echo -r:"%%~nxf" >> %temp%\csc-%fwkver%.rsp

:compile
for %%a in (%*) do (
	set x=%%a
	if "!x:~1,4!"=="out:" (
		set prog=!x:~5!
		goto compile2
	)
	if "!x:~-3!"==".cs" set prog=!x:~0,-3!.dll
)
:compile2
dotnet "%dotnethome%sdk\%sdkver%\Roslyn\bincore\csc.dll" -out:%prog% -nologo -lib:"%dotnetlib%" @%temp%\csc-%fwkver%.rsp %*

if %errorlevel% == 0 (
    if exist %prog% (
	for %%a in (%*) do (
		set x=%%a
		if "!x:~1!"=="target:library" goto done
		if "!x:~1!"=="t:library" goto done
	)
	if not exist %prog:~0,-4%.runtimeconfig.json (
	    (
		echo {
  		echo   "runtimeOptions": {
    		echo     "framework": {
      		echo       "name": "Microsoft.NETCore.App",
      		echo       "version": "%fwkver%"
    		echo     }
  		echo   }
		echo }
	    ) > %prog:~0,-4%.runtimeconfig.json

	)

    )
)

goto done
:help
dotnet "%dotnethome%sdk\%sdkver%\Roslyn\bincore\csc.dll" -help
:done
exit /b %errorlevel%
