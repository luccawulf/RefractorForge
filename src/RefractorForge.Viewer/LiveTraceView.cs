using System.Diagnostics;
using System.Numerics;
using RefractorForge.Render;
using Silk.NET.OpenGL;

namespace RefractorForge.Viewer;

/// <summary>
/// The viewport's live traced view: the light of the Realistic Lighting window, ray-traced on the GPU every frame
/// and accumulated while nothing changes, so it starts grainy and clears up in a second or two.
///
/// <para><b>How a frame is made.</b> The rasteriser draws the terrain and the objects into a small G-buffer - each
/// object's texture colour and its distance from the camera - once per change. A compute shader then traces one
/// sample a pixel: the camera ray into the level's BVH (the same scene the CPU preview traces), the sun through its
/// disc, one hemisphere path that may bounce twice, and the placed lamps, and adds it to a running sum. A full-screen
/// pass divides that out, multiplies it onto the colour, draws the sky and the water as the CPU preview does, and
/// writes depth so gizmos and markers still sit in the scene.</para>
///
/// <para><b>The kernel</b> is a transliteration of <see cref="LiveTraceKernel"/>, which the tests hold against the CPU
/// tracer. Before the mode is first shown the GPU is run against that C# copy on a small view, and the mode refuses
/// to start if they disagree - nothing here has been run on the GPU by the person who wrote it.</para>
///
/// <para><b>Hardware.</b> Software BVH traversal in an OpenGL 4.3 compute shader, as the Ultra lightmap bake already
/// does: OpenGL has no access to RT cores. Refused on anything <see cref="GpuAdapter"/> does not call a dedicated
/// card, because a sustained load on this machine's integrated GPU has already ended in a bugcheck. Each frame is
/// split into bands of rows so no single dispatch can run long enough for Windows to reset the driver.</para>
/// </summary>
internal sealed unsafe class LiveTraceView : IDisposable
{
    // ---- the compute kernel (keep in step with Render/LiveTrace.cs) ---------------------------------------------
    public const string KernelSource = @"#version 430
layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 0) readonly buffer NodeBoundsBuf { vec4  nodeBounds[]; };
layout(std430, binding = 1) readonly buffer NodeLinksBuf  { int   nodeLinks[];  };
layout(std430, binding = 2) readonly buffer TrisBuf       { vec4  tris[];       };
layout(std430, binding = 3) readonly buffer IndexBuf      { int   triIndex[];   };
layout(std430, binding = 4) readonly buffer CutBuf        { vec4  cuts[];       };
layout(std430, binding = 5) readonly buffer MaskBuf       { uint  maskBits[];   };
layout(std430, binding = 6) buffer AccumBuf               { vec4  accum[];      };
layout(std430, binding = 7) writeonly buffer SurfBuf      { vec4  surf[];       };

uniform mat4  uInvVP;
uniform vec3  uCamPos;
uniform ivec2 uSize;
uniform int   uRowStart;
uniform uint  uFrame;
uniform int   uReset;
uniform int   uRoot;
uniform int   uTerrainTris;
uniform vec3  uToSun;
uniform vec3  uSunIrr;
uniform float uSunTanHalf;
uniform vec3  uZenith;
uniform vec3  uHorizon;
uniform vec3  uGroundRad;
uniform float uExposure;
uniform float uGamma;
uniform float uBounceGain;
uniform int   uBounces;
uniform float uWorldSize;
uniform int   uHasGround;
uniform sampler2D uGround;
uniform int   uUseRaster;
uniform sampler2D uGAlb;
uniform sampler2D uGDist;
const int MAXL = 24;
uniform int   uLampCount;
uniform vec4  uLampA[MAXL];   // position, radius
uniform vec4  uLampB[MAXL];   // colour, intensity
uniform vec4  uLampC[MAXL];   // spot direction, cos(outer) or -2
uniform vec4  uLampD[MAXL];   // cos(inner), falloff, source size, flags

const float FLT_MAX = 3.402823466e+38;
const float PI_F = 3.14159265;
const float TAU_F = 6.28318531;
const int STACK = 66;
const int MAX_VISITS = 1048576;
const float FIXTURE = 0.35;

uint mixs(uint a, uint b)
{
    uint x = a ^ (b + 0x9E3779B9u + (a << 6u) + (a >> 2u));
    x ^= x >> 16u; x *= 0x7feb352du;
    x ^= x >> 15u; x *= 0x846ca68bu;
    x ^= x >> 16u;
    return x;
}

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
    return vec3(1.0 / (d.x != 0.0 ? d.x : 1e-20), 1.0 / (d.y != 0.0 ? d.y : 1e-20), 1.0 / (d.z != 0.0 ? d.z : 1e-20));
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

bool cutSolid(int c, float u, float v)
{
    vec4 c0 = cuts[c * 2], c1 = cuts[c * 2 + 1];
    float uvx = c0.x * (1.0 - u - v) + c0.z * u + c1.x * v;
    float uvy = c0.y * (1.0 - u - v) + c0.w * u + c1.y * v;
    int first = int(c1.z), wh = int(c1.w);
    int w = wh >> 8, h = wh & 255;
    float fu = uvx - floor(uvx), fv = uvy - floor(uvy);
    int x = min(w - 1, int(fu * float(w))), y = min(h - 1, int(fv * float(h)));
    int b = y * w + x;
    return ((maskBits[first + (b >> 5)] >> uint(b & 31)) & 1u) != 0u;
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
    float t = dot(e2, qvec) * inv;
    float cut = tris[i * 4 + 3].w;
    if (cut >= 0.0 && t > 0.0 && !cutSolid(int(cut), u, v)) return -1.0;
    return t;
}

