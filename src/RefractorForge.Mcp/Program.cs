using System.Text.Json;
using System.Text.Json.Nodes;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Terrain;

namespace RefractorForge.Mcp;

/// <summary>
/// MCP server entry point: registers the map-editing tools and runs the stdio loop. One <see cref="EditSession"/> is
/// held at a time (open_level swaps it). This is Phase 1 — a headless core; Phase 2 adds a live bridge so the same
/// tools stream edits into a running editor over the collaboration relay.
/// </summary>
internal static class Program
{
    private static EditSession? _session;
    private static EditSession Need() => _session ?? throw new InvalidOperationException("No level open — call open_level first.");

    private static int Main()
    {
        var server = new McpServer("refractorforge", "0.1.0");
        Register(server);
        server.Run();
        return 0;
    }

    private static void Register(McpServer s)
    {
        s.Add(new McpTool("open_level",
            "Open a Battlefield 1942/Vietnam level for editing, from either a packed .rfa or an EXTRACTED LEVEL FOLDER (what a RefractorForge project holds). Optionally pass patch .rfa paths that override it.",
            Schema(("path", "string", "Absolute path to the level .rfa, or to an extracted level folder", true),
                   ("patches", "string[]", "Optional patch .rfa paths (later overrides earlier)", false)),
            a =>
            {
                string path = S(a, "path");
                if (path.Length == 0) throw new ArgumentException("path is required");
                if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException($"no file or folder at '{path}'");
                var paths = new List<string> { path };
                paths.AddRange(Arr(a, "patches"));
                _session = EditSession.OpenRfa(paths.ToArray());
                return Info(_session);
            }));

        s.Add(new McpTool("level_info",
            "Summarize the currently open level (size, water level, object/template counts, undo depth).",
            Schema(), _ => Info(Need())));

        s.Add(new McpTool("attach_editor",
            "Attach to a RUNNING RefractorForge editor so edits appear in its 3D viewport immediately, instead of being written to a file. Turn on 'Collab > AI Bridge' in the editor first. A level must already be open here (open_level) because terrain heights for scatter/generate_city come from it - open the SAME level the editor has.",
            Schema(("host", "string", "Editor host (default 127.0.0.1)", false),
                   ("port", "number", "Relay port (default 7777)", false),
                   ("password", "string", "Only if the editor's session is password-protected. Sending one to an open session disconnects you.", false),
                   ("name", "string", "Display name shown to the other people in the session (default Claude)", false)),
            a =>
            {
                var s2 = Need();
                string host = S(a, "host", "127.0.0.1");
                int port = I(a, "port", 7777);
                string pass = S(a, "password");
                LiveBridge bridge;
                try { bridge = new LiveBridge(host, port, pass.Length == 0 ? null : pass, S(a, "name", "Claude")); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"could not reach an editor at {host}:{port} ({ex.Message}). In RefractorForge: Collab > AI Bridge.");
                }

                bridge.WaitSynced(TimeSpan.FromSeconds(10));
                if (bridge.Disconnected is { } why) { bridge.Dispose(); throw new InvalidOperationException(why); }

                s2.AttachLive(bridge);
                int live = s2.So.Objects.Count;
                double overlap = s2.LiveTemplateOverlap();
                var peers = bridge.PeerNames();
                var msg = $"attached to the editor at {host}:{port} as '{bridge.DisplayName}'. " +
                          $"Live document: {live} objects. Edits now appear in the editor as they happen.";
                if (peers.Count > 0) msg += $" Sharing with: {string.Join(", ", peers)}.";
                // A mismatched level would sample heights from the wrong terrain, so say so plainly rather than
                // quietly generating a city that floats or sinks.
                if (overlap < 0.5)
                    msg += $" WARNING: only {overlap:P0} of the editor's objects use templates from the level opened here - " +
                           "the editor probably has a DIFFERENT level open. Terrain heights will be wrong; re-open the matching level.";
                return msg;
            }));

        s.Add(new McpTool("detach_editor",
            "Disconnect from the running editor and go back to editing the local copy of the level.",
            Schema(), _ =>
            {
                var s2 = Need();
                if (!s2.IsLive) return "not attached to an editor";
                s2.DetachLive();
                return "detached; edits now apply to the local level again";
            }));

