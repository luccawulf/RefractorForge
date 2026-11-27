using System.Net.Http.Headers;
using System.Text.Json;
using RefractorForge.Formats.Mesh;

namespace RefractorForge.Formats.Online;

/// <summary>One model a library offers: what the browser shows and what the credit needs.</summary>
public sealed record ModelHit(
    string Source, string Id, string Title, string Author,
    string License, string? LicenseUrl, string PageUrl,
    string? ThumbnailUrl, int? Triangles, IReadOnlyList<string> Tags,
    bool Animated = false, string? Attribution = null, string? DownloadUrl = null,
    float[]? SizeMeters = null, long DownloadCount = 0, string? Category = null)
{
    public ModelCredit Credit => new(Title, Author, Source, PageUrl, License, LicenseUrl, Attribution);
}

/// <summary>Who made a model, where it came from and under what licence: the line that has to travel with the map.</summary>
public sealed record ModelCredit(string Title, string Author, string Source, string PageUrl,
                                 string License, string? LicenseUrl, string? Attribution = null);

/// <summary>A downloaded model, ready for the importer.</summary>
public sealed record ModelDownload(string ModelPath, ModelCredit Credit, ModelHit Hit);

/// <summary>One page of search results. <see cref="More"/> says whether asking for the next page can return anything.</summary>
public sealed record ModelPage(IReadOnlyList<ModelHit> Hits, int Total, bool More);

/// <summary>What the browser asks a library for.</summary>
public sealed record ModelFilter(bool Cc0Only = false, bool AllowAnimated = true, string? Category = null);

public interface IModelSource
{
    string Name { get; }
    Task<ModelPage> SearchAsync(string query, int page, ModelFilter filter, CancellationToken ct);
    /// <summary>Fetch the model (and only the files the importer reads) into <paramref name="cacheRoot"/>; a file
    /// already there at the right size is not fetched again.</summary>
    Task<ModelDownload> DownloadAsync(ModelHit hit, string cacheRoot, string? quality,
                                      IProgress<(long Done, long Total)>? progress, CancellationToken ct);
}

/// <summary>Creative Commons licence names as the libraries spell them, made comparable.</summary>
public static class ModelLicense
{
    public sealed record Info(string Name, string? Url, bool Cc0, bool Attribution, bool NoDerivatives, bool NonCommercial, bool ShareAlike);

    public static Info Parse(string? raw)
    {
        var s = (raw ?? "").Trim();
        var u = s.ToUpperInvariant().Replace('_', '-').Replace(' ', '-');
        string? version = null;
        foreach (var tok in s.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries))
            if (tok.Length >= 3 && char.IsDigit(tok[0]) && tok.Contains('.')) version = tok;
        if (u.Contains("CC0") || u.Contains("PUBLIC-DOMAIN") || u.Contains("PUBLICDOMAIN"))
            return new Info("CC0 1.0", "https://creativecommons.org/publicdomain/zero/1.0/", true, false, false, false, false);
        if (u.Contains("CC") && u.Contains("BY"))
        {
            bool nc = u.Contains("-NC"), nd = u.Contains("-ND"), sa = u.Contains("-SA");
            string code = "by" + (nc ? "-nc" : "") + (nd ? "-nd" : "") + (sa ? "-sa" : "");
            string name = "CC-" + code.ToUpperInvariant() + (version is null ? "" : " " + version);
            string? url = version is null ? null : $"https://creativecommons.org/licenses/{code}/{version}/";
            return new Info(name, url, false, true, nd, nc, sa);
        }
        return new Info(s.Length == 0 ? "unknown licence" : s, null, false, true, false, false, false);
    }
}

/// <summary>The HTTP client the libraries are called with. Poly Haven's API terms ask every client to name itself in
/// its User-Agent, so this one does - and nothing else about the user goes with a request.</summary>
public static class ModelHttp
{
    public const string UserAgent = "RefractorForge/1.0 (map editor model browser)";