bool occluded(vec3 origin, vec3 dir, float maxDist, float skip)
{
    origin += dir * skip;
    maxDist -= skip;
    if (maxDist <= 0.0) return false;
    vec3 inv = reciprocal(dir);
    int stack[STACK];
    int sp = 0;
    int node = uRoot;
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

bool trace(vec3 origin, vec3 dir, float maxDist, float skip,
           out int tri, out float dist, out vec3 point, out vec3 normal, out vec3 alb)
{
    tri = -1; dist = 0.0; point = vec3(0.0); normal = vec3(0.0); alb = vec3(0.0);
    origin += dir * skip;
    maxDist -= skip;
    if (maxDist <= 0.0) return false;
    vec3 inv = reciprocal(dir);
    float best = maxDist;
    int bestTri = -1;
    int stack[STACK];
    int sp = 0;
    int node = uRoot;
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
    tri = bestTri; dist = best + skip; point = origin + dir * best; normal = nrm;
    alb = tris[bestTri * 4 + 3].xyz;
    return true;
}

vec3 environment(vec3 d)
{
    return d.y > 0.0 ? mix(uHorizon, uZenith, pow(clamp(d.y, 0.0, 1.0), 0.6)) : uGroundRad;
}

vec3 fromDisplay(vec3 d)
{
    if (uExposure <= 1e-9) return vec3(0.0);
    return pow(max(d, vec3(0.0)), vec3(uGamma)) / uExposure;
}

vec3 sunDisc(float e1, float e2)
{
    if (uSunTanHalf <= 0.0) return uToSun;
    vec3 u, v;
    basis(uToSun, u, v);
    float r = uSunTanHalf * sqrt(e1), a = e2 * TAU_F;
    return normalize(uToSun + u * (cos(a) * r) + v * (sin(a) * r));
}

float attenuation(int i, vec3 q)
{
    vec3 dv = q - uLampA[i].xyz;
    float d2 = dot(dv, dv), r2 = uLampA[i].w * uLampA[i].w;
    if (d2 >= r2) return 0.0;
    float dist = sqrt(d2), t = dist / uLampA[i].w;
    float window = 1.0 - t * t; window *= window;
    float a = uLampB[i].w * pow(1.0 - t, uLampD[i].y) * window;
    float co = uLampC[i].w, ci = uLampD[i].x;
    if (co > -1.5)
    {
        if (dist < 1e-4) return a;
        float cosA = dot(dv, uLampC[i].xyz) / dist;
        if (cosA <= co) return 0.0;
        if (!(cosA >= ci || ci <= co))
        {
            float k = (cosA - co) / (ci - co);
            a *= k * k * (3.0 - 2.0 * k);
        }
    }
    return a;
}

bool lampClear(vec3 q, vec3 n, vec3 lightPos)
{
    vec3 o = q + n * 0.03;
    vec3 d = lightPos - o;
    float len = length(d);
    if (len < 1e-4) return true;
    float reach = len - max(0.05, FIXTURE);
    if (reach <= 0.0) return true;
    return !occluded(o, d / len, reach, 0.01);
}

vec3 lamps(vec3 q, vec3 n, int ground, uint seed)
{
    vec3 sum = vec3(0.0);
    const float wrap = 0.15;
    for (int i = 0; i < uLampCount; i++)
    {
        int flags = int(uLampD[i].w);
        if (ground == 1 && (flags & 2) == 0) continue;
        if (ground == 2 && (flags & 4) == 0) continue;
        float a = attenuation(i, q);
        if (a <= 0.0) continue;
        vec3 toL = uLampA[i].xyz - q;
        float len = length(toL);
        float raw = len > 1e-4 ? dot(n, toL / len) : 1.0;
        if (raw <= 0.0) continue;
        float vis = 1.0;
        if ((flags & 1) != 0 && len >= 1e-4)
        {
            float size = uLampD[i].z;
            if (size <= 0.001) vis = lampClear(q, n, uLampA[i].xyz) ? 1.0 : 0.0;
            else
            {
                vec3 dir = toL / len;
                vec3 u = normalize(cross(dir, abs(dir.y) < 0.9 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0)));
                vec3 v = cross(dir, u);
                float rot = hash01(seed) * TAU_F;
                float r1 = size * sqrt(hash01(mixs(seed, 31u)));
                vis = lampClear(q, n, uLampA[i].xyz + u * (cos(rot) * r1) + v * (sin(rot) * r1)) ? 1.0 : 0.0;
            }
        }
        if (vis <= 0.0) continue;
        sum += uLampB[i].xyz * (a * (raw * (1.0 - wrap) + wrap) * vis);
    }
    return sum;
}

vec3 groundAt(vec3 q)
{
    if (uHasGround == 0) return vec3(0.45, 0.42, 0.33);
    vec2 uv = clamp(q.xz / uWorldSize, 0.0, 1.0);
    return textureLod(uGround, uv, 0.0).rgb;
}

