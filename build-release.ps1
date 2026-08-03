$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$rid = "win-x64"
$rel = Join-Path $PSScriptRoot "release"
# only the two things a person double-clicks, plus the two files they edit, sit at the top. everything the build
# produced goes under server/, which is also where the server processes resolve data/ and stubs/ from
$srv = Join-Path $rel "server"

# the stash is NOT wiped first: a run that throws after the release dir is gone would otherwise leave nothing to
# restore, and the next run would stash that emptiness over the last good copy. card_master is the one that hurts,
# since the publish drops it and only the stash puts it back
$stash = Join-Path ([System.IO.Path]::GetTempPath()) "openverse-stash"
# all under server/. engine.txt is stashed but NOT restored here: it is rewritten below so the wording can change while
# the chosen value carries over, and a restore would put the old wording back
$carry = @("openverse.db", "data/card_master_full.csv.gz", "engine.txt")
$restore = $carry | Where-Object { $_ -ne "engine.txt" }
foreach ($f in $carry) {
  $src = Join-Path $srv $f
  if (Test-Path $src) {
    $dst = Join-Path $stash $f
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Force
  }
}

# what "the same build" means: a hash over the sources that go into it. the compiler's own output is not usable here
# because a rebuild of untouched code still has to count as the same version
$srcHash = (Get-ChildItem -Recurse -File -Path (Join-Path $PSScriptRoot "src"), (Join-Path $PSScriptRoot "tools") `
              -Include *.cs, *.csproj -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
            Sort-Object FullName |
            ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash }) -join ''
$buildId = (Get-FileHash -InputStream ([IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($srcHash))) -Algorithm SHA256).Hash.Substring(0, 16)

# logs accumulate across runs, but only while they still describe this build. a changed build makes them misleading,
# so they go with it rather than sitting there looking current
$logDir = Join-Path $PSScriptRoot "openverse_log"
$stashLogs = Join-Path $stash "openverse_log"
$prevId = if (Test-Path (Join-Path $srv "build.id")) { (Get-Content (Join-Path $srv "build.id") -Raw).Trim() } else { "" }
Remove-Item -Recurse -Force $stashLogs -ErrorAction SilentlyContinue
if ($prevId -eq $buildId -and (Test-Path $logDir)) {
  Copy-Item $logDir $stashLogs -Recurse -Force
  $kept = (Get-ChildItem $stashLogs -File -ErrorAction SilentlyContinue).Count
  Write-Host "logs: same build ($buildId), keeping $kept"
}
elseif (Test-Path $logDir) {
  $dropped = (Get-ChildItem $logDir -File -ErrorAction SilentlyContinue).Count
  Write-Host "logs: build changed ($prevId -> $buildId), dropping $dropped"
}
# a stashed card_master is the only copy once the release dir goes, so refuse to build without one rather than produce
# a release that cannot start
if (-not (Test-Path (Join-Path $stash "data/card_master_full.csv.gz"))) {
  $seed = Join-Path $PSScriptRoot "src/OpenVerse.Api/data/card_master_full.csv.gz"
  if (Test-Path $seed) {
    New-Item -ItemType Directory -Force (Join-Path $stash "data") | Out-Null
    Copy-Item $seed (Join-Path $stash "data/card_master_full.csv.gz") -Force
    Write-Host "card_master: seeded the stash from src/OpenVerse.Api/data"
  }
  else { throw "no card_master_full.csv.gz in release/, the stash, or src/OpenVerse.Api/data - run openverse-setup" }
}

Remove-Item -Recurse -Force $rel -ErrorAction SilentlyContinue

$projects = @(
  "tools/OpenVerse.Setup/OpenVerse.Setup.csproj",
  "tools/OpenVerse.Launcher/OpenVerse.Launcher.csproj",
  "src/OpenVerse.Api/OpenVerse.Api.csproj",
  "src/OpenVerse.Battle/OpenVerse.Battle.csproj"
)
# the two front doors land at the top, the servers underneath, so opening the folder shows two exes and two txt files
foreach ($p in $projects) {
  $dest = if ($p -match "Setup|Launcher") { $rel } else { $srv }
  dotnet publish $p -c Release -r $rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $dest
  if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}

$keep = @("practice_info.json", "starter_decks.json", "deck_intro.json", "leader_skin_list.json")
Get-ChildItem (Join-Path $srv "data") -File -ErrorAction SilentlyContinue |
  Where-Object { $keep -notcontains $_.Name } | Remove-Item -Force
Remove-Item -Recurse -Force (Join-Path $srv "certs") -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path $srv "openverse.cer") -ErrorAction SilentlyContinue

Get-ChildItem $rel, $srv -File | Where-Object {
  $_.Extension -in ".pdb", ".db" -or
  $_.Name -like "appsettings*.json" -or
  $_.Name -eq "web.config" -or
  $_.Name -like "*.staticwebassets.endpoints.json"
} | Remove-Item -Force

$keepComment = "# Lines starting with # are ignored, so this one can stay"
Set-Content -Path (Join-Path $rel "join_host.txt") -Encoding UTF8 -Value @(
  "# Put the host's IP on one line below to join someone. Leave it empty to host yourself"
  $keepComment)
Set-Content -Path (Join-Path $rel "username.txt") -Encoding UTF8 -Value @(
  "# Put your in-game name on one line below. Leave it empty to use your Steam name"
  $keepComment)
# the host's choice survives, the wording does not: keeping the whole file meant a build could never fix its own
# instructions, and writing the whole file meant a build silently reset the engine to Observe. carry the value over and
# rewrite the rest. it has to exist before the zip, so this runs here rather than with the restores below
$engineTxt = Join-Path $srv "engine.txt"
$keptEngine = Join-Path $stash "engine.txt"
$role = if (Test-Path $keptEngine) {
  (Get-Content $keptEngine | ForEach-Object { $_.Trim() } |
    Where-Object { $_.Length -gt 0 -and -not $_.StartsWith("#") } | Select-Object -First 1)
} else { $null }
if (-not $role) { $role = "AdviseCost" }
Set-Content -Path $engineTxt -Encoding UTF8 -Value @(
  "# Observe | AdviseCost | AnswerBlanks, least to most trusted"
  "# Replace the value below with one of those. $($keepComment.Substring(2))"
  $role)

# before the zip, so the recipient can see which build they were given
Set-Content -Path (Join-Path $srv "build.id") -Value $buildId -Encoding UTF8

$zip = Join-Path $PSScriptRoot "OpenVerse.zip"
Remove-Item -Force $zip -ErrorAction SilentlyContinue

$front = @("openverse-setup.exe", "openverse-launcher.exe")
$exes = $front + @("OpenVerse.Api.exe", "OpenVerse.Battle.exe")
foreach ($e in $front) { if (-not (Test-Path (Join-Path $rel $e))) { throw "missing from publish: $e" } }
foreach ($e in @("OpenVerse.Api.exe", "OpenVerse.Battle.exe")) {
  if (-not (Test-Path (Join-Path $srv $e))) { throw "missing from publish: server/$e" }
}
$deny = @("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll", "OpenVerse.EngineHost.dll", "card_master_full.csv.gz")
$lib = Join-Path $PSScriptRoot "src/OpenVerse.Engine/lib"
if (Test-Path $lib) { $deny += (Get-ChildItem $lib -Filter *.dll).Name }
$deny = $deny | Select-Object -Unique

# the zip mirrors the folder: two exes and two editable files at the top, the build under server/. it is staged rather
# than compressed in place because server/ also holds the engine assemblies, which are the game's and not ours to ship
$stage = Join-Path ([System.IO.Path]::GetTempPath()) "openverse-ship"
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($item in ($front + @("join_host.txt", "username.txt", "server"))) {
  Copy-Item (Join-Path $rel $item) (Join-Path $stage $item) -Recurse -Force
}
Get-ChildItem $stage -Recurse -File | Where-Object { $deny -contains $_.Name } | Remove-Item -Force
$ship = Get-ChildItem $stage | ForEach-Object { $_.FullName }
Compress-Archive -Path $ship -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
$leaked = @($archive.Entries | Where-Object { $deny -contains (Split-Path $_.FullName -Leaf) } | ForEach-Object { $_.FullName })
$archive.Dispose()
if ($leaked.Count) { Remove-Item -Force $zip; throw "not redistributable, refusing to ship: $($leaked -join ', ')" }

$zipMB = [math]::Round((Get-Item $zip).Length / 1MB, 1)

foreach ($f in $restore) {
  $src = Join-Path $stash $f
  if (Test-Path $src) {
    $dst = Join-Path $srv $f
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $src $dst -Force
    Write-Host "restored: $f"
  }
}
if (Test-Path $stashLogs) {
  Copy-Item $stashLogs $logDir -Recurse -Force
  Write-Host "restored: openverse_log ($((Get-ChildItem $logDir -File).Count) runs)"
}
Remove-Item -Recurse -Force $stash -ErrorAction SilentlyContinue

# the same two front doors beside build-release.ps1, so a developer can run the thing they just built without
# descending into release/. they resolve the servers through release/server, and Layout puts host.txt, name.txt and
# openverse_log up here rather than in a directory the next build deletes
foreach ($e in $front) { Copy-Item (Join-Path $rel $e) (Join-Path $PSScriptRoot $e) -Force }
foreach ($t in @("join_host.txt", "username.txt")) {
  $atRoot = Join-Path $PSScriptRoot $t
  if (-not (Test-Path $atRoot)) { Copy-Item (Join-Path $rel $t) $atRoot -Force }   # never overwrite what is already set
}
Write-Host "dev copies:    $($front -join ', ') + join_host.txt, username.txt at the repo root"

Write-Host "release ready: $rel"
Write-Host "zip ready:     $zip  ($zipMB MB)"
