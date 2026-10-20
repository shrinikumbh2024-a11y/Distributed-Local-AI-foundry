# Builds the solution. The environment redirects are only needed inside the
# DeepSeek Harness sandbox (where ~/.dotnet and the default NuGet cache are
# read-only); on a normal machine a plain `dotnet build` is enough.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

$env:DOTNET_CLI_HOME       = Join-Path $root ".dotnet-cli-home"
$env:NUGET_PACKAGES        = Join-Path $root ".nuget-packages"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:DOTNET_CLI_TELEMETRY_OPTOUT       = "1"
$env:DOTNET_NOLOGO = "1"

New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

Push-Location $root
try {
    # -m:1 -nodeReuse:false is required inside the sandbox: MSBuild's parallel
    # child-node processes can't spawn here. Harmless on a normal machine.
    dotnet build LocalAiMesh.sln -c Release -m:1 -nodeReuse:false
}
finally {
    Pop-Location
}