void main()
{
    int px = int(gl_GlobalInvocationID.x);
    int py = uRowStart + int(gl_GlobalInvocationID.y);
    if (px >= uSize.x || py >= uSize.y) return;
    int idx = py * uSize.x + px;
    uint seed = mixs(mixs(uint(px), uint(py)), uFrame);

    float nx = (float(px) + 0.5) / float(uSize.x) * 2.0 - 1.0, ny = (float(py) + 0.5) / float(uSize.y) * 2.0 - 1.0;
    vec4 pa = uInvVP * vec4(nx, ny, 0.0, 1.0), pb = uInvVP * vec4(nx, ny, 1.0, 1.0);
    vec3 origin = pa.xyz / pa.w;
    vec3 dir = normalize(pb.xyz / pb.w - origin);

    vec4 prev = uReset != 0 ? vec4(0.0) : accum[idx];
    int tri; float hd; vec3 hp, n, triAlb;
    if (!trace(origin, dir, FLT_MAX, 0.0, tri, hd, hp, n, triAlb))
    {
        accum[idx] = prev + vec4(0.0, 0.0, 0.0, 1.0);
        surf[idx] = vec4(0.0, 0.0, 0.0, -1.0);
        return;
    }
    float camDist = distance(hp, uCamPos);
    bool terrain = tri < uTerrainTris;

    vec3 albedo;
    if (terrain) albedo = groundAt(hp);
    else
    {
        vec4 ra = uUseRaster != 0 ? texelFetch(uGAlb, ivec2(px, py), 0) : vec4(0.0);
        float rd = uUseRaster != 0 ? texelFetch(uGDist, ivec2(px, py), 0).r : -1.0;
        if (ra.a > 0.5 && abs(rd - camDist) < 0.3 + 0.02 * camDist) albedo = ra.rgb;
        else albedo = pow(triAlb, vec3(1.0 / 2.2));
    }

    vec3 start = hp + n * 0.02;
    vec3 e = vec3(0.0);

    float ndl = dot(n, uToSun);
    if (ndl > 0.0)
    {
        vec3 sd = sunDisc(hash01(mixs(seed, 1u)), hash01(mixs(seed, 2u)));
        if (!occluded(start, sd, FLT_MAX, 0.02)) e += uSunIrr * ndl;
    }

    {
        vec3 u, v;
        basis(n, u, v);
        float r2 = hash01(mixs(seed, 3u)), a = hash01(mixs(seed, 4u)) * TAU_F, r = sqrt(r2);
        vec3 hdir = normalize(u * (cos(a) * r) + v * (sin(a) * r) + n * sqrt(max(0.0, 1.0 - r2)));
        int btri; float bd; vec3 bp, bn, balb;
        if (!trace(start, hdir, FLT_MAX, 0.02, btri, bd, bp, bn, balb))
            e += environment(hdir) * PI_F;
        else
        {
            vec3 throughput = vec3(1.0);
            vec3 radiance = vec3(0.0);
            uint ps = mixs(seed, 5u);
            for (int depth = 0; depth < uBounces; depth++)
            {
                throughput *= balb * uBounceGain;
                if (throughput.x + throughput.y + throughput.z < 1e-4) break;
                float bl = dot(bn, uToSun);
                if (bl > 0.0)
                {
                    vec3 sd = sunDisc(hash01(mixs(ps, uint(depth * 2 + 1))), hash01(mixs(ps, uint(depth * 2 + 2))));
                    if (!occluded(bp, sd, FLT_MAX, 0.02)) radiance += throughput * uSunIrr * (bl / PI_F);
                }
                if (uLampCount > 0)
                {
                    vec3 lamp = lamps(bp, bn, 0, mixs(ps, uint(depth * 2 + 201)));
                    if (lamp.x + lamp.y + lamp.z > 0.0) radiance += throughput * fromDisplay(lamp) * (1.0 / PI_F);
                }
                vec3 hu, hv;
                basis(bn, hu, hv);
                float e1 = hash01(mixs(ps, uint(depth * 2 + 101))), e2 = hash01(mixs(ps, uint(depth * 2 + 102)));
                float hr = sqrt(e1), ha = e2 * TAU_F;
                vec3 next = normalize(hu * (cos(ha) * hr) + hv * (sin(ha) * hr) + bn * sqrt(max(0.0, 1.0 - e1)));
                int ntri; float nd; vec3 np, nn, nalb;
                if (!trace(bp, next, FLT_MAX, 0.02, ntri, nd, np, nn, nalb))
                {
                    radiance += throughput * environment(next);
                    break;
                }
                bp = np; bn = nn; balb = nalb;
            }
            e += radiance * PI_F;
        }
    }

    if (uLampCount > 0)
        e += fromDisplay(lamps(start, n, terrain ? 1 : 2, mixs(seed, 6u)));

    accum[idx] = prev + vec4(e, 1.0);
    surf[idx] = vec4(albedo, camDist);
}
";

    // ---- the G-buffer: object colour and distance ------------------------------------------------------------
    public const string GBufVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=2) in vec2 aUv;
uniform mat4 uMVP; uniform mat4 uModel; out vec2 vUv; out vec3 vWorld;
void main(){ gl_Position = uMVP * vec4(aPos, 1.0); vUv = aUv; vWorld = (uModel * vec4(aPos, 1.0)).xyz; }";

    public const string GBufFrag = @"#version 330 core
in vec2 vUv; in vec3 vWorld;
uniform int uKind; uniform int uUseTex; uniform sampler2D uTex; uniform vec3 uColor;
uniform int uAlphaTest; uniform float uAlphaRef; uniform vec3 uCamPos;
layout(location=0) out vec4 gAlb;
layout(location=1) out vec4 gDist;
void main(){
    if (uKind == 1) {
        vec4 tc = texture(uTex, vUv);
        vec3 base = (uUseTex == 1) ? tc.rgb : uColor;
        float a = (uUseTex == 1) ? tc.a : 1.0;
        if (uAlphaTest == 2) discard;                     // glass: the trace looks through it
        if (uAlphaRef > 0.0 && a < uAlphaRef) discard;
        gAlb = vec4(base, 1.0);
    } else gAlb = vec4(0.0);
    gDist = vec4(length(vWorld - uCamPos), 0.0, 0.0, 1.0);
}";

    // ---- the composite ------------------------------------------------------------------------------------------
    public const string CompVert = @"#version 330 core
void main(){
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    gl_Position = vec4(p, 0.0, 1.0);
}";

    public const string CompFrag = @"#version 330 core
