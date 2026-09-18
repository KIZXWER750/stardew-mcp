# Game-free build. Does not install or execute the game, Copilot or OpenAI.
$ErrorActionPreference = 'Stop'
Get-Command go -CommandType Application -ErrorAction Stop | Out-Null
$oldOS=$env:GOOS; $oldArch=$env:GOARCH; $oldCGO=$env:CGO_ENABLED
Push-Location (Join-Path $PSScriptRoot 'mcp-server')
try {
    go test ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go tests failed' }
    go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go vet failed' }
    $env:GOOS='windows'; $env:GOARCH='amd64'; $env:CGO_ENABLED='0'
    go build -trimpath -buildvcs=false -o stardew-mcp-ingame.exe .
    if ($LASTEXITCODE -ne 0) { throw 'Windows server build failed' }
    Get-FileHash .\stardew-mcp-ingame.exe -Algorithm SHA256
    Write-Host 'Server ready. No game installation, C# build or paid API request was performed.'
} finally {
    $env:GOOS=$oldOS; $env:GOARCH=$oldArch; $env:CGO_ENABLED=$oldCGO
    Pop-Location
}
