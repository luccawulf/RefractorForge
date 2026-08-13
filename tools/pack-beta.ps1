# Build the public BETA package: one self-contained .exe plus the loose content files the editor reads at runtime.
#
# Why this script exists rather than just `dotnet publish`: with PublishSingleFile the SDK resolves every Content
# item for publish (verified via ComputeResolvedFilesToPublishList - textures\surf00.bmp, RefractorForge.ico and
# refractorforgesplash.png all appear with correct RelativePath) but silently does not emit them, while brushes\,
# lang\, TerrainTextures\ and ffmpeg\ come through. Rather than ship a package whose terrain paint palette and
# splash are missing, the content is copied from the ordinary build output afterwards and the result is VERIFIED
# against that build output - the script fails loudly if anything the editor expects is absent.
#
# Development is unaffected: this only ever writes to bin\Publish and dist\.

param(
    [string]$Version  = "v0.9.0-beta",
    [switch]$IncludeFfmpeg,          # bundle the ~49 MB GPL ffmpeg build (enables .bik playback out of the box)
    [string]$FfmpegFrom = "C:\Users\lucas\Desktop\RefractorForge\RefractorForge\ffmpeg"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$proj    = "src\RefractorForge.Viewer"
$pubDir  = Join-Path $repo "$proj\bin\Publish\Beta"
$devDir  = Join-Path $repo "$proj\bin\Release\net8.0-windows"
$distDir = Join-Path $repo "dist"
$stage   = Join-Path $distDir "RefractorForge-$Version"

Write-Host "== building =="
dotnet build -c Release --nologo | Out-Null          # produces the ordinary output we copy content from
if (Test-Path $pubDir) { Remove-Item $pubDir -Recurse -Force }
dotnet publish $proj -c Release -p:PublishProfile=Beta --nologo | Out-Null

Write-Host "== staging =="
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $pubDir "*") $stage -Recurse -Force
Get-ChildItem $stage -Recurse -Filter *.pdb | Remove-Item -Force   # debug symbols do not belong in a release

# Content the single-file publish dropped: take it from the normal build output.
foreach ($item in @("textures", "RefractorForge.ico", "refractorforgesplash.png")) {
    $src = Join-Path $devDir $item
    if (Test-Path $src) { Copy-Item $src $stage -Recurse -Force }
    else { throw "missing from build output: $item" }
}

if ($IncludeFfmpeg) {
    if (-not (Test-Path (Join-Path $FfmpegFrom "ffmpeg.exe"))) { throw "no ffmpeg.exe under $FfmpegFrom" }
    Copy-Item $FfmpegFrom (Join-Path $stage "ffmpeg") -Recurse -Force
}

Write-Host "== verifying against the build output =="
# Every non-assembly file the editor reads beside the exe must be present in the package.
$expect = Get-ChildItem $devDir -Recurse -File | Where-Object {
    $_.Extension -notin ".dll", ".pdb", ".exe" -and
    $_.FullName -notlike "*\win-x64\*" -and $_.FullName -notlike "*\runtimes\*" -and
    $_.Name -notlike "*.deps.json" -and $_.Name -notlike "*.runtimeconfig.json"
} | ForEach-Object { $_.FullName.Substring($devDir.Length + 1) }

$missing = @()
foreach ($rel in $expect) { if (-not (Test-Path (Join-Path $stage $rel))) { $missing += $rel } }
if ($missing.Count -gt 0) {
    Write-Host "MISSING $($missing.Count) file(s):" -ForegroundColor Red
    $missing | Select-Object -First 20 | ForEach-Object { Write-Host "   $_" }
    throw "package is incomplete"
}
if (-not (Test-Path (Join-Path $stage "RefractorForge.Viewer.exe"))) { throw "no exe in package" }
$loose = (Get-ChildItem $stage -Filter *.dll -File).Count
if ($loose -ne 0) { throw "$loose loose DLL(s) beside the exe - single-file bundling did not take" }

$zip = Join-Path $distDir "RefractorForge-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

$n  = (Get-ChildItem $stage -Recurse -File).Count
$mb = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "`nOK  $n files, $mb MB staged, 0 loose DLLs, $($expect.Count) content file(s) verified"
Write-Host ("zip $zip  ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB))