uniform sampler2D uAccum; uniform sampler2D uSurf;
uniform mat4 uInvVP; uniform mat4 uVP; uniform vec3 uCamPos; uniform vec2 uWin;
uniform float uExposure; uniform float uInvGamma;
uniform vec3 uZenith; uniform vec3 uHorizon; uniform vec3 uGroundRad;
uniform int uHasWater; uniform float uWaterLevel;
out vec4 frag;
vec3 env(vec3 d){ return d.y > 0.0 ? mix(uHorizon, uZenith, pow(clamp(d.y, 0.0, 1.0), 0.6)) : uGroundRad; }
vec3 toDisp(vec3 l){ return pow(max(l * uExposure, vec3(0.0)), vec3(uInvGamma)); }
vec3 sky(vec3 d){ return min(toDisp(env(vec3(d.x, max(d.y, 0.001), d.z)) * 2.4), vec3(1.0)); }
void main(){
    vec2 uv = gl_FragCoord.xy / uWin;
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 pa = uInvVP * vec4(ndc, 0.0, 1.0), pb = uInvVP * vec4(ndc, 1.0, 1.0);
    vec3 o = pa.xyz / pa.w;
    vec3 dir = normalize(pb.xyz / pb.w - o);
    vec4 s = texture(uSurf, uv);
    vec4 acc = texture(uAccum, uv);
    bool hit = s.a >= 0.0;
    float t = s.a;
    vec3 surface = hit ? s.rgb * toDisp(acc.rgb / max(acc.a, 1.0)) : vec3(0.0);
    vec3 c = hit ? surface : sky(dir);
    if (uHasWater == 1 && dir.y < -1e-4 && uCamPos.y > uWaterLevel) {
        float tw = (uWaterLevel - uCamPos.y) / dir.y;
        if (tw > 0.0 && (!hit || tw < t)) {
            vec3 under = hit ? surface : vec3(0.05, 0.08, 0.09);
            vec3 mirror = sky(vec3(dir.x, -dir.y, dir.z));
            float fres = 0.08 + 0.92 * pow(1.0 - min(1.0, -dir.y), 4.0);
            c = mix(under * vec3(0.45, 0.6, 0.6), mirror, clamp(fres, 0.0, 0.85));
            t = tw; hit = true;
        }
    }
    frag = vec4(clamp(c, 0.0, 1.0), 1.0);
    if (hit) {
        vec4 clip = uVP * vec4(uCamPos + dir * t, 1.0);
        gl_FragDepth = clamp(clip.z / clip.w * 0.5 + 0.5, 0.0, 1.0);
    } else gl_FragDepth = 1.0;
}";

    // ---- state ---------------------------------------------------------------------------------------------------
    private readonly GL _gl;
    private uint _kernel, _gbuf, _comp, _emptyVao;
    private readonly uint[] _ssbo = new uint[8];
    private readonly Dictionary<string, int> _uk = new(), _ug = new(), _uc = new();
    private uint _fbo, _gAlb, _gDist, _gDepth, _accumTex, _surfTex, _groundTex;
    private int _w, _h;
    private readonly uint[] _queries = new uint[4];
    private int _qNext;
    private readonly bool[] _qPending = new bool[4];

    /// <summary>The card can run the mode (a dedicated GPU, compute shaders, all three programs built).</summary>
    public bool Available { get; private set; }
    /// <summary>The card's name, or why the mode cannot run.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The scene on the GPU, or null.</summary>
    public LiveTraceScene? Scene { get; private set; }
    private bool _hasGround;
    /// <summary>The GPU against the C# kernel: null until run, then whether it passed and the numbers.</summary>
    public bool? SelfTestPassed { get; private set; }
    public string SelfTestReport { get; private set; } = "";

    /// <summary>Samples per pixel so far.</summary>
    public int Samples { get; private set; }
    /// <summary>The GPU time of the last measured frame, milliseconds.</summary>
    public double LastGpuMs { get; private set; }
    /// <summary>Stop adding samples after this many - the picture no longer changes and the GPU can rest.</summary>
    public int SampleCap = 2048;
    public int Width => _w;
    public int Height => _h;
    private uint _frame;
    private bool _needGBuffer = true;

    public LiveTraceView(GL gl)
    {
        _gl = gl;
        try { Init(); }
        catch (Exception ex) { Available = false; Status = "The traced view is unavailable: " + ex.Message; }
    }

    private void Init()
    {
        int major = _gl.GetInteger(GetPName.MajorVersion), minor = _gl.GetInteger(GetPName.MinorVersion);
        bool core43 = major > 4 || (major == 4 && minor >= 3);
        if (!core43 && !(HasExtension("GL_ARB_compute_shader") && HasExtension("GL_ARB_shader_storage_buffer_object")))
        {
            Status = $"The traced view needs OpenGL 4.3 compute shaders; this context is {major}.{minor}.";
            return;
        }
        string renderer = _gl.GetStringS(StringName.Renderer) ?? "";
        if (!GpuAdapter.LooksDiscrete(renderer))
        {
            Status = $"The traced view needs a dedicated graphics card; the editor is running on \"{renderer}\". " +
                     "On a laptop with two GPUs, set RefractorForge.exe to High performance in Windows Settings > System > " +
                     "Display > Graphics (or in the NVIDIA Control Panel) and restart the editor.";
            return;
        }
        int blocks = _gl.GetInteger((GetPName)GLEnum.MaxComputeShaderStorageBlocks);
        if (blocks < 8) { Status = $"The traced view needs 8 storage buffers in a compute shader; this card allows {blocks}."; return; }

        _kernel = Compute(KernelSource, out string err);
        if (_kernel == 0) { Status = "The traced view's kernel failed to build: " + err; Console.WriteLine(Status); return; }
        _gbuf = Program2(GBufVert, GBufFrag, out err);
        if (_gbuf == 0) { Status = "The traced view's G-buffer shader failed to build: " + err; Console.WriteLine(Status); return; }
        _comp = Program2(CompVert, CompFrag, out err);
        if (_comp == 0) { Status = "The traced view's composite shader failed to build: " + err; Console.WriteLine(Status); return; }

        foreach (var n in new[] { "uInvVP", "uCamPos", "uSize", "uRowStart", "uFrame", "uReset", "uRoot", "uTerrainTris", "uToSun",
                                  "uSunIrr", "uSunTanHalf", "uZenith", "uHorizon", "uGroundRad", "uExposure", "uGamma",
                                  "uBounceGain", "uBounces", "uWorldSize", "uHasGround", "uGround", "uUseRaster", "uGAlb",
                                  "uGDist", "uLampCount", "uLampA", "uLampB", "uLampC", "uLampD" })
            _uk[n] = _gl.GetUniformLocation(_kernel, n);
        foreach (var n in new[] { "uMVP", "uModel", "uKind", "uUseTex", "uTex", "uColor", "uAlphaTest", "uAlphaRef", "uCamPos" })
            _ug[n] = _gl.GetUniformLocation(_gbuf, n);
        foreach (var n in new[] { "uAccum", "uSurf", "uInvVP", "uVP", "uCamPos", "uWin", "uExposure", "uInvGamma", "uZenith",
                                  "uHorizon", "uGroundRad", "uHasWater", "uWaterLevel" })
            _uc[n] = _gl.GetUniformLocation(_comp, n);

        for (int i = 0; i < _ssbo.Length; i++) _ssbo[i] = _gl.GenBuffer();
        _emptyVao = _gl.GenVertexArray();
        for (int i = 0; i < _queries.Length; i++) _queries[i] = _gl.GenQuery();
        Status = $"{renderer} (OpenGL {major}.{minor})";
        Available = true;
    }

    private bool HasExtension(string name)
    {
        int n = _gl.GetInteger(GetPName.NumExtensions);
        for (int i = 0; i < n; i++)
            if (string.Equals(_gl.GetStringS(StringName.Extensions, (uint)i), name, StringComparison.Ordinal)) return true;
        return false;
    }

    private uint Compute(string src, out string error)
    {
        error = "";
        uint cs = _gl.CreateShader(ShaderType.ComputeShader);
        _gl.ShaderSource(cs, src);
        _gl.CompileShader(cs);
        _gl.GetShader(cs, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) { error = _gl.GetShaderInfoLog(cs); _gl.DeleteShader(cs); return 0; }
        uint p = _gl.CreateProgram();
        _gl.AttachShader(p, cs);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        _gl.DetachShader(p, cs); _gl.DeleteShader(cs);
        if (linked == 0) { error = _gl.GetProgramInfoLog(p); _gl.DeleteProgram(p); return 0; }
        return p;
    }

    private uint Program2(string vs, string fs, out string error)
    {
        error = "";
        uint v = _gl.CreateShader(ShaderType.VertexShader), f = _gl.CreateShader(ShaderType.FragmentShader);
        _gl.ShaderSource(v, vs); _gl.CompileShader(v);
        _gl.GetShader(v, ShaderParameterName.CompileStatus, out int okv);
        _gl.ShaderSource(f, fs); _gl.CompileShader(f);
        _gl.GetShader(f, ShaderParameterName.CompileStatus, out int okf);
        if (okv == 0 || okf == 0)
        {
            error = (okv == 0 ? _gl.GetShaderInfoLog(v) : "") + (okf == 0 ? _gl.GetShaderInfoLog(f) : "");
            _gl.DeleteShader(v); _gl.DeleteShader(f);
            return 0;
        }
        uint p = _gl.CreateProgram();
        _gl.AttachShader(p, v); _gl.AttachShader(p, f);
        _gl.LinkProgram(p);
        _gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
        _gl.DetachShader(p, v); _gl.DetachShader(p, f); _gl.DeleteShader(v); _gl.DeleteShader(f);
        if (linked == 0) { error = _gl.GetProgramInfoLog(p); _gl.DeleteProgram(p); return 0; }
        return p;
    }

    private void Check(string what)
    {
        var e = _gl.GetError();
        if (e != GLEnum.NoError) throw new InvalidOperationException($"OpenGL error {e} while {what}");
    }

    private void Upload<T>(int slot, T[] data) where T : unmanaged
    {
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssbo[slot]);
        if (data.Length == 0) _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, 16, null, BufferUsageARB.StaticDraw);
        else fixed (T* p = data) _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)(data.Length * sizeof(T)), p, BufferUsageARB.StaticDraw);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    /// <summary>Put a packed scene and the ground picture on the GPU.</summary>
    public void SetScene(LiveTraceScene scene, Texture2D? ground)
    {
        if (!Available) return;
        for (int i = 0; i < 32 && _gl.GetError() != GLEnum.NoError; i++) { }
        Upload(0, scene.NodeBounds); Upload(1, scene.NodeLinks); Upload(2, scene.Tris);
        Upload(3, scene.Index); Upload(4, scene.Cuts); Upload(5, scene.MaskBits);
        if (_groundTex == 0) _groundTex = _gl.GenTexture();
        _hasGround = ground is not null;
        _gl.BindTexture(TextureTarget.Texture2D, _groundTex);
        if (ground is not null)
            fixed (byte* p = ground.Rgba)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)ground.Width, (uint)ground.Height, 0,
                               PixelFormat.Rgba, PixelType.UnsignedByte, p);
        else
        {
            var one = new byte[] { 115, 107, 84, 255 };
            fixed (byte* p = one)
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }
        Linear(TextureMinFilter.Linear, TextureMagFilter.Linear);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        Check("uploading the traced scene");
        Scene = scene;
        Reset();
    }

    private void Linear(TextureMinFilter min, TextureMagFilter mag)
    {
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)min);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)mag);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    }

    /// <summary>
    /// Let go of the CPU's copy of the scene once the GPU has it - on a big level it is hundreds of megabytes. Only
    /// what a frame still reads (the root and where the terrain ends) is kept; the self-test needs the full copy, so
    /// this comes after it.
    /// </summary>
    public void ReleaseCpuCopy()
    {
        if (Scene is null) return;
        Scene = new LiveTraceScene { Root = Scene.Root, TerrainTris = Scene.TerrainTris };
    }

    /// <summary>Start the picture again: the camera, the light or the level changed.</summary>
    public void Reset() { Samples = 0; _needGBuffer = true; }

    // Internal targets: the G-buffer and the two textures the composite reads, at the traced resolution.
    private void EnsureTargets(int w, int h)
    {
        if (w == _w && h == _h && _fbo != 0) return;
        _w = w; _h = h;
        if (_fbo == 0)
        {
            _fbo = _gl.GenFramebuffer(); _gAlb = _gl.GenTexture(); _gDist = _gl.GenTexture(); _gDepth = _gl.GenRenderbuffer();
            _accumTex = _gl.GenTexture(); _surfTex = _gl.GenTexture();
        }
        _gl.BindTexture(TextureTarget.Texture2D, _gAlb);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        Linear(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        _gl.BindTexture(TextureTarget.Texture2D, _gDist);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R32f, (uint)w, (uint)h, 0, PixelFormat.Red, PixelType.Float, null);
        Linear(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        foreach (var t in new[] { _accumTex, _surfTex })
        {
            _gl.BindTexture(TextureTarget.Texture2D, t);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba32f, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.Float, null);
            Linear(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
        }
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _gDepth);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)w, (uint)h);
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, 0);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _gAlb, 0);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment1, TextureTarget.Texture2D, _gDist, 0);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, _gDepth);
        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        if (status != GLEnum.FramebufferComplete) throw new InvalidOperationException("the traced view's G-buffer is incomplete: " + status);
        AllocFrame(w, h);
        Reset();
    }

    private void AllocFrame(int w, int h)
    {
        foreach (int slot in new[] { 6, 7 })
        {
            _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssbo[slot]);
            _gl.BufferData(BufferTargetARB.ShaderStorageBuffer, (nuint)Math.Max(16, w * h * 16), null, BufferUsageARB.DynamicCopy);
        }
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
    }

    /// <summary>The G-buffer's program and its uniform locations, for the viewer's draw calls.</summary>
    public uint GBufferProgram => _gbuf;
    public int GBufferUniform(string name) => _ug.TryGetValue(name, out int l) ? l : -1;

    /// <summary>
    /// Make this frame's picture and draw it over the whole framebuffer, depth included.
    /// </summary>
    /// <param name="drawGeometry">Draws the terrain (uKind 0) and the objects (uKind 1) with
    /// <see cref="GBufferProgram"/>, the given view-projection and camera position.</param>
    public void Frame(LiveTraceKernel.Params p, Matrix4x4 viewProj, int winW, int winH, float? waterLevel,
                      Action<Matrix4x4> drawGeometry)
    {
        if (!Available || Scene is null) return;
        // The editor's own drawing can leave an error flag set; clear it so a check below reports only what this did.
        for (int i = 0; i < 32 && _gl.GetError() != GLEnum.NoError; i++) { }
        EnsureTargets(p.Width, p.Height);
        int prevProg = _gl.GetInteger(GetPName.CurrentProgram);
        _gl.GetInteger(GLEnum.DepthFunc, out int prevDepthFunc);
        ReadTimers();

        if (Samples < SampleCap)
        {
            if (_needGBuffer)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
                _gl.DrawBuffers(2, new[] { DrawBufferMode.ColorAttachment0, DrawBufferMode.ColorAttachment1 });
                _gl.Viewport(0, 0, (uint)_w, (uint)_h);
                _gl.Disable(EnableCap.Blend);
                _gl.Enable(EnableCap.DepthTest);
                _gl.DepthMask(true);
                float* clear = stackalloc float[4];
                _gl.GetFloat(GetPName.ColorClearValue, clear);
                _gl.ClearColor(0f, 0f, 0f, 0f);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                _gl.ClearColor(clear[0], clear[1], clear[2], clear[3]);   // the editor clears its own frame with this
                _gl.UseProgram(_gbuf);
                Set3(_ug, "uCamPos", p.CamPos);
                Set(_ug, "uTex", 0);
                drawGeometry(viewProj);
                _gl.DepthMask(true);
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                _gl.Viewport(0, 0, (uint)winW, (uint)winH);
                _needGBuffer = false;
            }
            bool reset = Samples == 0;
            if (reset) _frame = 0;
            BeginTimer();
            Dispatch(p, _frame, reset, useRaster: true);
            EndTimer();
            _frame++;
            Samples++;
            _gl.MemoryBarrier(MemoryBarrierMask.PixelBufferBarrierBit | MemoryBarrierMask.TextureUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
            CopyToTexture(6, _accumTex);
            CopyToTexture(7, _surfTex);
        }

        // The composite, over the whole window.
        _gl.UseProgram(_comp);
        _gl.ActiveTexture(TextureUnit.Texture0); _gl.BindTexture(TextureTarget.Texture2D, _accumTex);
        _gl.ActiveTexture(TextureUnit.Texture1); _gl.BindTexture(TextureTarget.Texture2D, _surfTex);
        _gl.ActiveTexture(TextureUnit.Texture0);
        Set(_uc, "uAccum", 0); Set(_uc, "uSurf", 1);
        Matrix4x4.Invert(viewProj, out var inv);
        SetMat(_uc, "uInvVP", inv); SetMat(_uc, "uVP", viewProj);
        Set3(_uc, "uCamPos", p.CamPos);
        if (_uc["uWin"] >= 0) _gl.Uniform2(_uc["uWin"], (float)winW, (float)winH);
        Set(_uc, "uExposure", p.Exposure); Set(_uc, "uInvGamma", 1f / p.Gamma);
        Set3(_uc, "uZenith", p.SkyZenith); Set3(_uc, "uHorizon", p.SkyHorizon); Set3(_uc, "uGroundRad", p.GroundRadiance);
        Set(_uc, "uHasWater", waterLevel is null ? 0 : 1); Set(_uc, "uWaterLevel", waterLevel ?? 0f);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Always);
        _gl.DepthMask(true);
        _gl.BindVertexArray(_emptyVao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        _gl.DepthFunc((DepthFunction)prevDepthFunc);
        _gl.BindVertexArray(0);
        _gl.UseProgram((uint)prevProg);
        Check("drawing the traced view");
    }

    // A frame is split into bands of rows, each its own dispatch, so that no single GPU command runs long enough
    // for Windows to reset the driver (about two seconds) however big the level is.
    private void Dispatch(LiveTraceKernel.Params p, uint frame, bool reset, bool useRaster)
    {
        var s = Scene!;
        _gl.UseProgram(_kernel);
        for (uint i = 0; i < _ssbo.Length; i++) _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, i, _ssbo[i]);
        _gl.ActiveTexture(TextureUnit.Texture0); _gl.BindTexture(TextureTarget.Texture2D, _groundTex);
        _gl.ActiveTexture(TextureUnit.Texture1); _gl.BindTexture(TextureTarget.Texture2D, _gAlb);
        _gl.ActiveTexture(TextureUnit.Texture2); _gl.BindTexture(TextureTarget.Texture2D, _gDist);
        _gl.ActiveTexture(TextureUnit.Texture0);
        Set(_uk, "uGround", 0); Set(_uk, "uGAlb", 1); Set(_uk, "uGDist", 2);
        SetMat(_uk, "uInvVP", p.InvViewProj);
        Set3(_uk, "uCamPos", p.CamPos);
        if (_uk["uSize"] >= 0) _gl.Uniform2(_uk["uSize"], p.Width, p.Height);
        if (_uk["uFrame"] >= 0) _gl.Uniform1(_uk["uFrame"], frame);
        Set(_uk, "uReset", reset ? 1 : 0);
        Set(_uk, "uRoot", s.Root); Set(_uk, "uTerrainTris", s.TerrainTris);
        Set3(_uk, "uToSun", p.ToSun); Set3(_uk, "uSunIrr", p.SunIrradiance); Set(_uk, "uSunTanHalf", p.SunTanHalf);
        Set3(_uk, "uZenith", p.SkyZenith); Set3(_uk, "uHorizon", p.SkyHorizon); Set3(_uk, "uGroundRad", p.GroundRadiance);
        Set(_uk, "uExposure", p.Exposure); Set(_uk, "uGamma", p.Gamma);
        Set(_uk, "uBounceGain", p.BounceGain); Set(_uk, "uBounces", p.Bounces);
        Set(_uk, "uWorldSize", p.WorldSize); Set(_uk, "uHasGround", _hasGround ? 1 : 0);
        Set(_uk, "uUseRaster", useRaster ? 1 : 0);
        int nl = Math.Min(p.Lamps.Length, LiveTraceKernel.MaxLamps);
        Set(_uk, "uLampCount", nl);
        if (nl > 0)
        {
            var a = new float[nl * 4]; var b = new float[nl * 4]; var c = new float[nl * 4]; var d = new float[nl * 4];
            for (int i = 0; i < nl; i++)
            {
                var l = p.Lamps[i]; int o = i * 4;
                a[o] = l.Position.X; a[o + 1] = l.Position.Y; a[o + 2] = l.Position.Z; a[o + 3] = l.Radius;
                b[o] = l.Colour.X; b[o + 1] = l.Colour.Y; b[o + 2] = l.Colour.Z; b[o + 3] = l.Intensity;
                c[o] = l.SpotDir.X; c[o + 1] = l.SpotDir.Y; c[o + 2] = l.SpotDir.Z; c[o + 3] = l.CosOuter;
                d[o] = l.CosInner; d[o + 1] = l.Falloff; d[o + 2] = l.SourceSize; d[o + 3] = l.Flags;
            }
            fixed (float* pa = a) if (_uk["uLampA"] >= 0) _gl.Uniform4(_uk["uLampA"], (uint)nl, pa);
            fixed (float* pb = b) if (_uk["uLampB"] >= 0) _gl.Uniform4(_uk["uLampB"], (uint)nl, pb);
            fixed (float* pc = c) if (_uk["uLampC"] >= 0) _gl.Uniform4(_uk["uLampC"], (uint)nl, pc);
            fixed (float* pd = d) if (_uk["uLampD"] >= 0) _gl.Uniform4(_uk["uLampD"], (uint)nl, pd);
        }
        const int band = 128;
        for (int row = 0; row < p.Height; row += band)
        {
            Set(_uk, "uRowStart", row);
            int rows = Math.Min(band, p.Height - row);
            _gl.DispatchCompute((uint)((p.Width + 7) / 8), (uint)((rows + 7) / 8), 1);
            _gl.Flush();
        }
        for (uint i = 0; i < _ssbo.Length; i++) _gl.BindBufferBase(BufferTargetARB.ShaderStorageBuffer, i, 0);
        Check("running the traced view's kernel");
    }

    private void CopyToTexture(int slot, uint tex)
    {
        _gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, _ssbo[slot]);
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, (uint)_w, (uint)_h, PixelFormat.Rgba, PixelType.Float, (void*)0);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        // Left bound, this would turn every later texture upload in the editor into a read from this buffer.
        _gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
    }

    // ---- GPU timing ---------------------------------------------------------------------------------------------
    private void BeginTimer()
    {
        int q = _qNext;
        if (_qPending[q]) return;               // still waiting on that one: skip timing this frame
        _gl.BeginQuery(QueryTarget.TimeElapsed, _queries[q]);
        _qPending[q] = true;
        _timing = true;
    }
    private bool _timing;
    private void EndTimer()
    {
        if (!_timing) return;
        _gl.EndQuery(QueryTarget.TimeElapsed);
        _timing = false;
        _qNext = (_qNext + 1) % _queries.Length;
    }
    private void ReadTimers()
    {
        for (int i = 0; i < _queries.Length; i++)
        {
            if (!_qPending[i]) continue;
            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.ResultAvailable, out int ready);
            if (ready == 0) continue;
            _gl.GetQueryObject(_queries[i], QueryObjectParameterName.Result, out ulong ns);
            LastGpuMs = ns / 1e6;
            _qPending[i] = false;
        }
    }

    // ---- the self-test ------------------------------------------------------------------------------------------

    /// <summary>
    /// Run the GPU kernel and its C# copy on the same small view, with the same random numbers, and compare. The
    /// camera looks down on the middle of the scene, so the view is all surfaces. A float that rounds differently on
    /// the GPU can send one path another way, so the bar is statistical, like the lightmap bake's GPU test.
    /// </summary>
    public bool SelfTest(LiveTraceKernel.Params live)
    {
        if (!Available || Scene is null) return false;
        var s = Scene;
        const int w = 48, h = 32, frames = 4;
        // Look down at the middle of the scene from above its highest point.
        var lo = new Vector3(s.NodeBounds[s.Root * 8], s.NodeBounds[s.Root * 8 + 1], s.NodeBounds[s.Root * 8 + 2]);
        var hi = new Vector3(s.NodeBounds[s.Root * 8 + 4], s.NodeBounds[s.Root * 8 + 5], s.NodeBounds[s.Root * 8 + 6]);
        var mid = (lo + hi) * 0.5f;
        float span = MathF.Max(hi.X - lo.X, hi.Z - lo.Z);
        var cam = new Camera { Position = new Vector3(mid.X - span * 0.05f, hi.Y + span * 0.08f, mid.Z - span * 0.05f),
                               Aspect = w / (float)h, MirrorX = true, Near = 1f, Far = span * 4f };
        cam.LookAt(new Vector3(mid.X, (lo.Y + hi.Y) * 0.5f, mid.Z));
        var p = live with
        {
            InvViewProj = Inverse(cam.ViewProjection), CamPos = cam.Position, Width = w, Height = h,
        };
        var sw = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < 32 && _gl.GetError() != GLEnum.NoError; i++) { }
            int prevProg = _gl.GetInteger(GetPName.CurrentProgram);
            EnsureTargets(w, h);
            for (uint f = 0; f < frames; f++) Dispatch(p, f, f == 0, useRaster: false);
            _gl.MemoryBarrier(MemoryBarrierMask.BufferUpdateBarrierBit | MemoryBarrierMask.ShaderStorageBarrierBit);
            _gl.Finish();
            double gpuMs = sw.Elapsed.TotalMilliseconds;
            var acc = Read(6, w * h * 4); var sf = Read(7, w * h * 4);
            _gl.UseProgram((uint)prevProg);

            // The C# side of the comparison, spread over the processor's cores: on a big level it is a few seconds.
            var cpuLight = new Vector3[w * h]; var cpuDist = new float[w * h];
            Parallel.For(0, w * h, i =>
            {
                var e = Vector3.Zero; LiveTraceKernel.Sample last = default;
                for (uint f = 0; f < frames; f++) { last = LiveTraceKernel.Shade(s, p, i % w, i / w, f, null); e += last.Light; }
                cpuLight[i] = e; cpuDist[i] = last.Distance;
            });
            int surfaces = 0, missMatch = 0, over = 0;
            double sumAbs = 0;
            for (int py = 0; py < h; py++)
                for (int px = 0; px < w; px++)
                {
                    var e = cpuLight[py * w + px];
                    var last = new LiveTraceKernel.Sample(e, Vector3.Zero, cpuDist[py * w + px]);
                    int o = (py * w + px) * 4;
                    bool cpuHit = last.Distance >= 0f, gpuHit = sf[o + 3] >= 0f;
                    if (cpuHit != gpuHit || (cpuHit && MathF.Abs(sf[o + 3] - last.Distance) > 1e-3f * last.Distance + 1e-3f)) { missMatch++; continue; }
                    if (!cpuHit) continue;
                    surfaces++;
                    float cpu = EngineLight.Luma(LiveTraceKernel.ToDisplay(p, e / frames));
                    float gpu = EngineLight.Luma(LiveTraceKernel.ToDisplay(p, new Vector3(acc[o], acc[o + 1], acc[o + 2]) / MathF.Max(acc[o + 3], 1f)));
                    float d = MathF.Abs(cpu - gpu);
                    sumAbs += d;
                    if (d > 24f / 255f) over++;
                    if (MathF.Abs(acc[o + 3] - frames) > 0.01f) missMatch++;
                }
            double mean = surfaces == 0 ? 0 : sumAbs / surfaces * 255.0;
            double overShare = surfaces == 0 ? 0 : (double)over / surfaces;
            bool pass = surfaces >= w * h / 4 && missMatch <= w * h / 100 && mean < 3.0 && overShare < 0.02;
            SelfTestPassed = pass;
            SelfTestReport = $"{(pass ? "passed" : "FAILED")}: {surfaces} surface pixels, {missMatch} hit mismatches, mean difference " +
                             $"{mean:0.00} of 255, {overShare * 100:0.0}% over 24; GPU {gpuMs:0} ms for {w}x{h}x{frames}";
            Console.WriteLine("Traced view self-test " + SelfTestReport);
            Reset();
            _w = 0; _h = 0;                       // the real size is set again on the next frame
            return pass;
        }
        catch (Exception ex)
        {
            SelfTestPassed = false;
            SelfTestReport = "FAILED: " + ex.Message;
            Console.WriteLine("Traced view self-test " + SelfTestReport);
            return false;
        }
    }

    private float[] Read(int slot, int floats)
    {
        var r = new float[floats];
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, _ssbo[slot]);
        fixed (float* dst = r) _gl.GetBufferSubData(BufferTargetARB.ShaderStorageBuffer, 0, (nuint)(floats * 4), dst);
        _gl.BindBuffer(BufferTargetARB.ShaderStorageBuffer, 0);
        Check("reading the traced view back");
        return r;
    }

    private static Matrix4x4 Inverse(Matrix4x4 m) { Matrix4x4.Invert(m, out var inv); return inv; }

    // ---- uniforms -----------------------------------------------------------------------------------------------
    private void Set(Dictionary<string, int> u, string n, int v) { if (u.TryGetValue(n, out int l) && l >= 0) _gl.Uniform1(l, v); }
    private void Set(Dictionary<string, int> u, string n, float v) { if (u.TryGetValue(n, out int l) && l >= 0) _gl.Uniform1(l, v); }
    private void Set3(Dictionary<string, int> u, string n, Vector3 v) { if (u.TryGetValue(n, out int l) && l >= 0) _gl.Uniform3(l, v.X, v.Y, v.Z); }
    private void SetMat(Dictionary<string, int> u, string n, Matrix4x4 m) { if (u.TryGetValue(n, out int l) && l >= 0) _gl.UniformMatrix4(l, 1, false, (float*)&m); }

    public void Dispose()
    {
        foreach (var p in new[] { _kernel, _gbuf, _comp }) if (p != 0) _gl.DeleteProgram(p);
        foreach (var b in _ssbo) if (b != 0) _gl.DeleteBuffer(b);
        foreach (var t in new[] { _gAlb, _gDist, _accumTex, _surfTex, _groundTex }) if (t != 0) _gl.DeleteTexture(t);
        if (_gDepth != 0) _gl.DeleteRenderbuffer(_gDepth);
        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        if (_emptyVao != 0) _gl.DeleteVertexArray(_emptyVao);
        foreach (var q in _queries) if (q != 0) _gl.DeleteQuery(q);
        Available = false;
    }
}
