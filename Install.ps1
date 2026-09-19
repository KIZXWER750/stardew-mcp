param(
    [string]$GamePath = "E:\SteamLibrary\steamapps\common\Stardew Valley",
    [switch]$BuildOnly
)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $GamePath 'Mods\StardewMCP'
if (!(Test-Path (Join-Path $GamePath 'StardewModdingAPI.exe'))) { throw "Game/SMAPI not found: $GamePath" }
if (Get-Process -Name StardewModdingAPI,StardewValley -ErrorAction SilentlyContinue) { throw 'Close the game and SMAPI first.' }
Get-Command go,gofmt,dotnet -CommandType Application -ErrorAction Stop | Out-Null
$copilot = $null
if (!$BuildOnly -and $env:STARDEW_AI_PROVIDER -eq 'copilot') { $copilot = (Get-Command copilot -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source }
$version = (Get-Content "$src\mod\StardewMCP\manifest.json" -Raw | ConvertFrom-Json).Version
if ($version -ne '1.19.0') { throw "Wrong source version: $version" }

Push-Location "$src\mcp-server"
try {
    Get-ChildItem -Filter '*.go' | ForEach-Object {
        & gofmt -w $_.FullName
        if ($LASTEXITCODE -ne 0) { throw 'gofmt failed' }
    }
    go test .
    if ($LASTEXITCODE -ne 0) { throw 'Go tests failed' }
    go build -buildvcs=false -o stardew-mcp-ingame.exe .
    if ($LASTEXITCODE -ne 0) { throw 'Go build failed' }
} finally { Pop-Location }
Push-Location "$src\mod\StardewMCP"
try {
    dotnet build "-p:GamePath=$GamePath" -p:EnableModDeploy=false
    if ($LASTEXITCODE -ne 0) { throw 'Mod build failed. Existing installation was not changed.' }
} finally { Pop-Location }
if ($BuildOnly) { Write-Host 'Build and Go tests completed. Nothing installed.'; return }

$backup = Join-Path (Split-Path $src -Parent) ('StardewMCP-before-1.19.0-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$hadOld = Test-Path $dest
if ($hadOld) { Copy-Item $dest $backup -Recurse }
$config = [ordered]@{
    ServerPath='agent/stardew-mcp-ingame.exe';CopilotCliPath=$copilot;OpenKey='F6';CancelKey='F7';
    EnableAutomaticBedtimeAlarm=$true;FirstBedtimeAlarm=2200;SecondBedtimeAlarm=2400;FinalBedtimeAlarm=2500
}
$configPath = Join-Path $dest 'config.json'
if (Test-Path $configPath) {
    $oldConfig = Get-Content $configPath -Raw | ConvertFrom-Json
    foreach ($property in $oldConfig.PSObject.Properties) { $config[$property.Name] = $property.Value }
    $config['ServerPath'] = 'agent/stardew-mcp-ingame.exe'
    $config['CopilotCliPath'] = $copilot
}
try {
    New-Item -ItemType Directory -Path "$dest\agent" -Force | Out-Null
    Copy-Item "$src\mod\StardewMCP\bin\Debug\net6.0\StardewMCP.dll" $dest -Force
    Get-ChildItem "$src\mod\StardewMCP\bin\Debug\net6.0" -Filter '*WebSocket*.dll' | Copy-Item -Destination $dest -Force
    Copy-Item "$src\mod\StardewMCP\manifest.json" $dest -Force
    Copy-Item "$src\mcp-server\stardew-mcp-ingame.exe" "$dest\agent" -Force
    $wikiSource = "$src\mod\StardewMCP\bin\Debug\net6.0\data\knowledge\wiki"
    if (Test-Path $wikiSource) {
        $wikiDestination = Join-Path $dest 'data\knowledge\wiki'
        New-Item -ItemType Directory -Path $wikiDestination -Force | Out-Null
        Copy-Item (Join-Path $wikiSource '*') $wikiDestination -Recurse -Force
    }
    [IO.File]::WriteAllText($configPath,($config | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
} catch {
    if ($hadOld) {
        Remove-Item $dest -Recurse -Force
        Copy-Item $backup $dest -Recurse
    } else { Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue }
    throw
}
Write-Host '1.19.0 installed. Phase 4 leased plan execution, verified step completion, fixed plot binding, and multi-day auto-resume are enabled. Restart Steam/SMAPI; F6 opens commands or a pending question, F7 pauses active goals and plans.'
if ($hadOld) { Write-Host "Previous installation: $backup" }
