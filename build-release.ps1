$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$rid = "win-x64"
$rel = Join-Path $PSScriptRoot "release"

# release/ doubles as this machine's working folder, so the wipe below would take the two files that cannot be rebuilt
# from source: the deck/score db and the card master extracted from this machine's game install. park them and restore
# after the archive (neither ships)
$stash = Join-Path ([System.IO.Path]::GetTempPath()) "openverse-stash"
Remove-Item -Recurse -Force $stash -ErrorAction SilentlyContinue
$carry = @("openverse.db", "data/card_master_full.csv.gz")
foreach ($f in $carry) {
  $src = Join-Path $rel $f
  if (Test-Path $src) {
    $dst = Join-Path $stash $f
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Force
  }
}

Remove-Item -Recurse -Force $rel -ErrorAction SilentlyContinue

$projects = @(
  "tools/OpenVerse.Setup/OpenVerse.Setup.csproj",
  "tools/OpenVerse.Launcher/OpenVerse.Launcher.csproj",
  "src/OpenVerse.Api/OpenVerse.Api.csproj",
  "src/OpenVerse.Battle/OpenVerse.Battle.csproj"
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
# per-host public cert written by the launcher at run time (clients fetch it from the host over HTTP)
Remove-Item -Force (Join-Path $rel "openverse.cer") -ErrorAction SilentlyContinue

# publish leftovers and per-machine state the shipped archive shouldn't carry
Get-ChildItem $rel -File | Where-Object {
  $_.Extension -in ".pdb", ".db" -or
  $_.Name -like "appsettings*.json" -or
  $_.Name -eq "web.config" -or
  $_.Name -like "*.staticwebassets.endpoints.json"
} | Remove-Item -Force

# empty host.txt: no IP -> host mode. a client puts the host IP on a line to join instead
Set-Content -Path (Join-Path $rel "host.txt") -Value "# put the host IP here (one line) to run as a client, e.g. 26.91.86.95" -Encoding UTF8
# empty name.txt: fall back to the Steam persona name
Set-Content -Path (Join-Path $rel "name.txt") -Value "# put your in-game name here (one line). delete the line to fall back to your Steam name." -Encoding UTF8

$zip = Join-Path $PSScriptRoot "OpenVerse.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $rel "*") -DestinationPath $zip -CompressionLevel Optimal
$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)

# after the archive, so the shipped zip stays free of them but this machine can run straight away again
foreach ($f in $carry) {
  $src = Join-Path $stash $f
  if (Test-Path $src) {
    $dst = Join-Path $rel $f
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Force
    Write-Host "restored: $f"
  }
}
Remove-Item -Recurse -Force $stash -ErrorAction SilentlyContinue

Write-Host "release ready: $rel"
Write-Host "zip ready:     $zip  ($zipMB MB)"
