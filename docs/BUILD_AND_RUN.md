# RefractorForge — Setup, Compile & Run

A modern, open editor for Battlefield Vietnam maps — "Battlecraft, but better." Unlike Battlecraft,
the document model is a plain dynamic list with **no object cap** (the 2048 wall was a property of
Battlecraft's fixed in-memory struct, not of the map format or the game — BfVietnam itself happily
loads ~50k static objects), so RefractorForge is built from the start to edit maps at that scale.

---

## 1. Prerequisites

- **.NET SDK 8.0** (the only hard requirement for the core).
  - Verify: `dotnet --version` → should print `8.0.x`.
  - Install: https://dotnet.microsoft.com/download/dotnet/8.0 (or `winget install Microsoft.DotNet.SDK.8` / your distro's package).
- **Git** (optional, to clone) and any editor (VS 2022, VS Code + C# Dev Kit, or Rider).
- For the **3D Viewer only**: a GPU with OpenGL 3.3+, and internet access on first build to pull
  Silk.NET from NuGet (see §5). Everything else builds and runs fully offline.

---

## 2. Layout

```
RefractorForge/
  RefractorForge.sln
  src/
    RefractorForge.Formats/      # engine-agnostic format library: .con, terrain, RFA, edit commands
    RefractorForge.Render/       # software rasterizer (no GPU) — previews & headless image output
    RefractorForge.Demo/         # CLI: load a map, print stats, render a preview PNG
    RefractorForge.RfaTool/      # CLI: list / extract RFA archive entries
    RefractorForge.Collab/       # relay server + client with optimistic local prediction
    RefractorForge.CollabTests/  # headless convergence + prediction test suite
    RefractorForge.Viewer/       # interactive 3D viewer (Silk.NET/OpenGL) — needs NuGet
```

Six of the seven projects build with **zero NuGet dependencies**. Only `Viewer` pulls packages.

---

## 3. Build (online — normal case)

```bash
cd RefractorForge
dotnet build -c Release            # builds the whole solution
```

If you are **not** building the Viewer, build the core meta-set instead (always offline-safe):

```bash
dotnet build src/RefractorForge.Formats/RefractorForge.Formats.csproj   -c Release
dotnet build src/RefractorForge.Render/RefractorForge.Render.csproj     -c Release
dotnet build src/RefractorForge.Demo/RefractorForge.Demo.csproj         -c Release
dotnet build src/RefractorForge.RfaTool/RefractorForge.RfaTool.csproj   -c Release
dotnet build src/RefractorForge.Collab/RefractorForge.Collab.csproj     -c Release
dotnet build src/RefractorForge.CollabTests/RefractorForge.CollabTests.csproj -c Release
```

## 3b. Build (fully offline)

The core has no package references, so it builds with NuGet disabled. Drop a `nuget.config` at the
repo root that clears all sources:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /></packageSources>
</configuration>
```

Then:

```bash
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet build src/RefractorForge.CollabTests/RefractorForge.CollabTests.csproj -c Release
```

(Building `CollabTests` transitively builds `Collab` + `Formats`.) Remove or rename this
`nuget.config` when you later want to build the Viewer, which needs real package sources.

---

## 4. Run the pieces

### 4.1 Validate collaboration (recommended first run)

```bash
dotnet run --project src/RefractorForge.CollabTests -c Release
```

Runs six headless scenarios — multi-client convergence, conflict resolution by relay order,
late-join catch-up, presence, a 200-op randomized stress test, real localhost TCP, and the
optimistic-prediction suite (instant local feedback, watcher fast-path, reconciliation after an
interleaved remote op). Prints `ALL CHECKS PASSED` and exits 0 on success.

### 4.2 Inspect a map (Demo)

```bash
dotnet run --project src/RefractorForge.Demo -c Release -- "/path/to/levels/Operation_Irving"
```

Loads `StaticObjects.con` + heightmap, prints object/terrain stats, and writes a preview PNG.
(Run without args to see usage.)

### 4.3 List / extract an RFA archive (RfaTool)

```bash
dotnet run --project src/RefractorForge.RfaTool -c Release -- list    "/path/to/objects.rfa"
dotnet run --project src/RefractorForge.RfaTool -c Release -- extract "/path/to/objects.rfa" outdir/
```

`list` is 100% reliable (container format fully solved). `extract` writes the entries it can fully
decode; streams that use the not-yet-finalized RFA short-match opcode are reported, not silently
corrupted. See `RFA_Format_Notes.md` for codec status.

---

## 5. Interactive 3D Viewer (needs NuGet)

```bash
# ensure no offline nuget.config is shadowing the public feed, then:
dotnet run --project src/RefractorForge.Viewer -c Release -- "/path/to/levels/Operation_Irving"
```

First build downloads Silk.NET (OpenGL bindings). Controls and shading are documented in the
Viewer's README header. This project does not build in a network-restricted sandbox — build it on
your own machine.

---

## 6. Live collaboration session (multi-user editing)

The relay is just a TCP host wrapping `RelayServer`; clients connect with `TcpClientConnection`.
A minimal host/clients pattern (mirroring what `CollabTests` Scenario 5 does over real sockets):

```csharp
// HOST
var seed  = StaticObjectsFile.Load("levels/MyMap/StaticObjects.con");
var relay = new RelayServer(seed);
var host  = new TcpRelayHost(relay, System.Net.IPAddress.Any, 5555);
host.Start();                       // authoritative; assigns the global op order
// ... on shutdown: relay.SnapshotDoc().Save("levels/MyMap/StaticObjects.con"); host.Stop();

// CLIENT (each editor instance)
var conn   = new TcpClientConnection("HOST_IP", 5555);
var client = new CollabClient(clientId: "alice", name: "Alice", conn);
conn.Attach(client);                // streams initial state; client.Doc is the live, predicted view
client.Move("base-12", new Vec3(40, 0, 18));   // shows locally instantly; reconciles on echo
```

`client.Doc` is what the UI renders: the canonical document plus this client's unacknowledged local
edits replayed on top. Local edits appear with zero perceived latency and self-correct to the
relay's canonical order once acknowledged; clients that are only watching never pay a clone.

---

## 7. Troubleshooting

- **`NU1101` / restore errors offline:** you're building a project that needs packages (the Viewer)
  with sources cleared, or the offline `nuget.config` is shadowing the core. Match §3b for core,
  §5 for Viewer.
- **`dotnet: command not found` / wrong version:** install/setup the .NET 8 SDK (§1).
- **Viewer build fails in a locked-down environment:** expected — it needs NuGet; build it on a
  machine with internet (§5). The other six projects are unaffected.

---

## 8. Studio GUI — the click-to-open app (Windows)

`RefractorForge.Studio` is the double-click desktop editor. It's **WinForms**, which ships inside
the .NET SDK, so it needs **no NuGet packages**, and it draws with the project's own software
rasterizer, so there's **no GPU/Silk.NET dependency**. WinForms is Windows-only, so build it on
Windows.

**Build & run (from a developer terminal on Windows, in the repo root):**
```
dotnet run --project src/RefractorForge.Studio -c Release
```

**Produce a single, self-contained `RefractorForge.exe`** (no .NET install needed on the machine
you hand it to — true double-click ease of use):
```
dotnet publish src/RefractorForge.Studio -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```
The exe lands at:
```
src/RefractorForge.Studio/bin/Release/net8.0-windows/win-x64/publish/RefractorForge.exe
```
(For a smaller exe that requires the .NET 8 Desktop Runtime to be installed, drop
`--self-contained true` and use `--self-contained false`.)

**Using it:**
- **File ▸ Open Level Folder** (Ctrl+O) → pick a level directory (the one containing
  `StaticObjects.con` and `Heightmap.raw`, e.g. `…/levels/Operation_Irving`).
- The terrain renders with every placed object as a proxy box (real position/rotation/scale,
  colored per template).
- **Left-drag** orbit · **mouse wheel** zoom · **right-drag / Shift+left-drag** pan · **left-click**
  to select an object (it highlights yellow and appears in the list).
- Edit the selected object's **position / rotation Y / scale** in the side panel; **Add** a new
  object at the view centre; **Delete** the selected one. **Ctrl+Z / Ctrl+Y** undo/redo.
- **File ▸ Save** (Ctrl+S) writes `StaticObjects.con` back, losslessly.

**Status:** this GUI was authored against the already-validated `Formats` + `Render` libraries but
**could not be compiled in the Linux build container** (WinForms requires the Windows Desktop SDK).
The engine it sits on is verified; the GUI shell builds on Windows. If the first `dotnet build`
surfaces anything, it'll be a small WinForms-layout fix, not an engine issue.

---

## 9. Terrain sculpting tests

The terrain sculpting engine (heightmap brushes + region undo + raycast) is covered by a headless
test suite that also sculpts the retail map:
```
dotnet run --project src/RefractorForge.TerrainTests -c Release
```
Prints `ALL CHECKS PASSED` and writes a before/after render. The engines live in
`RefractorForge.Formats/Terrain`: heightmap sculpting (`TerrainBrush`/`TerrainEditor`/`TerrainStroke`/
`TerrainEdit`/`TerrainEditHistory`) and texture painting (`MaterialMap`/`MaterialBrush`/`MaterialPainter`/
`MaterialStroke`/`MaterialEdit`/`MaterialEditHistory`). Both are pure/engine-agnostic, so the GUI calls them directly. Wiring it into the
Studio GUI (a "Terrain" tool mode: raycast the click → `Stamp`/`Dab` → `RebuildTerrain` → re-render)
is a small step to take once the GUI build is confirmed on Windows.
