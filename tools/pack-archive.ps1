# Package the archive tool on its own: one self-contained .exe plus a readme, zipped under its own name and
# version line, staged wherever -Out points. It shares the RefractorForge source tree because it is built on the
# same RFA implementation, but it is its own program with its own releases.
#
# Only ever writes to the project's bin\Publish, dist\, and -Out.

param(
    [string]$Version = "v0.1.0-beta",
    [string]$Out = ""                 # folder to place the zip and an unpacked copy in; default dist\
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$proj    = "src\RefractorForge.Archive"
$pubDir  = Join-Path $repo "$proj\bin\Publish\Standalone"
$distDir = if ($Out.Length -gt 0) { $Out } else { Join-Path $repo "dist" }
$name    = "RefractorForgeArchive-$Version"
$stage   = Join-Path $distDir $name

Write-Host "== publishing =="
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $proj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $pubDir --nologo | Out-Null
$exe = Join-Path $pubDir "RefractorForgeArchive.exe"
if (-not (Test-Path $exe)) { throw "the archive tool did not publish" }

Write-Host "== staging =="
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item $exe $stage -Force
Copy-Item (Join-Path $repo "docs\ARCHIVE_TOOL.md") (Join-Path $stage "README.md") -Force
if (Test-Path (Join-Path $repo "LICENSE.txt")) { Copy-Item (Join-Path $repo "LICENSE.txt") $stage -Force }

# Verify: the exe must start and exit cleanly when asked for nothing (a broken single-file publish dies here).
$size = (Get-Item (Join-Path $stage "RefractorForgeArchive.exe")).Length
if ($size -lt 20MB) { throw "published exe is implausibly small ($size bytes) - not self-contained?" }

Write-Host "== zipping =="
$zip = Join-Path $distDir "$name-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host ("OK  {0}  ({1:N1} MB exe)" -f $stage, ($size / 1MB))
Write-Host ("zip {0}  ({1:N1} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
