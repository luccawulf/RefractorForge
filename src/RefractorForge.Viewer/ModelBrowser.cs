using System.Collections.Concurrent;
using RefractorForge.Formats.Online;
using RefractorForge.Render;

namespace RefractorForge.Viewer;

/// <summary>
/// The free-model browser's state and its background work: searching a library, downloading a model, and fetching
/// thumbnails. Everything that touches the network runs on a task; <see cref="Poll"/> is called once a frame from the
/// UI thread to pick up what finished, and is the only place a GL texture is made. The window itself is drawn in
/// Program.cs with the editor's widget wrappers.
/// </summary>
public sealed class ModelBrowser : IDisposable
{
    public readonly PolyHavenSource PolyHaven;
    public readonly PolyPizzaSource PolyPizza;
    private readonly HttpClient _http = ModelHttp.Create();

    public int SourceIndex;                  // 0 Poly Haven, 1 Poly Pizza
    public IModelSource Source => SourceIndex == 0 ? PolyHaven : PolyPizza;
    public string Query = "";
    public int CategoryIndex;                // Poly Haven: 0 = any, else 1 + index into PolyHaven.Categories
    public bool Cc0Only;
    public bool HideAnimated = true;
    public int QualityIndex;                 // Poly Haven texture size: 0 = 1k, 1 = 2k

    public readonly List<ModelHit> Results = new();
    public int Total;
    public bool More;
    public ModelHit? Selected;
    public string Status = "";
    public string Error = "";
    public bool Searching => _search is { IsCompleted: false };
    public bool Downloading => _download is { IsCompleted: false };
    public ModelHit? DownloadingHit { get; private set; }
    public (long Done, long Total) DownloadProgress;

    private Task<ModelPage>? _search;
    private CancellationTokenSource? _searchCts;
    private int _searchPage;
    private bool _searchAppend;
    private Task<ModelDownload>? _download;
    private CancellationTokenSource? _downloadCts;
    private ModelDownload? _finished;

    private readonly Dictionary<string, (uint Tex, int W, int H)> _thumbs = new();
    private readonly HashSet<string> _thumbAsked = new();
    private readonly ConcurrentQueue<(string Key, Texture2D? Image)> _thumbReady = new();
    private readonly SemaphoreSlim _thumbGate = new(6);

    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RefractorForge", "ModelCache");

    public ModelBrowser(Func<string?> polyPizzaKey)
    {
        PolyHaven = new PolyHavenSource(_http);
        PolyPizza = new PolyPizzaSource(_http, polyPizzaKey);
    }

    public void Search(bool more)
    {
        if (more && (Searching || !More)) return;
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _searchAppend = more;
        _searchPage = more ? _searchPage + 1 : 0;
        Error = "";
        Status = more ? Loc.T("Loading more...") : string.Format(Loc.T("Searching {0}..."), Source.Name);
        string? cat = SourceIndex == 0 && CategoryIndex > 0 && CategoryIndex <= PolyHaven.Categories.Count
            ? PolyHaven.Categories[CategoryIndex - 1] : null;
        var filter = new ModelFilter(Cc0Only, !HideAnimated, cat);
        var src = Source; var q = Query; int page = _searchPage; var ct = _searchCts.Token;
        _search = Task.Run(() => src.SearchAsync(q, page, filter, ct), ct);
    }

    public void Download(ModelHit hit)
    {
        if (Downloading) return;
        _downloadCts = new CancellationTokenSource();
        DownloadingHit = hit;
        DownloadProgress = (0, 0);
        Error = "";
        var src = hit.Source == PolyPizza.Name ? (IModelSource)PolyPizza : PolyHaven;
        string? quality = src == PolyHaven ? (QualityIndex == 1 ? "2k" : "1k") : null;
        var progress = new Progress<(long Done, long Total)>(p => DownloadProgress = p);
        var ct = _downloadCts.Token;
        _download = Task.Run(() => src.DownloadAsync(hit, CacheRoot, quality, progress, ct), ct);
    }

    public void CancelDownload() => _downloadCts?.Cancel();

    /// <summary>A finished download, once: the window hands it to the importer.</summary>
    public ModelDownload? TakeFinished() { var d = _finished; _finished = null; return d; }

    /// <summary>The thumbnail's texture and size when it is ready (texture 0 when it failed); asks for it the
    /// first time.</summary>
    public (uint Tex, int W, int H)? Thumbnail(ModelHit hit)
    {
        var key = hit.Source + ":" + hit.Id;
        if (_thumbs.TryGetValue(key, out var tex)) return tex;
        if (hit.ThumbnailUrl is null) return (0, 0, 0);
        if (_thumbAsked.Add(key)) _ = Task.Run(() => FetchThumbnail(key, hit));
        return null;
    }