    public static HttpClient Create(HttpMessageHandler? handler = null)
    {
        var c = handler is null ? new HttpClient() : new HttpClient(handler);
        c.Timeout = TimeSpan.FromSeconds(90);
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return c;
    }

    /// <summary>Download to <paramref name="dest"/> through a .part file, so a cancelled or broken transfer never
    /// leaves something that looks complete. A file already there at the expected size is kept.</summary>
    public static async Task Download(HttpClient http, string url, string dest, long expectedSize,
                                      Action<long>? onBytes, CancellationToken ct, IEnumerable<KeyValuePair<string, string>>? headers = null)
    {
        if (File.Exists(dest) && (expectedSize <= 0 || new FileInfo(dest).Length == expectedSize)) { onBytes?.Invoke(new FileInfo(dest).Length); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null) foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var part = dest + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                onBytes?.Invoke(n);
            }
        }
        File.Move(part, dest, overwrite: true);
    }

    /// <summary>A relative path from a server, refused if it would land outside <paramref name="root"/>.</summary>
    public static string SafeCombine(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/').TrimStart('/')));
        var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"The server named a file outside the download folder: {relative}");
        return full;
    }

    /// <summary>A file name made from an id the server chose.</summary>
    public static string SafeName(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray();
        var s = new string(chars).Trim('.');
        return s.Length == 0 ? "model" : s;
    }

    // ---- case-insensitive JSON, because the libraries' field names drift (Poly Pizza writes "Tri Count") ----

    private static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static JsonElement? Prop(JsonElement e, params string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var n in names)
        {
            var want = Norm(n);
            foreach (var p in e.EnumerateObject())
                if (Norm(p.Name) == want && p.Value.ValueKind != JsonValueKind.Null) return p.Value;
        }
        return null;
    }

    public static string? Str(JsonElement e, params string[] names) =>
        Prop(e, names) is { } v ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind is JsonValueKind.Number ? v.GetRawText() : null : null;

    public static long? Long(JsonElement e, params string[] names) =>
        Prop(e, names) is { } v ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
                                 : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var ls) ? ls : null : null;

    public static bool Bool(JsonElement e, params string[] names) =>
        Prop(e, names) is { } v && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "true"));

    public static List<string> Strings(JsonElement e, params string[] names)
    {
        var outp = new List<string>();
        if (Prop(e, names) is { ValueKind: JsonValueKind.Array } a)
            foreach (var x in a.EnumerateArray()) if (x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 } s) outp.Add(s);
        return outp;
    }
}

/// <summary>
/// Poly Haven (polyhaven.com): about five hundred CC0 models - scanned props, furniture, rocks, plants - with no key
/// and no account. The API serves the whole catalogue in one response, so it is fetched once and searched here; a
/// model is downloaded as glTF at 1k (or 2k) and only the files the importer reads come down: the .gltf, its .bin and
/// the base colour images. The API's terms ask that the user can see the models came from Poly Haven (the browser
/// says so) and that the client names itself (<see cref="ModelHttp.UserAgent"/>).
/// </summary>
public sealed class PolyHavenSource : IModelSource
{
    public const string Api = "https://api.polyhaven.com";
    public const int PageSize = 48;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<ModelHit>? _catalog;

    public PolyHavenSource(HttpClient http) => _http = http;
    public string Name => "Poly Haven";

    /// <summary>Every category the catalogue uses, most common first, once it has been fetched.</summary>
    public IReadOnlyList<string> Categories =>
        _catalog is null ? Array.Empty<string>()
        : _catalog.Where(h => h.Category is not null).GroupBy(h => h.Category!, StringComparer.OrdinalIgnoreCase)
                  .OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();

