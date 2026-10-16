param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
& $Dotnet restore (Join-Path $PSScriptRoot 'src\CueGlass.csproj') -r win-x64 --locked-mode -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $Dotnet publish (Join-Path $PSScriptRoot 'src\CueGlass.csproj') -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o (Join-Path $PSScriptRoot 'app')
exit $LASTEXITCODE
