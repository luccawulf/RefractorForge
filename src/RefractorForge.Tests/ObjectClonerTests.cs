using System.Text;
using System.Text.RegularExpressions;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Rfa;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>A clone meant to ship next to its donor must define NO name the donor defines - in any create form - and
/// every path it references must still resolve. Both are checked on a synthetic folder and, with an install, on every
/// retail vehicle of both games.</summary>
public class ObjectClonerTests
{
    private const string Objects =
        "ObjectTemplate.create PlayerControlObject Willy\r\n" +
        "ObjectTemplate.setNetworkableInfo WillyBodyInfo\r\n" +
        "ObjectTemplate.aiTemplate Willy\r\n" +
        "ObjectTemplate.setVehicleIcon \"Vehicle/Icon_willy.tga\"\r\n" +
        "ObjectTemplate.addTemplate lodWilly\r\n" +
        "ObjectTemplate.addTemplate SharedSeat\r\n" +                    // defined elsewhere: must stay
        "ObjectTemplate.createInvisible 1\r\n" +                          // a setter, not a definition
        "ObjectTemplate.create LodObject lodWilly\r\n" +
        "ObjectTemplate.lodSelector WillyLodSelector\r\n" +
        "LodSelectorTemplate.create DistCompareSelector2 WillyLodSelector\r\n" +
        "ObjectTemplate.create AnimatedBundle TrackLeft\r\n" +            // no donor name in it
        "ObjectTemplate.geometry TrackLeft_M1\r\n" +
        "ObjectTemplate.loadSoundScript Sounds/horn_willy.ssc\r\n" +
        "beginrem\r\nObjectTemplate.create SupplyDepot Ghost\r\nendrem\r\n" +
        "rem ObjectTemplate.create Bundle AlsoGhost\r\n";
    private const string Geometries =
        "GeometryTemplate.create AnimatedMesh TrackLeft_M1\r\n" +
        "GeometryTemplate.file Willy_Track_M1\r\n" +
        "GeometryTemplate.setSkin animations/tracks/TrackLeft.skn\r\n" +  // the template name appears in the path
        "GeometryTemplate.createSkeleton animations/tracks/TrackLeft.ske\r\n";
    private const string Network = "NetworkableInfo.createNewInfo WillyBodyInfo\r\nNetworkableInfo.setPredictionMode PMLinear\r\n";
    private const string Ai =
        "aiTemplatePlugIn.create Mobile WillyMobile\r\naiTemplatePlugIn.create Physical PhysicalLight\r\n" +
        "aiTemplate.create Willy\r\naiTemplate.addPlugIn WillyMobile\r\naiTemplate.addPlugIn PhysicalLight\r\n" +
        "weaponTemplate.create WillyGunAI\r\n";

    private static (string, string)[] Folder() => new[]
    {
        ("objects/Vehicles/Land/Willy/Objects.con", Objects),
        ("objects/Vehicles/Land/Willy/Geometries.con", Geometries),
        ("objects/Vehicles/Land/Willy/Network.con", Network),
        ("objects/Vehicles/Land/Willy/AI/Objects.con", Ai),
        ("objects/Vehicles/Land/Willy/Sounds/horn_willy.ssc", "newPatch\r\nload @ROOT/Sound/@RTD/horn.wav\r\n"),
    };

    [Fact]
    public void ConCreates_sees_every_create_form_and_nothing_else()
    {
        var all = ConCreates.Extract(Folder().Select(f => (f.Item1, f.Item2))).Select(c => $"{c.Family}:{c.Name}").ToList();
        Assert.Equal(new[]
        {
            "ObjectTemplate:Willy", "ObjectTemplate:lodWilly", "LodSelectorTemplate:WillyLodSelector", "ObjectTemplate:TrackLeft",
            "GeometryTemplate:TrackLeft_M1", "NetworkableInfo:WillyBodyInfo",
            "aiTemplatePlugIn:WillyMobile", "aiTemplatePlugIn:PhysicalLight", "aiTemplate:Willy", "weaponTemplate:WillyGunAI",
        }, all);
    }

