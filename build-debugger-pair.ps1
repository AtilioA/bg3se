param(
    [string]$MSBuild = 'D:\VisualStudio\MSBuild\Current\Bin\MSBuild.exe',
    [string]$PlatformToolset = 'v143'
)

$ErrorActionPreference = 'Stop'
$buildExitCode = 1
if (-not (Test-Path $MSBuild)) { throw "Missing MSBuild: $MSBuild" }
Push-Location $PSScriptRoot
try {
    & git diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'Build only from a clean, committed worktree.' }
    & git diff --cached --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw 'Build only from a clean, committed worktree.' }
    $status = & git status --porcelain --untracked-files=normal
    if ($LASTEXITCODE -ne 0 -or $status) { throw 'Build only from a clean, committed worktree.' }
    $sourceCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Could not resolve the full source commit.' }

    & $MSBuild BG3Tools.sln '/t:BG3Extender;LuaDebugger' '/p:Configuration=Game Release' /p:Platform=x64 "/p:PlatformToolset=$PlatformToolset" /p:PostBuildEventUseInBuild=false "/p:BG3SEPairSourceCommit=$sourceCommit" /m /nologo
    $buildExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($buildExitCode -ne 0) { exit $buildExitCode }
