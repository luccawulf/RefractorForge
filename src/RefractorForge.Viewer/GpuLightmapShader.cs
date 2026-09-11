using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using Silk.NET.OpenGL;

namespace RefractorForge.Viewer;

/// <summary>
/// Shades lightmap samples on the GPU with an OpenGL compute shader.
///
/// <para><b>What it runs.</b> The GLSL below is a line-by-line transliteration of
/// <see cref="LightmapKernel"/>, which is proven bit-for-bit against the CPU shader by the test suite. That proof
/// covers the part of a GPU port that usually goes wrong - the buffer layout, the absolute indices across two BVHs
/// packed into one set of buffers, the traversal stack, the heightmap indexing. What it cannot cover is GPU
/// ARITHMETIC: sin, cos, tan and sqrt are not bit-exact with .NET's, and drivers fuse multiply-adds. So GPU results
/// are close to the CPU's rather than identical, and <see cref="LightmapGpuSelfTest"/> exists to measure how close on
/// the actual card before anyone bakes a map with it.</para>
///
/// <para><b>Threads.</b> An OpenGL context belongs to the thread that made it - here the render thread - while the
/// bake runs on worker threads so the editor stays usable. So a worker never touches GL: <see cref="Shade"/> queues
/// its chunk and waits, and the render thread does the GPU work in <see cref="Pump"/>, a few milliseconds per
/// frame. If nothing pumps (the window minimised, no frames drawn) a queued chunk falls back to the CPU rather
/// than hanging the bake.</para>
///
/// <para><b>The watchdog.</b> Windows resets the display driver if one GPU command runs past about two seconds.
/// Each dispatch is therefore a slice of a chunk, sized from how long the previous slice took and held well under
/// that limit.</para>
///
/// <para><b>What stays on the CPU.</b> The classic tiers (one binary sun ray each - already fast), placed lamps,
/// a bounce depth above 2 (a shader cannot recurse; no tier uses more), and everything when the editor is running on
/// an integrated GPU (<see cref="GpuAdapter"/>).</para>
/// </summary>
internal sealed class GpuLightmapShader : ILightmapShader, IDisposable
{
    // ---- the kernel --------------------------------------------------------------------------------------------
    // Exactly eight storage buffers: OpenGL 4.3 guarantees no more than eight to a compute shader.
    public const string Source = @"#version 430
layout(local_size_x = 64) in;

layout(std430, binding = 0) readonly  buffer NodeBoundsBuf { vec4  nodeBounds[]; };   // 2 per node: min, max
layout(std430, binding = 1) readonly  buffer NodeLinksBuf  { int   nodeLinks[];  };   // 2 per node: leftFirst, count
layout(std430, binding = 2) readonly  buffer TrisBuf       { vec4  tris[];       };   // 4 per tri: a, e1, e2, albedo
layout(std430, binding = 3) readonly  buffer IndexBuf      { int   triIndex[];   };
layout(std430, binding = 4) readonly  buffer HeightsBuf    { float heights[];    };
layout(std430, binding = 5) readonly  buffer SampleBuf     { vec4  sampleData[]; };   // 2 per sample: position, normal
layout(std430, binding = 6) readonly  buffer SeedBuf       { uint  seedData[];   };
layout(std430, binding = 7) writeonly buffer ResultBuf     { vec4  resultData[]; };   // 2 per sample: (sun,sky,0,0), (bounce,0)

uniform int   uRootLevel;
uniform int   uRootSelf;
uniform int   uHW;
uniform int   uHH;
uniform float uWorldSize;
uniform float uMaxH;
uniform vec3  uSunDir;              // as given - the terrain march and the bounce's N.L use it unnormalised
uniform vec3  uSun;                 // normalised
uniform int   uSoftSun;
uniform float uSunAngleDeg;
uniform int   uSunPerSample;
uniform int   uSkyPerSample;
uniform float uAoRadius;
uniform int   uDoSky;
uniform int   uBouncePerSample;
uniform int   uBounceDepth;
uniform float uBounceDistance;
uniform vec3  uSunColour;
uniform int   uBounceShadowSamples;
uniform int   uDoBounce;
uniform int   uOffset;
uniform int   uCount;

const float SKIP = 0.02;
const float FLT_MAX = 3.402823466e+38;
const float PI_F = 3.14159265;
const float TAU_F = 6.28318531;
const int STACK = 66;
const int MAX_VISITS = 1048576;     // a guard against a hang on corrupt data; a valid BVH never gets near it

float hash01(uint x)
{
    x ^= x >> 16u; x *= 0x7feb352du;
    x ^= x >> 15u; x *= 0x846ca68bu;
    x ^= x >> 16u;
    return float(x & 0xFFFFFFu) / 16777216.0;
}

void basis(vec3 n, out vec3 u, out vec3 v)
{
    vec3 pick = abs(n.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    u = normalize(cross(n, pick));
    v = cross(n, u);
}

vec3 reciprocal(vec3 d)
{
    return vec3(1.0 / (d.x != 0.0 ? d.x : 1e-20),
                1.0 / (d.y != 0.0 ? d.y : 1e-20),
                1.0 / (d.z != 0.0 ? d.z : 1e-20));
}

bool slabHit(int node, vec3 o, vec3 inv, float maxDist)
{
    vec3 lo = nodeBounds[node * 2].xyz;
    vec3 hi = nodeBounds[node * 2 + 1].xyz;
    float t0 = (lo.x - o.x) * inv.x, t1 = (hi.x - o.x) * inv.x;
    float tmin = min(t0, t1), tmax = max(t0, t1);
    t0 = (lo.y - o.y) * inv.y; t1 = (hi.y - o.y) * inv.y;
    tmin = max(tmin, min(t0, t1)); tmax = min(tmax, max(t0, t1));
    t0 = (lo.z - o.z) * inv.z; t1 = (hi.z - o.z) * inv.z;
    tmin = max(tmin, min(t0, t1)); tmax = min(tmax, max(t0, t1));
    return tmax >= max(tmin, 0.0) && tmin <= maxDist;
}

float hitDistance(int i, vec3 o, vec3 d)
{
    vec3 a  = tris[i * 4].xyz;
    vec3 e1 = tris[i * 4 + 1].xyz;
    vec3 e2 = tris[i * 4 + 2].xyz;
    vec3 pvec = cross(d, e2);
    float det = dot(e1, pvec);
    if (abs(det) < 1e-9) return -1.0;
    float inv = 1.0 / det;
    vec3 tvec = o - a;
    float u = dot(tvec, pvec) * inv;
    if (u < 0.0 || u > 1.0) return -1.0;
    vec3 qvec = cross(tvec, e1);
    float v = dot(d, qvec) * inv;
    if (v < 0.0 || u + v > 1.0) return -1.0;
    return dot(e2, qvec) * inv;
}

bool occluded(int root, vec3 origin, vec3 dir, float maxDist)
{
    origin += dir * SKIP;
    maxDist -= SKIP;
    if (maxDist <= 0.0) return false;
    vec3 inv = reciprocal(dir);
    int stack[STACK];
    int sp = 0;
    int node = root;
    for (int visits = 0; visits < MAX_VISITS; visits++)
    {
        if (slabHit(node, origin, inv, maxDist))
        {
            int lf = nodeLinks[node * 2];
            int cnt = nodeLinks[node * 2 + 1];
            if (cnt > 0)
            {
                for (int i = lf; i < lf + cnt; i++)
                {
                    float t = hitDistance(triIndex[i], origin, dir);
                    if (t > 1e-4 && t <= maxDist) return true;
                }
            }
            else
            {
                if (sp < STACK) stack[sp++] = lf + 1;
                node = lf;
                continue;
            }
        }
        if (sp == 0) return false;
        node = stack[--sp];
    }
    return false;
}

bool trace(int root, vec3 origin, vec3 dir, float maxDist,
           out float dist, out vec3 point, out vec3 normal, out vec3 alb)
{
    dist = 0.0; point = vec3(0.0); normal = vec3(0.0); alb = vec3(0.0);
    origin += dir * SKIP;
    maxDist -= SKIP;
    if (maxDist <= 0.0) return false;
    vec3 inv = reciprocal(dir);
    float best = maxDist;
    int bestTri = -1;
    int stack[STACK];
    int sp = 0;
    int node = root;
    for (int visits = 0; visits < MAX_VISITS; visits++)
    {
        if (slabHit(node, origin, inv, best))
        {
            int lf = nodeLinks[node * 2];
            int cnt = nodeLinks[node * 2 + 1];
            if (cnt > 0)
            {
                for (int i = lf; i < lf + cnt; i++)
                {
                    int t = triIndex[i];
                    float d = hitDistance(t, origin, dir);
                    if (d > 1e-4 && d < best) { best = d; bestTri = t; }
                }
            }
            else
            {
                if (sp < STACK) stack[sp++] = lf + 1;
                node = lf;
                continue;
            }
        }
        if (sp == 0) break;
        node = stack[--sp];
    }
    if (bestTri < 0) return false;
    vec3 nrm = cross(tris[bestTri * 4 + 1].xyz, tris[bestTri * 4 + 2].xyz);
    float len = length(nrm);
    nrm = len > 1e-12 ? nrm / len : vec3(0.0, 1.0, 0.0);
    if (dot(nrm, dir) > 0.0) nrm = -nrm;
    dist = best + SKIP;
    point = origin + dir * best;
    normal = nrm;
    alb = tris[bestTri * 4 + 3].xyz;
    return true;
}

bool pointLit(float wx, float wy, float wz, vec3 sunDir)
{
    float ws = uWorldSize;
    int hw = uHW, hh = uHH;
    float horiz = sqrt(sunDir.x * sunDir.x + sunDir.z * sunDir.z);
    if (horiz < 1e-4) horiz = 1e-4;
    float dirX = sunDir.x / horiz, dirZ = sunDir.z / horiz;
    float rise = max(sunDir.y, 0.02) / horiz;
    float stepLen = ws / 1024.0;
    float cx = wx, cz = wz, rh = wy + 0.35;
    for (int s = 1; s <= 2200; s++)
    {
        cx += dirX * stepLen; cz += dirZ * stepLen; rh += rise * stepLen;
        if (rh > uMaxH) return true;
        if (cx < 0.0 || cz < 0.0 || cx > ws || cz > ws) return true;
        float fx = cx / ws * float(hw - 1), fz = cz / ws * float(hh - 1);
        int hx = clamp(int(fx + 0.5), 0, hw - 1), hy = clamp(int(fz + 0.5), 0, hh - 1);
        if (heights[hy * hw + hx] > rh) return false;
    }
    return true;
}

bool clearRay(vec3 p, vec3 dirV, vec3 dir)
{
    return pointLit(p.x, p.y, p.z, dirV)
        && (uRootLevel < 0 || !occluded(uRootLevel, p, dir, FLT_MAX))
        && (uRootSelf  < 0 || !occluded(uRootSelf,  p, dir, FLT_MAX));
}

float sunVisibility(vec3 p, vec3 sunDir, float angDeg, int count, uint seed)
{
    vec3 sun = normalize(sunDir);
    count = max(1, count);
    float halfAngle = max(0.0, angDeg) * 0.5 * PI_F / 180.0;
    if (count == 1 || halfAngle <= 1e-6) return clearRay(p, sunDir, sun) ? 1.0 : 0.0;
    vec3 u, v;
    basis(sun, u, v);
    float tanHalf = tan(halfAngle);
    float rot = hash01(seed) * TAU_F;
    int seen = 0;
    for (int k = 0; k < count; k++)
    {
        float r = tanHalf * sqrt((float(k) + 0.5) / float(count));
        float a = float(k) * 2.399963 + rot;
        vec3 dir = normalize(sun + u * (cos(a) * r) + v * (sin(a) * r));
        if (clearRay(p, dir, dir)) seen++;
    }
    return float(seen) / float(count);
}

float skyVisibility(vec3 p, vec3 n, int count, float maxDist, uint seed)
{
    count = max(1, count);
    if (dot(n, n) < 1e-12) return 1.0;
    n = normalize(n);
    vec3 u, v;
    basis(n, u, v);
    float rot = hash01(seed) * TAU_F;
    bool bounded = maxDist > 0.0 && !isinf(maxDist);
    int open = 0;
    for (int k = 0; k < count; k++)
    {
        float r2 = (float(k) + 0.5) / float(count);
        float r = sqrt(r2);
        float a = float(k) * 2.399963 + rot;
        vec3 dir = u * (cos(a) * r) + v * (sin(a) * r) + n * sqrt(max(0.0, 1.0 - r2));
        float len = length(dir);
        if (len < 1e-6) { open++; continue; }
        dir /= len;
        float reach = bounded ? maxDist : FLT_MAX;
        bool blocked = (uRootLevel >= 0 && occluded(uRootLevel, p, dir, reach))
                    || (uRootSelf  >= 0 && occluded(uRootSelf,  p, dir, reach));
        if (!blocked && !bounded) blocked = !pointLit(p.x, p.y, p.z, dir);
        if (!blocked) open++;
    }
    return float(open) / float(count);
}

bool traceNearest(vec3 p, vec3 dir, float reach, out vec3 point, out vec3 normal, out vec3 alb)
{
    bool ha = false, hb = false;
    float da = 0.0, db = 0.0;
    vec3 pa = vec3(0.0), na = vec3(0.0), aa = vec3(0.0), pb = vec3(0.0), nb = vec3(0.0), ab = vec3(0.0);
    if (uRootLevel >= 0) ha = trace(uRootLevel, p, dir, reach, da, pa, na, aa);
    if (uRootSelf  >= 0) hb = trace(uRootSelf,  p, dir, reach, db, pb, nb, ab);
    if (ha && hb)
    {
        bool first = da <= db;
        point = first ? pa : pb; normal = first ? na : nb; alb = first ? aa : ab;
        return true;
    }
    if (ha) { point = pa; normal = na; alb = aa; return true; }
    if (hb) { point = pb; normal = nb; alb = ab; return true; }
    point = vec3(0.0); normal = vec3(0.0); alb = vec3(0.0);
    return false;
}

// One bounce. A shader cannot recurse, so a second bounce is bounceTop calling this - the same body without the
// recursive branch - rather than a function calling itself.
vec3 bounce1(vec3 p, vec3 n, int count, float maxDist, uint seed)
{
    if (count <= 0 || (uRootLevel < 0 && uRootSelf < 0)) return vec3(0.0);
    if (dot(n, n) < 1e-12) return vec3(0.0);
    n = normalize(n);
    vec3 u, v;
    basis(n, u, v);
    float rot = hash01(seed) * TAU_F;
    float reach = maxDist > 0.0 ? maxDist : FLT_MAX;
    vec3 sum = vec3(0.0);
    for (int k = 0; k < count; k++)
    {
        float r2 = (float(k) + 0.5) / float(count);
        float r = sqrt(r2);
        float a = float(k) * 2.399963 + rot;
        vec3 dir = u * (cos(a) * r) + v * (sin(a) * r) + n * sqrt(max(0.0, 1.0 - r2));
        float len = length(dir);
        if (len < 1e-6) continue;
        dir /= len;
        vec3 hp, hn, hc;
        if (!traceNearest(p, dir, reach, hp, hn, hc)) continue;
        float ndl = max(0.0, dot(hn, uSunDir));
        if (ndl > 0.0)
        {
            float vis = sunVisibility(hp, uSunDir, uSunAngleDeg, uBounceShadowSamples, seed ^ (uint(k) * 2654435761u));
            if (vis > 0.0) sum += hc * uSunColour * (ndl * vis);
        }
    }
    return sum / float(count);
}

vec3 bounceTop(vec3 p, vec3 n, int count, int depth, float maxDist, uint seed)
{
    if (depth <= 0) return vec3(0.0);
    if (depth == 1) return bounce1(p, n, count, maxDist, seed);
    if (count <= 0 || (uRootLevel < 0 && uRootSelf < 0)) return vec3(0.0);
    if (dot(n, n) < 1e-12) return vec3(0.0);
    n = normalize(n);
    vec3 u, v;
    basis(n, u, v);
    float rot = hash01(seed) * TAU_F;
    float reach = maxDist > 0.0 ? maxDist : FLT_MAX;
    vec3 sum = vec3(0.0);
    for (int k = 0; k < count; k++)
    {
        float r2 = (float(k) + 0.5) / float(count);
        float r = sqrt(r2);
        float a = float(k) * 2.399963 + rot;
        vec3 dir = u * (cos(a) * r) + v * (sin(a) * r) + n * sqrt(max(0.0, 1.0 - r2));
        float len = length(dir);
        if (len < 1e-6) continue;
        dir /= len;
        vec3 hp, hn, hc;
        if (!traceNearest(p, dir, reach, hp, hn, hc)) continue;
        float ndl = max(0.0, dot(hn, uSunDir));
        if (ndl > 0.0)
        {
            float vis = sunVisibility(hp, uSunDir, uSunAngleDeg, uBounceShadowSamples, seed ^ (uint(k) * 2654435761u));
            if (vis > 0.0) sum += hc * uSunColour * (ndl * vis);
        }
        sum += hc * bounce1(hp, hn, max(1, count / 4), reach, seed ^ (uint(k) * 40503u + 7u));
    }
    return sum / float(count);
}

void main()
{
    uint gid = gl_GlobalInvocationID.x;
    if (gid >= uint(uCount)) return;
    int i = uOffset + int(gid);
    vec3 wp = sampleData[i * 2].xyz;
    vec3 fn = sampleData[i * 2 + 1].xyz;
    uint seed = seedData[i];
    float sunVis = uSoftSun != 0
        ? sunVisibility(wp, uSunDir, uSunAngleDeg, uSunPerSample, seed)
        : (clearRay(wp, uSunDir, uSun) ? 1.0 : 0.0);
    float sky = (uDoSky != 0 && uSkyPerSample > 0) ? skyVisibility(wp, fn, uSkyPerSample, uAoRadius, seed) : 1.0;
    vec3 b = (uDoBounce != 0 && uBouncePerSample > 0)
        ? bounceTop(wp, fn, uBouncePerSample, uBounceDepth, uBounceDistance, seed)
        : vec3(0.0);
    resultData[i * 2] = vec4(sunVis, sky, 0.0, 0.0);
    resultData[i * 2 + 1] = vec4(b, 0.0);
}
";

    // ---- state -------------------------------------------------------------------------------------------------
    private readonly GL _gl;
    private readonly uint[] _buf = new uint[8];
    private uint _prog;
    private readonly Dictionary<string, int> _u = new();

    private volatile bool _available;
    /// <summary>The card can run the kernel: OpenGL 4.3 or the two ARB extensions, and the shader compiled.</summary>
    public bool Available => _available;
    /// <summary>The user wants it used. Off until <see cref="LightmapGpuSelfTest"/> passes on this machine.</summary>
    public volatile bool Enabled;
    /// <summary>What to show the user: the card's name, or why the GPU cannot be used.</summary>
    public string Status { get; private set; } = "";

    // Render-thread upload state.
    private LightmapGpuScene? _uploadedLevel;
    private int _nodeCap, _triCap, _indexCap, _heightCap, _sampleCap;
    // The first slice is small - a few hundred samples is a fraction of a second even on a slow card - and the size
    // grows from what the card is measured to do, so no guess about its speed is ever near the watchdog.
    private int _dispatch = 256;
    private const double TargetSliceMs = 8.0;   // far below the ~2 s driver watchdog

    // Worker-side caches.
    private readonly object _levelLock = new();
    private (ObjectLightmapBaker.Advanced Adv, Heightmap Hm, LightmapGpuScene Pack)? _levelPack;
    private readonly ConditionalWeakTable<ShadeContext, LightmapGpuScene> _tails = new();

    private sealed class Request
    {
        public required SampleChunk Chunk;
        public required LightmapKernel.Params P;
        public required LightmapGpuScene Level;
        public required LightmapGpuScene Tail;
        public required bool DoSky, DoBounce;
        public readonly ManualResetEventSlim Done = new(false);
        /// <summary>0 queued, 1 started on the render thread, 2 abandoned by the worker.</summary>
        public int State;
        public volatile bool Cancelled;
        public int Next;
        public Exception? Error;
    }
    private readonly ConcurrentQueue<Request> _queue = new();
    private Request? _current;

    /// <summary>How long a queued chunk may wait for the render thread before the worker gives up on the GPU for
    /// it and shades it on the CPU instead. Frames are ~16 ms apart, so this only fires when nothing is pumping.</summary>
    private static readonly TimeSpan PickupTimeout = TimeSpan.FromSeconds(5);

    public int PreferredChunk => 65536;
    /// <summary>Samples the GPU actually shaded, and samples that were meant for it but ended up on the CPU (the
    /// render thread never picked them up, or the GPU failed). The self-test reads both: a "GPU" bake that quietly
    /// ran on the CPU would match the CPU perfectly and prove nothing.</summary>
    public long SamplesShaded, SamplesFellBack;

    private ILightmapShader? _unproven;
    /// <summary>This shader with the <see cref="Enabled"/> switch bypassed, for the self-test - which is what decides
    /// Enabled. Going through a separate view rather than flipping the switch on for the test means a bake started
    /// while the test runs still sees the GPU as off.</summary>
    public ILightmapShader Unproven => _unproven ??= new UnprovenView(this);

    private sealed class UnprovenView(GpuLightmapShader g) : ILightmapShader
    {
        public int PreferredChunk => g.PreferredChunk;
        public void Shade(SampleChunk c, ShadeContext x, CancellationToken cancel) => g.ShadeCore(c, x, cancel, requireEnabled: false);
    }

    public GpuLightmapShader(GL gl)
    {
        _gl = gl;
        try { Init(); }
        catch (Exception ex) { _available = false; Status = "GPU lightmaps unavailable: " + ex.Message; }
    }

    private void Init()
    {
        int major = _gl.GetInteger(GetPName.MajorVersion), minor = _gl.GetInteger(GetPName.MinorVersion);
        bool core43 = major > 4 || (major == 4 && minor >= 3);
        if (!core43 && !(HasExtension("GL_ARB_compute_shader") && HasExtension("GL_ARB_shader_storage_buffer_object")))
        {
            Status = $"GPU lightmaps need OpenGL 4.3 (compute shaders); this context is {major}.{minor}.";
            return;
        }
        // Checked before anything is compiled: on a machine with two GPUs the editor runs on the integrated one unless
        // Windows or the driver is told otherwise, and that is the one place this must not run (see GpuAdapter).
        string renderer = _gl.GetStringS(StringName.Renderer) ?? "";
        if (!GpuAdapter.LooksDiscrete(renderer))
        {
            Status = $"GPU lightmaps are off: the editor is running on \"{renderer}\", which is not a dedicated graphics card. " +
                     "To use the NVIDIA card, set RefractorForge.exe to High performance in Windows Settings > System > Display > " +
                     "Graphics (or in the NVIDIA Control Panel) and restart the editor.";
            return;
        }
        int blocks = _gl.GetInteger((GetPName)GLEnum.MaxComputeShaderStorageBlocks);
        if (blocks < 8)
        {
            Status = $"GPU lightmaps need 8 storage buffers in a compute shader; this card allows {blocks}.";
            return;
        }

        uint cs = _gl.CreateShader(ShaderType.ComputeShader);
        _gl.ShaderSource(cs, Source);
        _gl.CompileShader(cs);
        _gl.GetShader(cs, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0)
        {
            string log = _gl.GetShaderInfoLog(cs);
            _gl.DeleteShader(cs);
            Status = "GPU lightmap shader failed to compile: " + log;
            Console.WriteLine(Status);
            return;
        }
        _prog = _gl.CreateProgram();
        _gl.AttachShader(_prog, cs);
        _gl.LinkProgram(_prog);
        _gl.GetProgram(_prog, ProgramPropertyARB.LinkStatus, out int linked);
        _gl.DetachShader(_prog, cs);
        _gl.DeleteShader(cs);
        if (linked == 0)
        {
            Status = "GPU lightmap shader failed to link: " + _gl.GetProgramInfoLog(_prog);
            Console.WriteLine(Status);
            _gl.DeleteProgram(_prog); _prog = 0;
            return;
        }
        foreach (var n in new[] { "uRootLevel", "uRootSelf", "uHW", "uHH", "uWorldSize", "uMaxH", "uSunDir", "uSun",
                                  "uSoftSun", "uSunAngleDeg", "uSunPerSample", "uSkyPerSample", "uAoRadius", "uDoSky",
                                  "uBouncePerSample", "uBounceDepth", "uBounceDistance", "uSunColour",
                                  "uBounceShadowSamples", "uDoBounce", "uOffset", "uCount" })
            _u[n] = _gl.GetUniformLocation(_prog, n);
        for (int i = 0; i < _buf.Length; i++) _buf[i] = _gl.GenBuffer();

        Status = $"{renderer} (OpenGL {major}.{minor})";
        _available = true;
    }

    private bool HasExtension(string name)
    {
        int n = _gl.GetInteger(GetPName.NumExtensions);
        for (int i = 0; i < n; i++)
            if (string.Equals(_gl.GetStringS(StringName.Extensions, (uint)i), name, StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- worker side -------------------------------------------------------------------------------------------

    public void Shade(SampleChunk c, ShadeContext x, CancellationToken cancel) => ShadeCore(c, x, cancel, requireEnabled: true);

    private void ShadeCore(SampleChunk c, ShadeContext x, CancellationToken cancel, bool requireEnabled)
    {
        var adv = x.Advanced;
        if ((requireEnabled && !Enabled) || adv is null || (adv.BounceSamples > 0 && adv.BounceDepth > 2))
        {
            CpuLightmapShader.Instance.Shade(c, x, cancel);
            return;
        }
        if (!_available)
        {
            Interlocked.Add(ref SamplesFellBack, c.Count);
            CpuLightmapShader.Instance.Shade(c, x, cancel);
            return;
        }

        var level = LevelPack(x);
        var tail = _tails.GetValue(x, ctx => LightmapGpuScene.BuildTail(ctx.SelfScene, level.LevelNodes, level.LevelTris, level.LevelIndex));
        var req = new Request
        {
            Chunk = c, P = LightmapKernel.Params.From(x), Level = level, Tail = tail,
            DoSky = adv.SkySamples > 0, DoBounce = adv.BounceSamples > 0,
        };
        _queue.Enqueue(req);

        var waited = Stopwatch.StartNew();
        try
        {
            while (!req.Done.Wait(50, cancel))
            {
                // Nothing has picked it up - the window may be minimised and not drawing frames. Take it back and
                // do it here rather than stall the bake. CompareExchange makes it exactly one of the two.
                if (waited.Elapsed > PickupTimeout && Interlocked.CompareExchange(ref req.State, 2, 0) == 0)
                {
                    Interlocked.Add(ref SamplesFellBack, c.Count);
                    CpuLightmapShader.Instance.Shade(c, x, cancel);
                    return;
                }
            }
        }
        catch (OperationCanceledException) { req.Cancelled = true; Interlocked.CompareExchange(ref req.State, 2, 0); return; }

        if (req.Error is not null)
        {
            // A GPU failure mid-bake must not lose the bake. Switch the GPU off for the rest of the session, say
            // why, and finish this chunk - and everything after it - on the CPU.
            _available = false;
            Status = "GPU lightmaps switched off after an error: " + req.Error.Message;
            Console.WriteLine(Status);
            Interlocked.Add(ref SamplesFellBack, c.Count);
            CpuLightmapShader.Instance.Shade(c, x, cancel);
            return;
        }

        // Placed lamps are not in the kernel; they run here, on this worker, with a real per-thread cursor.
        if (x.Rig is { Lights.Count: > 0 })
        {
            var pool = x.CursorPool;
            Parallel.For(0, c.Count, new ParallelOptions { CancellationToken = cancel },
                () => pool.TryTake(out var cur) ? cur : (Grid: x.SelfGrid?.NewCursor(), Lamp: x.Night?.NewCursor()),
                (i, _, cur) => { c.Lamp[i] = CpuLightmapShader.ShadeLamp(c, i, x, cur.Lamp); return cur; },
                cur => pool.Add(cur));
        }
        else Array.Clear(c.Lamp, 0, c.Count);
        Interlocked.Add(ref SamplesShaded, c.Count);
    }

    /// <summary>The level's packed arrays, built once per bake on a worker - not per object, and not on the render
    /// thread, where packing hundreds of thousands of triangles would freeze the editor.</summary>
    private LightmapGpuScene LevelPack(ShadeContext x)
    {
        var adv = x.Advanced!;
        lock (_levelLock)
        {
            // Keyed on the bake's settings record, which every bake makes fresh. Not on the heightmap: the sculpt
            // tools edit it IN PLACE, so the same reference would hand the next bake the terrain as it used to be.
            if (_levelPack is { } p && ReferenceEquals(p.Adv, adv) && ReferenceEquals(p.Hm, x.Hm)) return p.Pack;
            var pack = LightmapGpuScene.Build(adv.Scene, null, x.Hm, x.Cfg, x.MaxH);
            _levelPack = (adv, x.Hm, pack);
            return pack;
        }
    }

    // ---- render thread -----------------------------------------------------------------------------------------

    /// <summary>Do GPU work for at most <paramref name="budgetMs"/> of this frame. Call every frame.</summary>
    public void Pump(double budgetMs)
    {
        if (!_available || (_current is null && _queue.IsEmpty)) return;
        int prevProgram = _gl.GetInteger(GetPName.CurrentProgram);
        // Clear any error flag the editor's own drawing left set, so a check below reports only what this did.
        for (int i = 0; i < 32 && _gl.GetError() != GLEnum.NoError; i++) { }
        var sw = Stopwatch.StartNew();
        try
        {
            while (sw.Elapsed.TotalMilliseconds < budgetMs)
            {
                if (_current is null)
                {
                    if (!_queue.TryDequeue(out var next)) break;
                    if (Interlocked.CompareExchange(ref next.State, 1, 0) != 0) { next.Done.Set(); continue; }
                    _current = next;
                    try { Upload(next); }
                    catch (Exception ex) { Fail(next, ex); continue; }
                }
                var r = _current!;
                if (r.Cancelled) { r.Done.Set(); _current = null; continue; }
                try
                {
                    int n = Math.Min(_dispatch, r.Chunk.Count - r.Next);
                    double t0 = sw.Elapsed.TotalMilliseconds;
                    Dispatch(r, r.Next, n);
                    double took = sw.Elapsed.TotalMilliseconds - t0;
                    // Size the next slice from this one: double while it is quick, halve if it ran long. A fixed
                    // size would either crawl on a fast card or trip the watchdog on a slow one.
                    if (took < TargetSliceMs * 0.5 && _dispatch < (1 << 20)) _dispatch *= 2;
                    else if (took > TargetSliceMs * 2 && _dispatch > 64) _dispatch /= 2;
                    r.Next += n;
                    if (r.Next >= r.Chunk.Count)
                    {
                        ReadBack(r);
                        r.Done.Set();
                        _current = null;
                    }
                }
                catch (Exception ex) { Fail(r, ex); }
            }
        }
        finally { _gl.UseProgram((uint)prevProgram); }
    }

    private void Fail(Request r, Exception ex)
    {
        r.Error = ex;
        r.Done.Set();
        _current = null;
        _available = false;
        // Everything still queued goes back to its worker, which will see the GPU is off and use the CPU.
        while (_queue.TryDequeue(out var q)) { q.Error = ex; q.Done.Set(); }
    }

    private unsafe void Upload(Request r)
    {
        var lv = r.Level; var tl = r.Tail;
        int tailNodes = tl.NodeLinks.Length / LightmapGpuScene.IntsPerNode;
        int tailTris = tl.Tris.Length / LightmapGpuScene.FloatsPerTri;
        int tailIndex = tl.Index.Length;

        // The level goes up once per bake. The object's own BVH is a tail after it, rewritten per object; if a
        // tail does not fit the room left, everything is reallocated with more headroom and the level re-sent.
        bool levelChanged = !ReferenceEquals(_uploadedLevel, lv);
        bool tailFits = lv.LevelNodes + tailNodes <= _nodeCap && lv.LevelTris + tailTris <= _triCap
                        && lv.LevelIndex + tailIndex <= _indexCap;
        if (levelChanged || !tailFits)
        {
            _nodeCap = lv.LevelNodes + Math.Max(tailNodes * 2, 4096);
            _triCap = lv.LevelTris + Math.Max(tailTris * 2, 4096);
            _indexCap = lv.LevelIndex + Math.Max(tailIndex * 2, 4096);
            Alloc(0, (nuint)_nodeCap * LightmapGpuScene.FloatsPerNode * 4);
            Alloc(1, (nuint)_nodeCap * LightmapGpuScene.IntsPerNode * 4);
            Alloc(2, (nuint)_triCap * LightmapGpuScene.FloatsPerTri * 4);
            Alloc(3, (nuint)_indexCap * 4);
            SubData(0, 0, lv.NodeBounds); SubData(1, 0, lv.NodeLinks);
            SubData(2, 0, lv.Tris); SubData(3, 0, lv.Index);
            if (levelChanged || lv.Heights.Length > _heightCap)
            {
                _heightCap = Math.Max(1, lv.Heights.Length);
                Alloc(4, (nuint)_heightCap * 4);
                SubData(4, 0, lv.Heights);
            }
            _uploadedLevel = lv;
        }
        if (tailNodes > 0)
        {
            SubData(0, lv.LevelNodes * LightmapGpuScene.FloatsPerNode * 4, tl.NodeBounds);
            SubData(1, lv.LevelNodes * LightmapGpuScene.IntsPerNode * 4, tl.NodeLinks);
            SubData(2, lv.LevelTris * LightmapGpuScene.FloatsPerTri * 4, tl.Tris);
            SubData(3, lv.LevelIndex * 4, tl.Index);
        }

        // The chunk's sample points: position and normal as two vec4, and the seed on its own - integers are never
        // bit-cast into floats here, because some GPUs flush the denormals that small integers become.
        var c = r.Chunk;
        if (c.Count > _sampleCap)
        {
            _sampleCap = Math.Max(c.Count, PreferredChunk);
            Alloc(5, (nuint)_sampleCap * 8 * 4);
            Alloc(6, (nuint)_sampleCap * 4);
            Alloc(7, (nuint)_sampleCap * 8 * 4);
        }
        var sampleData = new float[c.Count * 8];
        var seeds = new uint[c.Count];
        for (int i = 0; i < c.Count; i++)
        {
            var p = c.Position[i]; var n = c.Normal[i];
            int o = i * 8;
            sampleData[o] = p.X; sampleData[o + 1] = p.Y; sampleData[o + 2] = p.Z;
            sampleData[o + 4] = n.X; sampleData[o + 5] = n.Y; sampleData[o + 6] = n.Z;
            seeds[i] = c.SubSeed[i];
        }
        SubData(5, 0, sampleData);
        SubData(6, 0, seeds);
        r.Next = 0;
        Check("uploading the scene");
    }

    /// <summary>
    /// A failed OpenGL call does not throw; it sets a flag and carries on. A buffer that failed to allocate (a big
    /// level on a card short of memory) would leave the kernel reading past the end of what is there - garbage at
    /// best, a hung GPU at worst. So every upload, dispatch and read-back is checked, and any error hands the work
    /// back to the CPU through <see cref="Fail"/>.
    /// </summary>
    private void Check(string what)
    {
        var e = _gl.GetError();
        if (e != GLEnum.NoError) throw new InvalidOperationException($"OpenGL error {e} while {what}");
    }

    private unsafe void Dispatch(Request r, int offset, int count)
    {
        var p = r.P;
        _gl.UseProgram(_prog);
        for (uint i = 0; i < _buf.Length; i++) _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, i, _buf[i]);
        Set("uRootLevel", r.Level.RootLevel);
        Set("uRootSelf", r.Tail.RootSelf);
        Set("uHW", r.Level.HW); Set("uHH", r.Level.HH);
        Set("uWorldSize", r.Level.WorldSize); Set("uMaxH", r.Level.MaxH);
        Set3("uSunDir", p.SunDir.X, p.SunDir.Y, p.SunDir.Z);
        Set3("uSun", p.Sun.X, p.Sun.Y, p.Sun.Z);
        Set("uSoftSun", p.SoftSun ? 1 : 0);
        Set("uSunAngleDeg", p.SunAngleDeg);
        Set("uSunPerSample", p.SunPerSample);
        Set("uSkyPerSample", p.SkyPerSample);
        Set("uAoRadius", p.AoRadius);
        Set("uDoSky", r.DoSky ? 1 : 0);
        Set("uBouncePerSample", p.BouncePerSample);
        Set("uBounceDepth", p.BounceDepth);
        Set("uBounceDistance", p.BounceDistance);
        Set3("uSunColour", p.SunColour.X, p.SunColour.Y, p.SunColour.Z);
        Set("uBounceShadowSamples", p.BounceShadowSamples);
        Set("uDoBounce", r.DoBounce ? 1 : 0);
        Set("uOffset", offset);
        Set("uCount", count);
        _gl.DispatchCompute((uint)((count + 63) / 64), 1, 1);
        _gl.MemoryBarrier(MemoryBarrierMask.ShaderStorageBarrierBit | MemoryBarrierMask.BufferUpdateBarrierBit);
        // Wait for it. That is what makes the slice timing mean anything, and it keeps every GPU command short.
        _gl.Finish();
        Check("running the lightmap kernel");
    }

    private unsafe void ReadBack(Request r)
    {
        var c = r.Chunk;
        var res = new float[c.Count * 8];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _buf[7]);
        fixed (float* dst = res)
            _gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(res.Length * 4), dst);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        Check("reading the results back");
        for (int i = 0; i < c.Count; i++)
        {
            int o = i * 8;
            c.Sun[i] = res[o];
            c.Sky[i] = res[o + 1];
            c.Bounce[i] = new System.Numerics.Vector3(res[o + 4], res[o + 5], res[o + 6]);
        }
    }

    private void Set(string name, int v) { if (_u.TryGetValue(name, out int l) && l >= 0) _gl.Uniform1(l, v); }
    private void Set(string name, float v) { if (_u.TryGetValue(name, out int l) && l >= 0) _gl.Uniform1(l, v); }
    private void Set3(string name, float x, float y, float z) { if (_u.TryGetValue(name, out int l) && l >= 0) _gl.Uniform3(l, x, y, z); }

    private unsafe void Alloc(int slot, nuint bytes)
    {
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _buf[slot]);
        _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, Math.Max(bytes, 16), null, BufferUsageARB.DynamicDraw);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    private unsafe void SubData<T>(int slot, int byteOffset, T[] data) where T : unmanaged
    {
        if (data.Length == 0) return;
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _buf[slot]);
        fixed (T* src = data)
            _gl.BufferSubData(BufferTargetARB.ShaderStorageBuffer, byteOffset, (nuint)(data.Length * sizeof(T)), src);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    public void Dispose()
    {
        if (_prog != 0) { _gl.DeleteProgram(_prog); _prog = 0; }
        foreach (var b in _buf) if (b != 0) _gl.DeleteBuffer(b);
        _available = false;
    }
}
