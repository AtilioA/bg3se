param(
    [string]$MSBuild = 'D:\VisualStudio\MSBuild\Current\Bin\amd64\MSBuild.exe',
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

    Push-Location BG3Extender
    try {
        & python make_enumerations.py
        if ($LASTEXITCODE -ne 0) { throw 'Enumeration generation failed.' }
        & python make_property_map.py
        if ($LASTEXITCODE -ne 0) { throw 'Property-map generation failed.' }
    } finally {
        Pop-Location
    }

    $protoc = Join-Path $PSScriptRoot 'External\protobuf\tools\protobuf\protoc.exe'
    if (-not (Test-Path $protoc)) { throw "Missing protoc: $protoc" }
    & $protoc --proto_path=BG3Extender --cpp_out=BG3Extender Osiris/Debugger/osidebug.proto Lua/Debugger/LuaDebug.proto Extender/Shared/ExtenderProtocol.proto
    if ($LASTEXITCODE -ne 0) { throw 'Native protobuf generation failed.' }

    & $MSBuild BG3Tools.sln '/t:BG3Extender;LuaDebugger' '/p:Configuration=Game Release' /p:Platform=x64 "/p:PlatformToolset=$PlatformToolset" /p:PostBuildEventUseInBuild=false "/p:BG3SEPairSourceCommit=$sourceCommit" /m /nologo
    $buildExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}
if ($buildExitCode -ne 0) { exit $buildExitCode }
