# RefractorForge MCP server — editing maps with an AI assistant

`RefractorForge.Mcp` is a [Model Context Protocol](https://modelcontextprotocol.io) server that exposes the
editor's map-editing core to an AI assistant. It lets you say *"line the north road with fishing huts"* or
*"build a village in the clearing east of the bridge"* and have the objects actually appear.

It runs in two modes:

- **Headless** — open a `.rfa` or an extracted level folder, edit it, save it. The editor need not be running.
- **Live** — attach to a **running RefractorForge**, in which case every edit appears in the 3D viewport as it
  happens, and the assistant sees what you place too. This is the interesting one.

## Setup

Build the solution, then register the server with your MCP client. This repo ships a `.mcp.json`, so Claude Code
picks it up automatically when run from the repo root — just approve it when prompted.

```bash
dotnet build -c Release
```

To register it elsewhere (or from another directory), the command is:

```bash
claude mcp add refractorforge -- dotnet "C:/path/to/RefractorForge/src/RefractorForge.Mcp/bin/Release/net8.0/RefractorForge.Mcp.dll"
```

## Live editing

1. Open your level in RefractorForge.
2. **Collab ▸ AI Bridge.** The status bar confirms `AI Bridge listening on 127.0.0.1:7777`.
3. Ask the assistant to `open_level` the same level, then `attach_editor`.

From then on, objects it places show up in your viewport immediately, and objects *you* place show up in its view
of the map. It is an ordinary peer in the collaboration session — the same machinery two humans use to edit
together — so you can both work at once, and your own undo, save and selection all behave normally.

**The bridge listens on 127.0.0.1 only.** Nothing is exposed to the network. (Regular `Collaborate...` hosting
still binds every interface, because that is the point of it.)

**Open the same level in both.** The assistant samples terrain heights from the level *it* opened, because the
collaboration protocol carries objects but not the heightmap. `attach_editor` compares the two and warns you if
they look like different maps, rather than quietly generating a village that floats.

**The editor owns the file.** `save_level` is refused while attached — press Ctrl+S in the editor as usual.

## Tools

| Tool | What it does |
| --- | --- |
| `open_level` | Open a `.rfa`, an extracted level folder, or a base plus patch archives |
| `attach_editor` / `detach_editor` | Attach to a running editor for live editing |
| `level_info` | World size, water level, object and template counts, undo depth, live status |
| `list_templates` | Distinct templates already placed, with counts — the level's own palette |
| `list_objects` | Placed objects, optionally filtered by template |
| `place_object` | One object at x/z (y follows the terrain unless given), with rotation |
| `move_object`, `rotate_object`, `scale_object`, `delete_object` | Edit by id |
| `scatter` | Random placement across the map, constrained by slope, water clearance and spacing |
| `generate_city` | A street grid over a world-space rectangle, with buildings lining each block |
| `set_water_level` | Water level in metres (applies live when attached) |
| `undo` / `redo` | One entry per tool call — a generated city undoes as one thing, not 123 |
| `save_level` | Folder sessions write back in place; `.rfa` sessions repack to a new path |

## Notes and limits

- **Ask for templates the level already has.** `list_templates` is the reliable palette. The protocol accepts any
  template name without validation, so an invented one enters the document and gets saved, but the editor will not
  find a mesh for it and it will not draw.
- **Template names cannot contain spaces.** The collab wire is space-delimited with fixed field positions. The
  bridge rejects such names rather than sending a corrupt op.
- **Live undo is a compensating edit, not a rewind.** Undoing an add sends a delete, which everyone in the session
  sees. That is the only honest thing a shared document can do.
- **Object ids are per-session.** They are not written to `StaticObjects.con`; ids from one session mean nothing in
  the next. Re-list rather than remembering them.
- **Roads are data, not paint.** `generate_city` returns street centrelines and places the buildings; texturing the
  roads onto the terrain is still a manual step.
