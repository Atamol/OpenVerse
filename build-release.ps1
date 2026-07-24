$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$rid = "win-x64"
$rel = Join-Path $PSScriptRoot "release"

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

$keep = @("practice_info.json", "starter_decks.json", "deck_intro.json")
Get-ChildItem (Join-Path $rel "data") -File -ErrorAction SilentlyContinue |
  Where-Object { $keep -notcontains $_.Name } | Remove-Item -Force
Remove-Item -Recurse -Force (Join-Path $rel "certs") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $rel "openverse.cer") -ErrorAction SilentlyContinue

Get-ChildItem $rel -File | Where-Object {
  $_.Extension -in ".pdb", ".db" -or
  $_.Name -like "appsettings*.json" -or
  $_.Name -eq "web.config" -or
  $_.Name -like "*.staticwebassets.endpoints.json"
} | Remove-Item -Force

Set-Content -Path (Join-Path $rel "host.txt") -Value "# to join as a client, put the host's IP here on one line. if you are the host, leave this empty" -Encoding UTF8
Set-Content -Path (Join-Path $rel "name.txt") -Value "# put your in-game name here (one line). delete the line to fall back to your Steam name." -Encoding UTF8

$zip = Join-Path $PSScriptRoot "OpenVerse.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue

$exes = @("openverse-setup.exe", "openverse-launcher.exe", "OpenVerse.Api.exe", "OpenVerse.Battle.exe")
foreach ($e in $exes) { if (-not (Test-Path (Join-Path $rel $e))) { throw "missing from publish: $e" } }
$ship = ($exes + @("host.txt", "name.txt", "data", "stubs")) | ForEach-Object { Join-Path $rel $_ }
Compress-Archive -Path $ship -DestinationPath $zip -CompressionLevel Optimal

$deny = @("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "OpenVerse.EngineHost.dll", "card_master_full.csv.gz")
$lib = Join-Path $PSScriptRoot "src/OpenVerse.Engine/lib"
if (Test-Path $lib) { $deny += (Get-ChildItem $lib -Filter *.dll).Name }
$deny = $deny | Select-Object -Unique
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
$leaked = @($archive.Entries | Where-Object { $deny -contains (Split-Path $_.FullName -Leaf) } | ForEach-Object { $_.FullName })
$archive.Dispose()
if ($leaked.Count) { Remove-Item -Force $zip; throw "not redistributable, refusing to ship: $($leaked -join ', ')" }

$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)

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