    private async Task FetchThumbnail(string key, ModelHit hit)
    {
        await _thumbGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var url = hit.ThumbnailUrl!;
            string ext = Path.GetExtension(new Uri(url).LocalPath).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp")) ext = ".png";
            string file = Path.Combine(CacheRoot, "thumbs", ModelHttp.SafeName(hit.Source), ModelHttp.SafeName(hit.Id) + ext);
            if (!File.Exists(file)) await ModelHttp.Download(_http, url, file, -1, null, default).ConfigureAwait(false);
            var img = Decode(file);
            // No WebP codec on this machine: Poly Pizza keeps a (larger) JPEG of every thumbnail beside the WebP.
            if (img is null && ext == ".webp")
            {
                string jpg = Path.ChangeExtension(file, ".jpg");
                if (!File.Exists(jpg)) await ModelHttp.Download(_http, Path.ChangeExtension(url, ".jpg"), jpg, -1, null, default).ConfigureAwait(false);
                img = Decode(jpg);
            }
            _thumbReady.Enqueue((key, img is null ? null : Shrink(img, 256)));
        }
        catch { _thumbReady.Enqueue((key, null)); }
        finally { _thumbGate.Release(); }
    }

    /// <summary>PNG, JPEG, GIF, BMP and TIFF through System.Drawing; anything else Windows has a codec for (WebP)
    /// through WIC.</summary>
    public static Texture2D? Decode(string path)
    {
        if (!Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var bmp = new System.Drawing.Bitmap(path);
                int w = bmp.Width, h = bmp.Height;
                var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly,
                                     System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var tmp = new byte[w * h * 4];
                System.Runtime.InteropServices.Marshal.Copy(d.Scan0, tmp, 0, tmp.Length);
                bmp.UnlockBits(d);
                for (int i = 0; i < w * h; i++) (tmp[i * 4], tmp[i * 4 + 2]) = (tmp[i * 4 + 2], tmp[i * 4]);   // BGRA -> RGBA
                return new Texture2D(w, h, tmp);
            }
            catch { }
        }
        return WicImage.Decode(path);
    }

    /// <summary>A box-filtered copy no larger than <paramref name="maxSide"/>, so a page of thumbnails stays small.</summary>
    public static Texture2D Shrink(Texture2D t, int maxSide)
    {
        int f = 1;
        while (Math.Max(t.Width, t.Height) / f > maxSide) f++;
        if (f == 1) return t;
        int w = Math.Max(1, t.Width / f), h = Math.Max(1, t.Height / f);
        var o = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < f; dy++)
                        for (int dx = 0; dx < f; dx++)
                            sum += t.Rgba[((y * f + dy) * t.Width + x * f + dx) * 4 + c];
                    o[(y * w + x) * 4 + c] = (byte)(sum / (f * f));
                }
        return new Texture2D(w, h, o);
    }

    /// <summary>Once a frame, on the UI thread: finished searches and downloads, and thumbnails into GL.</summary>
    public void Poll(Func<Texture2D, uint> upload, Action<uint> delete)
    {
        if (_search is { IsCompleted: true } s)
        {
            _search = null;
            if (s.IsCanceled) { }
            else if (s.IsFaulted) { Error = s.Exception?.GetBaseException().Message ?? Loc.T("The search failed."); Status = ""; }
            else
            {
                var page = s.Result;
                if (!_searchAppend) { Results.Clear(); Selected = null; }
                var seen = new HashSet<string>(Results.Select(r => r.Id));
                int added = 0;
                foreach (var h in page.Hits) if (seen.Add(h.Id)) { Results.Add(h); added++; }
                Total = page.Total;
                More = page.More && added > 0;
                Status = Results.Count == 0 ? Loc.T("Nothing found.") : string.Format(Loc.T("{0} of {1} models"), Results.Count, Math.Max(Total, Results.Count));
            }
        }
        if (_download is { IsCompleted: true } d)
        {
            _download = null;
            if (d.IsCanceled) Status = Loc.T("Download cancelled.");
            else if (d.IsFaulted) Error = Loc.T("Download failed: ") + (d.Exception?.GetBaseException().Message ?? "");
            else _finished = d.Result;
            DownloadingHit = null;
        }
        for (int i = 0; i < 8 && _thumbReady.TryDequeue(out var t); i++)
            _thumbs[t.Key] = t.Image is null ? (0, 0, 0) : (upload(t.Image), t.Image.Width, t.Image.Height);
        if (_thumbs.Count > 400)   // a long browsing session: drop them all, the disk cache brings them back quickly
        {
            foreach (var tex in _thumbs.Values) if (tex.Tex != 0) delete(tex.Tex);
            _thumbs.Clear(); _thumbAsked.Clear();
        }
    }

    public void Dispose()
    {
        _searchCts?.Cancel(); _downloadCts?.Cancel();
        _http.Dispose();
    }
}
