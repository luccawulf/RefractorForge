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
$devDir  = Join-Path $repo "$proj\bin\Release\net10.0-windows"
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

# Licence and docs, straight from the repo root. These are NOT build output, so nothing above would ever pull them
# in - and the zip is distributed standalone (ModDB, forums), where the GitHub repo is not there to supply them.
# RefractorForge is GPLv3: the licence text has to travel WITH the distribution, not just live next to the source.
# The user guide matters for the same reason - a download outside GitHub otherwise arrives with no documentation.
foreach ($doc in @("LICENSE.txt", "README.md", "USER_GUIDE.md")) {
    $src = Join-Path $repo $doc
    if (Test-Path $src) { Copy-Item $src $stage -Force }
    else { throw "missing from the repo root: $doc" }
}

# The MCP server, so an AI assistant can drive the editor straight out of the download. It is a SEPARATE exe (an
# MCP client spawns it and talks stdio), and it is published self-contained for the same reason the editor is: a
# modder should not have to install a .NET runtime. Without this the release's headline feature would only be
# usable by people who build from source.
Write-Host "== publishing the MCP server =="
$mcpPub = Join-Path $repo "src\RefractorForge.Mcp\bin\Publish\Beta"
if (Test-Path $mcpPub) { Remove-Item $mcpPub -Recurse -Force }
dotnet publish "src\RefractorForge.Mcp" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o $mcpPub --nologo | Out-Null
$mcpExe = Join-Path $mcpPub "RefractorForge.Mcp.exe"
if (-not (Test-Path $mcpExe)) { throw "the MCP server did not publish" }
Copy-Item $mcpExe $stage -Force
Copy-Item (Join-Path $repo "docs\MCP_SERVER.md") $stage -Force
# The archive tool is its own product with its own releases: see tools\pack-archive.ps1.

if ($IncludeFfmpeg) {
    if (-not (Test-Path (Join-Path $FfmpegFrom "ffmpeg.exe"))) { throw "no ffmpeg.exe under $FfmpegFrom" }
    # Copy the CONTENTS, not the folder: publish already created ffmpeg\ (for the notice), and Copy-Item -Recurse
    # onto an existing directory nests the source inside it as ffmpeg\ffmpeg\ - which the editor would never find.
    $dst = Join-Path $stage "ffmpeg"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $FfmpegFrom "*") $dst -Recurse -Force
    if (-not (Test-Path (Join-Path $dst "ffmpeg.exe"))) { throw "ffmpeg.exe did not land at ffmpeg\ffmpeg.exe" }
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
foreach ($must in @("RefractorForge.Mcp.exe", "MCP_SERVER.md")) {
    if (-not (Test-Path (Join-Path $stage $must))) { $missing += $must }
}
if ($missing.Count -gt 0) {
    Write-Host "MISSING $($missing.Count) file(s):" -ForegroundColor Red
    $missing | Select-Object -First 20 | ForEach-Object { Write-Host "   $_" }
    throw "package is incomplete"
}
if (-not (Test-Path (Join-Path $stage "RefractorForge.exe"))) { throw "no exe in package" }
# Runtime-loaded assets the editor resolves by exact relative path. The build-output comparison above cannot see
# these (ffmpeg's binaries are not in the repo, only its notice), so assert them explicitly.
foreach ($must in @("textures\surf00.bmp", "brushes\Round.bmp", "lang\ja.json", "refractorforgesplash.png",
                    "LICENSE.txt", "README.md", "USER_GUIDE.md")) {
    if (-not (Test-Path (Join-Path $stage $must))) { throw "package is missing $must" }
}
# A GPLv3 package that ships an empty or wrong-licence file is worse than one that ships none, so check the text.
$lic = Get-Content (Join-Path $stage "LICENSE.txt") -Raw
if ($lic.Length -lt 10000 -or $lic -notmatch "GNU GENERAL PUBLIC LICENSE") { throw "LICENSE.txt does not look like the GPL text" }
if ($IncludeFfmpeg -and -not (Test-Path (Join-Path $stage "ffmpeg\ffmpeg.exe"))) { throw "ffmpeg requested but not packaged" }
if (Get-ChildItem $stage -Recurse -Directory | Where-Object { $_.Name -eq "ffmpeg" -and $_.Parent.Name -eq "ffmpeg" }) {
    throw "ffmpeg was nested as ffmpeg\ffmpeg - the editor will not find it"
}
# The MANAGED assemblies must be bundled into the exe - that is what single-file buys. The few NATIVE libraries
# (glfw3, cimgui, NAudio's) stay loose on purpose: Silk.NET resolves them relative to the exe, and bundling them
# for self-extract made GlfwPlatform report itself "not applicable" so the editor never opened.
$managedLoose = Get-ChildItem $stage -Filter *.dll -File |
    Where-Object { $_.Name -like "RefractorForge.*" -or $_.Name -like "System.*" -or $_.Name -like "Silk.NET.*" -or $_.Name -like "Microsoft.*" }
if ($managedLoose) { throw "managed assemblies left loose ($($managedLoose.Count)) - single-file bundling did not take: $($managedLoose[0].Name)" }
$natives = (Get-ChildItem $stage -Filter *.dll -File).Count
if ($natives -eq 0) { throw "no native libraries beside the exe - glfw3.dll etc. must ship loose or the window cannot be created" }
if (-not (Test-Path (Join-Path $stage "glfw3.dll"))) { throw "glfw3.dll missing - Silk.NET will fail with 'no suitable window platform'" }

$zip = Join-Path $distDir "RefractorForge-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip -CompressionLevel Optimal

$n  = (Get-ChildItem $stage -Recurse -File).Count
$mb = [math]::Round(((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "`nOK  $n files, $mb MB staged, $natives native DLL(s) + 0 managed, $($expect.Count) content file(s) verified"
Write-Host ("zip $zip  ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB))