    [Fact]
    public void Rename_all_leaves_no_donor_definition_and_keeps_paths_and_shared_names()
    {
        var plan = ObjectCloner.Build("Willy", "Jeep", Folder(), renameAll: true);
        string Text(string leaf) => plan.Files.Single(f => f.NewPath.EndsWith(leaf, StringComparison.OrdinalIgnoreCase)).Text;

        var donor = ConCreates.Extract(Folder().Select(f => (f.Item1, f.Item2))).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clone = ConCreates.Extract(plan.Files.Select(f => (f.NewPath, f.Text))).Select(c => c.Name).ToList();
        Assert.DoesNotContain(clone, donor.Contains);
        Assert.Equal(donor.Count, clone.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // "Willy" is two families

        var objects = Text("Jeep/Objects.con");
        Assert.Contains("ObjectTemplate.create PlayerControlObject Jeep\r\n", objects);
        Assert.Contains("setNetworkableInfo JeepBodyInfo", objects);
        Assert.Contains("addTemplate SharedSeat", objects);                        // used, not defined here
        Assert.Contains("ObjectTemplate.create AnimatedBundle Jeep_TrackLeft", objects);
        Assert.Contains("\"Vehicle/Icon_willy.tga\"", objects);                    // a path: untouched
        Assert.Contains("loadSoundScript Sounds/horn_willy.ssc", objects);          // file keeps its name...
        Assert.Contains(plan.Files, f => f.NewPath == "objects/Vehicles/Land/Jeep/Sounds/horn_willy.ssc"); // ...and exists
        Assert.Contains("ObjectTemplate.createInvisible 1", objects);

        var geo = Text("Jeep/Geometries.con");
        Assert.Contains("GeometryTemplate.create AnimatedMesh Jeep_TrackLeft_M1", geo); // renamed, so not defined twice...
        Assert.Contains("ObjectTemplate.geometry Jeep_TrackLeft_M1", objects);           // ...and its user follows
        Assert.Contains("setSkin animations/tracks/TrackLeft.skn", geo);            // resource command: untouched
        Assert.Contains("GeometryTemplate.file Willy_Track_M1", geo);                // still the donor's mesh
        Assert.Contains(plan.GeometryFiles, g => g.Template == "Jeep_TrackLeft_M1" && g.File == "Willy_Track_M1");

        var ai = Text("Jeep/AI/Objects.con");
        Assert.Contains("aiTemplatePlugIn.create Physical Jeep_PhysicalLight", ai);
        Assert.Contains("aiTemplate.addPlugIn Jeep_PhysicalLight", ai);
        Assert.Contains("weaponTemplate.create JeepGunAI", ai);
    }

    [Fact]
    public void Without_rename_all_only_donor_named_definitions_change()
    {
        var plan = ObjectCloner.Build("Willy", "Jeep", Folder());
        Assert.Contains("TrackLeft", ConCreates.Extract(plan.Files.Select(f => (f.NewPath, f.Text))).Select(c => c.Name));
        Assert.DoesNotContain("PhysicalLight", plan.Templates.Keys);
    }

    [Fact]
    public void The_run_chain_file_of_a_level_object_follows_its_folder()
    {
        var plan = ObjectCloner.Build("Crate", "Box", new[]
        {
            ("bf1942/levels/M/Objects/Crate/Crate.con", "run Objects\r\nrun Geometries\r\n"),
            ("bf1942/levels/M/Objects/Crate/Objects.con", "ObjectTemplate.create SimpleObject Crate\r\n"),
        });
        Assert.Contains(plan.Files, f => f.NewPath == "bf1942/levels/M/Objects/Box/Box.con");
        Assert.Equal("run Box/Box", ObjectCloner.RunLine(plan));
    }

    // ---- every retail vehicle -------------------------------------------------------------------------------

    /// <summary>Written independently of ConCreates on purpose: a broad create matcher and its own comment rules.</summary>
    private static List<string> IndependentCreates(string text)
    {
        var names = new List<string>();
        bool block = false;
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim('\r', ' ', '\t');
            if (block) { if (l.StartsWith("endrem", StringComparison.OrdinalIgnoreCase)) block = false; continue; }
            if (l.StartsWith("beginrem", StringComparison.OrdinalIgnoreCase)) { block = true; continue; }
            if (Regex.IsMatch(l, @"^rem(\s|$)", RegexOptions.IgnoreCase)) continue;
            var m = Regex.Match(l, @"^(\w+)\.create(NewInfo)?\s+(.+)$", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            var args = m.Groups[3].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            names.Add(m.Groups[1].Value.ToLowerInvariant() + ":" + (m.Groups[2].Success || args.Length == 1 ? args[0] : args[1]).ToLowerInvariant());
        }
        return names;
    }

    [InstallFact(Installs.Bf1942CleanArchives, Installs.BfvOriginalArchives)]
    public void Cloning_any_retail_vehicle_defines_nothing_twice_and_keeps_every_script_path()
    {
        int vehicles = 0;
        foreach (var archive in new[] { Path.Combine(Installs.Bf1942CleanArchives, "Objects.rfa"), Path.Combine(Installs.BfvOriginalArchives, "objects.rfa") })
        {
            if (!File.Exists(archive)) continue;
            var a = new RefractorFlatArchive(archive);
            var byFolder = a.Entries
                .Select(e => (e, parts: e.Name.Replace('\\', '/').Split('/')))
                .Where(x => x.parts.Length >= 5 && x.parts[1].Equals("Vehicles", StringComparison.OrdinalIgnoreCase)
                            && !x.parts[3].Equals("Common", StringComparison.OrdinalIgnoreCase)
                            && (x.e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase) || x.e.Name.EndsWith(".ssc", StringComparison.OrdinalIgnoreCase) || x.e.Name.EndsWith(".inc", StringComparison.OrdinalIgnoreCase)))
                .GroupBy(x => x.parts[3], StringComparer.OrdinalIgnoreCase);

            foreach (var folder in byFolder)
            {
                vehicles++;
                var files = folder.Select(x => (x.e.Name, Encoding.Latin1.GetString(a.Read(x.e)))).ToList();
                var plan = ObjectCloner.Build(folder.Key, "RdkClone", files, renameAll: true);

                var donor = files.SelectMany(f => IndependentCreates(f.Item2)).ToHashSet();
                var clone = plan.Files.SelectMany(f => IndependentCreates(f.Text)).ToList();
                var twice = clone.Where(donor.Contains).ToList();
                Assert.True(twice.Count == 0, $"{archive} {folder.Key}: clone re-defines {string.Join(", ", twice.Take(5))}");
                Assert.Equal(donor.Count, clone.Distinct().Count());

                // Every script path that resolved inside the donor folder resolves inside the clone.
                var cloneFiles = plan.Files.Select(f => f.NewPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var f in plan.Files)
                    foreach (Match m in Regex.Matches(f.Text, @"(?:loadSoundScript|include)\s+""?([^\s""]+)", RegexOptions.IgnoreCase))
                    {
                        var dir = f.NewPath[..f.NewPath.LastIndexOf('/')];
                        var target = Normalize(dir + "/" + m.Groups[1].Value.Replace('\\', '/'));
                        var donorTarget = Normalize(f.OldPath[..f.OldPath.LastIndexOf('/')] + "/" + m.Groups[1].Value.Replace('\\', '/'));
                        bool donorHad = files.Any(x => x.Name.Replace('\\', '/').Equals(donorTarget, StringComparison.OrdinalIgnoreCase));
                        if (donorHad) Assert.True(cloneFiles.Contains(target), $"{folder.Key}: {f.NewPath} loads {m.Groups[1].Value}, missing in the clone");
                    }
            }
        }
        Assert.True(vehicles > 20, $"only {vehicles} vehicle folders found");
    }

    private static string Normalize(string path)
    {
        var stack = new List<string>();
        foreach (var p in path.Split('/'))
        {
            if (p == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else if (p != "." && p.Length > 0) stack.Add(p);
        }
        return string.Join('/', stack);
    }
}
