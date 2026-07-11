$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$rid = "win-x64"
$rel = Join-Path $PSScriptRoot "release"
Remove-Item -Recurse -Force $rel -ErrorAction SilentlyContinue

$projects = @(
  "tools/OpenVerse.Setup/OpenVerse.Setup.csproj",
  "tools/OpenVerse.Launcher/OpenVerse.Launcher.csproj",
  "src/OpenVerse.Api/OpenVerse.Api.csproj"
)
foreach ($p in $projects) {
  dotnet publish $p -c Release -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $rel
  if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}

# keep only the bundled tables (card_master and the dev cert are per-machine, not shipped)
$keep = @("practice_info.json", "starter_decks.json", "deck_intro.json")
Get-ChildItem (Join-Path $rel "data") -File -ErrorAction SilentlyContinue |
  Where-Object { $keep -notcontains $_.Name } | Remove-Item -Force
Remove-Item -Recurse -Force (Join-Path $rel "certs") -ErrorAction SilentlyContinue

Write-Host "release ready: $rel"