    private async Task<List<ModelHit>> Catalog(CancellationToken ct)
    {
        if (_catalog is not null) return _catalog;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_catalog is not null) return _catalog;
            var json = await _http.GetStringAsync(Api + "/assets?type=models", ct).ConfigureAwait(false);
            return _catalog = ParseCatalog(json);
        }
        finally { _gate.Release(); }
    }

    public static List<ModelHit> ParseCatalog(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var outp = new List<ModelHit>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var e = p.Value;
            if (e.ValueKind != JsonValueKind.Object) continue;
            if (ModelHttp.Long(e, "type") is long t && t != 2) continue;    // 2 = model
            var authors = ModelHttp.Prop(e, "authors") is { ValueKind: JsonValueKind.Object } a
                ? string.Join(", ", a.EnumerateObject().Select(x => x.Name)) : "Poly Haven";
            var cats = ModelHttp.Strings(e, "categories");
            var tags = ModelHttp.Strings(e, "tags").Concat(cats).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            float[]? size = null;
            if (ModelHttp.Prop(e, "dimensions") is { ValueKind: JsonValueKind.Array } dims && dims.GetArrayLength() == 3)
                size = dims.EnumerateArray().Select(d => d.ValueKind == JsonValueKind.Number ? (float)(d.GetDouble() / 1000.0) : 0f).ToArray();   // mm
            outp.Add(new ModelHit(
                Source: "Poly Haven", Id: p.Name, Title: ModelHttp.Str(e, "name") ?? p.Name, Author: authors,
                License: "CC0 1.0", LicenseUrl: "https://creativecommons.org/publicdomain/zero/1.0/",
                PageUrl: "https://polyhaven.com/a/" + Uri.EscapeDataString(p.Name),
                ThumbnailUrl: PngThumbnail(ModelHttp.Str(e, "thumbnail_url"), p.Name),
                Triangles: ModelHttp.Long(e, "polycount") is long pc ? (int)Math.Min(pc, int.MaxValue) : null,
                Tags: tags, SizeMeters: size, DownloadCount: ModelHttp.Long(e, "download_count") ?? 0,
                Category: cats.FirstOrDefault()));
        }
        return outp;
    }

    /// <summary>The catalogue's thumbnails are WebP; the same URL serves PNG when asked, which any decoder reads.</summary>
    public static string PngThumbnail(string? url, string id)
    {
        if (string.IsNullOrEmpty(url))
            return $"https://cdn.polyhaven.com/asset_img/thumbs/{Uri.EscapeDataString(id)}.png?width=256&height=256&format=png";
        if (url.Contains("format=", StringComparison.OrdinalIgnoreCase))
            return System.Text.RegularExpressions.Regex.Replace(url, "format=[a-zA-Z]+", "format=png");
        return url + (url.Contains('?') ? "&" : "?") + "format=png";
    }

    public async Task<ModelPage> SearchAsync(string query, int page, ModelFilter filter, CancellationToken ct)
    {
        var all = await Catalog(ct).ConfigureAwait(false);
        var hits = Filter(all, query, filter.Category);
        var slice = hits.Skip(page * PageSize).Take(PageSize).ToList();
        return new ModelPage(slice, hits.Count, (page + 1) * PageSize < hits.Count);
    }

    /// <summary>Every word of the query must appear in the name, the id, a tag or a category; the most downloaded
    /// come first (that is what people found useful).</summary>
    public static List<ModelHit> Filter(IEnumerable<ModelHit> all, string query, string? category)
    {
        var words = (query ?? "").ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return all.Where(h => category is null || h.Tags.Contains(category, StringComparer.OrdinalIgnoreCase))
                  .Where(h =>
                  {
                      var hay = (h.Title + " " + h.Id + " " + string.Join(" ", h.Tags)).ToLowerInvariant();
                      return words.All(w => hay.Contains(w, StringComparison.Ordinal));
                  })
                  .OrderByDescending(h => h.DownloadCount).ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
                  .ToList();
    }

    public async Task<ModelDownload> DownloadAsync(ModelHit hit, string cacheRoot, string? quality,
                                                   IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{Api}/files/{Uri.EscapeDataString(hit.Id)}", ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (ModelHttp.Prop(doc.RootElement, "gltf") is not { ValueKind: JsonValueKind.Object } gltf)
            throw new InvalidDataException($"Poly Haven offers no glTF for {hit.Title}.");
        // The resolution asked for, else the smallest there is: the engine's textures top out at 1024 anyway.
        var resolutions = gltf.EnumerateObject().Select(r => r.Name).ToList();
        string res = quality is not null && resolutions.Contains(quality, StringComparer.OrdinalIgnoreCase)
            ? resolutions.First(r => r.Equals(quality, StringComparison.OrdinalIgnoreCase))
            : resolutions.OrderBy(r => int.TryParse(r.TrimEnd('k', 'K'), out var k) ? k : 99).First();
        var entry = ModelHttp.Prop(gltf.GetProperty(res), "gltf") ?? throw new InvalidDataException("The glTF entry has no file.");
        string url = ModelHttp.Str(entry, "url") ?? throw new InvalidDataException("The glTF entry has no URL.");
        var include = ModelHttp.Prop(entry, "include") is { ValueKind: JsonValueKind.Object } inc
            ? inc.EnumerateObject().ToDictionary(p => p.Name.Replace('\\', '/'), p => (Url: ModelHttp.Str(p.Value, "url"), Size: ModelHttp.Long(p.Value, "size") ?? -1),
                                                 StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, (string? Url, long Size)>(StringComparer.OrdinalIgnoreCase);

        string folder = Path.Combine(cacheRoot, "polyhaven", ModelHttp.SafeName(hit.Id), ModelHttp.SafeName(res));
        string gltfPath = ModelHttp.SafeCombine(folder, Path.GetFileName(new Uri(url).LocalPath));
        long total = ModelHttp.Long(entry, "size") ?? 0, done = 0;
        void Count(long n) { done += n; progress?.Report((done, Math.Max(total, done))); }
        await ModelHttp.Download(_http, url, gltfPath, ModelHttp.Long(entry, "size") ?? -1, Count, ct).ConfigureAwait(false);

        var needed = GltfMesh.RequiredFiles(await File.ReadAllTextAsync(gltfPath, ct).ConfigureAwait(false));
        var plan = new List<(string Rel, string Url, long Size)>();
        foreach (var rel in needed)
            if (include.TryGetValue(rel.Replace('\\', '/'), out var f) && f.Url is not null) plan.Add((rel, f.Url, f.Size));
            else throw new InvalidDataException($"Poly Haven's file list for {hit.Title} does not include {rel}.");
        total += plan.Sum(p => Math.Max(0, p.Size));
        foreach (var (rel, fileUrl, size) in plan)
            await ModelHttp.Download(_http, fileUrl, ModelHttp.SafeCombine(folder, rel), size, Count, ct).ConfigureAwait(false);
        return new ModelDownload(gltfPath, hit.Credit, hit);
    }
}