        s.Add(new McpTool("list_templates",
            "List the distinct object templates already placed in the level, with counts (a ready-made palette for scatter/generate_city). Optional substring filter.",
            Schema(("filter", "string", "Case-insensitive substring to match template names", false)),
            a =>
            {
                var s2 = Need(); string filter = S(a, "filter");
                var groups = s2.So.Objects.GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
                    .Where(g => filter.Length == 0 || g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(g => g.Count()).Take(300).ToList();
                if (groups.Count == 0) return "no matching templates";
                return string.Join("\n", groups.Select(g => $"{g.Count(),6}  {g.Key}"));
            }));

        s.Add(new McpTool("list_objects",
            "List placed objects (id, template, x/y/z), optionally filtered by template. Capped by 'max' (default 50).",
            Schema(("template", "string", "Only objects of this exact template", false),
                   ("max", "integer", "Maximum rows to return (default 50)", false)),
            a =>
            {
                var s2 = Need(); string tmpl = S(a, "template"); int max = Math.Clamp(I(a, "max", 50), 1, 2000);
                var q = s2.So.Objects.AsEnumerable();
                if (tmpl.Length > 0) q = q.Where(o => o.Template.Equals(tmpl, StringComparison.OrdinalIgnoreCase));
                var rows = q.Take(max).Select(o => $"{o.Id}  {o.Template}  {o.Position.X:0.#}/{o.Position.Y:0.#}/{o.Position.Z:0.#}").ToList();
                return rows.Count == 0 ? "no matching objects" : string.Join("\n", rows);
            }));

        s.Add(new McpTool("place_object",
            "Place one static object. y defaults to the terrain height at (x,z). Rotation is Euler degrees (yaw=around vertical).",
            Schema(("template", "string", "Object template name", true),
                   ("x", "number", "World X (metres, 0..worldSize)", true),
                   ("z", "number", "World Z (metres, 0..worldSize)", true),
                   ("y", "number", "World Y (metres); omit to snap to terrain", false),
                   ("yaw", "number", "Yaw degrees (default 0)", false),
                   ("pitch", "number", "Pitch degrees (default 0)", false),
                   ("roll", "number", "Roll degrees (default 0)", false),
                   ("avoidOverlap", "boolean", "Refuse if it would sit inside something already there, judged by the templates' real mesh footprints (default true)", false),
                   ("clearance", "number", "Extra metres to keep clear of other footprints (default 0)", false)),
            a =>
            {
                var s2 = Need(); string t = S(a, "template");
                if (t.Length == 0) throw new ArgumentException("template is required");
                float x = F(a, "x"), z = F(a, "z");
                float? y = Has(a, "y") ? F(a, "y") : null;
                string id = s2.PlaceObject(t, x, z, y, new Vec3(F(a, "yaw"), F(a, "pitch"), F(a, "roll")),
                                           B(a, "avoidOverlap", true), F(a, "clearance", 0f));
                return $"placed {t} as {id} at {x:0.#}/{(y ?? s2.HeightAt(x, z)):0.#}/{z:0.#}";
            }));

        s.Add(new McpTool("move_object", "Move an object to an absolute world position.",
            Schema(("id", "string", "Object id", true), ("x", "number", "World X", true), ("y", "number", "World Y", true), ("z", "number", "World Z", true)),
            a => { var s2 = Need(); string id = S(a, "id"); return s2.Move(id, new Vec3(F(a, "x"), F(a, "y"), F(a, "z"))) ? $"moved {id}" : $"no object '{id}'"; }));

        s.Add(new McpTool("rotate_object", "Rotate an object (Euler degrees, yaw=around vertical).",
            Schema(("id", "string", "Object id", true), ("yaw", "number", "Yaw degrees", true), ("pitch", "number", "Pitch degrees", false), ("roll", "number", "Roll degrees", false)),
            a => { var s2 = Need(); string id = S(a, "id"); return s2.Rotate(id, new Vec3(F(a, "yaw"), F(a, "pitch"), F(a, "roll"))) ? $"rotated {id}" : $"no object '{id}'"; }));

        s.Add(new McpTool("scale_object", "Set an object's uniform scale.",
            Schema(("id", "string", "Object id", true), ("scale", "number", "Uniform scale (1 = default)", true)),
            a => { var s2 = Need(); string id = S(a, "id"); return s2.ScaleObj(id, F(a, "scale", 1f)) ? $"scaled {id}" : $"no object '{id}'"; }));

        s.Add(new McpTool("delete_object", "Delete an object by id.",
            Schema(("id", "string", "Object id", true)),
            a => { var s2 = Need(); string id = S(a, "id"); return s2.Delete(id) ? $"deleted {id}" : $"no object '{id}'"; }));

        s.Add(new McpTool("find_overlaps",
            "List placed objects whose footprints intersect - a house inside a house, a crate inside a wall. Footprints come from each template's real mesh, so a hut and a hangar are judged by their actual sizes rather than one shared guess. Worst first. Use it to check work after placing, then delete_object or move_object to fix what it finds.",
            Schema(("clearance", "number", "Also count pairs within this many extra metres (default 0)", false),
                   ("max", "number", "How many pairs to return (default 25)", false)),
            a =>
            {
                var s2 = Need();
                var hits = s2.FindOverlaps(F(a, "clearance", 0f), Math.Clamp(I(a, "max", 25), 1, 200));
                if (hits.Count == 0) return "no overlapping objects";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{hits.Count} overlapping pair(s), deepest first:");
                foreach (var (x, y, d) in hits)
                    sb.AppendLine($"  {d,5:0.#} m in:  {x.Template} ({x.Id}) {x.Position.X:0}/{x.Position.Z:0}"
                                  + $"   <->   {y.Template} ({y.Id}) {y.Position.X:0}/{y.Position.Z:0}");
                return sb.ToString();
            }));

        s.Add(new McpTool("scatter",
            "Randomly scatter objects across the whole map, constrained to a slope band, above water, and min-spaced. Returns how many landed.",
            Schema(("templates", "string[]", "Candidate template names", true),
                   ("count", "integer", "How many to attempt to place", true),
                   ("minSlope", "number", "Min slope degrees (default 0)", false),
                   ("maxSlope", "number", "Max slope degrees (default 30)", false),
                   ("avoidWater", "boolean", "Skip placements under water (default true)", false),
                   ("waterClearance", "number", "Metres above water required (default 0.5)", false),
                   ("spacing", "number", "Min metres between placements (default 8)", false),
                   ("seed", "integer", "RNG seed (default 1)", false),
                   ("edgeMargin", "number", "Keep this many metres from the map edge (default 0)", false),
                   ("minScale", "number", "Min per-object scale (default 1)", false),
                   ("maxScale", "number", "Max per-object scale (default 1)", false)),
            a =>
            {
                var s2 = Need(); var t = Arr(a, "templates");
                if (t.Count == 0) throw new ArgumentException("templates[] is required");
                int count = I(a, "count", 0);
                int placed = s2.Scatter(t, count, F(a, "minSlope", 0f), F(a, "maxSlope", 30f),
                    B(a, "avoidWater", true), F(a, "waterClearance", 0.5f), F(a, "spacing", 8f),
                    I(a, "seed", 1), F(a, "edgeMargin", 0f), F(a, "minScale", 1f), F(a, "maxScale", 1f), B(a, "avoidOverlap", true), F(a, "clearance", 0f));
                return $"scattered {placed}/{count} objects (the rest were rejected by water/slope/spacing)";
            }));

        s.Add(new McpTool("generate_city",
            "Procedurally build a grid city in a world-space rectangle: a street grid lined with buildings, snapped to terrain, avoiding water/cliffs. If no palette is given, the level's existing templates are used.",
            Schema(("minX", "number", "Area min X (metres)", true), ("minZ", "number", "Area min Z (metres)", true),
                   ("maxX", "number", "Area max X (metres)", true), ("maxZ", "number", "Area max Z (metres)", true),
                   ("palette", "string[]", "Building template names (default: templates already in the level)", false),
                   ("seed", "integer", "RNG seed (default 1)", false),
                   ("blockSize", "number", "City block size in metres (default 64)", false),
                   ("roadWidth", "number", "Street width in metres (default 8)", false),
                   ("setback", "number", "Building setback from the street in metres (default 4)", false),
                   ("lotWidth", "number", "Spacing between buildings along a street in metres (default 16)", false),
                   ("spacing", "number", "Min metres between buildings (default 10)", false),
                   ("maxSlope", "number", "Max build slope degrees (default 18)", false),
                   ("avoidWater", "boolean", "Skip lots under water (default true)", false),
                   ("waterClearance", "number", "Metres above water required (default 0.5)", false),
                   ("minScale", "number", "Min building scale (default 1)", false),
                   ("maxScale", "number", "Max building scale (default 1)", false)),
            a =>
            {
                var s2 = Need();
                var palette = Arr(a, "palette");
                if (palette.Count == 0) palette = s2.PlacedTemplates().ToList();
                if (palette.Count == 0) throw new InvalidOperationException("no palette: pass palette[] (the level has no placed templates to borrow)");
                var layout = s2.GenerateCity(F(a, "minX"), F(a, "minZ"), F(a, "maxX"), F(a, "maxZ"), palette,
                    I(a, "seed", 1), F(a, "blockSize", 64f), F(a, "roadWidth", 8f), F(a, "setback", 4f),
                    F(a, "lotWidth", 16f), F(a, "spacing", 10f), F(a, "maxSlope", 18f),
                    B(a, "avoidWater", true), F(a, "waterClearance", 0.5f), F(a, "minScale", 1f), F(a, "maxScale", 1f), B(a, "avoidOverlap", true), F(a, "clearance", 0f));
                return $"built city: {layout.Buildings.Count} buildings on a {layout.BlocksX}x{layout.BlocksZ} block grid " +
                       $"({layout.Roads.Count} streets) from {palette.Count} templates. Streets are generated as data; road texturing is a follow-up.";
            }));

        s.Add(new McpTool("paint_road",
            "Paint a road into the terrain's ground texture along a curve through the given points. Uses the editor's own centripetal Catmull-Rom spline, so it bends the way the Road tool would, with soft shoulders that blend into the ground. Needs attach_editor: the road appears in the viewport immediately and is written into the terrain tiles when the editor saves. Anyone who joins the session LATER will not see it until they reload.",
            Schema(("points", "string", "The centreline as \"x,z\" pairs, e.g. [\"1760,1705\", \"1200,1200\", \"400,420\"]. At least two.", true),
                   ("width", "number", "Road width in metres (default 8)", false),
                   ("color", "string", "Road colour as \"r,g,b\" 0-255 (default a pale dirt track)", false),
                   ("seed", "number", "Varies the surface grain (default 1)", false)),
            a =>
            {
                var s2 = Need();
                var pts = new List<(float X, float Z)>();
                foreach (var raw in Arr(a, "points"))
                {
                    var bits = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (bits.Length < 2) continue;
                    if (float.TryParse(bits[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)
                     && float.TryParse(bits[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pz))
                        pts.Add((px, pz));
                }
                if (pts.Count < 2) throw new ArgumentException("points needs at least two \"x,z\" entries");

                (byte, byte, byte) col = (196, 176, 140);   // pale dirt, close to worn desert track
                var cs = S(a, "color");
                if (cs.Length > 0)
                {
                    var cb = cs.Split(',', StringSplitOptions.TrimEntries);
                    if (cb.Length >= 3 && byte.TryParse(cb[0], out var cr) && byte.TryParse(cb[1], out var cg) && byte.TryParse(cb[2], out var cbl))
                        col = (cr, cg, cbl);
                }

                var (n, len, pw, ph) = s2.PaintRoad(pts, F(a, "width", 8f), col, I(a, "seed", 1));
                return $"painted a {len:0} m road through {pts.Count} point(s) ({n} curve samples, {pw}x{ph} patch). "
                     + "It is in the editor now and will be written into the terrain tiles on save.";
            }));

        s.Add(new McpTool("raise_mountain",
            "Sculpt a mountain into the terrain, centred on a world position. It is ADDITIVE - the existing ground is kept and the mountain laid on top, fading to nothing at the rim so it blends in. Ridges and fractal detail make it read as rock rather than as a cone. Applies live when attached to a running editor.",
            Schema(("x", "number", "Centre X in world metres", true),
                   ("z", "number", "Centre Z in world metres", true),
                   ("radius", "number", "Footprint radius in metres (default 250)", false),
                   ("height", "number", "How far the summit rises above the ground it sits on, in metres (default 80)", false),
                   ("roughness", "number", "0 = a smooth hill, 1 = broken and rocky (default 0.35)", false),
                   ("ridges", "number", "Spurs running down from the summit; 0 keeps the footprint round (default 5)", false),
                   ("seed", "number", "Change for a different mountain of the same size (default 1)", false)),
            a =>
            {
                var s2 = Need();
                float radius = F(a, "radius", 250f);
                float height = F(a, "height", 80f);
                if (radius <= 0f) throw new ArgumentException("radius must be positive");
                if (height <= 0f) throw new ArgumentException("height must be positive");
                var (peak, cells) = s2.RaiseMountain(F(a, "x"), F(a, "z"), radius, height,
                    I(a, "seed", 1), F(a, "roughness", 0.35f), I(a, "ridges", 5));
                return $"raised a mountain at {F(a, "x"):0}/{F(a, "z"):0}: radius {radius:0} m, {height:0} m of rise, " +
                       $"summit now {peak:0.#} m above sea level ({cells} heightmap cells rewritten)" +
                       (s2.IsLive ? ". The editor's terrain has been updated." : ".");
            }));

        s.Add(new McpTool("list_catalog",
            "List EVERY object template the mod can place - not just the ones already in this level. This is the palette to choose from; list_templates only shows what the level already uses. Filter by name and/or category (categories come from the archive folders, e.g. 'Land Vehicles', 'Buildings'). Always check here before inventing a template name.",
            Schema(("filter", "string", "Case-insensitive substring of the template name", false),
                   ("category", "string", "Case-insensitive substring of the category", false),
                   ("max", "number", "Maximum entries to return (default 200)", false)),
            a =>
            {
                var s2 = Need();
                var lib = s2.Catalog;
                if (lib is null)
                    return "no catalog available - the level was not opened from inside a mod tree " +
                           "(<game>/Mods/<mod>/Archives/...), so the mod's object archives could not be found. " +
                           "list_templates still shows what this level already places.";

                string nameF = S(a, "filter"), catF = S(a, "category");
                int max = Math.Clamp(I(a, "max", 200), 1, 2000);
                var cats = lib.CategoryOf;

                string Cat(string t) => cats.TryGetValue(StripLod(t).ToLowerInvariant(), out var c) ? c : "Other";

                var names = lib.AssembledTemplateNames
                    .Concat(lib.MeshBaseNames.Select(StripMesh))
                    .Where(n => n.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                var rows = names
                    .Select(n => (Name: n, Category: Cat(n)))
                    .Where(r => (nameF.Length == 0 || r.Name.Contains(nameF, StringComparison.OrdinalIgnoreCase))
                             && (catF.Length == 0 || r.Category.Contains(catF, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(r => r.Category, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (rows.Count == 0) return "nothing in the catalog matches that filter";
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"{rows.Count} template(s) available" + (rows.Count > max ? $", showing {max}" : "") + ":");
                foreach (var g in rows.Take(max).GroupBy(r => r.Category))
                {
                    sb.AppendLine($"  [{g.Key}]");
                    foreach (var r in g) sb.AppendLine($"     {r.Name}");
                }
                if (rows.Count > max) sb.AppendLine($"  ... {rows.Count - max} more (narrow it with filter/category)");
                return sb.ToString();
            }));

        s.Add(new McpTool("terrain_at",
            "What the ground is doing at a world position: height, slope, whether it is under water, and the painted material. Use this before placing anything that cares about the ground.",
            Schema(("x", "number", "World X in metres", true),
                   ("z", "number", "World Z in metres", true)),
            a =>
            {
                var s2 = Need();
                float x = F(a, "x"), z = F(a, "z");
                var t = s2.Probe(x, z);
                return $"{x:0}/{z:0}: ground {t.Height:0.#} m, slope {t.SlopeDeg:0.#} deg, " +
                       (t.UnderWater ? $"UNDER WATER by {t.DepthBelowWater:0.#} m" : "dry") +
                       (t.Material >= 0 ? $", material {t.Material}" : "") +
                       $" (water line {s2.Cfg.WaterLevel:0.#} m)";
            }));

        s.Add(new McpTool("find_flat_area",
            "Find ground flat and dry enough to build on, best first. Use this to choose WHERE to put a village or a base - placing without it is guesswork, and a settlement generated across a hillside comes out terraced. Height spread is the number that matters: it is how far the ground rises and falls across the patch.",
            Schema(("radius", "number", "How much flat ground is needed, in metres (default 100)", false),
                   ("maxSlope", "number", "What counts as steep, degrees (default 12)", false),
                   ("maxSteepFraction", "number", "How much of the patch may be steeper than maxSlope and still pass, 0-1 (default 0.05). A field crossed by one ditch or hedgerow bank scores near zero; judging by the single steepest cell would reject it.", false),
                   ("maxSpread", "number", "Largest height difference tolerated across the patch, metres (default 6)", false),
                   ("avoidWater", "boolean", "Reject anything under or near the water line (default true)", false),
                   ("waterClearance", "number", "Metres of clearance above the water line (default 1)", false),
                   ("clearOfObjects", "boolean", "Keep away from objects already placed (default true)", false),
                   ("max", "number", "How many sites to return (default 6)", false),
                   ("minX", "number", "Restrict the search area", false),
                   ("minZ", "number", "Restrict the search area", false),
                   ("maxX", "number", "Restrict the search area", false),
                   ("maxZ", "number", "Restrict the search area", false)),
            a =>
            {
                var s2 = Need();
                float radius = F(a, "radius", 100f);
                if (radius <= 0f) throw new ArgumentException("radius must be positive");
                float slopeLim = F(a, "maxSlope", 12f), spreadLim = F(a, "maxSpread", 6f);
                float steepLim = Math.Clamp(F(a, "maxSteepFraction", 0.05f), 0f, 1f);
                var sites = s2.FindSites(radius, slopeLim, spreadLim,
                    B(a, "avoidWater", true), F(a, "waterClearance", 1f), Math.Clamp(I(a, "max", 6), 1, 50),
                    B(a, "clearOfObjects", true),
                    F(a, "minX", 0f), F(a, "minZ", 0f), F(a, "maxX", s2.Cfg.WorldSize), F(a, "maxZ", s2.Cfg.WorldSize),
                    steepLim);

                if (sites.Count == 0)
                    return $"nowhere on this map has {radius:0} m of dry ground in one piece" +
                           (B(a, "clearOfObjects", true) ? " clear of what is already placed" : "") +
                           ". Try a smaller radius, or clearOfObjects false.";

                bool anyPass = sites.Any(b => SiteFinder.Meets(b, spreadLim, steepLim));

                var sb = new System.Text.StringBuilder();
                sb.AppendLine(anyPass
                    ? $"{sites.Count} site(s), flattest first (radius {radius:0} m):"
                    : $"NOTHING meets those limits (spread <= {spreadLim:0.#} m, at most {steepLim:P0} steeper than " +
                      $"{slopeLim:0.#} deg). The flattest ground of radius {radius:0} m is below - decide whether it " +
                      "is good enough, or search a smaller radius:");
                foreach (var b in sites)
                    sb.AppendLine($"   {b.X:0}/{b.Z:0}  ground {b.Height:0.#} m, spread {b.HeightSpread:0.#} m, "
                                  + $"{b.SteepFraction:P0} steep (worst {b.MaxSlopeDeg:0.#} deg)"
                                  + (SiteFinder.Meets(b, spreadLim, steepLim) ? "" : "   (over the limit)"));
                return sb.ToString();
            }));

        s.Add(new McpTool("render_map",
            "Render the level top-down as an image: terrain colour, hill shading, a dot for every placed object, and a 256 m coordinate grid. LOOK at this before deciding where to build - it shows where the open ground, water and existing settlements are in a way no list of coordinates does.",
            Schema(("size", "number", "Image edge in pixels, 256-2048 (default 768)", false),
                   ("grid", "boolean", "Draw the coordinate grid (default true)", false)),
            a =>
            {
                var s2 = Need();
                int size = Math.Clamp(I(a, "size", 768), 256, 2048);
                var png = s2.RenderMap(size, null, B(a, "grid", true));
                float ws = s2.Cfg.WorldSize;
                return new ToolResult(
                    $"{s2.Name}, {ws:0} m across, {s2.So.Objects.Count} objects. North (+Z) is up, east (+X) is right; " +
                    $"grid lines every 256 m, so the image spans 0..{ws:0} on both axes. Water line {s2.Cfg.WaterLevel:0.#} m.",
                    png);
            }));

        s.Add(new McpTool("set_water_level", "Set the level's water level (metres). Saved by patching Terrain.con.",
            Schema(("meters", "number", "Water level in metres", true)),
            a => { var s2 = Need(); float m = F(a, "meters"); s2.SetWaterLevel(m); return $"water level set to {m:0.##} m"; }));

        s.Add(new McpTool("undo", "Undo the last edit.", Schema(),
            _ => { var s2 = Need(); return s2.Undo() ? $"undone (undo depth {s2.UndoDepth})" : "nothing to undo"; }));
        s.Add(new McpTool("redo", "Redo the last undone edit.", Schema(),
            _ => { var s2 = Need(); return s2.Redo() ? $"redone (undo depth {s2.UndoDepth})" : "nothing to redo"; }));

        s.Add(new McpTool("save_level",
            "Save the edits. A level opened from an extracted FOLDER is written back in place as loose files and needs no path. A level opened from a .rfa is repacked to the given output path, overriding only the changed files and leaving the source archive untouched.",
            Schema(("path", "string", "Output .rfa path. Required for a .rfa session; ignored for a folder session.", false)),
            a =>
            {
                var s2 = Need(); string path = S(a, "path");
                if (s2.IsFolder)
                {
                    var w = s2.Save(null);
                    return $"saved {w.Count} edited file(s) into the level folder {s2.SourceRfa}";
                }
                if (path.Length == 0) throw new ArgumentException("path is required when the level was opened from a .rfa");
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (dir != null) Directory.CreateDirectory(dir);
                var names = s2.Save(path);
                return $"saved {names.Count} edited file(s) into {path}";
            }));
    }

    /// <summary>"sheridan_m1.sm" -> "sheridan_m1". The catalog wants template names, not file names.</summary>
    private static string StripMesh(string meshFile)
    {
        var n = meshFile;
        int dot = n.LastIndexOf('.');
        if (dot > 0) n = n[..dot];
        return n;
    }

    /// <summary>Drop a trailing LOD suffix so a name matches the category index, which is keyed LOD-stripped.</summary>
    private static string StripLod(string name)
    {
        for (int i = 1; i <= 3; i++)
            if (name.EndsWith("_m" + i, StringComparison.OrdinalIgnoreCase)) return name[..^3];
        return name;
    }

    private static string Info(EditSession s)
    {
        var text = $"{s.Name}: worldSize {s.Cfg.WorldSize} m, materialSize {s.Cfg.MaterialSize}, waterLevel {s.Cfg.WaterLevel:0.##} m, " +
                   $"{s.So.Objects.Count} objects, {s.PlacedTemplates().Count} distinct templates. Undo depth {s.UndoDepth}.";
        if (s.Live is { } b)
            text += b.Disconnected is { } why
                ? $" LIVE LINK LOST ({why}) - call attach_editor again."
                : $" LIVE: attached to the editor at {b.Host}:{b.Port} as '{b.DisplayName}'.";
        return text;
    }

    // ---- argument readers (operate on the tool-call 'arguments' object) ----
    private static bool Obj(JsonElement a) => a.ValueKind == JsonValueKind.Object;
    private static bool Has(JsonElement a, string k) => Obj(a) && a.TryGetProperty(k, out _);
    private static double Num(JsonElement a, string k, double def) =>
        Obj(a) && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : def;
    private static float F(JsonElement a, string k, float def = 0f) => (float)Num(a, k, def);
    private static int I(JsonElement a, string k, int def = 0) => (int)Math.Round(Num(a, k, def));
    private static string S(JsonElement a, string k, string def = "") =>
        Obj(a) && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : def;
    private static bool B(JsonElement a, string k, bool def) =>
        Obj(a) && a.TryGetProperty(k, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => def } : def;
    private static List<string> Arr(JsonElement a, string k)
    {
        var l = new List<string>();
        if (Obj(a) && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var e in v.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String) l.Add(e.GetString()!);
        return l;
    }

    // ---- JSON-Schema builder for a flat object of typed properties ----
    private static JsonObject Schema(params (string name, string type, string desc, bool req)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, desc, req) in props)
        {
            properties[name] = type == "string[]"
                ? new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = desc }
                : new JsonObject { ["type"] = type, ["description"] = desc };
            if (req) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }
}
