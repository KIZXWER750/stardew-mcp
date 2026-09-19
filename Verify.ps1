param([string]$GamePath = '', [switch]$Race)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Push-Location (Join-Path $root 'mcp-server')
try {
    $format = & gofmt -l (Get-ChildItem -Filter '*.go').FullName
    if ($LASTEXITCODE -ne 0 -or $format) { throw "Go formatting failed: $format" }
    & go test ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go tests failed' }
    & go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go vet failed' }
    if ($Race) {
        & go test -race ./...
        if ($LASTEXITCODE -ne 0) { throw 'Go race test failed (requires CGO and a C compiler)' }
    }
} finally { Pop-Location }
& (Join-Path $root 'Build-Server.ps1')
& dotnet run --project (Join-Path $root 'tests/PolicyChecks/PolicyChecks.csproj')
if ($LASTEXITCODE -ne 0) { throw 'C# policy regression checks failed' }
if ($GamePath) {
    & dotnet build (Join-Path $root 'mod/StardewMCP/StardewMCP.csproj') "-p:GamePath=$GamePath" -p:EnableModDeploy=false -p:EnableModZip=false
    if ($LASTEXITCODE -ne 0) { throw 'Game-reference C# build failed' }
}
Write-Host 'Verification passed. No installation or live game actions performed.'
