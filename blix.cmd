@echo off
rem blix — the front door on Windows. Find a project's apps and run one.
rem
rem   blix ls                    what this project has
rem   blix run <app> [args...]   run one
rem   blix run <project>:<app>   reach across folders
rem
rem == Why this is so much shorter than ./blix ==============================
rem Almost everything in the bash script is a macOS workaround, not a front door.
rem There, dyld freezes DYLD_* at exec and strips it from SIP-protected
rem interpreters, so the environment has to be built before the process starts and
rem the apphost has to be exec'd directly rather than going through the dotnet
rem muxer. None of that exists here: Windows resolves DLLs from the executable's
rem own directory, the Vulkan loader is vulkan-1.dll in System32, and the ICD is
rem registered by the GPU driver rather than found through an environment
rem variable. So what is left is the part that was always the actual front door —
rem bootstrap the two tools, then hand over.
rem
rem UNVERIFIED. Written from the audit in plan.md stage C, on a machine with no
rem Windows. It has never been run. Treat a failure here as expected work, not as
rem a surprise.

setlocal
set "REPO_ROOT=%~dp0"
if "%BLIX_CONFIG%"=="" (set "CONFIG=Debug") else (set "CONFIG=%BLIX_CONFIG%")
set "CLI=%REPO_ROOT%src\Blix.Cli\bin\%CONFIG%\net8.0\blix.exe"
set "INDEXER=%REPO_ROOT%src\Blix.Tools.Apps\bin\%CONFIG%\net8.0\Blix.Tools.Apps.dll"

rem The loader ships with the GPU driver. Without it there is no point going on,
rem and the failure further in is unrecognisable.
if not exist "%SystemRoot%\System32\vulkan-1.dll" (
    echo vulkan-1.dll not found in System32 - install your GPU vendor's driver, 1>&2
    echo or the Vulkan SDK from https://vulkan.lunarg.com/ 1>&2
    exit /b 1
)

rem The indexer is the bootstrap: nothing in the tree references it, so on a fresh
rem clone the first assemblies build before it exists and are not indexed. Building
rem it here means that by the time an index is ASKED for, the next build writes a
rem correct one - which is why the targets file can guard on Exists() and no
rem project needs a reference.
if not exist "%INDEXER%" (
    echo blix: building the app indexer ^(first run^) 1>&2
    dotnet build "%REPO_ROOT%src\Blix.Tools.Apps\Blix.Tools.Apps.csproj" -c "%CONFIG%" --nologo -v:q || exit /b 1
)

if not exist "%CLI%" (
    echo blix: building the resolver ^(first run^) 1>&2
    dotnet build "%REPO_ROOT%src\Blix.Cli\Blix.Cli.csproj" -c "%CONFIG%" --nologo -v:q || exit /b 1
)

"%CLI%" %*
exit /b %ERRORLEVEL%