/// <summary>
/// Poly Pizza (poly.pizza): about ten thousand low-poly models, CC0 or CC-BY - the size a 2002 engine wants. Each user
/// needs a free API key (poly.pizza/settings/api), sent as <c>x-auth-token</c>; models download as a single .glb. Its
/// field names have changed spelling between versions ("Tri Count", "triCount"), so they are read loosely. Models
/// whose licence forbids changes (CC-BY-ND) are left out: the import simplifies and converts every model.
/// </summary>
public sealed class PolyPizzaSource : IModelSource
{
    public const string Api = "https://api.poly.pizza/v1.1";
    public const int PageSize = 32;
    private readonly HttpClient _http;
    private readonly Func<string?> _key;

    public PolyPizzaSource(HttpClient http, Func<string?> apiKey) { _http = http; _key = apiKey; }
    public string Name => "Poly Pizza";
    public bool HasKey => !string.IsNullOrWhiteSpace(_key());

    public sealed class NoKeyException : InvalidOperationException
    {
        public NoKeyException() : base("Poly Pizza needs a free API key: make one at poly.pizza/settings/api and paste it in.") { }
    }

    public async Task<ModelPage> SearchAsync(string query, int page, ModelFilter filter, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new NoKeyException();
        var q = (query ?? "").Trim();
        if (q.Length == 0) return new ModelPage(Array.Empty<ModelHit>(), 0, false);
        var url = $"{Api}/search/{Uri.EscapeDataString(q)}?limit={PageSize}" + (page > 0 ? $"&page={page}" : "")
                + (filter.Cc0Only ? "&license=CC0" : "");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("x-auth-token", key.Trim());
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Poly Pizza turned the API key down - check it at poly.pizza/settings/api.");
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Poly Pizza answered {(int)resp.StatusCode}: {Trim(body)}");
        var (hits, total) = ParseSearch(body);
        var kept = hits.Where(h => !ModelLicense.Parse(h.License).NoDerivatives)
                       .Where(h => filter.AllowAnimated || !h.Animated)
                       .Where(h => !filter.Cc0Only || ModelLicense.Parse(h.License).Cc0)
                       .ToList();
        // A full page with more behind it by the total (or no total given) can have a next page.
        return new ModelPage(kept, total, hits.Count >= PageSize && (total <= 0 || (page + 1) * PageSize < total));
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] + "..." : s;

