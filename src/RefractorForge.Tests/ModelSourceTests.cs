using System.Net;
using System.Text;
using RefractorForge.Formats.Online;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The model browser's libraries, against canned answers shaped like the real ones (Poly Haven's captured 2026-11,
/// Poly Pizza's from its developer's own Unity demo) - no network in a test run.
/// </summary>
public class ModelSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rf_models_" + Guid.NewGuid().ToString("N")[..8]);
    public ModelSourceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    /// <summary>Answers by URL and remembers every request, headers included.</summary>
    private sealed class FakeHttp : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly List<HttpRequestMessage> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var url = request.RequestUri!.AbsoluteUri;   // escaped, as sent (ToString() would turn %20 back into a space)
            return Task.FromResult(Routes.TryGetValue(url, out var r) ? r() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        public void Json(string url, string body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        public void Bytes(string url, byte[] body) => Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private const string PhCatalog = """
        {
          "ArmChair_01": { "name": "Arm Chair 01", "type": 2, "authors": { "Kirill Sannikov": "All" },
            "categories": ["furniture", "seating"], "tags": ["gothic", "vintage", "chair", "wood"],
            "polycount": 5626, "download_count": 33264, "dimensions": [848.43, 765.76, 1065.09],
            "thumbnail_url": "https://cdn.polyhaven.com/asset_img/thumbs/ArmChair_01.png?width=256&height=256&format=webp&v=abbc4732" },
          "boulder_01": { "name": "Boulder 01", "type": 2, "authors": { "Rico Cilliers": "All", "Dario Barresi": "Photos" },
            "categories": ["nature", "rocks"], "tags": ["stone", "rock"], "polycount": 12000, "download_count": 90000 },
          "wooden_crate": { "name": "Wooden Crate", "type": 2, "authors": { "James Ray Cock": "All" },
            "categories": ["props"], "tags": ["box", "wood"], "polycount": 300, "download_count": 1000 }
        }
        """;

    [Fact]
    public async Task Poly_Haven_searches_its_catalogue_by_words_and_popularity()
    {
        var fake = new FakeHttp();
        fake.Json("https://api.polyhaven.com/assets?type=models", PhCatalog);
        var src = new PolyHavenSource(ModelHttp.Create(fake));

        var wood = await src.SearchAsync("wood", 0, new ModelFilter(), default);
        Assert.Equal(new[] { "ArmChair_01", "wooden_crate" }, wood.Hits.Select(h => h.Id));   // most downloaded first
        var all = await src.SearchAsync("", 0, new ModelFilter(), default);
        Assert.Equal(3, all.Total);
        Assert.Equal("boulder_01", all.Hits[0].Id);
        Assert.Single((await src.SearchAsync("rock stone", 0, new ModelFilter(), default)).Hits);
        Assert.Empty((await src.SearchAsync("rock chair", 0, new ModelFilter(), default)).Hits);   // every word must match
        Assert.Single((await src.SearchAsync("", 0, new ModelFilter(Category: "seating"), default)).Hits);
        Assert.Single(fake.Requests);                                                             // the catalogue is fetched once

        var chair = wood.Hits[0];
        Assert.Equal("Arm Chair 01", chair.Title);
        Assert.Equal("Kirill Sannikov", chair.Author);
        Assert.Equal("CC0 1.0", chair.License);
        Assert.Equal("https://polyhaven.com/a/ArmChair_01", chair.PageUrl);
        Assert.Equal(5626, chair.Triangles);
        Assert.Equal(1.065f, chair.SizeMeters![2], 3);                                            // millimetres -> metres
        Assert.Contains("format=png", chair.ThumbnailUrl);                                        // WebP asked for as PNG
        Assert.Contains("v=abbc4732", chair.ThumbnailUrl);
        Assert.Equal("Rico Cilliers, Dario Barresi", all.Hits[0].Author);
        Assert.Contains("RefractorForge", fake.Requests[0].Headers.UserAgent.ToString());          // the API terms ask for it
    }

    [Fact]
    public async Task Poly_Haven_downloads_only_what_the_importer_reads_and_reuses_the_cache()
    {
        var fake = new FakeHttp();
        fake.Json("https://api.polyhaven.com/assets?type=models", PhCatalog);
        const string dl = "https://dl.polyhaven.org/file/ph-assets/Models/";
        fake.Json("https://api.polyhaven.com/files/ArmChair_01", "{\"gltf\":{\"2k\":{},\"1k\":{\"gltf\":{\"url\":\"" + dl + "gltf/1k/ArmChair_01/ArmChair_01_1k.gltf\",\"size\":-1," +
            "\"include\":{\"ArmChair_01.bin\":{\"url\":\"" + dl + "gltf/4k/ArmChair_01/ArmChair_01.bin\",\"size\":4}," +
            "\"textures/Armchair_01_diff_1k.jpg\":{\"url\":\"" + dl + "jpg/1k/ArmChair_01/Armchair_01_diff_1k.jpg\",\"size\":3}," +
            "\"textures/Armchair_01_nor_gl_1k.jpg\":{\"url\":\"" + dl + "jpg/1k/ArmChair_01/Armchair_01_nor_gl_1k.jpg\",\"size\":3}}}}}}");
        fake.Json(dl + "gltf/1k/ArmChair_01/ArmChair_01_1k.gltf",
            "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"ArmChair_01.bin\",\"byteLength\":4}]," +
            "\"materials\":[{\"normalTexture\":{\"index\":1},\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}}}]," +
            "\"textures\":[{\"source\":0},{\"source\":1}],\"images\":[{\"uri\":\"textures/Armchair_01_diff_1k.jpg\"},{\"uri\":\"textures/Armchair_01_nor_gl_1k.jpg\"}]}");
        fake.Bytes(dl + "gltf/4k/ArmChair_01/ArmChair_01.bin", new byte[] { 1, 2, 3, 4 });
        fake.Bytes(dl + "jpg/1k/ArmChair_01/Armchair_01_diff_1k.jpg", new byte[] { 9, 9, 9 });
        var src = new PolyHavenSource(ModelHttp.Create(fake));
        var chair = (await src.SearchAsync("chair", 0, new ModelFilter(), default)).Hits[0];

        var reports = new List<(long, long)>();
        var d = await src.DownloadAsync(chair, _dir, null, new Progress<(long, long)>(reports.Add), default);

        Assert.Equal(Path.Combine(_dir, "polyhaven", "ArmChair_01", "1k", "ArmChair_01_1k.gltf"), d.ModelPath);   // the smallest resolution
        Assert.True(File.Exists(Path.Combine(_dir, "polyhaven", "ArmChair_01", "1k", "ArmChair_01.bin")));
        Assert.True(File.Exists(Path.Combine(_dir, "polyhaven", "ArmChair_01", "1k", "textures", "Armchair_01_diff_1k.jpg")));
        Assert.DoesNotContain(fake.Requests, r => r.RequestUri!.ToString().Contains("nor_gl"));                     // the normal map stays there
        Assert.Equal("Poly Haven", d.Credit.Source);

        int before = fake.Requests.Count;
        await src.DownloadAsync(chair, _dir, null, null, default);
        Assert.Equal(before + 1, fake.Requests.Count);   // only the file list again: every file is already in the cache
    }

    private const string PpSearch = """
        { "total": 3, "results": [
          { "ID": "abc123", "Title": "Pine Tree", "Attribution": "Pine Tree by Quaternius [CC0] via Poly Pizza",
            "Thumbnail": "https://static.poly.pizza/aaaa.webp", "Download": "https://static.poly.pizza/aaaa.glb",
            "Tri Count": 412, "Creator": { "Username": "Quaternius", "DPURL": "https://static.poly.pizza/q.webp" },
            "Category": "Nature", "Tags": ["tree", "pine"], "Licence": "CC0 1.0", "Animated": false },
          { "ID": "def456", "Title": "Jeep", "Attribution": "Jeep by Poly by Google [CC-BY] via Poly Pizza",
            "Thumbnail": "https://static.poly.pizza/bbbb.webp", "Download": "https://static.poly.pizza/bbbb.glb",
            "Tri Count": 2100, "Creator": { "Username": "Poly by Google" }, "Licence": "CC-BY 3.0", "Animated": true },
          { "ID": "ghi789", "Title": "Statue", "Download": "https://static.poly.pizza/cccc.glb",
            "Creator": { "Username": "Someone" }, "Licence": "CC-BY-ND 4.0" }
        ] }
        """;

    [Fact]
    public async Task Poly_Pizza_needs_a_key_and_leaves_out_models_that_may_not_be_changed()
    {
        var fake = new FakeHttp();
        fake.Json("https://api.poly.pizza/v1.1/search/pine%20tree?limit=32", PpSearch);
        string? key = null;
        var src = new PolyPizzaSource(ModelHttp.Create(fake), () => key);
        await Assert.ThrowsAsync<PolyPizzaSource.NoKeyException>(() => src.SearchAsync("pine tree", 0, new ModelFilter(), default));

        key = "secret-key";
        var page = await src.SearchAsync("pine tree", 0, new ModelFilter(), default);
        Assert.Equal(new[] { "abc123", "def456" }, page.Hits.Select(h => h.Id));         // the ND statue is left out
        Assert.Equal("secret-key", fake.Requests.Last().Headers.GetValues("x-auth-token").Single());
        var tree = page.Hits[0];
        Assert.Equal(("Pine Tree", "Quaternius", 412), (tree.Title, tree.Author, tree.Triangles));
        Assert.Equal("CC0 1.0", tree.License);
        Assert.Equal("https://poly.pizza/m/abc123", tree.PageUrl);
        Assert.Equal("Jeep by Poly by Google [CC-BY] via Poly Pizza", page.Hits[1].Attribution);
        Assert.Equal("https://creativecommons.org/licenses/by/3.0/", page.Hits[1].LicenseUrl);

        var still = await src.SearchAsync("pine tree", 0, new ModelFilter(AllowAnimated: false), default);
        Assert.Equal(new[] { "abc123" }, still.Hits.Select(h => h.Id));

        fake.Bytes("https://static.poly.pizza/aaaa.glb", new byte[] { 0x67, 0x6C, 0x54, 0x46 });
        var d = await src.DownloadAsync(tree, _dir, null, null, default);
        Assert.Equal(Path.Combine(_dir, "polypizza", "abc123", "abc123.glb"), d.ModelPath);
        Assert.True(File.Exists(d.ModelPath));
    }

    [Fact]
    public async Task A_refused_key_says_so()
    {
        var fake = new FakeHttp();
        fake.Routes["https://api.poly.pizza/v1.1/search/tree?limit=32"] = () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            { Content = new StringContent("{\"error\":\"You need an API key to do that dingus\"}") };
        var src = new PolyPizzaSource(ModelHttp.Create(fake), () => "wrong");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => src.SearchAsync("tree", 0, new ModelFilter(), default));
        Assert.Contains("API key", ex.Message);
    }

    [Theory]
    [InlineData("CC0 1.0", "CC0 1.0", true, false)]
    [InlineData("CC-BY 3.0", "CC-BY 3.0", false, false)]
    [InlineData("CC BY-NC 4.0", "CC-BY-NC 4.0", false, false)]
    [InlineData("CC-BY-ND", "CC-BY-ND", false, true)]
    [InlineData("CC-BY", "CC-BY", false, false)]
    public void Licences_are_read_as_the_libraries_spell_them(string raw, string name, bool cc0, bool nd)
    {
        var l = ModelLicense.Parse(raw);
        Assert.Equal((name, cc0, nd), (l.Name, l.Cc0, l.NoDerivatives));
    }

    [Fact]
    public void The_credits_file_keeps_one_entry_per_model()
    {
        var chair = new ModelCredit("Arm Chair 01", "Kirill Sannikov", "Poly Haven", "https://polyhaven.com/a/ArmChair_01",
                                    "CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/");
        var jeep = new ModelCredit("Jeep", "Poly by Google", "Poly Pizza", "https://poly.pizza/m/def456", "CC-BY 3.0",
                                   "https://creativecommons.org/licenses/by/3.0/", "Jeep by Poly by Google [CC-BY] via Poly Pizza");
        var t = ModelCredits.Add(null, "ArmChair_01", chair);
        t = ModelCredits.Add(t, "Jeep_1", jeep);
        t = ModelCredits.Add(t, "ArmChair_01", chair with { Title = "Arm Chair 01 (again)" });   // imported again: replaced

        Assert.Equal(new[] { "Jeep_1", "ArmChair_01" }, ModelCredits.Templates(t));
        Assert.Single(t.Split("Third-party models in this map"), s => s.Length == 0);              // the header once, at the top
        Assert.Contains("\"Arm Chair 01 (again)\" by Kirill Sannikov", t);
        Assert.DoesNotContain("\"Arm Chair 01\" by", t);
        Assert.Contains("Source:  Poly Pizza - https://poly.pizza/m/def456", t);
        Assert.Contains("Licence: CC-BY 3.0 - https://creativecommons.org/licenses/by/3.0/", t);
        Assert.Contains("Credit:  Jeep by Poly by Google [CC-BY] via Poly Pizza", t);
        Assert.DoesNotContain("\n\n\n", t.Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_server_cannot_write_outside_the_download_folder()
    {
        Assert.Throws<InvalidDataException>(() => ModelHttp.SafeCombine(_dir, "../evil.dll"));
        Assert.Throws<InvalidDataException>(() => ModelHttp.SafeCombine(_dir, "textures/../../evil.dll"));
        Assert.Equal(Path.Combine(_dir, "textures", "a.jpg"), ModelHttp.SafeCombine(_dir, "textures/a.jpg"));
        Assert.Equal("a_b", ModelHttp.SafeName("a/b"));
    }
}