    public static (List<ModelHit> Hits, int Total) ParseSearch(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        JsonElement list = root.ValueKind == JsonValueKind.Array ? root
                         : ModelHttp.Prop(root, "results", "models") is { ValueKind: JsonValueKind.Array } r ? r : default;
        var hits = new List<ModelHit>();
        if (list.ValueKind == JsonValueKind.Array)
            foreach (var m in list.EnumerateArray())
                if (Parse(m) is { } h) hits.Add(h);
        int total = root.ValueKind == JsonValueKind.Object && ModelHttp.Long(root, "total") is long t ? (int)t : hits.Count;
        return (hits, total);
    }

    public static ModelHit? Parse(JsonElement m)
    {
        var id = ModelHttp.Str(m, "ID", "id");
        var download = ModelHttp.Str(m, "Download", "download");
        if (id is null || download is null) return null;
        string author = "unknown";
        if (ModelHttp.Prop(m, "Creator", "creator") is { ValueKind: JsonValueKind.Object } c)
            author = ModelHttp.Str(c, "Username", "name", "username") ?? author;
        var lic = ModelLicense.Parse(ModelHttp.Str(m, "Licence", "License", "license"));
        var tris = ModelHttp.Long(m, "Tri Count", "triCount", "TriangleCount", "triangles");
        return new ModelHit(
            Source: "Poly Pizza", Id: id, Title: ModelHttp.Str(m, "Title", "title", "name") ?? id, Author: author,
            License: lic.Name, LicenseUrl: lic.Url, PageUrl: "https://poly.pizza/m/" + Uri.EscapeDataString(id),
            ThumbnailUrl: ModelHttp.Str(m, "Thumbnail", "thumbnail"),
            Triangles: tris is long tc ? (int)Math.Min(tc, int.MaxValue) : null,
            Tags: ModelHttp.Strings(m, "Tags", "tags"),
            Animated: ModelHttp.Bool(m, "Animated", "animated"),
            Attribution: ModelHttp.Str(m, "Attribution", "attribution"),
            DownloadUrl: download,
            Category: ModelHttp.Str(m, "Category", "category"));
    }

    public async Task<ModelDownload> DownloadAsync(ModelHit hit, string cacheRoot, string? quality,
                                                   IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (hit.DownloadUrl is null) throw new InvalidDataException($"Poly Pizza gave no download for {hit.Title}.");
        string dest = Path.Combine(cacheRoot, "polypizza", ModelHttp.SafeName(hit.Id), ModelHttp.SafeName(hit.Id) + ".glb");
        long done = 0;
        await ModelHttp.Download(_http, hit.DownloadUrl, dest, -1, n => { done += n; progress?.Report((done, done)); }, ct).ConfigureAwait(false);
        return new ModelDownload(dest, hit.Credit, hit);
    }
}
