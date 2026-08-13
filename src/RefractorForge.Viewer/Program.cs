using System.Numerics;
using System.Reflection;
using System.Text.Json;
using RefractorForge.Formats.Con;
using RefractorForge.Formats.Editing;
using RefractorForge.Formats.Geometry;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Sound;
using RefractorForge.Formats.Terrain;
using RefractorForge.Render;
using RefractorForge.Viewer;
using RefractorForge.Collab;
using Message = RefractorForge.Collab.Message;   // disambiguate from System.Windows.Forms.Message
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using ImGuiNET;
using ImGui = ImGuiNET.ImGui;
using RefractorForge.Formats.Rfa;   // disambiguate from the Silk.NET.OpenGL.Extensions.ImGui namespace

// ============================================================================
// RefractorForge interactive viewer/editor.
//   (no arguments)                 choose the level folder + .rfa archives via dialogs;
//                                  the choice is remembered (refractorforge.json) for next time
//   <levelDir> [mesh.rfa ...]      open explicitly (e.g. from a script)
//   --pick                         force the picker even if a level was remembered
//
// Camera : RIGHT MOUSE drag to look, WASD move, Q/E down/up, Shift fast, SCROLL = zoom,
//          F = focus the selected object. Move/zoom scale with height.
// Edit   : LEFT-CLICK to select; arrow keys nudge; Delete removes; Z undo, Y redo,
//          F5 saves StaticObjects.con (lossless).
//
// Mesh/camera/picking come from RefractorForge.Render; edits from RefractorForge.Formats.Editing.
// The GL/window wiring and the WinForms path pickers live here.
// ============================================================================

// Headless CENTRAL relay mode: `--relay <port> [seedLevelFolder | StaticObjects.con | level.rfa] [--save <file>]`.
// Runs ONLY the collaboration relay - no GL window - so a group can all JOIN one always-on server instead of one
// person hosting (whose local document would otherwise overwrite everyone else's on connect). With --save the
// canonical document is persisted to <file> and RESUMED from it next start, so edits survive a server restart.
// Console diagnostics include non-ASCII (em-dashes etc.); render them as UTF-8 instead of the default code page,
// which garbles them. Harmless (try/catch) when there's no console.
// A self-contained launch folder's deps.json is generated at publish time; a dependency added later and deployed as a
// LOOSE DLL (e.g. NAudio for sound playback) is NOT in that deps.json, so the runtime won't resolve it even though the
// DLL sits right beside the exe -> the first use of its types hard-crashes the process (uncatchable at the call site).
// Resolve any such assembly from the app base directory so loose-deployed dependencies always load (no republish needed).
System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
    try { var p = System.IO.Path.Combine(System.AppContext.BaseDirectory, name.Name + ".dll"); return System.IO.File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null; }
    catch { return null; }
};

// A WinExe has no console, so an unhandled exception terminates the process SILENTLY - the editor just never
// appears and the user has nothing to report. Surface it instead: show the message and write it next to the exe.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    var ex = e.ExceptionObject as Exception;
    var text = ex?.ToString() ?? "unknown error";
    try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.AppContext.BaseDirectory, "crash.log"), text); } catch { }
    try
    {
        Picker.Error("RefractorForge hit an unexpected error and has to close:\n\n" +
                     (ex?.Message ?? "unknown error") +
                     "\n\nThe full details were written to crash.log next to the editor.",
                     "RefractorForge - crash");
    }
    catch { }
};

// Headless relay (WinExe has no console): re-attach the launching terminal's console so its output/input work.
if (args.Length >= 1 && args[0] == "--relay") ConsoleLog.AttachParentConsole();
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
ConsoleLog.Install();   // tee console output into an in-app Log/Errors box (shown instead of relying on the CMD)

if (args.Length >= 1 && args[0] == "--relay")
{
    var relayArgs = args.Skip(1).ToList();
    int saveIdx = relayArgs.IndexOf("--save");
    string? savePath = null;
    if (saveIdx >= 0) { savePath = saveIdx + 1 < relayArgs.Count ? relayArgs[saveIdx + 1] : null; relayArgs.RemoveRange(saveIdx, savePath is null ? 1 : 2); }
    int passIdx = relayArgs.IndexOf("--pass");
    string? relayPass = null;
    if (passIdx >= 0) { relayPass = passIdx + 1 < relayArgs.Count ? relayArgs[passIdx + 1] : null; relayArgs.RemoveRange(passIdx, relayPass is null ? 1 : 2); }
    int relayPort = relayArgs.Count >= 1 && int.TryParse(relayArgs[0], out var rp) ? rp : 7777;
    string? seedPath = relayArgs.Count >= 2 ? relayArgs[1] : null;
    CollabSession.RunRelay(relayPort, seedPath, savePath, relayPass);
    return;
}

// GUI mode: show the launch splash immediately (its own STA thread keeps it painted during the level/GL load).
Loc.Init();          // UI language (must precede the ImGui controller: the font atlas depends on the script)
// First run only: ask which language to use, in both languages. Without this a Japanese speaker has to find
// View > Language inside an English menu to discover the editor speaks Japanese at all.
if (!Loc.HasChosenLanguage) Loc.SetLanguage(LanguagePrompt.Ask());
AppPrefs.Load();     // level-assembly options (inherited mod deps, base-map layering) - read by the load block below
SplashScreen.Show();

// GLSL shaders. Declared up front: in top-level programs a local function (OnLoad) cannot reference
// a const declared textually below it, so these must precede the function bodies that use them.
const string TerrainVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uMVP; out vec3 vN; out float vH; out vec2 vUv; out vec3 vWorld;
void main(){ gl_Position = uMVP * vec4(aPos,1.0); vN = aNormal; vH = aPos.y; vUv = aUv; vWorld = aPos; }";

const string TerrainFrag = @"#version 330 core
in vec3 vN; in float vH; in vec2 vUv; in vec3 vWorld;
uniform vec3 uLightDir; uniform float uWater; uniform float uMaxH; uniform vec3 uDeepColor;
uniform int uFogEnable; uniform vec3 uFogColor; uniform float uFogStart; uniform float uFogEnd; uniform vec3 uCamPos;
uniform int uHasTex; uniform sampler2D uTer;
// Terrain-atlas UV correction. The painted ground was landing in the wrong place (features magnified outward
// from the map's origin corner), and the correct mapping could not be derived from the data alone - so it is
// exposed as a live control instead of a hardcoded guess. 1.0 / 0.0 is the untouched mapping.
uniform vec2 uTerUvScale; uniform vec2 uTerUvOffset;
uniform int uShowMat; uniform sampler2D uMat;
uniform int uHasDetail; uniform sampler2D uDetail; uniform float uDetailScale;
uniform int uUseShadowMap; uniform sampler2D uShadowMap; uniform mat4 uLightSpace;   // real-time sun shadow map
out vec4 frag;
// Project a world point into the sun's light space and PCF-sample the depth map: 1 = lit, 0 = in cast shadow.
float shadowVis(vec3 wp, vec3 nrm){
    if (uUseShadowMap==0) return 1.0;
    vec4 lp = uLightSpace * vec4(wp, 1.0);
    vec3 pc = lp.xyz / lp.w * 0.5 + 0.5;
    if (pc.z > 1.0 || pc.x < 0.0 || pc.x > 1.0 || pc.y < 0.0 || pc.y > 1.0) return 1.0;   // outside the sun frustum = lit
    float ndl = max(dot(nrm, normalize(uLightDir)), 0.0);
    float bias = max(0.003 * (1.0 - ndl), 0.0008);
    vec2 tx = 1.0 / vec2(textureSize(uShadowMap, 0));
    float lit = 0.0;
    for (int x=-1; x<=1; x++) for (int y=-1; y<=1; y++)
        lit += (pc.z - bias) > texture(uShadowMap, pc.xy + vec2(x,y)*tx).r ? 0.0 : 1.0;
    return lit / 9.0;
}
vec3 rampLand(float h){
    float t = clamp((h-uWater)/max(uMaxH-uWater,1.0),0.0,1.0);
    vec3 c = mix(vec3(0.25,0.55,0.20), vec3(0.55,0.45,0.30), t);
    if (t > 0.85) c = mix(c, vec3(0.8,0.8,0.82), (t-0.85)/0.15);
    return c;
}
vec3 matColor(int i){
    vec3 p[16] = vec3[16](
        vec3(0.85,0.75,0.45), vec3(0.30,0.70,0.28), vec3(0.50,0.36,0.22), vec3(0.55,0.55,0.58),
        vec3(0.20,0.45,0.68), vec3(0.78,0.58,0.28), vec3(0.42,0.58,0.30), vec3(0.78,0.30,0.30),
        vec3(0.28,0.55,0.58), vec3(0.62,0.62,0.32), vec3(0.52,0.40,0.62), vec3(0.80,0.68,0.52),
        vec3(0.32,0.64,0.48), vec3(0.58,0.46,0.34), vec3(0.72,0.72,0.74), vec3(0.85,0.40,0.62));
    return p[i & 15];
}
void main(){
    vec3 baseCol = (uHasTex==1) ? texture(uTer, vUv * uTerUvScale + uTerUvOffset).rgb : rampLand(vH);
    if (uHasDetail==1) baseCol *= clamp(texture(uDetail, vUv*uDetailScale).rgb*2.0, 0.0, 1.0);
    vec3 n = normalize(vN);
    float vis = shadowVis(vWorld, n);                              // real-time sun cast-shadow visibility
    float d = 0.4 + 0.6*max(0.0, dot(n, normalize(uLightDir)))*vis;
    vec3 c = baseCol * d;
    if (vH < uWater) {                              // shallow shows the riverbed, deep reads as water
        float depth = clamp((uWater - vH)/8.0, 0.0, 1.0);
        c = mix(c, uDeepColor, 0.45 + 0.45*depth);
    }
    if (uShowMat==1) {                              // material-paint mode: tint by material index
        int idx = int(texture(uMat, vUv).r * 255.0 + 0.5);
        c = mix(c, matColor(idx), 0.55);
    }
    else if (uShowMat==2) {                         // foliage-paint mode: tint only cells that carry foliage
        int idx = int(texture(uMat, vUv).r * 255.0 + 0.5);
        if (idx > 0) c = mix(c, matColor(idx), 0.6);
    }
    else if (uShowMat==3) {                         // AI path: white = blocked, green = passable
        float blk = texture(uMat, vUv).r;           // 1 blocked / 0 passable
        c = mix(c, mix(vec3(0.25,0.85,0.40), vec3(1.0,1.0,1.0), blk), 0.55);
    }
    if (uFogEnable==1) {                             // linear distance fog
        float fog = clamp((length(vWorld - uCamPos) - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        c = mix(c, uFogColor, fog);
    }
    frag = vec4(c, 1.0);
}";

const string MarkerVert = @"#version 330 core
layout(location=0) in vec3 aPos; uniform mat4 uMVP; uniform float uSize;
void main(){ gl_Position = uMVP * vec4(aPos,1.0); gl_PointSize = uSize; }";

const string MarkerFrag = @"#version 330 core
uniform vec3 uColor; out vec4 frag;
void main(){ frag = vec4(uColor,1.0); }";

// Collision wireframe: like the marker line shader but distance-faded so it only draws as far as the user can
// see - it dissolves across the fog band (uFogStart..uFogEnd) instead of rendering into / beyond the fog.
const string CollisionVert = @"#version 330 core
layout(location=0) in vec3 aPos; uniform mat4 uMVP; uniform vec3 uCamPos; out float vDist;
void main(){ gl_Position = uMVP * vec4(aPos,1.0); vDist = distance(aPos, uCamPos); }";
const string CollisionFrag = @"#version 330 core
in float vDist; out vec4 frag; uniform vec3 uColor; uniform float uFogStart; uniform float uFogEnd;
void main(){
    float a = 1.0 - clamp((vDist - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    if (a <= 0.003) discard;          // fully in the fog -> not drawn
    frag = vec4(uColor, a);
}";

// Water surface: a flat quad at the water level; ripples are procedural in the fragment shader
// (two crossing sine waves perturb the normal), with a sun specular highlight and a grazing-angle
// sky reflection so the plane reads as translucent water rather than a flat blue sheet.
const string WaterVert = @"#version 330 core
layout(location=0) in vec3 aPos; uniform mat4 uMVP; uniform float uWaterY; out vec3 vWorld;
void main(){ vec3 wp = vec3(aPos.x, uWaterY, aPos.z); vWorld = wp; gl_Position = uMVP * vec4(wp,1.0); }";

const string WaterFrag = @"#version 330 core
in vec3 vWorld; uniform vec3 uLightDir; uniform vec3 uCamPos; uniform float uTime; out vec4 frag;
uniform int uFogEnable; uniform vec3 uFogColor; uniform float uFogStart; uniform float uFogEnd;
uniform vec3 uWaterColor; uniform float uWaterAlpha;
// Textured path (BF1942 water.texLayer1/2 + normalMap): two scrolling diffuse layers + a scrolling ripple normal map.
uniform int uHasWaterTex;                 // 1 = use the level's water textures, 0 = procedural fallback
uniform sampler2D uTexL1; uniform sampler2D uTexL2; uniform sampler2D uNormal;
uniform vec2 uScroll1; uniform vec2 uScroll2; uniform vec2 uScrollN;   // direction * speed (UV/sec)
uniform float uTile1; uniform float uTile2; uniform float uTileN;     // water.tileLayer*
uniform vec3 uSpecColor;
void main(){
    vec3 viewDir = normalize(uCamPos - vWorld);
    vec3 n; vec3 col; float alpha;
    if (uHasWaterTex == 1) {
        vec2 w = vWorld.xz;
        vec3 nm = texture(uNormal, w * (uTileN*0.04) + uScrollN*uTime).rgb * 2.0 - 1.0;   // tangent-space ripple normal
        n = normalize(vec3(nm.x, 6.0, nm.y));                                             // bias strongly toward up (flat water)
        vec3 t1 = texture(uTexL1, w * (uTile1*0.04) + uScroll1*uTime).rgb;
        vec3 t2 = texture(uTexL2, w * (uTile2*0.04) + uScroll2*uTime).rgb;
        vec3 tex = (t1 + t2) * 0.5;
        col = uWaterColor * (0.35 + 1.3 * tex);                                           // tint the water textures by water.color
        float fres = pow(1.0 - max(dot(n, viewDir), 0.0), 3.0);
        vec3 h = normalize(normalize(uLightDir) + viewDir);
        col += uSpecColor * pow(max(dot(n,h),0.0), 80.0) * 0.9;                           // sun glint along the ripple normal
        col = mix(col, uFogColor, fres*0.30);
        alpha = mix(max(uWaterAlpha, 0.55), 1.0, fres*0.4);
    } else {
        vec2 p = vWorld.xz * 0.05;
        float w = sin(p.x + uTime*0.8) + sin(p.y*1.3 - uTime*0.6) + 0.5*sin((p.x+p.y)*2.1 + uTime*1.3);
        n = normalize(vec3(0.08*cos(p.x + uTime*0.8), 1.0, 0.08*cos(p.y*1.3 - uTime*0.6)));
        float fres = pow(1.0 - max(dot(n, viewDir), 0.0), 3.0);
        col = uWaterColor * (0.8 + 0.45 * clamp(0.5 + 0.4*w, 0.0, 1.0));   // the level's water.color + ripple variation
        vec3 h = normalize(normalize(uLightDir) + viewDir);
        col += vec3(1.0,0.96,0.82) * pow(max(dot(n,h),0.0), 64.0) * 0.7;        // sun glint
        col = mix(col, uFogColor, fres*0.35);                                   // grazing-angle reflection of the sky/haze
        alpha = mix(uWaterAlpha, 1.0, fres*0.4);                          // transparent looking down, opaque at grazing
    }
    if (uFogEnable==1) {                              // fade into the fog like the land, at the SAME distance
        float fog = clamp((length(vWorld - uCamPos) - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        col = mix(col, uFogColor, fog);
        alpha = mix(alpha, 1.0, fog);                  // distant water turns to opaque fog so it vanishes like land
    }
    frag = vec4(col, alpha);
}";

// Sky: a fullscreen NDC quad. The vertex stage reconstructs a world-space view RAY per corner from the inverse
// view-projection; the fragment stage either samples the level's real skybox CUBEMAP (the Sky_<map>_0N.dds faces,
// rotated by Sky.setRotAngle) or, when no cubemap is loaded, paints a sun-aware procedural sky (zenith->horizon
// gradient, fog-tinted horizon haze, and a sun glow + disc at the level's sun direction). Drawn first, depth off.
const string SkyVert = @"#version 330 core
layout(location=0) in vec2 aPos; uniform mat4 uInvVP; uniform vec3 uCamPos; out vec3 vDir;
void main(){ vec4 wp = uInvVP * vec4(aPos, 1.0, 1.0); vDir = wp.xyz/wp.w - uCamPos; gl_Position = vec4(aPos, 0.0, 1.0); }";

const string SkyFrag = @"#version 330 core
in vec3 vDir; out vec4 frag;
uniform vec3 uSunDir;      // world direction toward the sun (normalized)
uniform vec3 uFogColor;
uniform float uRot;        // skybox rotation (radians) - Sky.setRotAngle
uniform int uHasCube;
uniform samplerCube uCube;
uniform int uHasCloud;     // animated cloud layer overlay
uniform sampler2D uCloudTex;
uniform vec3 uCloudColor;
uniform vec2 uCloudScroll; // UV offset = time * Cloud.setSpeed
uniform float uCloudScale; // ray->UV projection scale (from Cloud.setTexScale)
uniform float uCloudOpacity;
void main(){
    vec3 d = normalize(vDir);
    float cr = cos(uRot), sr = sin(uRot);
    vec3 dr = vec3(cr*d.x + sr*d.z, d.y, -sr*d.x + cr*d.z);   // rotate sampling dir around Y
    vec3 sky;
    if (uHasCube == 1) { sky = texture(uCube, dr).rgb; }
    else {
        // --- procedural sky ---
        float t = clamp(d.y, -1.0, 1.0);
        vec3 zenith  = vec3(0.16, 0.33, 0.62);
        vec3 horizon = vec3(0.74, 0.82, 0.90);
        vec3 ground  = vec3(0.20, 0.22, 0.21);
        sky = (t >= 0.0) ? mix(horizon, zenith, pow(t, 0.55)) : mix(horizon, ground, pow(-t, 0.5));
        float haze = pow(1.0 - abs(t), 8.0);
        sky = mix(sky, uFogColor, haze * 0.45);
        float s = max(dot(d, normalize(uSunDir)), 0.0);
        sky += vec3(1.0, 0.86, 0.62) * pow(s, 8.0) * 0.45;                     // warm glow around the sun
        sky += vec3(1.0, 0.97, 0.88) * smoothstep(0.9992, 0.9997, s) * 2.0;    // sun disc
    }
    // --- animated clouds: a scrolling layer projected onto the upper hemisphere (dome) ---
    if (uHasCloud == 1 && d.y > 0.015) {
        vec2 uv = (d.xz / d.y) * uCloudScale + uCloudScroll;
        float dens = texture(uCloudTex, uv).r;
        float horizonFade = smoothstep(0.015, 0.22, d.y);     // fade out toward the horizon (avoids smearing)
        float a = clamp(dens * uCloudOpacity * horizonFade, 0.0, 1.0);
        sky = mix(sky, uCloudColor, a);
    }
    frag = vec4(sky, 1.0);
}";

// Real skybox / cloud MESH (the level's actual Sky_* and cloud .sm, drawn with their EMBEDDED textures instead of the
// procedural gradient/cloud overlay). Unlit, per-part texture; the mesh is centred on the camera and pinned to the far
// plane (gl_Position.xyww) so it never near/far-clips and always sits behind all real geometry. uScroll animates cloud
// UVs; uOpaque forces alpha 1 for skybox faces (DXT1 1-bit alpha would otherwise punch holes), else texture alpha blends.
const string SkyMeshVert = @"#version 330 core
layout(location=0) in vec3 aPos; layout(location=1) in vec2 aUv;
uniform mat4 uMVP; uniform vec2 uScroll; uniform int uPin;
out vec2 vUv;
void main(){ vec4 p = uMVP * vec4(aPos, 1.0); gl_Position = (uPin == 1) ? p.xyww : p; vUv = aUv + uScroll; }";

const string SkyMeshFrag = @"#version 330 core
in vec2 vUv; out vec4 frag;
uniform sampler2D uTex; uniform int uHasTex; uniform int uOpaque; uniform vec4 uTint;
void main(){
    vec4 c = (uHasTex == 1) ? texture(uTex, vUv) : vec4(1.0);
    if (uOpaque == 1) { frag = vec4(c.rgb * uTint.rgb, 1.0); }
    // Clouds: the cloud material is materialDiffuse 1 1 1 (white) and the texture supplies the SHAPE via its alpha; the
    // texture's RGB is an incidental tint (e.g. jellyfish_clouds is blue) so we paint uTint (white) masked by tex alpha.
    else { frag = vec4(uTint.rgb, c.a * uTint.a); if (frag.a < 0.02) discard; }
}";

// Weather particles: textured point sprites (so the generated/imported particle image shows). Distance-scaled
// pixel size; the fragment samples the texture across the sprite via gl_PointCoord.
const string WeatherVert = @"#version 330 core
layout(location=0) in vec3 aPos; uniform mat4 uMvp; uniform float uSize;
void main(){ vec4 cp = uMvp * vec4(aPos, 1.0); gl_Position = cp; gl_PointSize = clamp(uSize * 10.0 / max(cp.w, 0.5), 3.0, 44.0); }";
const string WeatherFrag = @"#version 330 core
out vec4 frag; uniform sampler2D uTex; uniform vec3 uColor;
void main(){ vec4 t = texture(uTex, gl_PointCoord); frag = vec4(uColor * t.rgb, t.a); }";

// Particle EFFECTS (waterfalls / lava / fire / smoke): camera-facing textured BILLBOARDS sized in WORLD metres (unlike
// the pixel-capped weather point sprites). Each particle is 6 verts: center(3) + quad corner(2, in [-1,1]) + size(1) +
// alpha(1); the vertex stage expands the corner along the camera's right/up so the quad always faces the camera.
const string EffectVert = @"#version 330 core
layout(location=0) in vec3 aCenter;
layout(location=1) in vec2 aCorner;
layout(location=2) in float aSize;
layout(location=3) in float aAlpha;
layout(location=4) in float aRot;                 // per-particle billboard rotation (radians)
uniform mat4 uMvp; uniform vec3 uRight; uniform vec3 uUp;
out vec2 vUv; out float vAlpha; out vec3 vWorld;
void main(){
    float c = cos(aRot), s = sin(aRot);
    vec2 r = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);   // rotate the quad corner
    vec3 wp = aCenter + (uRight * r.x + uUp * r.y) * aSize;
    vWorld = wp;
    gl_Position = uMvp * vec4(wp, 1.0);
    vUv = aCorner * 0.5 + 0.5;
    vAlpha = aAlpha;
}";
const string EffectFrag = @"#version 330 core
in vec2 vUv; in float vAlpha; in vec3 vWorld; out vec4 frag;
uniform sampler2D uTex; uniform vec3 uTint;
uniform int uFogEnable; uniform float uFogStart; uniform float uFogEnd; uniform vec3 uCamPos;
void main(){
    vec4 t = texture(uTex, vUv);
    float a = t.a * vAlpha;
    if (uFogEnable == 1) {
        // effects must stay WITHIN the view distance - dissolve to nothing across the fog band instead of rendering
        // into / past the fog (alpha-fade to 0, not tint-to-fog: additive particles tinted to fog would BRIGHTEN it).
        float d = length(vWorld - uCamPos);
        a *= 1.0 - clamp((d - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
    }
    frag = vec4(t.rgb * uTint, a);
}";


// Object meshes: position via model*viewProj (uMVP), normal via the model rotation (uModel), flat
// per-material colour with two-sided lambert so inconsistent winding never reads as pure black.
const string ObjVert = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
layout(location=3) in vec2 aLmUv;
uniform mat4 uMVP; uniform mat4 uModel; out vec3 vN; out vec2 vUv; out vec2 vLmUv; out vec3 vWorld;
void main(){ gl_Position = uMVP * vec4(aPos,1.0); vN = mat3(uModel) * aNormal; vUv = aUv; vLmUv = aLmUv; vWorld = (uModel * vec4(aPos,1.0)).xyz; }";

const string ObjFrag = @"#version 330 core
in vec3 vN; in vec2 vUv; in vec2 vLmUv; in vec3 vWorld;
uniform vec3 uLightDir; uniform vec3 uColor; uniform vec3 uTint;
uniform int uUseTex; uniform int uAlphaTest; uniform int uAlphaEnable; uniform sampler2D uTex;
uniform int uHasLightmap; uniform sampler2D uLightmap;   // baked per-object lightmap (sampled via the 2nd UV)
uniform int uUseShadowMap; uniform sampler2D uShadowMap; uniform mat4 uLightSpace;   // real-time sun shadow map (unit 2)
uniform int uFogEnable; uniform vec3 uFogColor; uniform float uFogStart; uniform float uFogEnd; uniform vec3 uCamPos;
out vec4 frag;
float shadowVis(vec3 wp, vec3 nrm){
    if (uUseShadowMap==0) return 1.0;
    vec4 lp = uLightSpace * vec4(wp, 1.0);
    vec3 pc = lp.xyz / lp.w * 0.5 + 0.5;
    if (pc.z > 1.0 || pc.x < 0.0 || pc.x > 1.0 || pc.y < 0.0 || pc.y > 1.0) return 1.0;
    float ndl = max(dot(nrm, normalize(uLightDir)), 0.0);
    float bias = max(0.003 * (1.0 - ndl), 0.0008);
    vec2 tx = 1.0 / vec2(textureSize(uShadowMap, 0));
    float lit = 0.0;
    for (int x=-1; x<=1; x++) for (int y=-1; y<=1; y++)
        lit += (pc.z - bias) > texture(uShadowMap, pc.xy + vec2(x,y)*tx).r ? 0.0 : 1.0;
    return lit / 9.0;
}
void main(){
    vec4 tc = texture(uTex, vUv);
    vec3 base = (uUseTex==1) ? tc.rgb : uColor;
    float a   = (uUseTex==1) ? tc.a   : 1.0;
    if (uAlphaTest==1 && uAlphaEnable==1 && a < 0.33) discard;   // cutout discard only while transparency is enabled (toggle off -> solid)
    vec3 c;
    if (uHasLightmap==1) {
        vec3 lm = texture(uLightmap, vLmUv).rgb;          // baked lighting already has shadows baked in -> no shadow map
        c = base * uTint * (0.08 + 0.92*lm);              // 8% ambient floor so deep shadow stays readable in-editor
    } else {
        vec3 n = normalize(vN);
        float vis = shadowVis(vWorld, n);                 // real-time sun cast-shadow
        float ndl = abs(dot(n, normalize(uLightDir)));
        // Cutout foliage (leaf cards + tree sprite billboards) has arbitrary/sideways normals, and the sun can point
        // straight up (e.g. underwater maps) -> N.L ~ 0 would render the whole canopy near-black. Light it mostly-flat
        // (high ambient floor) like the engine does for vegetation; opaque geometry keeps normal diffuse shading.
        float d = (uAlphaTest==1) ? (0.78 + 0.22*ndl*vis) : (0.4 + 0.6*ndl*vis);
        c = base * uTint * d;
    }
    if (uFogEnable==1) {
        float fog = clamp((length(vWorld - uCamPos) - uFogStart) / max(uFogEnd - uFogStart, 1.0), 0.0, 1.0);
        c = mix(c, uFogColor, fog);
    }
    frag = vec4(c, (uAlphaTest!=0 && uAlphaEnable==1) ? a : 1.0);   // cutout(1)+glass(2) carry real alpha when enabled; else fully opaque
}";

// Depth-only shader for the sun shadow-map pass: render terrain + objects from the sun's POV into a depth texture.
// Uses ONLY position (location 0), which both the terrain VAO and every object VAO carry, so one program covers both.
const string DepthVert = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uLightSpace; uniform mat4 uModel;
void main(){ gl_Position = uLightSpace * uModel * vec4(aPos, 1.0); }";
const string DepthFrag = @"#version 330 core
void main(){}";

// Resolve the level folder + mesh archives, in priority order:
//   1. explicit command-line paths (back-compat with scripts);
//   2. saved selections from a previous run (refractorforge.json beside the exe);
//   3. native folder/file pickers (GUI) - first run, or whenever --pick is passed.
string? levelDir = null;
string[] levelArchives = Array.Empty<string>();   // all level .rfa (base + patches); empty when the level is a FOLDER
string[] meshArchives = Array.Empty<string>();     // standardMesh/objects .rfa + patches - no limit
string[] texPicks = Array.Empty<string>();   // texture*.rfa the user picked (their folders are also scanned for siblings)
RefractorForge.Formats.RfProject? activeRfProject = null;   // the loaded .rfproj (project workflow); Ctrl+S updates it
{
    var pathArgs = args.Where(a => !a.StartsWith("-", StringComparison.Ordinal)).ToArray();
    bool forcePick = args.Any(a => a is "--pick" or "-p");
    // RESUMING THE LAST MAP IS OPT-IN, and only the editor's own relaunch asks for it.
    //
    // It used to be the default, which made a bad level unrecoverable: the load-failure handler below cleared the
    // remembered plain level but NOT the active .rfproj, so a project whose level fails to load was retried on
    // every single launch and the editor could never reach the startup screen again. Starting fresh at the picker
    // means a failed map costs one click, not a reinstall. The in-editor Open Mod / Open Level / language switch
    // still round-trip correctly because RelaunchAndExit passes --resume.
    bool resume = args.Any(a => a is "--resume");
    if (pathArgs.Length >= 1)
    {
        levelDir = pathArgs[0];
        meshArchives = pathArgs.Skip(1).Where(a => a.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    else
    {
        var saved = Settings.Load();
        var activeProj = ActiveProject.Get();
        // 1) An active project (.rfproj) -> load it. Folder-based: the level data lives in the project folder, with
        //    mesh/texture libraries referenced (Custom) or derived from the mod chain (Default).
        if (resume && !forcePick && activeProj is not null && File.Exists(activeProj))
        {
            try
            {
                var proj = RefractorForge.Formats.RfProject.Load(activeProj);
                var (pld, pma, pta) = proj.Resolve();
                levelDir = pld; meshArchives = pma; texPicks = pta; levelArchives = Array.Empty<string>();
                RecentProjects.Touch(proj);
                activeRfProject = proj;
            }
            catch { ActiveProject.Clear(); }   // corrupt/missing -> fall through to the startup screen
        }
        // 2) Back-compat: a remembered plain level from before the project system (FOLDER or one-or-more .rfa).
        if (levelDir is null && resume && !forcePick && saved is { Level: string sl } && (Directory.Exists(sl) || File.Exists(sl)))
        {
            levelDir = sl;
            levelArchives = (saved.LevelArchives is { Length: > 0 } la ? la
                             : (File.Exists(sl) && sl.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? new[] { sl } : Array.Empty<string>()))
                            .Where(File.Exists).ToArray();
            meshArchives = (saved.MeshArchives is { Length: > 0 } ma ? ma : new[] { saved.StdMesh, saved.Objects })
                .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).Select(p => p!).ToArray();
            texPicks = (saved.Textures ?? Array.Empty<string>()).Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToArray();
        }
        // 3) No project + no remembered level -> the interactive startup screen (Recent Projects + Open/New).
        if (levelDir is null)
        {
            SplashScreen.Close();   // dismiss the splash so the startup window is visible
            var startup = ProjectFlows.RunStartup();
            if (startup.OpenMod)
            {
                // Mod folder first, then the map from that mod - the same pairing File > Open Mod uses. Loaded in
                // place (no relaunch) because the GL window does not exist yet.
                if (!GatherModPaths(out var modLvls, out var modMesh, out var modTex)) Environment.Exit(0);
                levelArchives = modLvls; levelDir = modLvls[0]; meshArchives = modMesh; texPicks = modTex;
                Settings.Save(new LevelPaths(modLvls[0], null, null, modTex, modMesh, modLvls));
                ActiveProject.Clear();   // the Open-Mod path is Settings-based, not a project
            }
            else if (startup.Project is { } proj)
            {
                var (pld, pma, pta) = proj.Resolve();
                levelDir = pld; meshArchives = pma; texPicks = pta; levelArchives = Array.Empty<string>();
                activeRfProject = proj;
            }
            else Environment.Exit(0);   // cancelled or the flow failed -> exit before opening the GL window
        }
    }
}

TerrainConfig cfg;
TerrainMesh mesh;
StaticObjectsFile? so = null;
string? soPath = null;
Vector3[] markers = Array.Empty<Vector3>();
// Declared before the load block because SyncMarkers() (called during load) reads meshLib and writes
// pointMarkers; in top-level programs both must be assigned/declared before that first call.
MeshLibrary? meshLib = null;
Vector3[] pointMarkers = Array.Empty<Vector3>();
TerrainTexture? terrainTex = null;   // level's baked tiles, flattened to a GPU atlas in OnLoad
Texture2D? atlasCpu = null;          // CPU copy of the baked atlas, kept so the Texture paint tool can edit it
bool atlasPainted = false;           // the atlas was texture-painted -> re-emit txCxR.dds tiles on save
string? texturesDir = null;          // the level's Textures/ dir (where txCxR.dds tiles live), for save
Heightmap? heightmap = null;         // kept so the ground-pick can sample terrain height for placement
TerrainPick? terrainPick = null;

// Loaded-level state assigned straight-line by the load block below - declared here (before it) so the
// top-level code can assign them; the editor's local functions capture these same variables by reference.
GameplayObjects gameplay = GameplayObjects.Empty;
EditableGameplay gameplayEdit = new(GameplayObjects.Empty);   // mutable editing view of the gameplay layer
MaterialMap? materialMap = null;
MaterialPainter? matPainter = null;
GrowthMaps? growth = null;             // foliage layers (undergrowth/overgrowth), null if the level has none
MaterialPainter? underPainter = null;  // built per-layer after load (each layer has its own resolution)
MaterialPainter? overPainter = null;
TerrainEditor? terrainEd = null;       // built from the loaded heightmap + config
EnvironmentSettings? env = null;       // sun direction / sky settings (lighting + water specular)
SoundLibrary sounds = SoundLibrary.Empty;   // level's placeable sound emitters (Sounds/*.con -> *.ssc); folder levels
LightmapShadowBits? loadedShadowBits = null; // the level's stored terrain sun-shadow (Textures/LightmapShadowBits.lsb), if present
ObjectLightmaps? objectLightmaps = null;     // the level's baked per-object lightmaps (ObjectLightMaps/*.tga), if present
bool showObjectLightmaps = false;            // Layers toggle: baked object lighting (lazy-loaded on first enable) vs dynamic
bool objectLightmapsLoaded = false;          // the per-object lightmap decode is deferred off the load path (it re-reads the .rfa)
Dictionary<string, byte[]> bakedObjectLightmaps = new(StringComparer.OrdinalIgnoreCase);   // freshly baked <leaf>.tga -> bytes, for save

// The level is .rfa-based if we have explicit level archives, or levelDir itself is a .rfa file.
string[] rfaList = levelArchives.Length > 0 ? levelArchives
                 : (levelDir is not null && LevelArchive.IsRfa(levelDir) ? new[] { levelDir } : Array.Empty<string>());
// AUTO-MOUNT PATCH ARCHIVES: the engine layers <Level>_NNN.rfa over the base (Dystopia_City_001.rfa overrides
// 3 StaticObjects.con + 1400 entries). Users usually pick just the base, so add numeric-suffix siblings of every
// picked archive automatically - appended AFTER their base (LevelArchive is last-wins) and numerically ordered.
if (rfaList.Length > 0)
{
    var expanded = new List<string>(rfaList);
    foreach (var baseRfa in rfaList)
    {
        var dir0 = Path.GetDirectoryName(Path.GetFullPath(baseRfa));
        var stem0 = Path.GetFileNameWithoutExtension(baseRfa);
        if (dir0 is null || !Directory.Exists(dir0)) continue;
        var sibs = Directory.EnumerateFiles(dir0, stem0 + "_*.rfa")
            .Where(s => System.Text.RegularExpressions.Regex.IsMatch(
                Path.GetFileNameWithoutExtension(s), "^" + System.Text.RegularExpressions.Regex.Escape(stem0) + @"_\d+$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);
        foreach (var s in sibs)
            if (!expanded.Any(x => Path.GetFullPath(x).Equals(Path.GetFullPath(s), StringComparison.OrdinalIgnoreCase)))
            { expanded.Add(s); Console.WriteLine($"Auto-mounted level patch: {Path.GetFileName(s)}"); }
    }
    rfaList = expanded.ToArray();
}
// TERRAIN-REUSE add-on maps: many mod maps (FHSW/FH/DC/bf1918 Aberdeen, Bocage, Battleaxe, Adak_Island...) ship
// Heightmap.raw + StaticObjects but NO Terrain.con - they LAYER over the base game's same-named map, which the engine
// mounts to supply the terrain config. The editor only loaded the mod's rfa -> "Terrain.con not found" -> total load
// failure (23 such maps across 13 mods in the multi-mod audit). If no picked rfa carries terrain, find the SAME-NAMED
// level .rfa elsewhere in the mod chain (init.con deps + base game) and layer it UNDERNEATH (lowest priority, so the
// mod's own files still win), supplying the missing Terrain.con/tiles. Uses the cheap TOC reader (no full-file load).
if (rfaList.Length > 0)
{
    const StringComparison OIC = StringComparison.OrdinalIgnoreCase;
    bool HasTerrain(string rfa)
    {
        try
        {
            bool t = false, h = false;
            var rfaFile = new RefractorFlatArchive(rfa);
            foreach (var toc in rfaFile.Entries)
            {
                var e = toc.Name.Replace('\\', '/');
                if (e.EndsWith("/Terrain.con", OIC) || e.Equals("Terrain.con", OIC)) t = true;
                if (e.EndsWith("/Heightmap.raw", OIC) || e.Equals("Heightmap.raw", OIC)) h = true;
            }
            return t && h;
        }
        catch { return true; }   // unreadable -> assume fine, don't add a fallback
    }
    // Only when the opened archive has no terrain of its own AND the user wants add-on maps layered over their base
    // (an FHSWEurope patch .rfa carries only ObjectiveMode + custom ships; its ground and objects live in the base map).
    if (AppPrefs.LayerBaseMap && !rfaList.Any(HasTerrain))
    {
        var name = Path.GetFileNameWithoutExtension(rfaList[0]);
        DirectoryInfo? arc = null; try { arc = new FileInfo(Path.GetFullPath(rfaList[0])).Directory; } catch { }
        for (; arc is not null; arc = arc.Parent) if (arc.Name.Equals("Archives", OIC)) break;
        var modDir = arc?.Parent; DirectoryInfo? gameRoot = null;
        for (var pp = modDir; pp?.Parent is not null; pp = pp.Parent) if (pp.Name.Equals("Mods", OIC)) { gameRoot = pp.Parent; break; }
        if (gameRoot is not null)
        {
            // Transitive chain (ModChain): lets an add-on map borrow its base terrain from ANY mod in the stack -
            // e.g. an FHSW map whose terrain actually lives in FH, which one-level parsing never reached.
            var chain = modDir is not null
                ? RefractorForge.Formats.ModChain.Resolve(gameRoot.FullName, modDir.FullName, AppPrefs.ResolveInheritedMods).Paths.ToList()
                : new List<string>();
            foreach (var mp in chain)
            {
                var searchDir = Directory.Exists(Path.Combine(mp, "Archives")) ? Path.Combine(mp, "Archives") : mp;
                string? hit = null;
                try
                {
                    hit = Directory.EnumerateFiles(searchDir, name + ".rfa", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.Replace('\\', '/').ToLowerInvariant().Contains("/levels/")
                                          && !rfaList.Any(x => Path.GetFullPath(x).Equals(Path.GetFullPath(f), OIC))
                                          && HasTerrain(f));
                }
                catch { }
                if (hit is not null)
                {
                    rfaList = new[] { hit }.Concat(rfaList).ToArray();   // base terrain UNDER the mod's overrides
                    Console.WriteLine($"Layered base terrain for '{name}' from {Path.GetFileName(mp)} ({hit}).");
                    break;
                }
            }
        }
    }
}
// Loading a level can fail on a bad/incomplete pick (wrong folder, no Terrain.con, corrupt .rfa). Catch it so
// the app NEVER hard-crashes on startup on someone else's machine - report it and fall back to demo terrain.
try
{
if (rfaList.Length > 0)
{
    var lvl = LevelArchive.FromRfa(rfaList);
    cfg = lvl.Config;
    heightmap = lvl.Heightmap;
    mesh = TerrainMesh.FromHeightmap(lvl.Heightmap, cfg, 1);
    so = lvl.StaticObjects;
    terrainTex = lvl.Terrain;
    gameplay = lvl.Gameplay;
    gameplayEdit = new EditableGameplay(gameplay);
    materialMap = lvl.Material;
    growth = lvl.Growth;
    env = lvl.Environment;
    sounds = lvl.Sounds ?? SoundLibrary.Empty;   // .rfa levels edit sounds too (saved back into the repack/patch)
    loadedShadowBits = lvl.Shadow;               // the level's baked terrain sun-shadow (display via the Shadows toggle)
    // NOTE: object lightmaps are loaded LAZILY (EnsureObjectLightmaps) on first enable - decoding them here re-opens
    // the level .rfa and was a big chunk of the load time.
    // Can't write back into the .rfa yet, so F5 saves a loose StaticObjects.con beside the archive.
    soPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(levelDir)) ?? ".",
                          Path.GetFileNameWithoutExtension(levelDir) + ".StaticObjects.con");
    SyncMarkers();
    string rfaDesc = rfaList.Length == 1 ? Path.GetFileName(levelDir) : $"{rfaList.Length} archives (base+patches)";
    Console.WriteLine($"Loaded {rfaDesc} (.rfa, read directly): {cfg.MaterialSize}^2 terrain, " +
                      $"worldSize {cfg.WorldSize}, {markers.Length} objects" +
                      (terrainTex is not null ? $", terrain tiles {terrainTex.AtlasSize}px." : ", no terrain tiles."));
}
else if (levelDir is not null && Directory.Exists(levelDir))
{
    // FirstOrDefault + a clear message: pointing at a non-level folder (no Terrain.con) used to throw
    // "Sequence contains no elements" and hard-crash startup. Now it surfaces a friendly error so the
    // try/catch below can fall back to demo terrain instead of dying.
    // Shallowest match first: heavily-scripted maps keep per-game-mode copies in sub-folders (BattleMode/
    // StaticObjects.con, Animations/Init.con...) that must not shadow the root file (see LevelArchive.Find).
    string Find(string n) => Directory.EnumerateFiles(levelDir, n, SearchOption.AllDirectories)
        .OrderBy(p => p.Count(c => c is '\\' or '/')).FirstOrDefault()
        ?? throw new FileNotFoundException($"This folder isn't a Battlefield level - no '{n}' found anywhere under it.");
    cfg = TerrainConfig.Load(Find("Terrain.con"));
    heightmap = Heightmap.LoadForMaterialSize(Find("Heightmap.raw"), cfg.MaterialSize);
    mesh = TerrainMesh.FromHeightmap(heightmap, cfg, 1);
    soPath = Find("StaticObjects.con");
    so = StaticObjectsFile.Load(soPath);
    gameplay = GameplayObjects.LoadFolder(levelDir);
    gameplayEdit = new EditableGameplay(gameplay);
    var matFile = Directory.EnumerateFiles(levelDir, "MaterialMap.raw", SearchOption.AllDirectories).FirstOrDefault();
    if (matFile is not null) materialMap = MaterialMap.LoadForMaterialSize(matFile, cfg.MaterialSize);
    growth = GrowthMaps.LoadFolder(levelDir);
    env = EnvironmentSettings.LoadFolder(levelDir);
    sounds = SoundLibrary.LoadFolder(levelDir);   // recognise + edit placed sound emitters (.ssc)
    loadedShadowBits = LightmapShadowBits.TryLoadFolder(levelDir);   // the level's baked terrain sun-shadow, if present
    // object lightmaps loaded lazily (EnsureObjectLightmaps) - see the .rfa branch note above.
    SyncMarkers();
    var terrainTexDir = Directory.EnumerateDirectories(levelDir, "Textures", SearchOption.AllDirectories).FirstOrDefault();
    texturesDir = terrainTexDir;   // where txCxR.dds tiles live, so the Texture paint tool can re-emit them on save
    if (terrainTexDir is not null) terrainTex = TerrainTexture.Load(terrainTexDir, cfg.WorldSize);
    Console.WriteLine($"Loaded {levelDir}: {cfg.MaterialSize}^2 terrain, worldSize {cfg.WorldSize}, {markers.Length} objects" +
                      (terrainTex is not null ? $", terrain tiles {terrainTex.AtlasSize}px." : ", no terrain tiles (height ramp)."));
}
else
{
    cfg = new TerrainConfig { MaterialSize = 1024, WorldSize = 4096, YScale = 0.5f, WaterLevel = 30 };
    heightmap = HeightmapGenerator.DiamondSquare(cfg.MaterialSize, 2026, 0.55f);
    mesh = TerrainMesh.FromHeightmap(heightmap, cfg, 1);
    Console.WriteLine("No level selected - generated 4 km demo terrain (not savable).");
}
}
catch (Exception loadEx)
{
    // Bad/incomplete selection - don't crash. Tell the user plainly and open the demo terrain instead.
    Picker.Error(
        "Couldn't open the level you selected:\n\n" +
        $"    {levelDir}\n\n" +
        $"{loadEx.Message}\n\n" +
        "Opening a demo terrain instead. Restart to pick a different map.",
        "RefractorForge - level not loaded");
    // Forget the bad pick BOTH ways. Clearing only the remembered level used to leave a broken .rfproj active,
    // and since that was auto-loaded on every launch the editor could never get back to the startup screen.
    Settings.Save(new LevelPaths(null, null, null));
    ActiveProject.Clear();
    levelDir = null;
    cfg = new TerrainConfig { MaterialSize = 1024, WorldSize = 4096, YScale = 0.5f, WaterLevel = 30 };
    heightmap = HeightmapGenerator.DiamondSquare(cfg.MaterialSize, 2026, 0.55f);
    mesh = TerrainMesh.FromHeightmap(heightmap, cfg, 1);
    Console.WriteLine($"Level load failed: {loadEx.Message} - generated demo terrain.");
}

// Ground-pick for placing objects: intersects screen rays with the terrain surface.
if (heightmap is not null) terrainPick = new TerrainPick(heightmap, cfg);
if (heightmap is not null) terrainEd = new TerrainEditor(heightmap, cfg);
if (materialMap is not null) matPainter = new MaterialPainter(materialMap, cfg);
if (growth?.Under is not null)
    underPainter = new MaterialPainter(growth.Under, new TerrainConfig { MaterialSize = growth.UnderSide, WorldSize = cfg.WorldSize, YScale = cfg.YScale, WaterLevel = cfg.WaterLevel });
if (growth?.Over is not null)
    overPainter = new MaterialPainter(growth.Over, new TerrainConfig { MaterialSize = growth.OverSide, WorldSize = cfg.WorldSize, YScale = cfg.YScale, WaterLevel = cfg.WaterLevel });

CollabSession? collab = null;   // the collaboration session (set via the Collab menu); null when offline
var hist = so is not null ? new EditHistory(so) : null;
// (the hist.OnDo collaboration hook is wired in OnLoad, where all the captured editor state is assigned)

// Where to look for loose Standardmesh shader overrides + texture archives (the level's own folder, or
// the folder containing the .rfa). A .rfa level keeps these inside the archive, so the scans simply
// come up empty and we fall back gracefully.
string? scanDir = levelDir is null ? null
    : (LevelArchive.IsRfa(levelDir) ? Path.GetDirectoryName(Path.GetFullPath(levelDir)) : levelDir);

// Open the chosen mesh archives so placed objects render as their real geometry. The level's OWN .rfa(s) are
// added too - a map .rfa can embed its own .sm meshes / object .con / .dds (mod + custom maps), which were
// invisible before. The user's global archives come first (MeshLibrary/TextureLibrary are first-wins), so the
// map only supplies meshes/textures it uniquely carries.
if (levelDir is not null && so is not null && (meshArchives.Length > 0 || rfaList.Length > 0))
{
    // AUTO-MOUNT MESH PATCH ARCHIVES: the engine mounts <stem>_NNN.rfa right after its base (StandardMesh.rfa +
    // StandardMesh_001.rfa, Objects.rfa + Objects_001.rfa). Bocage's SuburbHouse/Ruin_suburbhouse buildings live
    // ONLY in StandardMesh_001.rfa, so picking just standardMesh.rfa made a whole house vanish while the medic
    // (in aiMeshes.rfa) rendered fine. For every picked mesh archive, pull in its numeric-suffix siblings from the
    // same folder (the texture path already does this via its texture*.rfa glob). Mirrors the level _NNN auto-mount.
    static IEnumerable<string> WithMeshSiblings(IEnumerable<string> picks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in picks)
        {
            if (seen.Add(Path.GetFullPath(a))) yield return a;
            string? dir; string stem;
            try { dir = Path.GetDirectoryName(Path.GetFullPath(a)); stem = Path.GetFileNameWithoutExtension(a); } catch { continue; }
            if (dir is null || !Directory.Exists(dir)) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(stem, @"_\d+$")) continue;   // already a patch archive
            foreach (var sib in Directory.EnumerateFiles(dir, stem + "_*.rfa"))
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(sib),
                        "^" + System.Text.RegularExpressions.Regex.Escape(stem) + @"_\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    && seen.Add(Path.GetFullPath(sib)))
                { Console.WriteLine($"Auto-mounted mesh patch archive: {Path.GetFileName(sib)}"); yield return sib; }
        }
    }
    // AUTO-DISCOVER THE GAME'S BASE ARCHIVES the way the engine mounts the whole Archives folder. BF1942 spreads
    // its meshes across MANY top-level archives - Bocage's church/windmill/lumbermill/farm/hospital/barack live in
    // aiMeshes.rfa, its suburbhouses/ruins in StandardMesh_001.rfa, only a few in standardMesh.rfa. Picking just
    // standardMesh.rfa (or relying on the _NNN sibling) left whole buildings missing. Walk up from each level .rfa to
    // its "Archives" ancestor and pull in every top-level *.rfa there (texture* go to the texture lib; the bulky
    // audio/menu/font archives are skipped - they carry no meshes). Self-contained levels just find their own folder;
    // a level that isn't under an Archives dir discovers nothing and falls back to the picked archives.
    static bool IsTexArc(string p) => Path.GetFileName(p).StartsWith("texture", StringComparison.OrdinalIgnoreCase);
    static bool IsNonMeshArc(string p)
    {
        var n = Path.GetFileNameWithoutExtension(p).ToLowerInvariant();
        return n.StartsWith("sound") || n.StartsWith("movie") || n.StartsWith("music") || n is "menu" or "font" or "shaders";
    }
    static IEnumerable<string> DiscoverArchives(IEnumerable<string> levelRfas)
    {
        var seenDir = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> Glob(string archivesDir)
        {
            if (!Directory.Exists(archivesDir) || !seenDir.Add(Path.GetFullPath(archivesDir))) yield break;
            string[] files; try { files = Directory.EnumerateFiles(archivesDir, "*.rfa").ToArray(); } catch { yield break; }
            foreach (var f in files)
                if (!Path.GetFileName(f).StartsWith("~") && seenFile.Add(Path.GetFullPath(f))) yield return f;
        }
        foreach (var lvl in levelRfas)
        {
            DirectoryInfo? arc; try { arc = new FileInfo(Path.GetFullPath(lvl)).Directory; } catch { continue; }
            for (; arc is not null; arc = arc.Parent) if (arc.Name.Equals("Archives", StringComparison.OrdinalIgnoreCase)) break;
            if (arc is null) continue;
            // 1) the mod's OWN archives (the Archives folder the level sits under).
            foreach (var f in Glob(arc.FullName)) yield return f;
            // 2) the MOD DEPENDENCY CHAIN: a custom map (e.g. interstate's Dystopia_City) embeds most of its meshes but
            //    still references BASE-game archives (trees in treeMesh.rfa, suburbhouses in StandardMesh_001.rfa) that
            //    live in Mods\bf1942\Archives, NOT the mod's folder. Parse the mod's init.con `game.addModPath` chain
            //    (relative to gameRoot) + ALWAYS the base game mod, and glob each one's Archives - exactly how OpenMod
            //    + the engine mount the chain. So opening a mod LEVEL directly resolves 100%, no manual archive picks.
            var modDir = arc.Parent;
            DirectoryInfo? gameRoot = null;
            for (var pp = modDir; pp?.Parent is not null; pp = pp.Parent)
                if (pp.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) { gameRoot = pp.Parent; break; }
            if (modDir is null || gameRoot is null) continue;
            // TRANSITIVE: ModChain follows each dependency's own init.con too, so opening an FHSW-family map
            // directly (the common case - no .rfproj involved) mounts FH as well, not just FHSW + the base game.
            var resolved = RefractorForge.Formats.ModChain.Resolve(gameRoot.FullName, modDir.FullName, AppPrefs.ResolveInheritedMods);
            Console.WriteLine($"Mod chain for {modDir.Name}: {resolved.Describe()}");
            if (resolved.Missing.Count > 0)
                Console.WriteLine($"   WARNING - init.con names {resolved.Missing.Count} mod(s) that are NOT installed: {string.Join(", ", resolved.Missing)}");
            foreach (var mp in resolved.Paths)
            {
                var mpArc = Path.Combine(mp, "Archives");
                foreach (var f in Glob(Directory.Exists(mpArc) ? mpArc : mp)) yield return f;
            }
        }
    }
    var discovered = DiscoverArchives(rfaList.Where(File.Exists)).ToList();
    if (discovered.Count > 0)
        Console.WriteLine($"Auto-discovered {discovered.Count} archive(s) from the level's mod + dependency chain.");

    var meshA = WithMeshSiblings(meshArchives
            .Where(a => !Path.GetFileName(a).StartsWith("texture", StringComparison.OrdinalIgnoreCase)))
        .Concat(discovered.Where(a => !IsTexArc(a) && !IsNonMeshArc(a)))   // base mesh/object archives (aiMeshes, treeMesh, _001, ...)
        .Concat(rfaList.Where(File.Exists))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
    if (meshA.Length > 0)
    {
        meshLib = MeshLibrary.Open(meshA);
        var smDir = scanDir is not null && Directory.Exists(scanDir)
            ? Directory.EnumerateDirectories(scanDir, "Standardmesh", SearchOption.AllDirectories).FirstOrDefault() : null;
        if (smDir is not null) meshLib.AttachShaderOverrides(smDir);
        Console.WriteLine($"Opened mesh library from {meshA.Length} archive(s).");

        // Object textures: combine the texture archives the user picked (e.g. texture.rfa AND
        // texture_001.rfa) with any texture*.rfa siblings found beside the picked archives / level.
        var texDirs = meshArchives
            .Concat(texPicks)
            .Select(a => string.IsNullOrEmpty(a) ? null : Path.GetDirectoryName(Path.GetFullPath(a)))
            .Append(scanDir)
            .Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        // The level's OWN .rfa(s) go FIRST so a map's shipped texture OVERRIDES win — a custom map retextures objects /
        // vehicles (e.g. the humvee skin, the galleon, hi-res skybox faces) by shipping .dds with the SAME names inside
        // the level .rfa, and the engine searches the level's path before mod/base. TextureLibrary is first-wins. (An
        // earlier session reverted this fearing it broke tree leaves — but the bald trees were a separate .tm cutout bug,
        // now fixed in MeshFromTreeMesh/GlTextureFor, so level-first is safe AND engine-correct.)
        var texArchives = rfaList.Where(File.Exists)
            .Concat(texPicks.Where(File.Exists))
            .Concat(texDirs.SelectMany(d => Directory.EnumerateFiles(d, "texture*.rfa", SearchOption.TopDirectoryOnly)))
            .Concat(discovered.Where(IsTexArc))   // texture*.rfa auto-discovered from the level's mod-chain Archives folders
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (texArchives.Length > 0)
        {
            meshLib.AttachTextures(TextureLibrary.Open(texArchives));
            Console.WriteLine($"Object textures: {texArchives.Length} archive(s) - {string.Join(", ", texArchives.Select(Path.GetFileName))}.");
        }
        else
        {
            Console.WriteLine("No texture*.rfa found - objects render untextured. " +
                              "Pick texture.rfa and texture_001.rfa at startup (multi-select), or run with --pick.");
        }
    }
}

float minH = float.MaxValue, maxH = float.MinValue;
foreach (var p in mesh.Positions) { if (p.Y < minH) minH = p.Y; if (p.Y > maxH) maxH = p.Y; }

// Silk.NET finds its windowing/input backends by PROBING THE APP DIRECTORY for Silk.NET.Windowing.Glfw.dll and
// Silk.NET.Input.Glfw.dll. A single-file publish has no such files to probe, so nothing registers and the very
// next line throws PlatformNotSupportedException ("Couldn't find a suitable window platform") - which, in a
// WinExe with no console, kills the process with no message at all. That is exactly how the first packaged
// build failed: the startup screen and file pickers (plain WinForms) worked, then the editor simply never opened.
// Registering the backends explicitly does not depend on files on disk, so it works in BOTH layouts.
Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

var opts = WindowOptions.Default;
opts.Size = new Vector2D<int>(1280, 800);          // restored size when un-maximized
opts.WindowState = WindowState.Maximized;          // open filling the screen (keeps the title bar + window controls)
opts.Title = "RefractorForge Viewer";
IWindow window = Window.Create(opts);

GL gl = null!;
IInputContext input = null!;
IMouse? mouse = null;
IKeyboard? kb = null;

uint terrainProg = 0, markerProg = 0, terrainVao = 0, terrainVbo = 0, markerVao = 0, markerVbo = 0;
uint previewVao = 0, previewVbo = 0;   // single point showing where a placement will land
uint gizmoVao = 0, gizmoVbo = 0;       // 3 axis handles (drawn as GL_LINES via the marker shader)
uint collisionVao = 0, collisionVbo = 0; int collisionLineCount = 0; bool collisionDirty = true;  // .sm collision wireframe overlay (RE'd DShape)
int collisionSig = -1;   // cheap change-detector so the collision overlay re-bakes when objects/vehicle-spawns/showVehicles change
uint collisionProg = 0; int uCMvp = -1, uCCam = -1, uCColor = -1, uCFogStart = -1, uCFogEnd = -1;  // fog-faded collision shader
uint ringVao = 0, ringVbo = 0;         // 3 rotation rings (drawn as GL_LINE_LOOP)
uint brushRingVao = 0, brushRingVbo = 0;  // terrain-brush radius ring (XZ circle at the cursor)
uint brushSquareVao = 0, brushSquareVbo = 0;  // square-brush radius outline (XZ square at the cursor)
uint brushDrapeVao = 0, brushDrapeVbo = 0;    // dynamic brush-cursor outline that drapes on the terrain
uint gridVao = 0, gridVbo = 0; int gridVertCount = 0; float gridStep = 0f; bool gridDirty = true;  // draped world-grid overlay
uint indicatorVao = 0; int indicatorCount = 0;  // small 3D diamond drawn at mesh-less objects so they're visible
uint gpVao = 0, gpVbo = 0;             // gameplay markers (control points / vehicle spawns / soldier spawns)
int terrainIndexCount = 0;

// gameplay layer (control points, vehicle spawners, soldier spawns) + per-layer visibility
GpKind gpKind = GpKind.ControlPoint;                          // kind of the selected gameplay handle
int gpIndex = -1;                                             // selected gameplay handle (-1 = none)
bool gpDragging = false; Vector3 gpDragStart = default;       // ground-plane drag-move of a handle
Vector3 gpInsPos = default; float gpInsRad = 0f;             // inspector numeric fields for the selected handle
Vector3 gpInsRot = default;                                   // inspector rotation (Euler: X=yaw, Y=pitch, Z=roll)
string gpNameBuf = "";                                        // inspector name field buffer
string gpVehBuf = "";                                         // inspector vehicle-template field buffer
// Common BFV vehicle templates for the vehicle-spawn picker (the current value is appended if it's not listed).
string[] vehicleCatalog = { "Sheridan", "M48Patton", "M113", "Mutt", "t54", "M46", "vespa", "uh1Assault",
                            "UH1Transport", "Chinook", "Mi8Cargo", "F4Phantom", "Corsair", "PBR", "Sampan", "ZSU", "bm21sam" };
string[]? vehCacheList = null; MeshLibrary? vehCacheFor = null;   // mod-aware vehicle dropdown cache (rebuilt per loaded mesh library)
(Vector3 pos, float yaw, float pitch)?[] camBookmarks = new (Vector3 pos, float yaw, float pitch)?[9];   // camera bookmarks: Ctrl+1..9 save, 1..9 recall
bool autoBackup = true;                 // copy the level to a timestamped Backups\ folder before each save (File menu toggle)
GpKind? gpPlaceKind = null;                                   // armed gameplay placement (null = place static object)
// Edit Control Point dialog (Battlecraft-style): a working copy of the selected control point's fields.
bool editCpRequest = false; int ecpIndex = -1;
bool measureMode = false; List<Vector3> measurePts = new();   // Measure tool: terrain points + running distance
// Road tool: a clicked centerline that a flatten+texture+material brush is swept along on Stamp. Editor42/Crysis-
// style upgrade: the points define a smooth Catmull-Rom SPLINE, points are draggable with per-point width overrides,
// the texture can orient ALONG the road (lane markings follow curves), and the flatten gets its own shoulder width.
bool roadMode = false; List<Vector3> roadPts = new();
float roadWidth = 12f; byte roadSurface = 15; bool roadFlatten = true;
float roadIntensity = 0.9f;   // 0..1 how strongly the road surface replaces the ground colour
float roadEdge = 2f;          // metres of soft feather outside the road edge (smooths the boundary)
List<float> roadPtW = new();  // per-point width override in metres (0 = use roadWidth), parallel to roadPts
int roadSelIdx = -1;          // selected road point (-1 = none); shows the per-point width slider
int roadDragIdx = -1;         // road point being dragged across the terrain (-1 = none)
bool roadOrient = true;       // orient the texture along the road (u = across, v = along); off = world tiling
bool roadTexRotate = true;    // road strip runs along the texture's horizontal axis (BF/ED42 convention); off = vertical strip
float roadTileAlong = 12f;    // metres of road length per texture repeat when oriented
bool roadUseLib = false;      // road texture comes from the Texture Library pick (else the Surface slot's texture)
Texture2D? roadLibTex = null; string? roadLibTexPath = null;   // the picked library road texture
float roadShoulder = 2f;      // extra flattened metres outside each road edge (the embankment shoulder)
bool validateRequest = false; string validateReport = "";     // Map validation report popup
string ecpName = "", ecpCpName = "";
float ecpRadius = 30f; int ecpTeam = 0, ecpArea = 0, ecpConv = 40, ecpGroup = 0;
// BF1942 control-point fields (Battlecraft "Edit Control Point" dialog).
int ecpOsId = 0, ecpTimeGet = 9999, ecpTimeLose = 9999, ecpDisEnemy = 0, ecpDisLosing = 0, ecpLoseClose = 1, ecpLoseNot = 0, ecpUnable = 0, ecpOnlyTeam = 0, ecpCollision = 1;
Vector3 ecpPos = default;
// "Edit Object Spawn" (vehicle) + "Edit Soldier Spawn" Battlecraft-style dialogs: working copies of the selected handle.
bool editVehRequest = false; int evIndex = -1; string evName = ""; Vector3 evPos = default, evRot = default; int evTeam = 0, evOsId = 1;
bool editSolRequest = false; int esIndex = -1; string esName = ""; Vector3 esPos = default, esRot = default; int esGroup = 0, esSpawnId = 0; bool esPara = false;
double gpLastClickTime = -1; GpKind gpLastClickKind = GpKind.ControlPoint; int gpLastClickIndex = -1;   // double-click-to-edit a gameplay handle
bool showHelp = false; string? helpText = null;             // Help > User Guide window (loads USER_GUIDE.md next to the exe)
bool gpRotDragging = false; float gpRotStartYaw = 0f, gpRotStartMouseX = 0f;  // Rotate-tool yaw drag on a spawn
bool showTerrain = true, showObjects = true, showVehicles = true, showControlPoints = true, showSpawns = true;
bool showSpawnLinks = true;                                   // lines from each vehicle/soldier spawn to its owning control point
bool showSounds = true;                                      // sound emitters: marker + audible-radius (minDistance) ring
bool playSounds = false;                                     // play placed LOOPING sounds while the camera is inside their ring
SoundPlayback? soundPlayback = null;                         // NAudio mixer for the placed-sound preview (lazy on first enable)
List<string>? soundWavArchives = null;                       // cached .rfa(s) searched for a level's / mod's .wav (lazy)
bool showCollision = false;                                  // .sm collision-mesh wireframe overlay (off by default)
bool expCollision = false;                                   // .obj export: include an experimental (empty-BSP) collision section
bool showFoliage = false;                                    // overgrowth-trees overlay: instance the .wst geometry on the map (a VIEW; never saved)
bool showAnimations = true;                                  // spin RotationalBundle parts (windmill blades, watermill wheel, mod rotors); view-only
float foliageSpacing = 12.5f;                                // patch grid size (m) -- the game uses ~12.5 m; drives the game-matched density
float foliageDensity = 1f;                                   // density multiplier on the per-patch tree count (1.0 = game-matched)
bool foliageDirty = true;                                    // rebuild the foliage overlay (toggled on / params changed / level loaded)
int foliageCount = 0;                                        // instances currently in the overlay (for the Layers readout)
// Weather (rain/snow/dust): a view-only preview overlay + optional generate-into-level on save.
bool showWeather = false;                                    // preview overlay on/off
int weatherTypeIdx = 0;                                      // 0=Snow 1=Rain 2=Dust (RefractorForge.Formats.Con.WeatherType)
var detectedLevelWeather = new List<(string Name, int TypeIdx)>();   // weather-ish effect templates the LEVEL itself defines/places (FH winter maps etc.)
bool levelWeatherScanned = false;                            // lazy: scanned on first Weather-panel draw
int weatherIntensity = 200;                                  // particles/sec for the generated .con (also scales the preview)
float weatherWind = 0f;                                      // horizontal drift (m/s)
bool weatherApply = false;                                   // write the weather Effects.con + texture into the level on save
uint weatherVao = 0, weatherVbo = 0;                         // preview GL buffers (built in OnLoad)
Vector3[] weatherPos = System.Array.Empty<Vector3>();        // preview particle positions (world)
Vector3[] weatherVel = System.Array.Empty<Vector3>();        // preview particle velocities
float[] weatherVerts = System.Array.Empty<float>();          // upload scratch (points, or 2 verts/streak for rain)
int weatherVertCount = 0;
System.Random weatherRng = new(12345);
uint weatherProg = 0;                                        // textured point-sprite shader for the preview
int uWMvp = -1, uWTex = -1, uWColor = -1, uWSize = -1;       // weatherProg uniforms
// Particle EFFECTS (the level's FX/*.con: waterfalls, lava, fire, smoke, steam...) shown as animated billboards.
EffectsLibrary? effectsLib = null;                           // parsed effect bundles (lazy - built on first enable)
bool effectsLoaded = false;                                  // the (heavy) effect-con parse is deferred off the load path
bool showEffects = false;                                    // Layers toggle: animated particle effects
uint effectProg = 0, effectVao = 0, effectVbo = 0;          // billboard shader + dynamic per-frame buffer
int uEMvp = -1, uERight = -1, uEUp = -1, uETex = -1, uETint = -1;
System.Collections.Generic.List<FxInstance2> fxInstances = new();
float[] fxVerts = System.Array.Empty<float>();              // per-frame billboard upload scratch
System.Random fxRng = new(9001);
double fxClock = 0;
Texture2D?[] weatherTexImg = new Texture2D?[4];              // imported particle image per type (null = use generated); for ship
uint[] weatherTexGl = new uint[4];                           // GL preview texture per type (0 = build lazily from generated/imported)
string sndWavBuf = "";                                       // inspector buffer for the selected sound's wav path
System.Collections.Generic.Dictionary<string, ObjMesh> importedObjs = new(StringComparer.OrdinalIgnoreCase);  // imported .obj meshes -> for .sm export
System.Collections.Generic.HashSet<string> remoteMeshNames = new(StringComparer.OrdinalIgnoreCase);            // .obj meshes received from collab peers (render + catalog only; no source for .sm export)
MeshLibrary.Mesh? soldierBoxMesh = null;                      // lazily-built soldier-sized box used as the soldier-spawn marker
System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<(string Mat, string? TexName, Vector3 Diffuse)>> importMaterials = new(StringComparer.OrdinalIgnoreCase);  // -> for .rs export
string toastText = ""; float toastT = 0f;                    // transient status-bar confirmation message (decays in OnRender)

// texture-material painting
MaterialStroke? matStroke = null;
// terrain TEXTURE painting (paints the visible atlas with a tiled surface texture; saved back to txCxR.dds).
AtlasPaintStroke? atlasStroke = null;
byte activeTexture = 1;              // which surface texture (indexes texPalette)
float texIntensity = 0.85f;         // 0..1 blend strength of the texture brush (the Intensity slider)
float texTileMeters = 8f;           // world metres per texture repeat
Texture2D?[] texPalette = new Texture2D?[16];   // the 16 bundled clean surface textures
Vector4[] texSwatch = new Vector4[16];          // each surface's average colour, for its picker swatch
string?[] texSource = new string?[16];          // null = bundled default; else the imported texture path (override + set save)
// Surface capture: click the terrain to save a square region of the painted atlas as a reusable .dds texture.
bool captureMode = false;
float captureMeters = 64f;       // world size of the captured square
int captureResIdx = 1;           // index into captureSizes
bool captureImport = true;       // also drop the captured texture into the active slot
int[] captureSizes = { 128, 256, 512, 1024 };
string[] captureSizeNames = { "128", "256", "512", "1024" };
// Surface names for the bundled Default ("GRASSY") texture set, straight from its index.dat (texture order 0..15).
string[] surfNames = { "Default", "Water", "Dry Grass", "Wet Grass", "Dry Dirt", "Damp Dirt", "Mud", "Outside Map",
                       "Gravel", "Frozen Ground", "Dry Sand", "Wet Sand", "Rock Surface", "Sand Road", "Dirt Road", "Paved Road" };
// ---- Texture Library + Editor42-style Layer Tool (paint imported tileable textures; height/slope/noise blend) ----
// A folder of user textures shipped beside the exe (TerrainTextures\<Category>\*) that users drop their own into.
string texLibRoot = Path.Combine(AppContext.BaseDirectory, "TerrainTextures");
List<(string Path, string Name, string Category)> texLibEntries = new();
string[] texLibCats = { "All" };
int texLibCatIdx = 0;
string texLibSearch = "";
Dictionary<string, uint> texLibThumb = new();   // file path -> 64px GL thumbnail (lazy, rate-limited per frame)
int texLibThumbBudget = 0;                       // thumbnails allowed to build this frame
bool showTexLibrary = false;
Texture2D? libTex = null;                        // the active library paint texture (full-res)
string? libTexPath = null;
float libTileMeters = 8f;
bool paintFromLib = false;                       // Surface brush paints libTex instead of the 16-slot palette
// Layer tool: two textures blended by height/slope + fractal noise (Editor42's Height/Slope layer dialog).
bool showLayerTool = false;
int layerPickTarget = 0;                         // library click target: 0 = active paint tex, 1 = layer A, 2 = layer B
int layerSelectorIdx = 0;                        // 0 = Height, 1 = Slope
float layerThrLow = 20f, layerThrHigh = 60f;     // height: metres; slope: degrees
bool layerNoiseOn = true;
int layerSeed = 2300, layerFirstOctave = 2, layerOctaveCount = 6;
float layerThrWidth = 0.35f;
Texture2D? layerTexA = null, layerTexB = null;
string? layerTexAPath = null, layerTexBPath = null;
float layerTileA = 8f, layerTileB = 8f;
uint layerProofGl = 0;                           // GL id of the Proof preview (0 = none)
bool layerProofDirty = true;
string layerPresetName = "myLayer";
bool surfUseAlpha = false;                       // Surface brush: honor the source texture's alpha as a decal/splat mask
bool detailImported = false;                     // user imported a tiling detail texture -> ship Textures/detail.dds on save
float detailRepeatM = 8f;                        // world metres per detail-texture repeat (DetailRepeatMeters)
// foliage (undergrowth/overgrowth) painting - reuses the material-map painting stack on the two
// Growth/ index maps. Each layer has its own resolution (often != materialSize), so each painter
// gets a TerrainConfig whose MaterialSize is that layer's side (correct world<->grid spacing).
int paintLayer = 0;            // active paint target: 0 = Material, 1 = Undergrowth, 2 = Overgrowth
byte activeFoliage = 1;        // foliage index the brush stamps (0 = clear/no foliage)
uint matTexId = 0;
byte activeMaterial = 1;
float matHardness = 1f;
// Swatch colours + names mirroring Battlecraft's material set (averaged straight from its media\mat_*.bmp).
// The painted value is the material INDEX (0..15); the actual ground surface still comes from the level's
// texture set, so these are an authentic visual guide, not a guaranteed per-level mapping.
System.Numerics.Vector4[] matPalette =
{
    new(0.95f,0.95f,0.95f,1), new(0.631f,0.678f,0.408f,1), new(0.416f,0.678f,0.408f,1), new(0.698f,0.663f,0.545f,1),
    new(0.639f,0.580f,0.424f,1), new(0.502f,0.443f,0.306f,1), new(0.867f,0.859f,0.784f,1), new(0.804f,0.792f,0.671f,1),
    new(0.784f,0.761f,0.725f,1), new(0.620f,0.475f,0.027f,1), new(0.596f,0.588f,0.486f,1), new(0.816f,0.808f,0.706f,1),
    new(0.463f,0.463f,0.463f,1), new(0.600f,0.588f,0.486f,1), new(0.784f,0.784f,0.839f,1), new(0.0f,0.718f,0.718f,1),
};
string[] matNames =
{
    "Default", "Dry Grass", "Wet Grass", "Dry Dirt", "Wet Dirt", "Mud", "Dry Sand", "Wet Sand",
    "Gravel", "Rock", "Dirt Road", "Sand Road", "Paved Road", "Wet Road", "Frozen Ground", "Water",
};
// material index (matNames order) -> surface slot (surfNames order), matched by name, for the surface-atlas bake.
int[] matToSurf = { 0, 2, 3, 4, 5, 6, 10, 11, 8, 12, 14, 13, 15, 15, 9, 1 };

// terrain sculpting state
TerrainStroke? stroke = null;          // active sculpt drag (mouse-down..up), coalesced into one undo
bool terrainDirty = false;             // heightmap changed this frame -> re-upload the terrain VBO
float brushRadius = 40f;               // metres
float brushStrength = 2.0f;            // metres-at-centre per dab (Raise/Lower)
float smoothStrength = 0.5f;           // 0..1 blend-at-centre per dab (Smooth/Flatten)
// Sculpt brush mode + falloff (the model already supports all of these; the UI now exposes them).
int sculptModeIdx = 0;                  // Sculpt tool: 0 Raise, 1 Lower, 2 Flatten, 3 Set
int falloffIdx = 0;                     // 0 Smooth, 1 Linear, 2 Constant, 3 Gaussian (== BrushFalloff order)
bool flattenLockGround = true;          // Flatten/Set: lock target to the height under the cursor at stroke start
float flattenTarget = 30f;              // explicit Flatten/Set target height (m) when not locked
string[] sculptModeLabels = { "Raise", "Lower", "Flatten", "Set" };
string[] falloffLabels = { "Smooth", "Linear", "Constant", "Gaussian" };
BrushMode[] sculptModes = { BrushMode.Raise, BrushMode.Lower, BrushMode.Flatten, BrushMode.Set };
bool lrSculpt = false;                  // Sculpt option: LEFT mouse raises, RIGHT mouse lowers (instead of picking a Mode)
int activeStrokeDir = 0;                // +1 raise / -1 lower while an L/R-button sculpt stroke is live (0 = use the Mode)
bool alphaTransparency = true;          // render object/foliage texture alpha as transparency (cutout + soft blend)
bool showMinimapObjects = true;         // draw static-object dots on the mini-map (click one to select it)
// Battlecraft bitmap brush shapes (brushes\*.bmp beside the exe) + a procedural "Radial" default at index 0.
List<(string Name, BrushMask? Mask)> brushShapes = new() { ("Radial", null) };
int brushShapeIdx = 0;
{
    var bdir = Path.Combine(AppContext.BaseDirectory, "brushes");
    if (Directory.Exists(bdir))
        foreach (var bf in Directory.EnumerateFiles(bdir, "*.bmp").OrderBy(x => x))
            try { brushShapes.Add((Path.GetFileNameWithoutExtension(bf), BrushMask.FromBmp(bf))); } catch { }
}
string[] brushShapeNames = brushShapes.Select(b => b.Name).ToArray();
// Clean surface textures for the Texture paint tool (textures\surfNN.bmp beside the exe). Each swatch shows the
// texture's average colour so the picker looks like the surfaces themselves.
{
    var tdir = Path.Combine(AppContext.BaseDirectory, "textures");
    for (int i = 0; i < texPalette.Length; i++)
    {
        texSwatch[i] = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        var tf = Path.Combine(tdir, $"surf{i:D2}.bmp");
        if (!File.Exists(tf)) continue;
        try
        {
            var tx = Texture2D.LoadBmp(tf);
            texPalette[i] = tx;
            if (tx is not null)
            {
                double r = 0, g = 0, b2 = 0; int px = tx.Rgba.Length / 4;
                for (int p = 0; p < tx.Rgba.Length; p += 4) { r += tx.Rgba[p]; g += tx.Rgba[p + 1]; b2 += tx.Rgba[p + 2]; }
                if (px > 0) texSwatch[i] = new Vector4((float)(r / px / 255), (float)(g / px / 255), (float)(b2 / px / 255), 1f);
            }
        }
        catch { }
    }
}
int uMvp = -1, uLight = -1, uWater = -1, uMaxH = -1, uHasTexT = -1, uShowMat = -1, uMvpM = -1, uColor = -1, uSize = -1;
int uDeepColor = -1;   // terrain submerged-tint colour (water.deepcolor)
int uHasDetail = -1, uDetailScale = -1;
// water surface (translucent plane at the water level) + gradient sky background
uint waterProg = 0, waterVao = 0, waterVbo = 0;
uint skyProg = 0, skyVao = 0, skyVbo = 0, skyCubeTex = 0;   // skyCubeTex 0 = no real cubemap loaded -> procedural
int uMvpW = -1, uLightW = -1, uCamW = -1, uTimeW = -1, uWaterYW = -1, uWaterColorW = -1, uWaterAlphaW = -1;
// Textured-water uniforms + state (the level's water.texLayer1/2 + normalMap, resolved on load).
int uHasWaterTexW = -1, uTexL1W = -1, uTexL2W = -1, uNormalW = -1, uScroll1W = -1, uScroll2W = -1, uScrollNW = -1, uTile1W = -1, uTile2W = -1, uTileNW = -1, uSpecColW = -1;
uint waterTex1 = 0, waterTex2 = 0, waterNorm = 0;   // GL textures for the two diffuse layers + the normal map (0 = none)
bool haveWaterTex = false;                            // the level's water textures resolved
bool useWaterTextures = true;                         // Layers toggle: textured water vs procedural
int uInvVPS = -1, uCamPosS = -1, uSunDirS = -1, uFogColorS = -1, uRotS = -1, uHasCubeS = -1, uCubeS = -1;
int uHasCloudS = -1, uCloudTexS = -1, uCloudColorS = -1, uCloudScrollS = -1, uCloudScaleS = -1, uCloudOpacityS = -1;
// Real skybox + cloud MESH render (the level's actual Sky_* / cloud .sm with their embedded textures). One unlit shader,
// per-part texture binding. Resolved on load via meshLib; drawn instead of the procedural gradient/cloud overlay.
uint skyMeshProg = 0; int uSMmvp = -1, uSMscroll = -1, uSMpin = -1, uSMtex = -1, uSMhasTex = -1, uSMopaque = -1, uSMtint = -1;
uint skyMeshVao = 0; (int Off, int Count, uint Tex)[] skyMeshParts = System.Array.Empty<(int, int, uint)>();
bool skyMeshOk = false;                 // a real skybox mesh resolved -> draw it instead of the gradient/cubemap
// Skybox face editor: per sky-mesh material, the .rs texture reference and any user assignment (an image, shipped
// as a same-named .dds inside the level, or a .bik movie, shipped via an override .rs - the engine plays Bink
// texture paths, the classic GCMOD/EoD trick).
string?[] skyMeshTexNames = System.Array.Empty<string?>();
Dictionary<int, (string Kind, string Path)> skyFaceAssign = new();
bool skyFacesDirty = false;
uint cloudMeshVao = 0; (int Off, int Count, uint Tex)[] cloudMeshParts = System.Array.Empty<(int, int, uint)>();
bool cloudMeshOk = false;               // a real cloud mesh resolved -> draw it instead of the procedural overlay
bool showCloudMesh = true;              // SKY inspector toggle for the real cloud-layer meshes
uint cloudTex = 0;                      // procedural tileable cloud-density texture (built in OnLoad)
// Animated clouds (Refractor Cloud system): edit fields, the level's env.Clouds is the saved source of truth.
bool cloudsOn = false;                  // render + (on save) write the cloud layer
bool cloudsDirty = false;              // user touched clouds -> patch SkyAndSun.con on save
float cloudSpeedX = -0.03f, cloudSpeedY = 0.015f;   // Cloud.setSpeed (UV/sec)
float cloudScale = 0.5f;                // ray->UV projection scale (derived from TexScale)
float cloudOpacity = 0.65f;
float cloudHeight = 3500f;             // Cloud.setHeight (game), informational for the preview
Vector3 cloudColor = new(0.96f, 0.97f, 1.0f);
string? cloudMeshImportPath = null;    // imported cloud .sm/.obj to ship into the level on save (for in-game)
bool skyUseCubemap = true;              // use the level's real cubemap when loaded (else the procedural sun-sky)
float skyRotDeg = 0f;                   // user yaw offset added to the level's Sky.setRotAngle
double appClock = 0;                    // seconds since launch, drives the water ripple animation
bool showWater = true, showSky = true;  // Layers-panel toggles
uint shadowTexId = 0;                   // (legacy) baked sun-shadow buffer for the TerrainShadow.dds export only
bool showShadows = false;              // real-time sun shadow map toggle (OFF by default - no dark-by-default ground)
// Real-time sun shadow map: a depth render of terrain + objects from the sun's POV, sampled by the terrain/object
// shaders for live cast shadows that follow the controllable sun. Replaces the old baked .lsb display.
uint shadowMapFbo = 0, shadowMapDepthTex = 0;
const int shadowMapSize = 4096;        // bigger map = sharper shadows; paired with a camera-focused frustum
System.Numerics.Matrix4x4 lightSpace = System.Numerics.Matrix4x4.Identity;
bool shadowMapDirty = true;
Vector3 lastShadowFocus = new(float.MaxValue);   // re-render the shadow map when the camera focus/zoom moves enough
float lastShadowRadius = 0f;
uint depthProg = 0;
int uLightSpaceD = -1, uModelD = -1;                            // depth-pass program uniforms
int uLightSpaceT = -1, uShadowMapT = -1, uUseShadowMapT = -1;   // terrain program shadow uniforms
int uLightSpaceO = -1, uShadowMapO = -1, uUseShadowMapO = -1;   // object program shadow uniforms
// Sun-direction control (azimuth + elevation). When sunOverride is on, the editor lights with these instead of the
// level's SkyAndSun.con; moving them relights terrain + objects + re-renders the shadow map in real time.
bool sunOverride = false;
float sunAzimuthDeg = 135f, sunElevationDeg = 40f;
float camSpeedMult = 1f;               // user multiplier on WASD fly speed (and scroll dolly)
// Live correction for where the terrain atlas lands on the ground. Six separate attempts to derive this from the
// level data (colour classification, minimap and gradient correlation, land/water separation, per-tile matching,
// the height formula) all came back as noise, so the parameter is exposed rather than guessed: dial it until the
// paint sits on the terrain, then the value can be made the default. 1 / 0 = the original mapping.
float terUvScale = 1f, terUvOffX = 0f, terUvOffY = 0f;
int uTerUvScaleL = -1, uTerUvOffsetL = -1;
// Battlecraft-style camera: you travel toward whatever sits in the MIDDLE OF THE SCREEN. W/S follow the true view
// vector, so aiming down and holding W descends and aiming up climbs - the height comes from where you look, with
// no need to touch Q/E. The default fly camera instead flattens forward onto XZ and keeps your altitude fixed
// until you press Q/E, which is the difference between the two modes.
bool groundCam = AppPrefs.GroundCamera;   // AppPrefs.Load() already ran, so this picks up the remembered choice
bool writeShadowLsb = false;            // on save, also bake + write the engine's LightmapShadowBits.lsb
bool shadowLsbFlipX = false, shadowLsbFlipY = false;   // in-game shadow mirror correction (toggle if shadows land mirrored)
uint detailTexId = 0;   // tiling detail texture (REPEAT + mipmaps), 0 = none
uint minimapTexId = 0;  // top-down minimap shown in the in-editor Mini-Map panel (0 = not built)
bool showMinimap = true;
uint terrainTexId = 0;   // baked GPU terrain atlas (0 = none ÃƒÂ¢Ã¢â‚¬Â Ã¢â‚¬â„¢ height-ramp shading)

uint objProg = 0;
int uMvpO = -1, uModelO = -1, uColorO = -1, uLightO = -1, uUseTexO = -1, uAlphaTestO = -1, uAlphaEnableO = -1, uTintO = -1;
GlObjects? glObjects = null;
// Editable fog state (seeded from the level's Init.con; tweaked live in the Environment panel).
bool fogEnabled = false; Vector3 fogColor = new(0.72f, 0.83f, 0.83f); float fogStart = 100f, fogEnd = 450f;
float waterLevelLoaded = 30f; bool waterLevelEdited = false;   // live Water Level slider: original for Reset + dirty flag for save
Vector3 waterColor = new(0.10f, 0.22f, 0.30f); float waterAlpha = 0.6f;   // water surface colour + transparency (from the level's water.color)
Vector3 deepColor = new(0.16f, 0.35f, 0.55f);   // submerged-terrain tint (from the level's water.deepcolor)

Camera cam = Camera.FrameAerial(cfg.WorldSize, (minH + maxH) * 0.5f, opts.Size.X / (float)opts.Size.Y);
cam.MirrorX = true;   // no view reflection - the native orientation already matches the game (left stays left)
// Movement speed and zoom step both scale with how high the camera is above the lowest terrain, so
// navigation is fast at map-overview height and fine when zoomed in next to an object.
float Altitude() => MathF.Max(8f, cam.Position.Y - minH);
int selected = -1;                  // primary selection: gizmo anchor + Inspector target
HashSet<int> multi = new();         // full selection set (contains `selected` whenever it is >= 0)
Vector2 lastMouse = default; bool haveMouse = false;
double fpsTimer = 0; int fpsFrames = 0;
double lastFps = 0;

// ---- .bik video playback (decoded to PNG frames via FFmpeg; RAD binkplay.exe is the external fallback) ----
bool bikOpen = false;
string? bikName = null;
string[] bikFrames = Array.Empty<string>();   // temp PNG frame paths, in order
int bikFrameIdx = 0, bikLoadedFrame = -1, bikW = 0, bikH = 0;
bool bikPlaying = true, bikLoop = true;
float bikFps = 15f;
double bikClock = 0;
uint bikTex = 0;
string? ffmpegPath = null;   // cached FFmpeg location ("" = looked, not found)

// ---- editor UI state (Dear ImGui) ----
ImGuiController imgui = null!;
string[] toolNames = { "Select", "Move", "Rotate", "Scale", "Place", "Paint", "Sculpt", "Smooth", "AIPath" };
int tool = 1;                 // default: Move (within the Object mapper)
// Top-level "mapper" mode (Battlecraft-style): drives the underlying tool + paint layer below, so every
// existing input handler keeps working. 0 Terrain(sculpt+smooth) | 1 Material | 2 Object | 3 Surface | 4 Growth | 5 AI Path.
string[] mapperNames = { "Terrain", "Material", "Object", "Surface", "Growth", "AI Path" };
int mapper = 2;               // default: Object (matches the default Move tool)
string[] aiPathVehNames = { "Tank", "Infantry", "Boat", "LandingCraft", "Car", "Heli", "Amphib" };  // == SearchMapParams.Standard order
int aiPathVeh = 0;            // AI Pathmapping: index into SearchMapParams.Standard (Tank0..Amphibius6)
bool aiPathBlock = true;      // brush paints Blocked (0xFF/white) vs Passable (0x00/black)
byte[]?[] aiNavBufs = new byte[aiPathVehNames.Length][];  // per-vehicle WORLD-GRID finest map (null = not seeded); switching Vehicle swaps these, no reseed
bool[] aiNavBufDirty = new bool[aiPathVehNames.Length];   // per-vehicle painted-since-save flag (parallel to aiNavBufs)
byte[]? aiNav = null;         // ACTIVE view = aiNavBufs[aiNavVehLoaded]: WORLD-GRID, finest level, 0x00 pass / 0xFF block
int aiNavSide = 0;            // side of aiNav (= SearchMapGenerator.FinestSide)
int aiNavVehLoaded = -1;      // which vehicle aiNav holds (-1 none); switching Vehicle now swaps buffers (edits preserved)
bool aiNavDirty = false;      // active vehicle painted since last save (proxy for aiNavBufDirty[aiNavVehLoaded])
uint aiNavTexId = 0;          // R8 overlay texture (bound to uMat unit 1 in AI Path mode)
bool aiNavTexDirty = false;   // navmap changed -> (re)upload overlay texture
bool aiNavPainting = false;   // left-drag active in the AI Path mapper
AiNavStroke? aiNavStroke = null;  // accumulator for the active AI-path drag (per-stroke undo)
bool snapOn = false, gridOn = true;
float snapStep = 1f;          // grid step (m) used when Snap is on: object move/place X/Z round to this
bool gridLabels = false;      // toggle: draw world-coordinate text at grid intersections (separate overlay)
bool gridPrevOn = true;       // detect the Grid-toggle rising edge to re-drape the overlay
Vector3 gridColor = new(0.55f, 0.6f, 0.62f);   // draped grid line colour (user-customizable)
bool gameIsBf1942 = false;    // target game: false = Battlefield Vietnam (default), true = Battlefield 1942.
                              // Drives team names (Axis/Allies vs NVA/US) + gates BFV-only features (overgrowth, tunnels).
uint sliderEditId = 0;        // the slider currently being typed into (0 = none); right-click a slider to type a value
bool sliderEditStart = false; // focus the input on the first frame of editing
bool showLog = false;         // in-app Log / Errors window (captures console output; auto-pops on load warnings)
bool logErrorsOnly = false;   // filter the Log window to just error/warning lines
Vector2D<int> appliedFbSize = default;   // last framebuffer size the GL viewport/aspect were synced to (initial maximize fix)
bool squareBrush = true;      // square (box) footprint for the terrain + material brushes (default, per user request)
// UI layout metrics, hoisted so the 3D overlay (DrawGameplay labels) can clip world-space labels to the
// central viewport instead of painting them over the side/menu/status panels. uiMenuH is refreshed by BuildUi.
float uiMenuH = 0f;
const float uiStatusH = 30f, uiLeftW = 300f, uiRightW = 384f;   // Inspector wide enough for its labels
float uiToolH = 78f;   // top toolbar height; measured each frame from the actual content (mapper bar + per-mapper sub-toolbar) so it never clips
string searchText = "";
// New Map dialog state (the app's first ImGui modal). Used only inside the BuildUi local functions.
bool newMapRequest = false;                            // set by the New button/menu -> OpenPopup next frame
string nmName = "MyMap";
string nmFolder = "";                                  // parent folder the level folder is created under
int[] nmMatSizes = { 256, 512, 1024, 2048 };
string[] nmMatSizeLabels = { "256", "512", "1024", "2048" };
int nmMatSizeIdx = 1;                                  // -> 512
int nmWorldSize = 2048;
int[] nmWorldSizes = { 256, 512, 1024, 2048, 4096, 8192, 16384, 32768 };   // power-of-two metres; 32768 = 32 km (== BF1942 128_planes)
string[] nmWorldSizeLabels = { "256", "512", "1024", "2048", "4096", "8192", "16384 (16km)", "32768 (32km)" };
int nmWorldSizeIdx = 3;                                          // -> 2048
float nmYScale = 0.5f, nmWaterLevel = 30f;
int nmTerrainType = 1;                                 // 0=Flat 1=Rolling Hills 2=Mountains 3=Islands 4=Import .raw
string[] nmTerrainTypeLabels = { "Flat", "Rolling Hills", "Mountains", "Islands", "Heightmap (.raw)" };
string nmHeightmapPath = "";                           // 16-bit LE square .raw to seed the terrain from (type 4)
float nmFlatHeight = 32f;                              // metres (Flat)
int nmSeed = 2026;
float nmRoughness = 0.55f, nmMinH = 22f, nmMaxH = 160f; // Fractal: a more dramatic default relief (auto-fit yScale)

// ---- Scatter Objects (random buildings/vegetation placement) state ----
bool scatterRequest = false;
bool scatterVeg = true, scatterStruct = false, scatterProps = false, scatterAvoidWater = true;
int scatterCount = 150, scatterSeed = 1;
float scatterMaxSlope = 25f, scatterClearance = 1f, scatterSpacing = 6f;
float scatterScaleMin = 1f, scatterScaleMax = 1f;   // per-object random size variation (1/1 = uniform)
string scatterError = "";
bool nmPlayable = true;                                // also write a minimal Conquest layer (flags/spawns/kits)
bool nmGameBf1942 = false;                             // New Map target game (false = BF Vietnam, true = BF1942)
string nmError = "";
string? browserTemplate = null;                       // template highlighted in the Object Library
// 3D model viewer: double-click a model in the Object Library to inspect its mesh in an offscreen-rendered window.
bool meshViewerOpen = false;
string? meshViewerTemplate = null;
bool meshViewerAutoRotate = true;
float meshViewerYaw = 0f, meshViewerPitch = 0.3f;     // orbit angles (drag to change; auto-rotate spins yaw)
// AI Pathmap preview: decode a saved/opened Pathfinding .raw and show it as an image (to verify saves natively).
uint pathmapTex = 0;                                  // RGBA preview texture
bool pathmapPreviewOpen = false;
float pathmapPreviewT = 0f;                           // >0 = auto-close countdown (post-save); 0 while open = manual/persistent
string pathmapPreviewLabel = "";
int pathmapPreviewSide = 0;
float meshViewerZoom = 1f;                            // scroll/+/- zoom (1 = framed to fit; >1 closer, <1 farther)
uint mvFbo = 0, mvColorTex = 0, mvDepthRbo = 0;       // the preview render target (lazy, reused)
const int mvSize = 512;                                // preview resolution
string? dragTemplate = null;                          // template being drag-dropped from the library onto the map
List<(string label, string[] items)> catalog = new();  // categories -> template names
List<string> treeMeshNames = new();   // imported BF1942 treeMesh.rfa tree templates (render via the object pipeline)
// Sentinel "templates" for the draggable Gameplay category - a drop with one of these creates a
// gameplay spawn (control point / vehicle / soldier) instead of a static object.
const string GpDragControlPoint = "Control Point";
const string GpDragVehicle = "Vehicle Spawn";
const string GpDragSoldier = "Soldier Spawn";
GpKind? GpKindForDrag(string t) => t == GpDragControlPoint ? GpKind.ControlPoint
                                 : t == GpDragVehicle ? GpKind.Vehicle
                                 : t == GpDragSoldier ? GpKind.Soldier : (GpKind?)null;
// Object-group prefabs (Battlecraft-style stamps), loaded from prefabs\*.rfprefab beside the exe and shown
// as a draggable/placeable "Prefabs" library category. A drop stamps the whole group as one undo step.
List<Prefab> prefabs = new();
Dictionary<string, Prefab> prefabByKey = new(StringComparer.OrdinalIgnoreCase);
bool savePrefabRequest = false;                       // Edit menu -> open the Save-Prefab popup next frame
string spName = "MyPrefab";
string spError = "";
// ---- Collaboration (real-time multi-user object editing) ----
// (the `collab` field itself is declared earlier, next to `hist`, so the OnDo hook can capture it)
bool collabRequest = false;                            // open the Collaborate popup next frame
string collabName = Environment.UserName;
int collabPort = 7777;
string collabHostAddr = "127.0.0.1";
string collabPass = "";              // optional shared password for Host/Join (blank = open)
string collabError = "";
double collabPresenceTimer = 0;                        // throttles presence broadcasts
Vector3[] peerColors = { new(0.95f, 0.45f, 0.25f), new(0.35f, 0.80f, 0.45f), new(0.45f, 0.60f, 1f), new(0.95f, 0.80f, 0.30f), new(0.85f, 0.45f, 0.85f) };
// inspector edit buffers + drag-start snapshots (for correct, single undo entry per drag)
Vector3 insPos = default, insRot = default; float insScale = 1f;
Vec3 dragFromV3 = default; float dragFromScale = 1f;
// translate-gizmo drag state
int dragAxis = -1, hoverAxis = -1;
Vec3 gizmoStartPos = default; float gizmoStartT = 0f;
// free terrain-drag of the selected static object(s): grab the body (not an axis) and slide along the ground.
bool freeDragging = false; Vector3 freeDragGround = default;
// rotate-gizmo drag state
int rotDragChannel = -1, rotHover = -1;
Vec3 rotStartEuler = default; float rotLastAngle = 0f, rotAccumDeg = 0f;
// uniform-scale-gizmo drag state
bool scaleDragging = false; float scaleStartScale = 1f, scaleStartDist = 1f;
// snapshot of every selected object's transform at drag-start (for group move/rotate/scale)
List<(int idx, Vec3 pos, Vec3 rot, float scale)> dragSnap = new();

window.Load += OnLoad;
window.Update += OnUpdate;
window.Render += OnRender;
window.FramebufferResize += sz => { gl.Viewport(0, 0, (uint)sz.X, (uint)sz.Y); cam.Aspect = sz.X / (float)Math.Max(1, sz.Y); appliedFbSize = sz; };
window.Run();
return;

// ---------------------------------------------------------------------------
void SyncMarkers()
{
    markers = so!.Objects.Select(o => new Vector3(o.Position.X, o.Position.Y, o.Position.Z)).ToArray();
    // When meshes are loaded, only genuinely mesh-less objects (sound/effect emitters, logical points)
    // get an indicator marker; anything that resolves to a mesh OR assembles (vehicle/weapon) renders for real.
    pointMarkers = meshLib is null
        ? markers
        : so.Objects.Where(o => !meshLib.TryGet(o.Template, out _) && !meshLib.TryGetAssembledMesh(o.Template, out _))
                    .Select(o => new Vector3(o.Position.X, o.Position.Y, o.Position.Z)).ToArray();
}

// Give the GL window an icon for the taskbar / title bar / Alt-Tab. Silk's GLFW window does NOT pick up the
// exe's <ApplicationIcon> PE resource, so load the bundled .ico (copied beside the exe) and hand GLFW a few
// RGBA sizes. Purely cosmetic -- any failure is swallowed so it can never block startup.
void SetAppIcon()
{
    try
    {
        var icoPath = Path.Combine(AppContext.BaseDirectory, "RefractorForge.ico");
        if (!File.Exists(icoPath)) return;
        var imgs = new System.Collections.Generic.List<Silk.NET.Core.RawImage>();
        foreach (int sz in new[] { 16, 32, 48, 64 })
        {
            using var ico = new System.Drawing.Icon(icoPath, new System.Drawing.Size(sz, sz));
            using var bmp = ico.ToBitmap();
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
                System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            for (int i = 0; i < px.Length; i += 4) { (px[i], px[i + 2]) = (px[i + 2], px[i]); }   // BGRA -> RGBA
            imgs.Add(new Silk.NET.Core.RawImage(w, h, px));
        }
        if (imgs.Count > 0) window.SetWindowIcon(imgs.ToArray());
    }
    catch { /* icon is cosmetic; never block startup on it */ }
}

void OnLoad()
{
    var loadSw = System.Diagnostics.Stopwatch.StartNew();   // total GL-side load time (reported at the end)
    // Determine the target game (BF Vietnam vs BF1942) so team names + BFV-only features adapt. A refractorforge.game
    // sidecar (written by New Map) wins; else infer from the loaded paths. The user can override in the Environment panel.
    {
        string? side = null;
        try { if (levelDir is not null && System.IO.Directory.Exists(levelDir)) { var sp = System.IO.Path.Combine(levelDir, "refractorforge.game"); if (System.IO.File.Exists(sp)) side = System.IO.File.ReadAllText(sp).Trim().ToLowerInvariant(); } } catch { }
        if (side is not null) gameIsBf1942 = side.Contains("1942");
        else
        {
            var all = string.Join(" ", new[] { levelDir }.Concat(meshArchives).Concat(levelArchives).Where(p => p is not null)).ToLowerInvariant();
            if (all.Contains("vietnam")) gameIsBf1942 = false;
            else if (all.Contains("1942")) gameIsBf1942 = true;
        }
    }
    gl = window.CreateOpenGL();
    SetAppIcon();
    input = window.CreateInput();
    kb = input.Keyboards.Count > 0 ? input.Keyboards[0] : null;
    mouse = input.Mice.Count > 0 ? input.Mice[0] : null;
    if (mouse is not null)
    {
        mouse.MouseMove += (_, pos) =>
        {
            if (!haveMouse) { lastMouse = pos; haveMouse = true; return; }
            var d = pos - lastMouse; lastMouse = pos;
            // Dragging a gameplay handle: follow the terrain under the cursor (XZ from the ray, Y = ground).
            if (gpDragging && gpIndex >= 0 && terrainPick is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fbg = window.FramebufferSize;
                var rg = Picking.ScreenToRay(cam, pos.X, pos.Y, fbg.X, fbg.Y);
                if (terrainPick.Raycast(rg, out var hp))
                    gameplayEdit.SetPos(gpKind, gpIndex, SnapXZ(new Vec3(hp.X, hp.Y, hp.Z)));
                if (gpKind == GpKind.Vehicle) collisionDirty = true;   // live-update the vehicle collision overlay
                return;
            }
            // Free terrain-drag of selected static object(s): slide each by the ground delta under the cursor.
            if (freeDragging && so is not null && terrainPick is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fbf = window.FramebufferSize;
                var rf = Picking.ScreenToRay(cam, pos.X, pos.Y, fbf.X, fbf.Y);
                if (terrainPick.Raycast(rf, out var g))
                {
                    // Move on X/Z only (no free vertical drift); snap each object's Y to the terrain so it
                    // stays stuck to the ground. Lifting is done with the Y gizmo axis, not the free-drag.
                    float dx = g.X - freeDragGround.X, dz = g.Z - freeDragGround.Z;
                    foreach (var (idx, p0, _, _) in dragSnap)
                        if ((uint)idx < (uint)so.Objects.Count)
                        {
                            float nx = Snap1(p0.X + dx), nz = Snap1(p0.Z + dz);   // grid-snap when Snap is on
                            so.Objects[idx].Position = new Vec3(nx, terrainPick.HeightAt(nx, nz), nz);
                        }
                    SyncTransformEdit();
                }
                return;
            }
            // Rotating a gameplay spawn: horizontal drag spins its yaw (live; one command on release).
            if (gpRotDragging && gpIndex >= 0 && mouse!.IsButtonPressed(MouseButton.Left))
            {
                gameplayEdit.SetYaw(gpKind, gpIndex, gpRotStartYaw + (pos.X - gpRotStartMouseX) * 0.5f);
                if (gpKind == GpKind.Vehicle) collisionDirty = true;   // live-update the vehicle collision overlay
                return;
            }
            // Dragging a gizmo axis: slide the object along that world axis (continues even over a panel).
            if (dragAxis >= 0 && selected >= 0 && so is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fb = window.FramebufferSize;
                var ray = Picking.ScreenToRay(cam, pos.X, pos.Y, fb.X, fb.Y);
                var axis = Gizmo.Axis(dragAxis);
                var anchor = new Vector3(gizmoStartPos.X, gizmoStartPos.Y, gizmoStartPos.Z);
                float delta = Gizmo.ClosestAxisParam(ray, anchor, axis) - gizmoStartT;
                var dv = axis * delta;
                foreach (var (idx, p, _, _) in dragSnap)
                    so.Objects[idx].Position = SnapXZ(new Vec3(p.X + dv.X, p.Y + dv.Y, p.Z + dv.Z));
                SyncTransformEdit();
                return;
            }
            // Rotating a ring: accumulate the swept angle and write it to that Euler channel.
            if (rotDragChannel >= 0 && selected >= 0 && so is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fb = window.FramebufferSize;
                var ray = Picking.ScreenToRay(cam, pos.X, pos.Y, fb.X, fb.Y);
                var gp = SelPos();
                if (Gizmo.RayPlaneHit(ray, gp, Gizmo.RingFrame(rotDragChannel).axis, out var rhit, out float _hitT))
                {
                    float a = Gizmo.RingAngle(rhit, gp, rotDragChannel);
                    float dA = a - rotLastAngle;
                    while (dA > MathF.PI) dA -= MathF.PI * 2f;
                    while (dA < -MathF.PI) dA += MathF.PI * 2f;
                    rotAccumDeg += dA * 180f / MathF.PI;
                    rotLastAngle = a;
                    int ch = rotDragChannel;
                    foreach (var (idx, _, rot, _) in dragSnap)
                    {
                        float val = (ch == 0 ? rot.X : ch == 1 ? rot.Y : rot.Z) + rotAccumDeg;
                        so.Objects[idx].Rotation = ch == 0 ? new Vec3(val, rot.Y, rot.Z)
                                                 : ch == 1 ? new Vec3(rot.X, val, rot.Z)
                                                 : new Vec3(rot.X, rot.Y, val);
                    }
                    SyncTransformEdit();
                }
                return;
            }
            // Scaling: uniform scale from the cursor's radial distance to the object on screen.
            if (scaleDragging && selected >= 0 && so is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fb = window.FramebufferSize;
                var sp = Gizmo.Project(SelPos(), cam.ViewProjection, fb.X, fb.Y);
                if (!float.IsNaN(sp.X))
                {
                    float cur = MathF.Max(2f, Vector2.Distance(sp, pos));
                    float factor = cur / scaleStartDist;
                    foreach (var (idx, _, _, sc) in dragSnap)
                        so.Objects[idx].Scale = Math.Clamp(sc * factor, 0.02f, 1000f);
                    SyncTransformEdit();
                }
                return;
            }
            // Active terrain stroke: keep stamping the brush under the cursor as it drags.
            if (stroke is not null && terrainPick is not null
                && (mouse!.IsButtonPressed(MouseButton.Left) || (activeStrokeDir < 0 && mouse!.IsButtonPressed(MouseButton.Right))))
            {
                var fb = window.FramebufferSize;
                var ray = Picking.ScreenToRay(cam, pos.X, pos.Y, fb.X, fb.Y);
                if (terrainPick.Raycast(ray, out var thit))
                {
                    stroke.Dab(thit.X, thit.Z, MakeBrush());
                    terrainDirty = true;
                }
                return;
            }
            // Active material stroke: keep painting the active material as it drags.
            if (matStroke is not null && terrainPick is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fb = window.FramebufferSize;
                var ray = Picking.ScreenToRay(cam, pos.X, pos.Y, fb.X, fb.Y);
                if (terrainPick.Raycast(ray, out var mhit))
                {
                    matStroke.Dab(mhit.X, mhit.Z, MakeMatBrush());
                    UploadActivePaintTexture();
                }
                return;
            }
            // Active road-point drag: the grabbed point follows the ground under the cursor (height re-picked).
            if (roadMode && roadDragIdx >= 0 && roadDragIdx < roadPts.Count && terrainPick is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fbr = window.FramebufferSize;
                var rray = Picking.ScreenToRay(cam, pos.X, pos.Y, fbr.X, fbr.Y);
                if (terrainPick.Raycast(rray, out var rhit)) roadPts[roadDragIdx] = new Vector3(rhit.X, rhit.Y, rhit.Z);
                return;
            }
            // Active AI-path paint: keep stamping passable/blocked as it drags.
            if (aiNavPainting && toolNames[tool] == "AIPath" && terrainPick is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fb = window.FramebufferSize;
                var ray = Picking.ScreenToRay(cam, pos.X, pos.Y, fb.X, fb.Y);
                if (terrainPick.Raycast(ray, out var nhit)) AiNavDab(nhit.X, nhit.Z);
                return;
            }
            // Active TEXTURE stroke: keep painting the surface texture into the atlas as it drags (live preview).
            if (atlasStroke is not null && terrainPick is not null && mouse!.IsButtonPressed(MouseButton.Left))
            {
                var fb = window.FramebufferSize;
                var ray = Picking.ScreenToRay(cam, pos.X, pos.Y, fb.X, fb.Y);
                if (terrainPick.Raycast(ray, out var thit) && SurfPaintTex() is Texture2D tx)
                {
                    atlasStroke.Dab(tx, thit.X, thit.Z, brushRadius, matHardness, texIntensity, squareBrush, SurfPaintTile(), surfUseAlpha);
                    if (atlasStroke.LastW > 0) UploadAtlasRect(atlasStroke.LastX, atlasStroke.LastY, atlasStroke.LastW, atlasStroke.LastH);
                }
                return;
            }
            if (UiWantsMouse()) return;                 // dragging over a panel shouldn't orbit the camera
            // Right-drag orbits the camera. Under MirrorX the screen's left/right is flipped, so the yaw
            // delta must flip too (pitch/vertical is unaffected) to keep mouse-look intuitive.
            if (mouse!.IsButtonPressed(MouseButton.Right)) cam.Look((cam.MirrorX ? d.X : -d.X) * 0.003f, -d.Y * 0.003f);
        };
        mouse.MouseUp += (_, btn) =>
        {
            // L/R sculpt: a RIGHT-button lower stroke commits on right-up (mirrors the left terrain-stroke commit below).
            if (btn == MouseButton.Right && stroke is not null && activeStrokeDir < 0)
            {
                var redit = stroke.Finish(); stroke = null; activeStrokeDir = 0;
                if (redit is not null && heightmap is not null)
                {
                    if (hist is not null) hist.Do(new TerrainStrokeCommand(redit, heightmap, RebuildTerrain));
                    else RebuildTerrain();
                }
                terrainDirty = false;
                return;
            }
            if (btn != MouseButton.Left) return;
            // Release a road-point drag (the points are pre-stamp scratch state; Stamp is the undoable act).
            if (roadDragIdx >= 0) { roadDragIdx = -1; return; }
            // Finish a gameplay-handle drag: reset to the start, then push one move command (captures from->to).
            if (gpDragging)
            {
                gpDragging = false;
                if (gpIndex >= 0 && hist is not null)
                {
                    var cur = gameplayEdit.GetPos(gpKind, gpIndex);
                    var to = new Vec3(cur.X, cur.Y, cur.Z);
                    gameplayEdit.SetPos(gpKind, gpIndex, new Vec3(gpDragStart.X, gpDragStart.Y, gpDragStart.Z));
                    if (MathF.Abs(to.X - gpDragStart.X) > 1e-3f || MathF.Abs(to.Z - gpDragStart.Z) > 1e-3f || MathF.Abs(to.Y - gpDragStart.Y) > 1e-3f)
                        hist.Do(new GameplayMoveCommand(gameplayEdit, gpKind, gpIndex, to, null));
                }
                return;
            }
            // Finish a gameplay-spawn yaw drag: reset to the start, then push one rotate command.
            if (gpRotDragging)
            {
                gpRotDragging = false;
                if (gpIndex >= 0 && hist is not null)
                {
                    var finalRot = gameplayEdit.GetRotation(gpKind, gpIndex);
                    gameplayEdit.SetYaw(gpKind, gpIndex, gpRotStartYaw);   // reset so the command captures the original
                    if (MathF.Abs(finalRot.X - gpRotStartYaw) > 1e-3f)
                        hist.Do(new GameplayRotateCommand(gameplayEdit, gpKind, gpIndex, finalRot, null));
                }
                return;
            }
            // Finish a terrain stroke: coalesce into one edit and push it onto the shared undo stack.
            if (stroke is not null)
            {
                var edit = stroke.Finish(); stroke = null; activeStrokeDir = 0;
                if (edit is not null && heightmap is not null)
                {
                    if (hist is not null) hist.Do(new TerrainStrokeCommand(edit, heightmap, RebuildTerrain));
                    else RebuildTerrain();
                }
                terrainDirty = false;
                return;
            }
            // Finish a paint stroke: coalesce into one edit and push it onto the shared undo stack.
            if (matStroke is not null)
            {
                var medit = matStroke.Finish(); matStroke = null;
                var tgt = ActivePaintMap();
                if (medit is not null && tgt is not null)
                {
                    if (hist is not null) hist.Do(new MaterialStrokeCommand(medit, tgt, UploadActivePaintTexture));
                    else UploadActivePaintTexture();
                }
                return;
            }
            // Finish an AI-path paint drag: coalesce the whole drag into one undoable stroke (edits are already
            // live in aiNav; the command just records before/after so Z/Y can revert it).
            if (aiNavPainting)
            {
                aiNavPainting = false;
                var navCmd = aiNavStroke?.Finish(AiNavStrokeChanged);
                aiNavStroke = null;
                if (navCmd is not null && hist is not null) hist.Do(navCmd);
                return;
            }
            // Finish a TEXTURE stroke: coalesce the painted atlas rect into one undo step + refresh mipmaps.
            if (atlasStroke is not null)
            {
                var aedit = atlasStroke.Finish(UploadAtlasRectMips); atlasStroke = null;
                if (aedit is not null)
                {
                    atlasPainted = true;
                    if (hist is not null) hist.Do(aedit);
                    else aedit.Apply(so!);
                }
                return;
            }
            if (freeDragging)
            {
                freeDragging = false;
                if (so is null || hist is null) { dragSnap.Clear(); return; }
                var cmds = new List<IEditCommand>();
                foreach (var (idx, pos, _, _) in dragSnap)
                {
                    if (idx < 0 || idx >= so.Objects.Count) continue;
                    var final = so.Objects[idx].Position;
                    if (MathF.Abs(final.X - pos.X) < 1e-4f && MathF.Abs(final.Y - pos.Y) < 1e-4f && MathF.Abs(final.Z - pos.Z) < 1e-4f) continue;
                    so.Objects[idx].Position = pos;                 // revert so the command captures the pre-drag origin
                    cmds.Add(new MoveObject(so.Objects[idx].Id, final));
                }
                if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
                dragSnap.Clear(); SyncTransformEdit();
            }
            else if (dragAxis >= 0)
            {
                dragAxis = -1;
                if (so is null || hist is null) { dragSnap.Clear(); return; }
                var cmds = new List<IEditCommand>();
                foreach (var (idx, pos, _, _) in dragSnap)
                {
                    if (idx < 0 || idx >= so.Objects.Count) continue;
                    var final = so.Objects[idx].Position;
                    so.Objects[idx].Position = pos;                 // revert so the command captures the pre-drag origin
                    cmds.Add(new MoveObject(so.Objects[idx].Id, final));
                }
                if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
                dragSnap.Clear(); SyncTransformEdit();
            }
            else if (rotDragChannel >= 0)
            {
                rotDragChannel = -1;
                if (so is null || hist is null) { dragSnap.Clear(); return; }
                var cmds = new List<IEditCommand>();
                foreach (var (idx, _, rot, _) in dragSnap)
                {
                    if (idx < 0 || idx >= so.Objects.Count) continue;
                    var final = so.Objects[idx].Rotation;
                    so.Objects[idx].Rotation = rot;
                    cmds.Add(new RotateObject(so.Objects[idx].Id, final));
                }
                if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
                dragSnap.Clear(); SyncTransformEdit();
            }
            else if (scaleDragging)
            {
                scaleDragging = false;
                if (so is null || hist is null) { dragSnap.Clear(); return; }
                var cmds = new List<IEditCommand>();
                foreach (var (idx, _, _, sc) in dragSnap)
                {
                    if (idx < 0 || idx >= so.Objects.Count) continue;
                    var final = so.Objects[idx].Scale ?? 1f;
                    so.Objects[idx].Scale = sc;
                    cmds.Add(new ScaleObject(so.Objects[idx].Id, final));
                }
                if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
                dragSnap.Clear(); SyncTransformEdit();
            }
        };
        mouse.MouseDown += (_, btn) =>
        {
            if (UiWantsMouse()) return;                 // clicking a panel shouldn't select in the viewport
            var fb = window.FramebufferSize;
            var ray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fb.X, fb.Y);
            // L/R sculpt option: the RIGHT button begins a LOWER stroke in Sculpt mode (otherwise right = camera orbit).
            if (lrSculpt && btn == MouseButton.Right && toolNames[tool] == "Sculpt"
                && terrainEd is not null && terrainPick is not null && terrainPick.Raycast(ray, out var rLowHit))
            {
                activeStrokeDir = -1;
                stroke = terrainEd.BeginStroke();
                stroke.Dab(rLowHit.X, rLowHit.Z, MakeBrush());
                terrainDirty = true;
                return;
            }
            if (btn != MouseButton.Left) return;

            // Measure tool: each left-click drops a terrain point (Esc clears / exits).
            if (measureMode && terrainPick is not null && terrainPick.Raycast(ray, out var mpt))
            { measurePts.Add(new Vector3(mpt.X, mpt.Y, mpt.Z)); return; }

            // Road tool: clicking an existing point selects it and starts a drag (move it across the terrain);
            // clicking open ground appends a new centerline point. Stamp (Inspector) sweeps the road spline.
            if (roadMode && terrainPick is not null)
            {
                for (int i = 0; i < roadPts.Count; i++)   // screen-space handle hit test (matches DrawRoad's handles)
                {
                    var sp = Gizmo.Project(roadPts[i], cam.ViewProjection, fb.X, fb.Y);
                    if (float.IsNaN(sp.X)) continue;
                    float ddx = sp.X - lastMouse.X, ddy = sp.Y - lastMouse.Y;
                    if (ddx * ddx + ddy * ddy <= 100f) { roadSelIdx = i; roadDragIdx = i; return; }
                }
                if (terrainPick.Raycast(ray, out var rpt))
                {
                    roadPts.Add(new Vector3(rpt.X, rpt.Y, rpt.Z));
                    roadPtW.Add(0f);                       // 0 = use the global road width
                    roadSelIdx = roadPts.Count - 1;
                    return;
                }
                return;
            }

            // Place tool (gameplay armed): drop a new control point / vehicle / soldier spawn on the terrain.
            if (toolNames[tool] == "Place" && gpPlaceKind is GpKind pk && terrainPick is not null
                && hist is not null && terrainPick.Raycast(ray, out var ghit))
            {
                var gpos = SnapXZ(new Vec3(ghit.X, ghit.Y, ghit.Z));
                object item = pk switch
                {
                    GpKind.ControlPoint => EditableGameplay.NewControlPoint(gpos),
                    GpKind.Vehicle => EditableGameplay.NewVehicleSpawn(gpos),
                    _ => EditableGameplay.NewSoldierSpawn(gpos),
                };
                var addCmd = new GameplayAddCommand(gameplayEdit, pk, item, null);
                hist.Do(addCmd);
                gpKind = pk; gpIndex = addCmd.Index; selected = -1; multi.Clear();
                Console.WriteLine($"Placed {pk} at {ghit.X:0.#}, {ghit.Z:0.#}");
                return;
            }

            // Place tool + a prefab selected: stamp the whole group under the cursor.
            if (toolNames[tool] == "Place" && browserTemplate is not null && IsPrefab(browserTemplate)
                && terrainPick is not null && hist is not null && terrainPick.Raycast(ray, out var pfHit))
            {
                StampPrefab(browserTemplate, new Vec3(pfHit.X, pfHit.Y, pfHit.Z));
                return;
            }

            // Place tool: drop the Object-Library selection onto the terrain under the cursor.
            if (toolNames[tool] == "Place" && browserTemplate is not null && terrainPick is not null
                && so is not null && hist is not null && terrainPick.Raycast(ray, out var hitPos))
            {
                var id = Guid.NewGuid().ToString("N");
                var ppos = SnapXZ(new Vec3(hitPos.X, hitPos.Y, hitPos.Z));
                hist.Do(new AddObject(id, browserTemplate, ppos, Vec3.Zero));
                SyncMarkers(); RebuildObjects(); UploadMarkers();
                selected = so.Objects.FindIndex(o => o.Id == id);
                multi.Clear(); if (selected >= 0) multi.Add(selected);
                Console.WriteLine($"Placed {browserTemplate} at {ppos.X:0.#}, {ppos.Y:0.##}, {ppos.Z:0.#}");
                return;
            }

            // Terrain sculpt tools: begin a stroke and lay the first dab under the cursor.
            if ((toolNames[tool] == "Sculpt" || toolNames[tool] == "Smooth")
                && terrainEd is not null && terrainPick is not null && terrainPick.Raycast(ray, out var thit))
            {
                stroke = terrainEd.BeginStroke();
                activeStrokeDir = (lrSculpt && toolNames[tool] == "Sculpt") ? 1 : 0;   // left = raise when the L/R option is on
                stroke.Dab(thit.X, thit.Z, MakeBrush());
                terrainDirty = true;
                return;
            }

            // Paint eyedropper (Alt-click) + surface capture (capture mode) run before painting begins.
            if (toolNames[tool] == "Paint" && terrainPick is not null && terrainPick.Raycast(ray, out var phit))
            {
                bool alt = kb is not null && (kb.IsKeyPressed(Key.AltLeft) || kb.IsKeyPressed(Key.AltRight));
                if (alt) { EyedropAt(phit.X, phit.Z); return; }
                if (paintLayer == 3 && captureMode && atlasCpu is not null) { CaptureSurfaceAt(phit.X, phit.Z); return; }
            }

            // Paint tool, Texture layer: begin a texture stroke into the live atlas under the cursor.
            // Source = the active library texture (if one is chosen) else the selected 16-slot palette surface.
            if (toolNames[tool] == "Paint" && paintLayer == 3 && atlasCpu is not null
                && terrainPick is not null && terrainPick.Raycast(ray, out var thit2) && SurfPaintTex() is Texture2D tx0)
            {
                atlasStroke = new AtlasPaintStroke(atlasCpu, cfg.WorldSize);
                atlasStroke.Dab(tx0, thit2.X, thit2.Z, brushRadius, matHardness, texIntensity, squareBrush, SurfPaintTile(), surfUseAlpha);
                if (atlasStroke.LastW > 0) UploadAtlasRect(atlasStroke.LastX, atlasStroke.LastY, atlasStroke.LastW, atlasStroke.LastH);
                return;
            }

            // Paint tool: begin a stroke on the active paint layer and stamp under the cursor.
            if (toolNames[tool] == "Paint"
                && ActivePainter() is not null && terrainPick is not null && terrainPick.Raycast(ray, out var mhit))
            {
                matStroke = ActivePainter()!.BeginStroke();
                matStroke.Dab(mhit.X, mhit.Z, MakeMatBrush());
                UploadActivePaintTexture();
                return;
            }

            // AI Path: stamp passable/blocked into the editable navmap under the cursor.
            if (toolNames[tool] == "AIPath" && terrainPick is not null && terrainPick.Raycast(ray, out var nhit))
            {
                EnsureAiNav();
                aiNavPainting = true;
                aiNavStroke = aiNav is not null ? new AiNavStroke(aiNav, aiNavSide, aiNavVehLoaded) : null;  // begin one undoable stroke
                AiNavDab(nhit.X, nhit.Z);
                return;
            }

            // Move tool: grab an axis handle of the selected object's gizmo to start a constrained drag,
            // OR grab the object body to free-drag it across the terrain (mouse follows the ground).
            if (toolNames[tool] == "Move" && selected >= 0 && so is not null)
            {
                var gp = SelPos(); float len = GizmoLen(gp);
                int ax = Gizmo.PickAxis(ray, gp, len, len * 0.18f);
                if (ax >= 0)
                {
                    dragAxis = ax;
                    gizmoStartPos = so.Objects[selected].Position;
                    gizmoStartT = Gizmo.ClosestAxisParam(ray, gp, Gizmo.Axis(ax));
                    CaptureDragSnapshot();
                    return;
                }
                // No axis grabbed: if the click lands on the selected object's mesh, start a free ground-drag.
                int rayHit = glObjects?.Raycast(ray.Origin, ray.Dir) ?? -1;
                bool onSelected = rayHit == selected || (rayHit >= 0 && multi.Contains(rayHit));
                if (onSelected && terrainPick is not null && terrainPick.Raycast(ray, out var fg))
                {
                    freeDragging = true;
                    freeDragGround = new Vector3(fg.X, fg.Y, fg.Z);   // ground point under the cursor at grab time
                    CaptureDragSnapshot();
                    return;
                }
            }

            // Rotate tool: grab a ring to rotate about that axis.
            if (toolNames[tool] == "Rotate" && selected >= 0 && so is not null)
            {
                var gp = SelPos(); float len = GizmoLen(gp);
                int rc = Gizmo.PickRing(ray, gp, len, len * 0.14f);
                if (rc >= 0 && Gizmo.RayPlaneHit(ray, gp, Gizmo.RingFrame(rc).axis, out var rhit, out float _hitT))
                {
                    rotDragChannel = rc;
                    rotStartEuler = so.Objects[selected].Rotation;
                    rotLastAngle = Gizmo.RingAngle(rhit, gp, rc);
                    rotAccumDeg = 0f;
                    CaptureDragSnapshot();
                    return;
                }
            }

            // Scale tool: grab the handle near the object on screen for uniform scaling.
            if (toolNames[tool] == "Scale" && selected >= 0 && so is not null)
            {
                var sp = Gizmo.Project(SelPos(), cam.ViewProjection, fb.X, fb.Y);
                if (!float.IsNaN(sp.X) && Vector2.Distance(sp, lastMouse) <= 22f)
                {
                    scaleDragging = true;
                    scaleStartScale = so.Objects[selected].Scale ?? 1f;
                    scaleStartDist = MathF.Max(8f, Vector2.Distance(sp, lastMouse));
                    CaptureDragSnapshot();
                    return;
                }
            }

            // Rotate tool: select the gameplay spawn under the cursor and begin a yaw drag (CPs are radial).
            if (toolNames[tool] == "Rotate" && gameplayEdit.Count > 0
                && TryPickGameplay(lastMouse, out var rk, out var ri))
            {
                gpKind = rk; gpIndex = ri; selected = -1; multi.Clear();
                if (rk != GpKind.ControlPoint) { gpRotDragging = true; gpRotStartYaw = gameplayEdit.GetYaw(rk, ri); gpRotStartMouseX = lastMouse.X; }
                return;
            }

            // Gameplay handle: after gizmo handling, grab the handle under the cursor. In Move this
            // selects it and begins a ground drag; in Select it just selects. Misses fall through.
            if ((toolNames[tool] == "Move" || toolNames[tool] == "Select") && gameplayEdit.Count > 0
                && TryPickGameplay(lastMouse, out var gk, out var gi))
            {
                gpKind = gk; gpIndex = gi; selected = -1; multi.Clear();
                // Double-click a gameplay handle to open its Battlecraft-style edit dialog (skip the drag on that click).
                bool gpDbl = (appClock - gpLastClickTime) < 0.35 && gpLastClickKind == gk && gpLastClickIndex == gi;
                gpLastClickTime = appClock; gpLastClickKind = gk; gpLastClickIndex = gi;
                if (gpDbl) { OpenGpEditor(gk, gi); return; }
                if (toolNames[tool] == "Move" && terrainPick is not null)
                {
                    var gp0 = gameplayEdit.GetPos(gk, gi);
                    gpDragging = true; gpDragStart = new Vector3(gp0.X, gp0.Y, gp0.Z);
                }
                return;
            }

            // Otherwise: select. Shift toggles membership; a plain click replaces the selection.
            gpIndex = -1;                                   // leaving gameplay selection
            if (markers.Length == 0) return;
            // Pick on the object's actual geometry (ray vs each mesh's transformed bounding box) - clicks
            // land on what you see. Fall back to a screen-space marker pick, then a world-space ray sphere.
            int hit = glObjects?.Raycast(ray.Origin, ray.Dir) ?? -1;
            if (hit < 0) hit = Picking.PickNearestScreen(cam, lastMouse, fb.X, fb.Y, markers, 18f);
            if (hit < 0) hit = Picking.PickNearest(ray, markers, cfg.HorizontalSpacing * 3f);
            bool shift = kb is not null && (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight));
            if (hit < 0)
            {
                if (!shift) { multi.Clear(); selected = -1; }
            }
            else if (shift)
            {
                if (!multi.Add(hit)) multi.Remove(hit);                 // toggle in/out of the set
                selected = multi.Contains(hit) ? hit : (multi.Count > 0 ? multi.First() : -1);
            }
            else { multi.Clear(); multi.Add(hit); selected = hit; }
        };
        mouse.Scroll += (_, s) =>
        {
            if (UiWantsMouse()) return;
            if (toolNames[tool] is "Sculpt" or "Smooth" or "Paint")
                brushRadius = Math.Clamp(brushRadius * (1f + s.Y * 0.1f), 2f, 600f);   // wheel resizes the brush
            else
                cam.Dolly(s.Y * Altitude() * 0.2f * camSpeedMult);
        };
    }
    if (kb is not null) kb.KeyDown += OnKeyDown;

    // Dear ImGui editor UI - renders into this same GL context/window each frame.
    // When a non-English UI language is active, build the font atlas from a CJK-capable Windows font with the
    // Japanese glyph ranges - ImGui's built-in font is ASCII-only, so Japanese would otherwise draw as blank
    // boxes. The font atlas is baked once here, which is why switching language restarts the editor.
    var uiFont = Loc.FindUiFont();
    if (uiFont is not null)
    {
        try
        {
            imgui = new ImGuiController(gl, window, input,
                new ImGuiFontConfig(uiFont, 16, io => io.Fonts.GetGlyphRangesJapanese()));
            Console.WriteLine($"UI font: {Path.GetFileName(uiFont)} (Japanese glyph ranges) for language '{Loc.Current}'.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"UI font '{Path.GetFileName(uiFont)}' failed ({ex.Message}); falling back to the built-in font.");
            imgui = new ImGuiController(gl, window, input);
        }
    }
    else imgui = new ImGuiController(gl, window, input);
    ImGui.GetIO().ConfigWindowsMoveFromTitleBarOnly = true;   // body-drags (model-viewer orbit, minimap click) don't move the window; the title bar still does
    try { ClipboardBridge.Install(); } catch { }   // Ctrl+C/V in text boxes -> OS clipboard (e.g. paste a collab IP)
    ApplyTheme();
    LoadPrefabs();
    // Seed the editable fog state from the level's Init.con (renderer.vertexFogEnable / fogColorVec / fog start-end).
    if (env is not null)
    {
        fogEnabled = env.FogEnabled;
        fogColor = new Vector3(env.FogColor.X, env.FogColor.Y, env.FogColor.Z);
        fogStart = env.FogStart; fogEnd = env.FogEnd;
        waterColor = new Vector3(env.WaterColor.X, env.WaterColor.Y, env.WaterColor.Z);   // the level's water.color
        deepColor = new Vector3(env.DeepColor.X, env.DeepColor.Y, env.DeepColor.Z);       // the level's water.deepcolor
        waterAlpha = env.WaterAlpha;
    }
    waterLevelLoaded = cfg.WaterLevel;   // remember for the Water Level "Reset" button
    // Build the library catalog: objects + a draggable Gameplay category + a Prefabs category (if any).
    RebuildCatalog();

    // One-time diagnostic: list placed objects that can't resolve a mesh (they render as amber diamonds), and
    // WHY - distinguishes a genuinely missing asset (load the right .rfa) from a .sm we can't parse.
    if (meshLib is not null && so is not null)
    {
        var unresolved = so.Objects
            .Where(o => !meshLib.TryGet(o.Template, out _) && !meshLib.TryGetAssembledMesh(o.Template, out _))
            .GroupBy(o => o.Template, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Template: g.Key, Count: g.Count(), HasSm: meshLib.HasMeshEntry(g.Key)))
            .OrderByDescending(x => x.Count).ToList();
        if (unresolved.Count > 0)
        {
            Console.WriteLine($"{unresolved.Sum(u => u.Count)} placed object(s) have no resolvable mesh (amber diamonds) - {unresolved.Count} distinct template(s):");
            foreach (var u in unresolved.Take(40))
                Console.WriteLine($"   {u.Template} x{u.Count}  ->  {(u.HasSm ? "a matching .sm is present but FAILED TO PARSE" : "no matching .sm in the loaded archives")}");
        }
    }

    // Broadcast every locally-committed edit to the collaboration session (no-op when offline). Hooked here
    // (not in straight-line code) so the captured editor state is all definitely assigned.
    if (hist is not null) { hist.OnDo = OnLocalEdit; hist.OnUndoRedo = OnUndoRedo; }   // undo/redo broadcasts too

    gl.ClearColor(0.55f, 0.68f, 0.85f, 1f);
    gl.Enable(EnableCap.DepthTest);
    gl.Enable(EnableCap.ProgramPointSize);

    terrainProg = BuildProgram(TerrainVert, TerrainFrag);
    markerProg = BuildProgram(MarkerVert, MarkerFrag);
    collisionProg = BuildProgram(CollisionVert, CollisionFrag);
    uCMvp = gl.GetUniformLocation(collisionProg, "uMVP");
    uCCam = gl.GetUniformLocation(collisionProg, "uCamPos");
    uCColor = gl.GetUniformLocation(collisionProg, "uColor");
    uCFogStart = gl.GetUniformLocation(collisionProg, "uFogStart");
    uCFogEnd = gl.GetUniformLocation(collisionProg, "uFogEnd");
    uMvp = gl.GetUniformLocation(terrainProg, "uMVP");
    uLight = gl.GetUniformLocation(terrainProg, "uLightDir");
    uWater = gl.GetUniformLocation(terrainProg, "uWater");
    uMaxH = gl.GetUniformLocation(terrainProg, "uMaxH");
    uDeepColor = gl.GetUniformLocation(terrainProg, "uDeepColor");
    uHasTexT = gl.GetUniformLocation(terrainProg, "uHasTex");
    uShowMat = gl.GetUniformLocation(terrainProg, "uShowMat");
    uHasDetail = gl.GetUniformLocation(terrainProg, "uHasDetail");
    uDetailScale = gl.GetUniformLocation(terrainProg, "uDetailScale");
    uUseShadowMapT = gl.GetUniformLocation(terrainProg, "uUseShadowMap");
    uLightSpaceT = gl.GetUniformLocation(terrainProg, "uLightSpace");
    gl.UseProgram(terrainProg);
    gl.Uniform1(gl.GetUniformLocation(terrainProg, "uTer"), 0);   // sampler -> texture unit 0
    gl.Uniform1(gl.GetUniformLocation(terrainProg, "uMat"), 1);   // material sampler -> texture unit 1
    uTerUvScaleL = gl.GetUniformLocation(terrainProg, "uTerUvScale");
    uTerUvOffsetL = gl.GetUniformLocation(terrainProg, "uTerUvOffset");
    ApplyTerrainUv();
    gl.Uniform1(gl.GetUniformLocation(terrainProg, "uDetail"), 2); // detail sampler -> texture unit 2
    gl.Uniform1(gl.GetUniformLocation(terrainProg, "uShadowMap"), 3); // shadow-map sampler -> texture unit 3
    gl.Uniform1(uShowMat, 0);
    // Sun shadow-map depth program (depth render of terrain + objects from the sun).
    depthProg = BuildProgram(DepthVert, DepthFrag);
    uLightSpaceD = gl.GetUniformLocation(depthProg, "uLightSpace");
    uModelD = gl.GetUniformLocation(depthProg, "uModel");
    uMvpM = gl.GetUniformLocation(markerProg, "uMVP");
    uColor = gl.GetUniformLocation(markerProg, "uColor");
    uSize = gl.GetUniformLocation(markerProg, "uSize");

    // Flatten the level's terrain tiles into one GPU atlas (UV = world XZ / worldSize, matching the
    // software terrain texture). Baking ~1 s; falls back to the height ramp when no tiles are present.
    if (terrainTex is not null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Bake at a resolution that preserves the source tiles (high-res terrain textures keep their detail
        // instead of being squashed to 2048), clamped to the GPU's max texture size and a sane 8K ceiling.
        Span<int> mt = stackalloc int[1]; gl.GetInteger(GLEnum.MaxTextureSize, mt);
        int atlasCap = Math.Min(8192, mt[0] > 0 ? mt[0] : 8192);
        int atlasSize = Math.Clamp(terrainTex.NativeSize, 2048, atlasCap);
        atlasCpu = terrainTex.BakeAtlas(atlasSize);   // keep the CPU copy so the Texture paint tool can edit + re-upload it
        terrainTexId = UploadTexture(atlasCpu);
        Console.WriteLine($"Baked terrain atlas ({atlasSize}^2, native {terrainTex.NativeSize}) in {sw.ElapsedMilliseconds} ms.");
    }
    BuildMinimap();   // top-down map for the in-editor Mini-Map panel
    gl.UseProgram(terrainProg);
    gl.Uniform1(uHasTexT, terrainTexId != 0 ? 1 : 0);
    // Tiling detail texture (BF detailTexName): REPEAT-wrapped, mip-mapped, multiplied over the base
    // atlas in the shader so the ground reads crisp up close. Scale = world span per detail repeat.
    if (terrainTex?.Detail is not null)
    {
        detailTexId = UploadDetailTexture(terrainTex.Detail);
        gl.Uniform1(uDetailScale, terrainTex.DetailScale);
    }
    gl.Uniform1(uHasDetail, detailTexId != 0 ? 1 : 0);
    UploadActivePaintTexture();   // R8 index texture (for the Paint tool's overlay)

    float ws = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
    var verts = new float[mesh.Positions.Length * 8];
    for (int i = 0; i < mesh.Positions.Length; i++)
    {
        var p = mesh.Positions[i]; var n = mesh.Normals[i]; int o = i * 8;
        verts[o] = p.X; verts[o + 1] = p.Y; verts[o + 2] = p.Z;
        verts[o + 3] = n.X; verts[o + 4] = n.Y; verts[o + 5] = n.Z;
        verts[o + 6] = p.X / ws; verts[o + 7] = p.Z / ws;   // UV matches TerrainTexture.Uv()
    }
    var indices = Array.ConvertAll(mesh.Indices, x => (uint)x);
    terrainIndexCount = indices.Length;
    terrainVao = MakeMesh(verts, indices, out terrainVbo);

    markerVao = gl.GenVertexArray();
    gl.BindVertexArray(markerVao);
    markerVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    previewVao = gl.GenVertexArray();
    gl.BindVertexArray(previewVao);
    previewVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, previewVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    gizmoVao = gl.GenVertexArray();
    gl.BindVertexArray(gizmoVao);
    gizmoVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, gizmoVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    collisionVao = gl.GenVertexArray();
    gl.BindVertexArray(collisionVao);
    collisionVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, collisionVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    weatherVao = gl.GenVertexArray();
    gl.BindVertexArray(weatherVao);
    weatherVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, weatherVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    ringVao = gl.GenVertexArray();
    gl.BindVertexArray(ringVao);
    ringVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, ringVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    // Unit circle in the XZ plane for the terrain-brush radius preview (scaled + translated at draw time).
    {
        const int BN = 64;
        var bring = new float[BN * 3];
        for (int i = 0; i < BN; i++) { float a = i / (float)BN * MathF.PI * 2f; bring[i * 3] = MathF.Cos(a); bring[i * 3 + 1] = 0f; bring[i * 3 + 2] = MathF.Sin(a); }
        brushRingVao = gl.GenVertexArray();
        gl.BindVertexArray(brushRingVao);
        brushRingVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, brushRingVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, bring, BufferUsageARB.StaticDraw);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
        gl.EnableVertexAttribArray(0);
    }

    // Unit square (half-extent 1) in the XZ plane for the square-brush radius preview (LineLoop, scaled at draw).
    {
        var sq = new float[] { -1f, 0f, -1f,  1f, 0f, -1f,  1f, 0f, 1f,  -1f, 0f, 1f };
        brushSquareVao = gl.GenVertexArray();
        gl.BindVertexArray(brushSquareVao);
        brushSquareVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, brushSquareVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, sq, BufferUsageARB.StaticDraw);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
        gl.EnableVertexAttribArray(0);
    }

    // Draped brush-cursor outline (dynamic; refilled per-frame to follow the terrain like the grid).
    {
        brushDrapeVao = gl.GenVertexArray();
        gl.BindVertexArray(brushDrapeVao);
        brushDrapeVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, brushDrapeVbo);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
        gl.EnableVertexAttribArray(0);
    }

    // Draped world-grid overlay (dynamic; filled by BuildGrid() from the terrain heights).
    {
        gridVao = gl.GenVertexArray();
        gl.BindVertexArray(gridVao);
        gridVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gridVbo);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
        gl.EnableVertexAttribArray(0);
    }

    // Indicator diamond (octahedron) for mesh-less objects - drawn lit via objProg so it reads as a real
    // 3D marker. Unit shape; scaled to a few metres at draw time. 6 verts, 8 triangular faces.
    {
        var diaV = new (float x, float y, float z)[]
        { (0,1,0), (1,0,0), (0,0,1), (-1,0,0), (0,0,-1), (0,-1,0) };
        int[] diaFaces = { 0,2,1, 0,3,2, 0,4,3, 0,1,4, 5,1,2, 5,2,3, 5,3,4, 5,4,1 };
        var diaVerts = new float[diaV.Length * 8];
        for (int i = 0; i < diaV.Length; i++)
        {
            var n = Vector3.Normalize(new Vector3(diaV[i].x, diaV[i].y, diaV[i].z));
            int o = i * 8;
            diaVerts[o] = diaV[i].x; diaVerts[o + 1] = diaV[i].y; diaVerts[o + 2] = diaV[i].z;
            diaVerts[o + 3] = n.X; diaVerts[o + 4] = n.Y; diaVerts[o + 5] = n.Z;
            diaVerts[o + 6] = 0f; diaVerts[o + 7] = 0f;
        }
        indicatorVao = MakeMesh(diaVerts, diaFaces.Select(i => (uint)i).ToArray(), out _);
        indicatorCount = diaFaces.Length;
    }

    // Dynamic point buffer for gameplay markers (re-uploaded per layer at draw time).
    gpVao = gl.GenVertexArray();
    gl.BindVertexArray(gpVao);
    gpVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpVbo);
    unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
    gl.EnableVertexAttribArray(0);

    // Water surface: a quad at the water level spanning the world (two triangles, position only).
    waterProg = BuildProgram(WaterVert, WaterFrag);
    uMvpW = gl.GetUniformLocation(waterProg, "uMVP");
    uLightW = gl.GetUniformLocation(waterProg, "uLightDir");
    uCamW = gl.GetUniformLocation(waterProg, "uCamPos");
    uTimeW = gl.GetUniformLocation(waterProg, "uTime");
    uWaterYW = gl.GetUniformLocation(waterProg, "uWaterY");
    uWaterColorW = gl.GetUniformLocation(waterProg, "uWaterColor");
    uWaterAlphaW = gl.GetUniformLocation(waterProg, "uWaterAlpha");
    uHasWaterTexW = gl.GetUniformLocation(waterProg, "uHasWaterTex");
    uTexL1W = gl.GetUniformLocation(waterProg, "uTexL1");
    uTexL2W = gl.GetUniformLocation(waterProg, "uTexL2");
    uNormalW = gl.GetUniformLocation(waterProg, "uNormal");
    uScroll1W = gl.GetUniformLocation(waterProg, "uScroll1");
    uScroll2W = gl.GetUniformLocation(waterProg, "uScroll2");
    uScrollNW = gl.GetUniformLocation(waterProg, "uScrollN");
    uTile1W = gl.GetUniformLocation(waterProg, "uTile1");
    uTile2W = gl.GetUniformLocation(waterProg, "uTile2");
    uTileNW = gl.GetUniformLocation(waterProg, "uTileN");
    uSpecColW = gl.GetUniformLocation(waterProg, "uSpecColor");
    InitWaterTextures();   // resolve + upload the level's water.texLayer1/2 + normalMap (sets haveWaterTex)
    {
        float wl = cfg.WaterLevel, wsz = cfg.WorldSize;
        float[] wq = { 0,wl,0,  wsz,wl,0,  wsz,wl,wsz,   0,wl,0,  wsz,wl,wsz,  0,wl,wsz };
        waterVao = gl.GenVertexArray();
        gl.BindVertexArray(waterVao);
        waterVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, waterVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, wq, BufferUsageARB.StaticDraw);
        unsafe { gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * (uint)sizeof(float), (void*)0); }
        gl.EnableVertexAttribArray(0);
    }

    // Fullscreen gradient sky (NDC quad, 2D position only).
    skyProg = BuildProgram(SkyVert, SkyFrag);
    {
        float[] sq = { -1,-1, 1,-1, 1,1,  -1,-1, 1,1, -1,1 };
        skyVao = gl.GenVertexArray();
        gl.BindVertexArray(skyVao);
        skyVbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, skyVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, sq, BufferUsageARB.StaticDraw);
        unsafe { gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * (uint)sizeof(float), (void*)0); }
        gl.EnableVertexAttribArray(0);
    }
    uInvVPS = gl.GetUniformLocation(skyProg, "uInvVP");
    uCamPosS = gl.GetUniformLocation(skyProg, "uCamPos");
    uSunDirS = gl.GetUniformLocation(skyProg, "uSunDir");
    uFogColorS = gl.GetUniformLocation(skyProg, "uFogColor");
    uRotS = gl.GetUniformLocation(skyProg, "uRot");
    uHasCubeS = gl.GetUniformLocation(skyProg, "uHasCube");
    uCubeS = gl.GetUniformLocation(skyProg, "uCube");
    uHasCloudS = gl.GetUniformLocation(skyProg, "uHasCloud");
    uCloudTexS = gl.GetUniformLocation(skyProg, "uCloudTex");
    uCloudColorS = gl.GetUniformLocation(skyProg, "uCloudColor");
    uCloudScrollS = gl.GetUniformLocation(skyProg, "uCloudScroll");
    uCloudScaleS = gl.GetUniformLocation(skyProg, "uCloudScale");
    uCloudOpacityS = gl.GetUniformLocation(skyProg, "uCloudOpacity");
    // Real skybox/cloud MESH shader (unlit, per-part texture).
    skyMeshProg = BuildProgram(SkyMeshVert, SkyMeshFrag);
    uSMmvp = gl.GetUniformLocation(skyMeshProg, "uMVP");
    uSMscroll = gl.GetUniformLocation(skyMeshProg, "uScroll");
    uSMpin = gl.GetUniformLocation(skyMeshProg, "uPin");
    uSMtex = gl.GetUniformLocation(skyMeshProg, "uTex");
    uSMhasTex = gl.GetUniformLocation(skyMeshProg, "uHasTex");
    uSMopaque = gl.GetUniformLocation(skyMeshProg, "uOpaque");
    uSMtint = gl.GetUniformLocation(skyMeshProg, "uTint");
    cloudTex = BuildCloudTexture(256);
    LoadCloudsFromEnv();
    weatherProg = BuildProgram(WeatherVert, WeatherFrag);
    uWMvp = gl.GetUniformLocation(weatherProg, "uMvp");
    uWTex = gl.GetUniformLocation(weatherProg, "uTex");
    uWColor = gl.GetUniformLocation(weatherProg, "uColor");
    uWSize = gl.GetUniformLocation(weatherProg, "uSize");
    // Particle-effect billboards (dynamic VBO rebuilt each frame): center(3)+corner(2)+size(1)+alpha(1) = 7 floats/vert.
    effectProg = BuildProgram(EffectVert, EffectFrag);
    uEMvp = gl.GetUniformLocation(effectProg, "uMvp");
    uERight = gl.GetUniformLocation(effectProg, "uRight");
    uEUp = gl.GetUniformLocation(effectProg, "uUp");
    uETex = gl.GetUniformLocation(effectProg, "uTex");
    uETint = gl.GetUniformLocation(effectProg, "uTint");
    effectVao = gl.GenVertexArray();
    gl.BindVertexArray(effectVao);
    effectVbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, effectVbo);
    unsafe
    {
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)0);                 gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(3 * sizeof(float))); gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 1, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(5 * sizeof(float))); gl.EnableVertexAttribArray(2);
        gl.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(6 * sizeof(float))); gl.EnableVertexAttribArray(3);
        gl.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(7 * sizeof(float))); gl.EnableVertexAttribArray(4);
    }
    gl.BindVertexArray(0);
    LoadSkyCubemap();
    LoadSkyboxMesh();   // the level's real skybox mesh (e.g. Sky_Bocage_m1 = Immersed's underwater surface)
    LoadCloudMesh();    // the level's real cloud-layer meshes (the scrolling bubbles/clouds)

    if (meshLib is not null && so is not null)
    {
        objProg = BuildProgram(ObjVert, ObjFrag);
        uMvpO = gl.GetUniformLocation(objProg, "uMVP");
        uModelO = gl.GetUniformLocation(objProg, "uModel");
        uColorO = gl.GetUniformLocation(objProg, "uColor");
        uLightO = gl.GetUniformLocation(objProg, "uLightDir");
        uUseTexO = gl.GetUniformLocation(objProg, "uUseTex");
        uAlphaTestO = gl.GetUniformLocation(objProg, "uAlphaTest");
        uAlphaEnableO = gl.GetUniformLocation(objProg, "uAlphaEnable");
        uTintO = gl.GetUniformLocation(objProg, "uTint");
        uUseShadowMapO = gl.GetUniformLocation(objProg, "uUseShadowMap");
        uLightSpaceO = gl.GetUniformLocation(objProg, "uLightSpace");
        gl.UseProgram(objProg);
        gl.Uniform1(gl.GetUniformLocation(objProg, "uTex"), 0);          // sampler -> texture unit 0
        gl.Uniform1(gl.GetUniformLocation(objProg, "uLightmap"), 1);     // object lightmap -> texture unit 1
        gl.Uniform1(gl.GetUniformLocation(objProg, "uShadowMap"), 2);    // sun shadow map -> texture unit 2
        glObjects = GlObjects.Build(gl, so, meshLib);
        // Object lightmaps are matched lazily (EnsureObjectLightmaps) the first time the layer is enabled - keeps load fast.
        SyncMarkers(); // recompute mesh-less markers now that the library is known
        Console.WriteLine($"Object meshes: {glObjects.TemplateCount} templates, {glObjects.InstanceCount} instances, {glObjects.TextureCount} textures; {pointMarkers.Length} mesh-less markers.");
    }
    LoadOvergrowthSettings();   // restore the per-map overgrowth overlay config (spacing + on/off), if saved
    RefreshTextureLibrary();    // scan the bundled/user Texture Library folder for the Surface painter + Layer Tool
    // Seed the sun azimuth/elevation from the level's SkyAndSun.con so manual sun control starts where the level is,
    // and flag the real-time shadow map for a first render. (No baked .lsb auto-load - that darkened the whole ground.)
    { var s0 = EffectiveSun(); sunElevationDeg = MathF.Asin(Math.Clamp(s0.Y, -1f, 1f)) * 180f / MathF.PI; sunAzimuthDeg = MathF.Atan2(s0.X, s0.Z) * 180f / MathF.PI; }
    shadowMapDirty = true;
    UploadMarkers();
    Console.WriteLine($"Editor ready: GL-side load took {loadSw.ElapsedMilliseconds} ms.");
    SplashScreen.Close();       // editor is ready -> dismiss the launch splash
    // Like Battlecraft's "Load Errors" box: if the load produced any warnings (missing meshes etc.), pop the Log window.
    if (ConsoleLog.Snapshot().Any(ConsoleLog.LooksLikeError)) { showLog = true; logErrorsOnly = true; }
}

void OnUpdate(double dt)
{
    if (playSounds && soundPlayback is not null) soundPlayback.Update(cam.Position, PlacedSounds(), dt);   // placed-sound preview (no-op when off)
    UpdateWeather(dt);   // advance the weather preview particles (no-op when off)
    if (showEffects) EnsureEffects();   // lazy-build effect instances on first frame the layer is on
    UpdateEffects((float)dt);   // advance the level's particle effects (waterfalls/lava/fire/smoke; no-op when off)
    // Advance the .bik playback clock (frame-stepped at the video's fps; loops or stops at the end).
    if (bikOpen && bikPlaying && bikFrames.Length > 0)
    {
        bikClock += dt;
        double spf = 1.0 / Math.Max(1f, bikFps);
        while (bikClock >= spf)
        {
            bikClock -= spf;
            if (++bikFrameIdx >= bikFrames.Length) { if (bikLoop) bikFrameIdx = 0; else { bikFrameIdx = bikFrames.Length - 1; bikPlaying = false; break; } }
        }
    }
    // Collaboration: apply inbound edits on this (GL) thread, then broadcast our presence (camera + selection).
    CollabDrain();
    if (collab is not null)
    {
        collabPresenceTimer += dt;
        if (collabPresenceTimer >= 1.0 / 60.0)   // ~60 presence updates/sec so peer diamonds move smoothly (tiny bandwidth)
        {
            collabPresenceTimer = 0;
            string selId = (so is not null && selected >= 0 && selected < so.Objects.Count) ? so.Objects[selected].Id : "-";
            var cp = cam.Position;
            collab.SendPresence(selId, new Vec3(cp.X, cp.Y, cp.Z), cam.Yaw);   // heading so peers' diamonds show our look direction
        }
    }

    if (kb is null) return;
    if (imgui is not null && ImGui.GetIO().WantCaptureKeyboard) return;   // don't fly the camera with WASD/Q-E while typing in an inspector field
    float fwd = (kb.IsKeyPressed(Key.W) ? 1 : 0) - (kb.IsKeyPressed(Key.S) ? 1 : 0);
    float str = (kb.IsKeyPressed(Key.D) ? 1 : 0) - (kb.IsKeyPressed(Key.A) ? 1 : 0);
    if (cam.MirrorX) str = -str;   // the view is X-mirrored, so A/D and screen-left/right stay intuitive
    float up = (kb.IsKeyPressed(Key.E) ? 1 : 0) - (kb.IsKeyPressed(Key.Q) ? 1 : 0);
    float boost = kb.IsKeyPressed(Key.ShiftLeft) ? 4f : 1f;
    float amt = Altitude() * 1.2f * camSpeedMult * (float)dt * boost;
    if (groundCam)
    {
        // W/S along the TRUE view direction (Dolly keeps the Y component), so you fly toward the middle of the
        // screen and gain/lose height by aiming. Strafe and Q/E stay planar/vertical so they remain predictable.
        if (fwd != 0) cam.Dolly(fwd * amt);
        if (str != 0 || up != 0) cam.Move(0f, str, up, amt);
    }
    else if (fwd != 0 || str != 0 || up != 0) cam.Move(fwd, str, up, amt);
}

void OnKeyDown(IKeyboard k, Key key, int _)
{
    if (imgui is not null && ImGui.GetIO().WantCaptureKeyboard) return;   // don't fire shortcuts while typing in a field
    bool ctrl = k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight);
    // Mapper hotkeys (F1-F6) + Save (Ctrl+S) work even before a level's objects are loaded.
    switch (key)
    {
        case Key.F1: SetMapper(0); return;
        case Key.F2: SetMapper(1); return;
        case Key.F3: SetMapper(2); return;
        case Key.F4: SetMapper(3); return;
        case Key.F5: SetMapper(4); return;
        case Key.F6: SetMapper(5); return;
        case Key.F7: SetGroundCam(!groundCam); return;   // fly <-> Battlecraft-style ground camera
        case Key.S: if (ctrl) { DoSave(); return; } break;
        case Key.L: if (ctrl) { DoTestLevel(); return; } break;
    }
    // Camera bookmarks: Ctrl+1..9 saves the current view to a slot; 1..9 (no Ctrl) flies back to it. Works any time.
    int bmSlot = key switch { Key.Number1 => 0, Key.Number2 => 1, Key.Number3 => 2, Key.Number4 => 3, Key.Number5 => 4,
                              Key.Number6 => 5, Key.Number7 => 6, Key.Number8 => 7, Key.Number9 => 8, _ => -1 };
    if (bmSlot >= 0)
    {
        if (ctrl) { camBookmarks[bmSlot] = (cam.Position, cam.Yaw, cam.Pitch); Toast($"Saved camera bookmark {bmSlot + 1}"); }
        else if (camBookmarks[bmSlot] is { } bm) { cam.Position = bm.pos; cam.Yaw = bm.yaw; cam.Pitch = bm.pitch; Toast($"Camera bookmark {bmSlot + 1}"); }
        return;
    }
    if (hist is null || so is null) return;
    // Arrow keys nudge the selected object(s): FINE by default (0.5 m / 3°), COARSE with Shift (one terrain sample / 15°).
    // Plain arrows move on X/Z; Alt+Up/Down raises/lowers (Y); Alt+Left/Right rotates yaw.
    bool shiftN = k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight);
    bool altN   = k.IsKeyPressed(Key.AltLeft)   || k.IsKeyPressed(Key.AltRight);
    float mv  = shiftN ? cfg.HorizontalSpacing : 0.5f;
    float deg = shiftN ? 15f : 3f;
    switch (key)
    {
        case Key.Up:    if (altN) NudgeSelected(0,  mv, 0); else NudgeSelected(0, 0, -mv); break;
        case Key.Down:  if (altN) NudgeSelected(0, -mv, 0); else NudgeSelected(0, 0,  mv); break;
        case Key.Left:  if (altN) RotateSelectedYaw(-deg); else NudgeSelected(cam.MirrorX ?  mv : -mv, 0, 0); break;
        case Key.Right: if (altN) RotateSelectedYaw( deg); else NudgeSelected(cam.MirrorX ? -mv :  mv, 0, 0); break;
        case Key.Delete:
            if (gpIndex >= 0 && hist is not null)   // a gameplay handle is selected
            {
                hist.Do(new GameplayDeleteCommand(gameplayEdit, gpKind, gpIndex, null));
                gpIndex = -1;
                break;
            }
            if (multi.Count > 0)
            {
                var dels = multi.Where(i => i >= 0 && i < so.Objects.Count)
                                .Select(i => (IEditCommand)new DeleteObject(so.Objects[i].Id)).ToList();
                if (dels.Count > 0) hist.Do(new CompositeCommand(dels));
                multi.Clear(); selected = -1; SyncMarkers(); RebuildObjects(); UploadMarkers();
            }
            break;
        case Key.Z: DoUndo(); break;
        case Key.Y: DoRedo(); break;
        case Key.D: if (k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight)) DuplicateSelected(); break;
        case Key.G: DropSelectedToGround(); break;
        case Key.Escape:
            if (roadMode) { if (roadPts.Count > 0) { roadPts.Clear(); roadPtW.Clear(); roadSelIdx = -1; roadDragIdx = -1; } else roadMode = false; }
            else if (measurePts.Count > 0) measurePts.Clear(); else measureMode = false;
            break;
        case Key.F:
            if (selected >= 0)
            {
                var o = so.Objects[selected];
                var t = new Vector3(o.Position.X, o.Position.Y, o.Position.Z);
                cam.Position = t + new Vector3(0f, 25f, 55f);   // stand back ~60 m, slightly above
                cam.LookAt(t);
            }
            break;
    }
}

bool UiWantsMouse() => imgui is not null && ImGui.GetIO().WantCaptureMouse;

// Selected object's world position, and a gizmo length that stays ~constant on screen.
Vector3 SelPos() => selected >= 0 && so is not null
    ? new Vector3(so.Objects[selected].Position.X, so.Objects[selected].Position.Y, so.Objects[selected].Position.Z)
    : default;
float GizmoLen(Vector3 at) => MathF.Max(2f, Vector3.Distance(cam.Position, at) * 0.13f);

// Pick the visible gameplay handle nearest the cursor in screen space (within a pixel radius).
bool TryPickGameplay(Vector2 px, out GpKind kind, out int index)
{
    // Can't capture the out-params in the nested local function (CS1628); track in locals and assign back.
    var bestKind = GpKind.ControlPoint; int bestIndex = -1;
    var fb = window.FramebufferSize;
    float best = 28f;                                   // generous pixel threshold (was 16)
    // Test a spawn by projecting several points along its body height so a click anywhere on the mesh hits.
    void Test(GpKind k, int count, bool show, float bodyHeight)
    {
        if (!show) return;
        for (int i = 0; i < count; i++)
        {
            var p = gameplayEdit.GetPos(k, i);
            float dmin = float.MaxValue;
            for (float yo = 0f; yo <= bodyHeight + 0.01f; yo += MathF.Max(bodyHeight, 0.5f) / 3f)
            {
                var s = Gizmo.Project(new Vector3(p.X, p.Y + yo, p.Z), cam.ViewProjection, fb.X, fb.Y);
                if (float.IsNaN(s.X)) continue;
                dmin = MathF.Min(dmin, Vector2.Distance(s, px));
            }
            if (dmin < best) { best = dmin; bestKind = k; bestIndex = i; }
        }
    }
    Test(GpKind.ControlPoint, gameplayEdit.ControlPoints.Count, showControlPoints, 6f);   // flagpole is tall
    Test(GpKind.Vehicle, gameplayEdit.VehicleSpawns.Count, showVehicles, 3f);             // vehicle body height
    Test(GpKind.Soldier, gameplayEdit.SoldierSpawns.Count, showSpawns, 1f);
    kind = bestKind; index = bestIndex;
    return index >= 0;
}

// Re-drape the world-grid overlay onto the current terrain: fixed 4 m x 4 m cells, sampled onto the heightmap
// along each line and lifted a hair so they read as drawn on the ground. Rebuilt only when the terrain or
// spacing changes (gridDirty), so the per-line sampling cost stays off the hot path.
void BuildGrid()
{
    gridVertCount = 0;
    if (terrainPick is null) return;
    float ws = cfg.WorldSize;
    if (ws <= 0f) return;
    // 4 m grid. On very large maps a 4 m grid would be millions of lines, so coarsen to the smallest multiple
    // of 4 m that keeps the line count bounded (e.g. a 32 km map falls back to 32 m). Normal BFV maps (<= ~4 km)
    // get true 4 m cells, matching the terrain sample grid.
    const float cellM = 4f, capLines = 1024f;
    gridStep = ws / cellM > capLines ? MathF.Ceiling(ws / capLines / cellM) * cellM : cellM;
    if (gridStep <= 0f) return;
    float seg = MathF.Max(cfg.HorizontalSpacing, ws / 512f);   // drape step; caps each line at ~512 samples
    // Lift the lines just off the surface -- enough to beat z-fighting, small enough to read as ON the ground
    // (was ~0.6 m on Irving, which floated). Scales a touch with cell size so coarse big-map grids still clear.
    float bias = MathF.Max(0.15f, gridStep * 0.02f);
    int est = (int)((ws / gridStep + 1) * (ws / seg + 1) * 12) + 64;   // pre-size to avoid repeated List growth
    var verts = new List<float>(Math.Clamp(est, 8192, 24_000_000));
    void Drape(bool alongX, float fixedC)
    {
        float px = 0, py = 0, pz = 0; bool have = false;
        for (float a = 0f; a <= ws + 1e-3f; a += seg)
        {
            float wx = alongX ? a : fixedC, wz = alongX ? fixedC : a;
            float wy = terrainPick.HeightAt(wx, wz) + bias;
            if (have) { verts.Add(px); verts.Add(py); verts.Add(pz); verts.Add(wx); verts.Add(wy); verts.Add(wz); }
            px = wx; py = wy; pz = wz; have = true;
        }
    }
    for (float c = 0f; c <= ws + 1e-3f; c += gridStep) { Drape(true, c); Drape(false, c); }
    var arr = verts.ToArray();
    gridVertCount = arr.Length / 3;
    gl.BindVertexArray(gridVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, gridVbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, arr, BufferUsageARB.DynamicDraw);
}

// A brush-radius cursor outline that DRAPES on the terrain (samples height per point + a small hover bias) so it
// hugs slopes like the grid instead of a flat ring/square punching through the ground. World-space; draw with the
// plain view-projection. square=true -> a box outline (edges tessellated so they follow the terrain too).
void DrawDrapedBrushOutline(float cx, float cz, float radius, bool square)
{
    if (terrainPick is null) return;
    const int n = 64; const float bias = 0.2f;
    var pts = new float[n * 3];
    for (int i = 0; i < n; i++)
    {
        float ox, oz;
        if (square)
        {
            float t = i / (float)n * 4f; int edge = (int)t; float f = t - edge;   // walk the [-r,r] perimeter
            (ox, oz) = edge switch
            {
                0 => (-radius + 2f * radius * f, -radius),
                1 => (radius, -radius + 2f * radius * f),
                2 => (radius - 2f * radius * f, radius),
                _ => (-radius, radius - 2f * radius * f),
            };
        }
        else { float a = i / (float)n * MathF.PI * 2f; ox = MathF.Cos(a) * radius; oz = MathF.Sin(a) * radius; }
        float wx = cx + ox, wz = cz + oz;
        pts[i * 3] = wx; pts[i * 3 + 1] = terrainPick.HeightAt(wx, wz) + bias; pts[i * 3 + 2] = wz;
    }
    gl.BindVertexArray(brushDrapeVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, brushDrapeVbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, pts, BufferUsageARB.DynamicDraw);
    gl.DrawArrays(PrimitiveType.LineLoop, 0, (uint)n);
}

// Draw the draped grid as depth-tested lines (occluded by hills/objects in front), in a subtle cool grey.
void DrawGrid()
{
    if (!gridOn || gridVertCount <= 0) return;
    gl.UseProgram(markerProg);
    gl.UniformMatrix4(uMvpM, 1, false, ToFloats(cam.ViewProjection));
    gl.Uniform3(uColor, gridColor.X, gridColor.Y, gridColor.Z);
    gl.Uniform1(uSize, 1f);
    gl.BindVertexArray(gridVao);
    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)gridVertCount);
}

// The road's per-point width in metres (the per-point override when set, else the global road width).
float RoadPtWidth(int i) => (i >= 0 && i < roadPtW.Count && roadPtW[i] > 0f) ? roadPtW[i] : roadWidth;

// Densify the clicked road points into a smooth Catmull-Rom centerline (heights + widths ride the curve).
List<RefractorForge.Render.RoadSample> RoadSamples(float step)
{
    var ctrl = new List<(float X, float Y, float Z, float HalfW)>(roadPts.Count);
    for (int i = 0; i < roadPts.Count; i++)
        ctrl.Add((roadPts[i].X, roadPts[i].Y, roadPts[i].Z, MathF.Max(RoadPtWidth(i) * 0.5f, 1f)));
    return RefractorForge.Render.RoadSpline.Resample(ctrl, step);
}

// The road's paint texture: the Texture Library pick when set, else the chosen Surface slot's texture.
Texture2D? RoadPaintTex() => (roadUseLib && roadLibTex is not null) ? roadLibTex : texPalette[roadSurface & 15];

// Stamp the road: sweep a flatten + texture + material brush along the SPLINE through the clicked points
// (Editor42/Crysis-style: smooth curves, per-point widths, texture oriented along the road, flatten with a
// shoulder), then coalesce the terrain, atlas-paint and material edits into ONE composite undo step. The points
// are KEPT after stamping so the road can be tweaked and re-stamped (Ctrl+Z reverts the previous stamp).
void StampRoad()
{
    if (hist is null || roadPts.Count < 2 || terrainEd is null || heightmap is null) return;
    float feather = MathF.Max(roadEdge, 0f);
    // Dense spline samples: short segments keep the oriented UV seam-free and the flatten band smooth.
    float step = Math.Clamp(MathF.Min(roadWidth * 0.25f, cfg.HorizontalSpacing), 0.5f, 2f);
    var samples = RoadSamples(step);
    if (samples.Count < 2) return;
    var roadTex = RoadPaintTex();
    var tStroke = terrainEd.BeginStroke();
    var aStroke = (atlasCpu is not null && roadTex is not null) ? new AtlasPaintStroke(atlasCpu, cfg.WorldSize) : null;
    var mStroke = matPainter?.BeginStroke();
    // Texture: oriented (u across the width, v along the arc) or the classic world-tiled feathered sweep.
    if (aStroke is not null && roadTex is not null)
    {
        if (roadOrient)
            aStroke.SweepOriented(roadTex, samples.Select(s => (s.X, s.Z, s.HalfWidth, s.ArcLen)).ToList(), feather, roadIntensity, roadTileAlong, roadTexRotate);
        else
            aStroke.Sweep(roadTex, samples.Select(s => (s.X, s.Z)).ToList(), MathF.Max(roadWidth * 0.5f, 1f), feather, roadIntensity, texTileMeters);
    }
    // Flatten + material ride the dab engines along the dense samples. The flatten target is the SPLINE height
    // (smooth grades); its radius adds the shoulder so embankments extend past the visible road edge.
    foreach (var s in samples)
    {
        if (roadFlatten) tStroke.Dab(s.X, s.Z, new TerrainBrush(BrushMode.Flatten, s.HalfWidth + MathF.Max(roadShoulder, 0f), 0.85f, BrushFalloff.Smooth, s.Y));
        mStroke?.Dab(s.X, s.Z, new MaterialBrush(roadSurface, s.HalfWidth, 1f));
    }
    var cmds = new List<IEditCommand>();
    if (roadFlatten) { var te = tStroke.Finish(); if (te is not null) cmds.Add(new TerrainStrokeCommand(te, heightmap, RebuildTerrain)); }
    if (aStroke is not null) { var ae = aStroke.Finish(UploadAtlasRectMips); if (ae is not null) { atlasPainted = true; cmds.Add(ae); } }
    if (mStroke is not null) { var me = mStroke.Finish(); if (me is not null) cmds.Add(new MaterialStrokeCommand(me, matPainter!.Map, null)); }
    if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
    Toast(Loc.T("Road stamped. Points kept -- tweak + re-stamp (Ctrl+Z reverts), or Clear."));
}

// Road preview: the smooth spline centerline, the road's edge outlines (per-point widths shown live), and the
// clickable/draggable point handles (selected point highlighted). Drawn on the ImGui background drawlist.
void DrawRoad()
{
    if (!roadMode || roadPts.Count == 0) return;
    var fb = window.FramebufferSize;
    var dl = ImGui.GetBackgroundDrawList();   // world overlay: over the 3D scene but UNDER all UI chrome (panels/minimap/modals)
    uint lc = ImGui.GetColorU32(new Vector4(0.55f, 0.78f, 1f, 1f));
    uint ec = ImGui.GetColorU32(new Vector4(0.55f, 0.78f, 1f, 0.45f));
    uint selc = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.25f, 1f));

    if (roadPts.Count >= 2)
    {
        // Coarse spline (preview only): centerline + the two width-edge outlines, offset along the curve normal.
        var samples = RoadSamples(2f);
        Vector2? prevC = null, prevL = null, prevR = null;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            // Tangent from the neighbouring sample; normal = (-tz, tx) in the XZ plane.
            var nb = samples[Math.Min(i + 1, samples.Count - 1)];
            var pb = samples[Math.Max(i - 1, 0)];
            float tx = nb.X - pb.X, tz = nb.Z - pb.Z;
            float tl = MathF.Sqrt(tx * tx + tz * tz); if (tl < 1e-4f) tl = 1f;
            float nx = -tz / tl, nz = tx / tl;
            var c = Gizmo.Project(new Vector3(s.X, s.Y, s.Z), cam.ViewProjection, fb.X, fb.Y);
            var le = Gizmo.Project(new Vector3(s.X + nx * s.HalfWidth, s.Y, s.Z + nz * s.HalfWidth), cam.ViewProjection, fb.X, fb.Y);
            var re = Gizmo.Project(new Vector3(s.X - nx * s.HalfWidth, s.Y, s.Z - nz * s.HalfWidth), cam.ViewProjection, fb.X, fb.Y);
            if (float.IsNaN(c.X)) { prevC = prevL = prevR = null; continue; }
            if (prevC is Vector2 pc) dl.AddLine(pc, c, lc, 2.5f);
            if (prevL is Vector2 pl && !float.IsNaN(le.X)) dl.AddLine(pl, le, ec, 1.4f);
            if (prevR is Vector2 pr && !float.IsNaN(re.X)) dl.AddLine(pr, re, ec, 1.4f);
            prevC = c; prevL = float.IsNaN(le.X) ? null : le; prevR = float.IsNaN(re.X) ? null : re;
        }
    }
    // Point handles on top: draggable; the selected one (per-point width target) is highlighted.
    for (int i = 0; i < roadPts.Count; i++)
    {
        var s = Gizmo.Project(roadPts[i], cam.ViewProjection, fb.X, fb.Y);
        if (float.IsNaN(s.X)) continue;
        if (i == roadSelIdx) { dl.AddCircleFilled(s, 6f, selc); dl.AddCircle(s, 8f, selc, 16, 1.5f); }
        else dl.AddCircleFilled(s, 4.5f, lc);
    }
    // Rubber-band from the last point to the cursor (the segment the next click would add).
    if (terrainPick is not null && !UiWantsMouse() && roadDragIdx < 0)
    {
        var lray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fb.X, fb.Y);
        if (terrainPick.Raycast(lray, out var cur))
        {
            var a = Gizmo.Project(roadPts[^1], cam.ViewProjection, fb.X, fb.Y);
            var b = Gizmo.Project(new Vector3(cur.X, cur.Y, cur.Z), cam.ViewProjection, fb.X, fb.Y);
            if (!float.IsNaN(a.X) && !float.IsNaN(b.X)) dl.AddLine(a, b, lc, 1.5f);
        }
    }
}

// Measure tool overlay: the placed points + segments, each segment's length, a live segment to the cursor, and
// the running total (plus polygon area once there are 3+ points). Drawn in the ImGui foreground.
void DrawMeasure()
{
    if (!measureMode || measurePts.Count == 0) return;
    var fb = window.FramebufferSize;
    var dl = ImGui.GetBackgroundDrawList();   // world overlay: over the 3D scene but UNDER all UI chrome (panels/minimap/modals)
    uint lc = ImGui.GetColorU32(new Vector4(1f, 0.85f, 0.2f, 1f));
    uint tc = ImGui.GetColorU32(new Vector4(1f, 1f, 0.6f, 1f));
    float total = 0f;
    Vector2? prevS = null;
    for (int i = 0; i < measurePts.Count; i++)
    {
        var s = Gizmo.Project(measurePts[i], cam.ViewProjection, fb.X, fb.Y);
        if (!float.IsNaN(s.X)) dl.AddCircleFilled(s, 3f, lc);
        if (i > 0)
        {
            total += Vector3.Distance(measurePts[i - 1], measurePts[i]);
            if (prevS is Vector2 ps && !float.IsNaN(s.X))
            {
                dl.AddLine(ps, s, lc, 2f);
                var mid = (ps + s) * 0.5f; string st = $"{Vector3.Distance(measurePts[i - 1], measurePts[i]):0.0} m";
                dl.AddText(mid + new Vector2(1f, 1f), 0xCC000000, st); dl.AddText(mid, tc, st);
            }
        }
        prevS = float.IsNaN(s.X) ? null : s;
    }
    // live segment from the last point to the cursor + the running total
    float liveTotal = total;
    var last = measurePts[^1];
    if (terrainPick is not null && !UiWantsMouse())
    {
        var lray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fb.X, fb.Y);
        if (terrainPick.Raycast(lray, out var cur))
        {
            var a = Gizmo.Project(last, cam.ViewProjection, fb.X, fb.Y);
            var b = Gizmo.Project(new Vector3(cur.X, cur.Y, cur.Z), cam.ViewProjection, fb.X, fb.Y);
            if (!float.IsNaN(a.X) && !float.IsNaN(b.X)) dl.AddLine(a, b, lc, 1f);
            liveTotal += Vector3.Distance(last, new Vector3(cur.X, cur.Y, cur.Z));
        }
    }
    string totStr = $"total {liveTotal:0.0} m";
    if (measurePts.Count >= 3)
    {
        float area = 0f;   // shoelace on the XZ plane
        for (int i = 0, n = measurePts.Count; i < n; i++)
        { var p = measurePts[i]; var q = measurePts[(i + 1) % n]; area += p.X * q.Z - q.X * p.Z; }
        totStr += $"   area {MathF.Abs(area) * 0.5f:0} m^2";
    }
    var sl = Gizmo.Project(last, cam.ViewProjection, fb.X, fb.Y);
    if (!float.IsNaN(sl.X)) { var at = sl + new Vector2(14f, 14f); dl.AddText(at + new Vector2(1f, 1f), 0xCC000000, totStr); dl.AddText(at, tc, totStr); }
}

// Scan the level for common problems and return a short report (counts + warnings). Read-only.
string ValidateMap()
{
    if (so is null) return Loc.T("No level loaded.");
    var sb = new System.Text.StringBuilder();
    int objN = so.Objects.Count, meshless = pointMarkers.Length;
    int cps = gameplayEdit.ControlPoints.Count, vss = gameplayEdit.VehicleSpawns.Count, sss = gameplayEdit.SoldierSpawns.Count;
    sb.AppendLine($"Objects: {objN}  ({meshless} mesh-less markers)");
    sb.AppendLine($"Control points: {cps}");
    sb.AppendLine($"Vehicle spawns: {vss}");
    sb.AppendLine($"Soldier spawns: {sss}");
    sb.AppendLine($"World: {cfg.WorldSize} m   terrain {cfg.MaterialSize}^2   water {cfg.WaterLevel:0.#} m");
    sb.AppendLine();
    int warn = 0; void W(string s) { sb.AppendLine("WARNING: " + s); warn++; }
    if (heightmap is null) W("no heightmap loaded.");
    if (cps == 0) W("no control points -- Conquest won't be playable.");
    if (sss == 0) W("no soldier spawns -- players can't spawn.");
    int us = gameplayEdit.ControlPoints.Count(c => c.Team == 2), nva = gameplayEdit.ControlPoints.Count(c => c.Team == 1);
    if (cps > 0 && us == 0) W("no US (team 2) base flag.");
    if (cps > 0 && nva == 0) W("no NVA (team 1) base flag.");
    foreach (var v in gameplayEdit.VehicleSpawns)
        if (string.IsNullOrWhiteSpace(v.Vehicle)) { W($"vehicle spawn '{v.Name}' has no vehicle set."); }
    sb.AppendLine();
    sb.AppendLine(warn == 0 ? Loc.T("No problems found.") : string.Format(Loc.T("{0} warning(s)."), warn));
    return sb.ToString();
}

// The "Labels" overlay. With a material map present it's a Battlecraft-style SURFACE MAP: each nearby cell's
// material name printed in its square (zoom in to read it). Otherwise it falls back to world-coordinate text at
// grid intersections. Foreground + viewport-clipped + distance-culled so it stays legible and cheap. Text lives
// on the grid, never baked into the terrain.
void DrawGridLabels()
{
    if (!gridOn || !gridLabels || terrainPick is null) return;
    float ws = cfg.WorldSize;
    if (ws <= 0f) return;
    var fb = window.FramebufferSize;
    var vpMin = new Vector2(uiLeftW, uiMenuH + uiToolH);
    var vpMax = new Vector2(fb.X - uiRightW, fb.Y - uiStatusH);
    var dl = ImGui.GetBackgroundDrawList();   // world overlay: over the 3D scene but UNDER all UI chrome (panels/minimap/modals)
    dl.PushClipRect(vpMin, vpMax, true);

    if (materialMap is not null)
    {
        float sp = cfg.HorizontalSpacing; if (sp <= 0f) sp = 1f;
        float maxDist = 90f;                                   // near-field only; this is for zoomed-in surface editing
        uint col = ImGui.GetColorU32(new Vector4(0.96f, 0.95f, 0.72f, 0.95f));
        int ccx = (int)(cam.Position.X / sp), ccz = (int)(cam.Position.Z / sp);
        int rad = (int)(maxDist / sp) + 1;
        for (int gy = Math.Max(0, ccz - rad); gy <= Math.Min(materialMap.Height - 1, ccz + rad); gy++)
            for (int gx = Math.Max(0, ccx - rad); gx <= Math.Min(materialMap.Width - 1, ccx + rad); gx++)
            {
                float wx = (gx + 0.5f) * sp, wz = (gy + 0.5f) * sp;
                var wp = new Vector3(wx, terrainPick.HeightAt(wx, wz), wz);
                if (Vector3.Distance(cam.Position, wp) > maxDist) continue;
                var s = Gizmo.Project(wp, cam.ViewProjection, fb.X, fb.Y);
                if (float.IsNaN(s.X) || s.X < vpMin.X || s.X > vpMax.X || s.Y < vpMin.Y || s.Y > vpMax.Y) continue;
                int mi = materialMap[gx, gy] & 15;
                // The material map indexes the level's TEXTURE SET (0-15), so label with the texture-set names
                // (index.dat order) -- NOT the old matNames guess, which was a different, wrong order (it showed
                // jungle grass as "Wet Sand"). Name only (no leading index number).
                string name = mi < surfNames.Length ? surfNames[mi] : mi.ToString();
                var at = s - ImGui.CalcTextSize(name) * 0.5f;   // centre the name in the cell
                dl.AddText(at + new Vector2(1f, 1f), 0xCC000000, name);
                dl.AddText(at, col, name);
            }
    }
    else if (gridStep > 0f)
    {
        float target = ws / 14f;
        float labelStep = gridStep * MathF.Pow(2f, MathF.Round(MathF.Log2(MathF.Max(target / gridStep, 1f))));
        if (labelStep <= 0f) { dl.PopClipRect(); return; }
        float maxDist = fogEnabled ? fogEnd : ws * 0.4f;
        uint col = ImGui.GetColorU32(new Vector4(0.72f, 0.86f, 0.96f, 0.9f));
        for (float z = 0f; z <= ws + 1e-3f; z += labelStep)
            for (float x = 0f; x <= ws + 1e-3f; x += labelStep)
            {
                var wp = new Vector3(x, terrainPick.HeightAt(x, z), z);
                if (Vector3.Distance(cam.Position, wp) > maxDist) continue;
                var s = Gizmo.Project(wp, cam.ViewProjection, fb.X, fb.Y);
                if (float.IsNaN(s.X) || s.X < vpMin.X || s.X > vpMax.X || s.Y < vpMin.Y || s.Y > vpMax.Y) continue;
                string t = $"{x:0}, {z:0}";
                dl.AddText(s + new Vector2(1f, 1f), 0xCC000000, t);
                dl.AddText(s, col, t);
            }
    }
    dl.PopClipRect();
}

// True when a world point sits past the fog's far plane, so its marker / ring / link / label should be skipped
// (matches the terrain + water fade - distant overlays shouldn't float in the haze). Fog off => nothing culled.
bool FogCulled(Vector3 w) => fogEnabled && Vector3.Distance(cam.Position, w) > fogEnd;

// The team that actually owns a vehicle spawner (1 = Axis/NVA, 2 = Allies/US), from its owning control point (matched by
// OSId, else nearest CP). SpawnVehicleName returns THAT team's vehicle template, so the mesh, label and collision all
// agree and show the right faction's vehicle instead of the team-2-preferred display fallback.
int SpawnTeam(VehicleSpawnDef vs)
{
    int ci = GameplayObjects.OwningControlPointIndex(gameplayEdit.ControlPoints, vs.Position, vs.OsId, true);
    return ci >= 0 ? gameplayEdit.ControlPoints[ci].Team : 2;
}
string SpawnVehicleName(VehicleSpawnDef vs)
{
    string s = SpawnTeam(vs) == 1 ? vs.Vehicle1 : vs.Vehicle2;
    return string.IsNullOrEmpty(s) ? vs.Vehicle : s;
}

// Resolve a placed sound emitter's .wav bytes: level-local Sound/ (folder or the level .rfa) first, else a shared
// sound*.rfa in the mod chain's Archives folders. Handles the BFV macro path @ROOT/Sound/@RTD/x.wav by matching the
// leaf name and preferring the 44kHz variant. Returns null if not found; the caller caches it (resolves once/emitter).
byte[]? ResolveSoundWav(SoundEmitter em)
{
    var raw = em.Script?.Wav;
    if (string.IsNullOrWhiteSpace(raw)) return null;
    string norm = raw.Replace('\\', '/').TrimStart('/');
    int sl = norm.LastIndexOf('/');
    string leaf = (sl >= 0 ? norm[(sl + 1)..] : norm).ToLowerInvariant();   // e.g. frog1.wav / frogs_1.wav
    if (leaf.Length == 0) return null;

    if (levelDir is not null && Directory.Exists(levelDir))
    {
        var hit = Directory.EnumerateFiles(levelDir, leaf, SearchOption.AllDirectories).FirstOrDefault();
        if (hit is not null) { try { return File.ReadAllBytes(hit); } catch { } }
    }

    if (soundWavArchives is null)
    {
        var set = new List<string>(rfaList.Where(File.Exists));
        foreach (var lp in rfaList.Where(File.Exists))
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(lp))!); dir is not null; dir = dir.Parent)
                if (dir.Name.Equals("Archives", StringComparison.OrdinalIgnoreCase))
                { try { set.AddRange(Directory.EnumerateFiles(dir.FullName, "sound*.rfa")); } catch { } break; }
        soundWavArchives = set.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
    byte[]? best = null; int bestScore = -1;
    foreach (var ap in soundWavArchives)
    {
        RefractorForge.Formats.Rfa.RefractorFlatArchive arc;
        try { arc = new RefractorFlatArchive(ap); } catch { continue; }
        foreach (var e in arc.Entries)
        {
            var en = e.Name.Replace('\\', '/').ToLowerInvariant();
            if (en != leaf && !en.EndsWith("/" + leaf)) continue;
            int score = en.Contains("44khz") ? 2 : (en.Contains("22khz") || en.Contains("22050") ? 1 : 0);
            if (score > bestScore) { try { best = arc.Read(e); bestScore = score; } catch { } }
            if (bestScore == 2) break;
        }
        if (bestScore == 2) break;
    }
    return best;
}

// Placed sound emitters as (emitter, world pos, audible radius = the drawn minDistance ring) for the playback preview.
IEnumerable<(SoundEmitter Em, Vector3 Pos, float Radius)> PlacedSounds()
{
    if (so is null) yield break;
    foreach (var o in so.Objects)
    {
        var em = sounds.Get(o.Template);
        if (em is not null) yield return (em, new Vector3(o.Position.X, o.Position.Y, o.Position.Z), em.MinDistance);
    }
}

// Draw the gameplay layer: control-point capture rings + markers, vehicle spawns, soldier spawns, labels.
void DrawGameplay()
{
    if (gameplayEdit.Count == 0) return;
    var fb = window.FramebufferSize;
    var vpFloats = ToFloats(cam.ViewProjection);

    void Points(System.Collections.Generic.List<Vector3> pts, float r, float g, float b, float size)
    {
        if (pts.Count == 0) return;
        var buf = new float[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++) { buf[i * 3] = pts[i].X; buf[i * 3 + 1] = pts[i].Y; buf[i * 3 + 2] = pts[i].Z; }
        gl.UseProgram(markerProg);
        gl.UniformMatrix4(uMvpM, 1, false, vpFloats);
        gl.BindVertexArray(gpVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, buf, BufferUsageARB.DynamicDraw);
        gl.Uniform3(uColor, r, g, b); gl.Uniform1(uSize, size);
        gl.DrawArrays(PrimitiveType.Points, 0, (uint)pts.Count);
    }

    // Draw a batch of line segments (pairs of endpoints) in one colour - used for the spawn->control-point links.
    void Links(System.Collections.Generic.List<Vector3> segs, float r, float g, float b)
    {
        if (segs.Count < 2) return;
        var buf = new float[segs.Count * 3];
        for (int i = 0; i < segs.Count; i++) { buf[i * 3] = segs[i].X; buf[i * 3 + 1] = segs[i].Y; buf[i * 3 + 2] = segs[i].Z; }
        gl.UseProgram(markerProg);
        gl.UniformMatrix4(uMvpM, 1, false, vpFloats);
        gl.BindVertexArray(gizmoVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gizmoVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, buf, BufferUsageARB.DynamicDraw);
        gl.Uniform3(uColor, r, g, b); gl.Uniform1(uSize, 1f);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)segs.Count);
    }

    gl.Disable(EnableCap.DepthTest);   // gameplay markers read through terrain/objects

    // Spawn -> owning control-point links (drawn first, so the markers sit on top). Vehicle spawners match a flag
    // by objectSpawnerId == OSId; soldier spawns by spawnGroupId == group; unmatched spawns fall back to nearest.
    if (showSpawnLinks && gameplayEdit.ControlPoints.Count > 0)
    {
        var cps = gameplayEdit.ControlPoints;
        var vehSegs = new System.Collections.Generic.List<Vector3>();
        if (showVehicles)
            foreach (var v in gameplayEdit.VehicleSpawns)
            {
                var vp = new Vector3(v.Position.X, v.Position.Y, v.Position.Z);
                if (FogCulled(vp)) continue;                                  // its marker is culled too, so drop the line
                int ci = GameplayObjects.OwningControlPointIndex(cps, v.Position, v.OsId, true);
                if (ci >= 0) { var c = cps[ci].Position; vehSegs.Add(vp); vehSegs.Add(new Vector3(c.X, c.Y, c.Z)); }
            }
        var solSegs = new System.Collections.Generic.List<Vector3>();
        if (showSpawns)
            foreach (var s in gameplayEdit.SoldierSpawns)
            {
                var sp = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
                if (FogCulled(sp)) continue;
                int ci = GameplayObjects.OwningControlPointIndex(cps, s.Position, s.Group, false);
                if (ci >= 0) { var c = cps[ci].Position; solSegs.Add(sp); solSegs.Add(new Vector3(c.X, c.Y, c.Z)); }
            }
        Links(vehSegs, 0.85f, 0.45f, 0.12f);   // dim orange, matching the vehicle markers
        Links(solSegs, 0.30f, 0.78f, 0.34f);   // dim green, matching the soldier markers
    }

    if (showControlPoints && gameplayEdit.ControlPoints.Count > 0)
    {
        gl.UseProgram(markerProg);
        gl.BindVertexArray(brushRingVao);
        foreach (var cp in gameplayEdit.ControlPoints)
        {
            var cpw = new Vector3(cp.Position.X, cp.Position.Y, cp.Position.Z);
            if (FogCulled(cpw)) continue;
            var m = Matrix4x4.CreateScale(cp.Radius) * Matrix4x4.CreateTranslation(cpw.X, cpw.Y, cpw.Z);
            gl.UniformMatrix4(uMvpM, 1, false, ToFloats(m * cam.ViewProjection));
            gl.Uniform3(uColor, 0.25f, 0.85f, 1f); gl.Uniform1(uSize, 1f);
            gl.DrawArrays(PrimitiveType.LineLoop, 0, 64);
        }
        Points(gameplayEdit.ControlPoints.Select(c => new Vector3(c.Position.X, c.Position.Y, c.Position.Z)).Where(p => !FogCulled(p)).ToList(), 0.3f, 0.9f, 1f, 16f);
    }
    if (showVehicles)
        Points(gameplayEdit.VehicleSpawns.Select(v => new Vector3(v.Position.X, v.Position.Y, v.Position.Z)).Where(p => !FogCulled(p)).ToList(), 1f, 0.55f, 0.15f, 12f);
    if (showSpawns)
    {
        // The spawn body is a soldier-sized box in the object pass (DrawGp "gp::soldbox"); here we add a UI facing
        // arrow on the ground showing which way the soldier faces (yaw = Rotation.X), plus a small base dot.
        var solArrows = new System.Collections.Generic.List<Vector3>();
        var solDots = new System.Collections.Generic.List<Vector3>();
        foreach (var s in gameplayEdit.SoldierSpawns)
        {
            var c = new Vector3(s.Position.X, s.Position.Y + 0.15f, s.Position.Z);
            if (FogCulled(c)) continue;
            solDots.Add(c);
            float yaw = s.Rotation.X * MathF.PI / 180f;
            var f = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
            var r = new Vector3(MathF.Cos(yaw), 0f, -MathF.Sin(yaw));
            float len = Math.Clamp(Vector3.Distance(cam.Position, c) * 0.04f, 2.0f, 12f);
            float barb = len * 0.32f;
            var tip = c + f * len;
            solArrows.Add(c); solArrows.Add(tip);                          // shaft
            solArrows.Add(tip); solArrows.Add(tip - f * barb - r * barb);  // arrowhead
            solArrows.Add(tip); solArrows.Add(tip - f * barb + r * barb);
        }
        Links(solArrows, 0.4f, 1f, 0.45f);
        Points(solDots, 0.4f, 1f, 0.45f, 5f);
    }

    // Highlight the selected gameplay handle: bright white marker, plus a white radius ring for a CP.
    if (gpIndex >= 0 && gpIndex < gameplayEdit.CountOf(gpKind))
    {
        var gpp = gameplayEdit.GetPos(gpKind, gpIndex);
        var wp = new Vector3(gpp.X, gpp.Y, gpp.Z);
        if (gpKind == GpKind.ControlPoint)
        {
            gl.UseProgram(markerProg);
            gl.BindVertexArray(brushRingVao);
            var m = Matrix4x4.CreateScale(gameplayEdit.GetRadius(gpIndex)) * Matrix4x4.CreateTranslation(wp.X, wp.Y, wp.Z);
            gl.UniformMatrix4(uMvpM, 1, false, ToFloats(m * cam.ViewProjection));
            gl.Uniform3(uColor, 1f, 1f, 1f); gl.Uniform1(uSize, 1f);
            gl.DrawArrays(PrimitiveType.LineLoop, 0, 64);
        }
        Points(new System.Collections.Generic.List<Vector3> { wp }, 1f, 1f, 1f, 20f);

        // Facing tick for a vehicle/soldier spawn: a white line pointing along its yaw.
        if (gpKind != GpKind.ControlPoint)
        {
            float yawR = gameplayEdit.GetYaw(gpKind, gpIndex) * MathF.PI / 180f;
            float fl = MathF.Max(4f, Vector3.Distance(cam.Position, wp) * 0.05f);
            var tip = wp + new Vector3(MathF.Sin(yawR), 0f, MathF.Cos(yawR)) * fl;
            float[] line = { wp.X, wp.Y, wp.Z, tip.X, tip.Y, tip.Z };
            gl.UseProgram(markerProg);
            gl.UniformMatrix4(uMvpM, 1, false, vpFloats);
            gl.BindVertexArray(gizmoVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gizmoVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, line, BufferUsageARB.DynamicDraw);
            gl.Uniform3(uColor, 1f, 1f, 1f); gl.Uniform1(uSize, 1f);
            gl.DrawArrays(PrimitiveType.Lines, 0, 2);
        }
    }

    gl.Enable(EnableCap.DepthTest);

    // Labels for the few high-value markers (control points + vehicles), via ImGui's foreground layer.
    // Hard-clipped to the central viewport so they never paint over the side/menu/status panels, and culled
    // beyond the fog distance so distant markers don't clutter the haze (matches the terrain/water fade).
    var dl = ImGui.GetBackgroundDrawList();   // world overlay: over the 3D scene but UNDER all UI chrome (panels/minimap/modals)
    uint cpCol = ImGui.GetColorU32(new Vector4(0.45f, 0.9f, 1f, 1f));
    uint vehCol = ImGui.GetColorU32(new Vector4(1f, 0.62f, 0.25f, 1f));
    var vpMin = new Vector2(uiLeftW, uiMenuH + uiToolH);
    var vpMax = new Vector2(fb.X - uiRightW, fb.Y - uiStatusH);
    dl.PushClipRect(vpMin, vpMax, true);
    void Label(Vector3 world, string text, uint col)
    {
        if (FogCulled(world)) return;                                               // only as far as the fog
        var s = Gizmo.Project(world, cam.ViewProjection, fb.X, fb.Y);
        if (float.IsNaN(s.X)) return;                                               // behind the camera
        if (s.X < vpMin.X || s.X > vpMax.X || s.Y < vpMin.Y || s.Y > vpMax.Y) return;
        dl.AddText(new Vector2(s.X + 8f, s.Y - 6f), col, text);
    }
    if (showControlPoints)
        foreach (var cp in gameplayEdit.ControlPoints) Label(new Vector3(cp.Position.X, cp.Position.Y, cp.Position.Z), cp.Name, cpCol);
    if (showVehicles)
        foreach (var v in gameplayEdit.VehicleSpawns) Label(new Vector3(v.Position.X, v.Position.Y, v.Position.Z), SpawnVehicleName(v), vehCol);
    dl.PopClipRect();
}

// Draw placed sound emitters: a purple marker + a ring at the script's minDistance (the audible-radius hint),
// plus a name label. Reads the object layer + the loaded SoundLibrary; depth off so the rings read through terrain.
void DrawSounds()
{
    if (!showSounds || so is null || sounds.Count == 0) return;
    var fb = window.FramebufferSize;
    var vpFloats = ToFloats(cam.ViewProjection);
    gl.Disable(EnableCap.DepthTest);

    // audible-radius rings (unit ring scaled to minDistance), and gather the emitter positions for the points
    var pts = new System.Collections.Generic.List<Vector3>();
    gl.UseProgram(markerProg);
    gl.BindVertexArray(brushRingVao);
    foreach (var ob in so.Objects)
    {
        var em = sounds.Get(ob.Template);
        if (em is null) continue;
        var p = new Vector3(ob.Position.X, ob.Position.Y, ob.Position.Z);
        if (FogCulled(p)) continue;                          // skip the ring AND its marker once past the fog
        pts.Add(p);
        var m = Matrix4x4.CreateScale(em.MinDistance) * Matrix4x4.CreateTranslation(p.X, p.Y, p.Z);
        gl.UniformMatrix4(uMvpM, 1, false, ToFloats(m * cam.ViewProjection));
        gl.Uniform3(uColor, 0.80f, 0.42f, 1f); gl.Uniform1(uSize, 1f);
        gl.DrawArrays(PrimitiveType.LineLoop, 0, 64);
    }
    if (pts.Count > 0)
    {
        var buf = new float[pts.Count * 3];
        for (int i = 0; i < pts.Count; i++) { buf[i * 3] = pts[i].X; buf[i * 3 + 1] = pts[i].Y; buf[i * 3 + 2] = pts[i].Z; }
        gl.UniformMatrix4(uMvpM, 1, false, vpFloats);
        gl.BindVertexArray(gpVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, buf, BufferUsageARB.DynamicDraw);
        gl.Uniform3(uColor, 0.92f, 0.55f, 1f); gl.Uniform1(uSize, 11f);
        gl.DrawArrays(PrimitiveType.Points, 0, (uint)pts.Count);
    }
    gl.Enable(EnableCap.DepthTest);

    var dl = ImGui.GetBackgroundDrawList();   // world overlay: over the 3D scene but UNDER all UI chrome (panels/minimap/modals)
    uint col = ImGui.GetColorU32(new Vector4(0.86f, 0.58f, 1f, 1f));
    var vpMin = new Vector2(uiLeftW, uiMenuH + uiToolH);
    var vpMax = new Vector2(fb.X - uiRightW, fb.Y - uiStatusH);
    dl.PushClipRect(vpMin, vpMax, true);
    foreach (var ob in so.Objects)
    {
        if (!sounds.IsSound(ob.Template)) continue;
        var w = new Vector3(ob.Position.X, ob.Position.Y, ob.Position.Z);
        if (FogCulled(w)) continue;
        var s = Gizmo.Project(w, cam.ViewProjection, fb.X, fb.Y);
        if (float.IsNaN(s.X) || s.X < vpMin.X || s.X > vpMax.X || s.Y < vpMin.Y || s.Y > vpMax.Y) continue;
        dl.AddText(new Vector2(s.X + 8f, s.Y - 6f), col, ob.Template);
    }
    dl.PopClipRect();
}

// Build the collision wireframe overlay: each placed object's decoded .sm collision mesh (verts + tris from the
// reverse-engineered DShape, see docs/SM_Collision_RE.md), baked to world space as line segments. Rebuilt on
// demand (toggled on / after object edits) into a static VBO; cheap to draw thereafter.
void BuildCollisionLines()
{
    collisionDirty = false; collisionLineCount = 0;
    if (meshLib is null || so is null) return;
    var pts = new System.Collections.Generic.List<float>();

    // Append a collision mesh, transformed by world matrix m, as triangle-edge line segments.
    void Emit(MeshLibrary.CollisionMesh cm, Matrix4x4 m)
    {
        int n = cm.Positions.Length;
        var w = new Vector3[n];
        for (int i = 0; i < n; i++) w[i] = Vector3.Transform(cm.Positions[i], m);
        var idx = cm.Indices;
        for (int t = 0; t + 2 < idx.Length; t += 3)
        {
            int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
            if ((uint)ia >= (uint)n || (uint)ib >= (uint)n || (uint)ic >= (uint)n) continue;
            var a = w[ia]; var b = w[ib]; var c = w[ic];
            pts.Add(a.X); pts.Add(a.Y); pts.Add(a.Z); pts.Add(b.X); pts.Add(b.Y); pts.Add(b.Z);
            pts.Add(b.X); pts.Add(b.Y); pts.Add(b.Z); pts.Add(c.X); pts.Add(c.Y); pts.Add(c.Z);
            pts.Add(c.X); pts.Add(c.Y); pts.Add(c.Z); pts.Add(a.X); pts.Add(a.Y); pts.Add(a.Z);
        }
    }

    // Static objects + VEHICLES dropped as static objects: TryGetRenderCollision mirrors the render path
    // (assembled vehicle -> generic Bundle/static -> single .sm), so custom map vehicles defined OUTSIDE /Vehicles/
    // (e.g. le_mans's objects/Big_Ear/) get collision too, exactly matching what now renders.
    foreach (var o in so.Objects)
    {
        if (meshLib.TryGetRenderCollision(o.Template, out var vparts))
        {
            var mw = LevelScene.MeshWorld(o);                 // includes the object's scale/rotation/translation
            foreach (var (col, local) in vparts) Emit(col, local * mw);
        }
    }

    // Vehicle SPAWNS: per-part collision, matching the render path's spawnWorld (yaw/pitch/roll + translation, NO scale).
    if (showVehicles)
        foreach (var v in gameplayEdit.VehicleSpawns)
        {
            var veh = SpawnVehicleName(v);   // collision matches the team-correct rendered vehicle
            if (string.IsNullOrWhiteSpace(veh) || !meshLib.TryGetRenderCollision(veh, out var vparts)) continue;
            var spawnWorld = Matrix4x4.CreateFromYawPitchRoll(
                                 v.Rotation.X * MathF.PI / 180f, v.Rotation.Y * MathF.PI / 180f, v.Rotation.Z * MathF.PI / 180f)
                           * Matrix4x4.CreateTranslation(v.Position.X, v.Position.Y, v.Position.Z);
            foreach (var (col, local) in vparts) Emit(col, local * spawnWorld);
        }

    collisionLineCount = pts.Count / 3;
    if (collisionLineCount == 0) return;
    gl.BindVertexArray(collisionVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, collisionVbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, pts.ToArray(), BufferUsageARB.StaticDraw);
}

// Collision overlay: bright green wireframe of the real .sm collision meshes, drawn through the solid geometry
// (depth off) so you can inspect what's actually solid in-game. Toggle: Layers -> Collision.
void DrawCollision()
{
    if (!showCollision || so is null || meshLib is null) return;
    // Re-bake if the object set, the vehicle-spawn set, or the vehicle-visibility toggle changed (the overlay now
    // includes vehicle-spawn collision). Per-object transform edits already set collisionDirty elsewhere.
    int sig = so.Objects.Count * 397 ^ gameplayEdit.VehicleSpawns.Count * 31 ^ (showVehicles ? 1 : 0);
    if (sig != collisionSig) { collisionSig = sig; collisionDirty = true; }
    if (collisionDirty) BuildCollisionLines();
    if (collisionLineCount == 0) return;
    // Fog-faded shader: only draw as far as the user can see. With fog on, dissolve across fogStart..fogEnd so the
    // wireframe never renders into / beyond the fog; with fog off, push the fade band far out (effectively no cull).
    float fogS = fogEnabled ? fogStart : 1e9f;
    float fogE = fogEnabled ? MathF.Max(fogEnd, fogStart + 1f) : 1e9f + 1f;
    gl.UseProgram(collisionProg);
    gl.UniformMatrix4(uCMvp, 1, false, ToFloats(cam.ViewProjection));
    gl.Uniform3(uCCam, cam.Position.X, cam.Position.Y, cam.Position.Z);
    gl.Uniform3(uCColor, 0.2f, 1f, 0.45f);
    gl.Uniform1(uCFogStart, fogS); gl.Uniform1(uCFogEnd, fogE);
    gl.Enable(EnableCap.Blend);
    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    gl.Disable(EnableCap.DepthTest);
    gl.BindVertexArray(collisionVao);
    gl.DrawArrays(PrimitiveType.Lines, 0, (uint)collisionLineCount);
    gl.Enable(EnableCap.DepthTest);
    gl.Disable(EnableCap.Blend);
}

// ---- Weather preview overlay (rain/snow/dust): a view-only particle box that follows the camera. Never saved by
// itself; "Write weather to level on save" generates the real Effects.con + texture (see ApplyWeatherToLevel). ----
RefractorForge.Formats.Con.WeatherType WeatherKind() => (RefractorForge.Formats.Con.WeatherType)Math.Clamp(weatherTypeIdx, 0, 3);

// Scan the LEVEL'S OWN .con files (and its placed objects) for weather-looking effect templates — rain/snow/dust
// bundles that mods like FH define per-map. The editor can then announce "this map has snow" and arm the built-in
// weather preview to match, instead of the user guessing. Name-keyed detection: the effect chain's own textures
// stay in the mod's FX pipeline; this is a preview aid, not a byte-accurate particle clone.
void ScanLevelWeather()
{
    levelWeatherScanned = true;
    detectedLevelWeather.Clear();
    void Consider(string name)
    {
        var n = name.ToLowerInvariant();
        int t;
        if (n.Contains("snow")) t = 0;
        else if (n.Contains("rain")) t = 1;
        else if (n.Contains("duststorm") || n.Contains("sandstorm")) t = 3;
        else if (n.Contains("dust") || n.Contains("sand") && n.Contains("storm")) t = 2;
        else return;
        if (!detectedLevelWeather.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            detectedLevelWeather.Add((name, t));
    }
    try
    {
        IEnumerable<string> ConTexts()
        {
            if (levelDir is not null && System.IO.Directory.Exists(levelDir))
            {
                foreach (var f in System.IO.Directory.EnumerateFiles(levelDir, "*.con", System.IO.SearchOption.AllDirectories))
                    yield return System.IO.File.ReadAllText(f);
            }
            else
                foreach (var rp in rfaList.Where(File.Exists))
                {
                    RefractorForge.Formats.Rfa.RefractorFlatArchive a;
                    try { a = new RefractorForge.Formats.Rfa.RefractorFlatArchive(rp); } catch { continue; }
                    foreach (var e in a.Entries)
                        if (e.Name.EndsWith(".con", StringComparison.OrdinalIgnoreCase) && e.UncompressedSize < 512 * 1024)
                        {
                            string txt;
                            try { txt = System.Text.Encoding.Latin1.GetString(a.Read(e)); } catch { continue; }
                            yield return txt;
                        }
                }
        }
        foreach (var text in ConTexts())
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.Trim();
                if (!l.StartsWith("ObjectTemplate.create", StringComparison.OrdinalIgnoreCase)) continue;
                var pp = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (pp.Length >= 3 && (pp[1].Equals("EffectBundle", StringComparison.OrdinalIgnoreCase)
                                    || pp[1].Equals("Emitter", StringComparison.OrdinalIgnoreCase)
                                    || pp[1].Equals("SpriteParticle", StringComparison.OrdinalIgnoreCase)))
                    Consider(pp[2].Trim('"'));
            }
        if (so is not null) foreach (var o in so.Objects) Consider(o.Template);   // placed weather emitters
    }
    catch (Exception ex) { Console.WriteLine($"Level weather scan: {ex.Message}"); }
    if (detectedLevelWeather.Count > 0)
        Console.WriteLine($"Level weather detected: {string.Join(", ", detectedLevelWeather.Select(w => w.Name))}");
}

void WeatherRespawn(int i, float camX, float camZ, float vtop, float vbot, bool spreadY)
{
    var k = WeatherKind();
    float half = 90f;
    float R() => (float)(weatherRng.NextDouble() * 2.0 - 1.0);
    if (k == RefractorForge.Formats.Con.WeatherType.DustStorm)
    {
        // Ground-hugging sheets blowing sideways (no real fall) - sit just above the terrain under the cursor box.
        float x = camX + R() * half, z = camZ + R() * half;
        float g = terrainPick is not null ? terrainPick.HeightAt(x, z) : cam.Position.Y - 30f;
        weatherPos[i] = new Vector3(x, g + 0.5f + (float)weatherRng.NextDouble() * 6f, z);
        weatherVel[i] = new Vector3(weatherWind + 7f + R() * 2f, -0.3f, R() * 2f);   // dominant sideways drift
        return;
    }
    float fall = k == RefractorForge.Formats.Con.WeatherType.Rain ? 22f
               : k == RefractorForge.Formats.Con.WeatherType.Dust ? 0.6f : 3.0f;
    float drift = k == RefractorForge.Formats.Con.WeatherType.Snow ? 1.2f
                : k == RefractorForge.Formats.Con.WeatherType.Dust ? 2.0f : 0.25f;
    float y = spreadY ? vbot + (float)weatherRng.NextDouble() * (vtop - vbot) : vtop;
    weatherPos[i] = new Vector3(camX + R() * half, y, camZ + R() * half);
    weatherVel[i] = new Vector3(weatherWind + R() * drift, -fall, R() * drift);
}

void UpdateWeather(double dt)
{
    if (!showWeather) return;
    float ft = (float)Math.Min(dt, 0.05);
    int target = Math.Clamp(weatherIntensity * 8, 400, 6000);
    float camX = cam.Position.X, camZ = cam.Position.Z;
    float vtop = cam.Position.Y + 70f, vbot = cam.Position.Y - 80f, half = 90f;
    if (weatherPos.Length != target)
    {
        weatherPos = new Vector3[target]; weatherVel = new Vector3[target];
        for (int i = 0; i < target; i++) WeatherRespawn(i, camX, camZ, vtop, vbot, spreadY: true);
    }
    for (int i = 0; i < weatherPos.Length; i++)
    {
        weatherPos[i] += weatherVel[i] * ft;
        var p = weatherPos[i];
        if (p.Y < vbot || MathF.Abs(p.X - camX) > half || MathF.Abs(p.Z - camZ) > half)
            WeatherRespawn(i, camX, camZ, vtop, vbot, spreadY: false);
    }
}

// GL preview texture for a weather type: imported image if the user picked one, else the procedural particle. Cached.
unsafe uint WeatherGlTex(RefractorForge.Formats.Con.WeatherType t)
{
    int idx = (int)t;
    if (weatherTexGl[idx] != 0) return weatherTexGl[idx];
    Texture2D img = weatherTexImg[idx] ?? new Texture2D(32, 32, RefractorForge.Formats.Con.WeatherEffect.BuildParticleRgba(t, 32));
    uint id = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, id);
    fixed (byte* p = img.Rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.GenerateMipmap(TextureTarget.Texture2D);
    weatherTexGl[idx] = id;
    return id;
}

// Lazily parse the level's particle effects + build a render instance per placed effect (resolved emitter at its world
// position, with its texture uploaded). Deferred off the load path (the effect-con parse reads many cons); built on the
// first enable of the Effects layer. Uses the SAME archive set as the mesh library so GLOBAL effects (buildingsmoke,
// locomotivesteam in objects.rfa) resolve too, not just the level's own FX/ folder.
void EnsureEffects()
{
    if (effectsLoaded || so is null || meshLib?.Textures is null) return;
    effectsLoaded = true;
    // Build from the level rfas + the mod/base mesh archives (effects live in both the level FX/ and objects.rfa).
    var paths = new System.Collections.Generic.List<string>();
    if (rfaList.Length > 0) paths.AddRange(rfaList.Where(File.Exists));
    else if (levelDir is not null && Directory.Exists(levelDir))
        try { paths.AddRange(Directory.EnumerateFiles(levelDir, "*.rfa", SearchOption.AllDirectories)); } catch { }
    paths.AddRange(meshArchives.Where(File.Exists));
    effectsLib = EffectsLibrary.FromRfaPaths(paths.Distinct(StringComparer.OrdinalIgnoreCase));
    fxInstances.Clear();
    var texCache = new System.Collections.Generic.Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
    var lib = meshLib.Textures;
    foreach (var o in so.Objects)
    {
        if (!effectsLib.TryResolve(o.Template, out var ems)) continue;
        var ow = LevelScene.MeshWorld(o);   // object's full world transform (pos/rot/scale)
        foreach (var em in ems)
        {
            if (!texCache.TryGetValue(em.Texture, out var tex))
            {
                var t2 = lib.Resolve(em.Texture);
                tex = t2 is not null ? UploadTexture(t2) : 0u;
                texCache[em.Texture] = tex;
            }
            if (tex == 0) continue;   // no texture -> skip (can't render)
            var world = Vector3.Transform(new Vector3(em.LocalPos.X, em.LocalPos.Y, em.LocalPos.Z), ow);
            fxInstances.Add(new FxInstance2 { World = world, Def = em, Tex = tex });
        }
    }
    Console.WriteLine($"Effects: {effectsLib.BundleCount} bundle(s), {fxInstances.Count} placed emitter(s) lit.");
}

// Advance every effect's particle simulation (spawn at the emitter rate, integrate velocity + gravity, age out). Only
// emitters within their LOD distance of the camera spawn/update, so far-off effects cost nothing.
void UpdateEffects(float dt)
{
    if (!showEffects || fxInstances.Count == 0) return;
    fxClock += dt;
    const int capPerEmitter = 220;
    var camp = cam.Position;
    foreach (var inst in fxInstances)
    {
        float lod = inst.Def.LodDistance > 1 ? inst.Def.LodDistance : 250f;
        bool near = Vector3.DistanceSquared(camp, inst.World) <= lod * lod;
        var parts = inst.Parts;
        // integrate + age existing particles (even when far, so they finish their arc)
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            var p = parts[i]; p.Age += dt;
            if (p.Age >= p.Ttl) { parts.RemoveAt(i); continue; }
            p.Pos += p.Vel * dt;
            p.Vel.Y -= inst.Def.Gravity * dt;
        }
        if (!near) { inst.Accum = 0; continue; }
        inst.Accum += inst.Def.Rate * dt;
        while (inst.Accum >= 1f && parts.Count < capPerEmitter)
        {
            inst.Accum -= 1f;
            // fan out using the emitter's OWN velocity deviation (the engine's spread), not an arbitrary jitter.
            var sd = inst.Def.Spread;
            var vel = inst.Def.Velocity + new Vector3((float)(fxRng.NextDouble() * 2 - 1) * sd.X,
                                                      (float)(fxRng.NextDouble() * 2 - 1) * sd.Y,
                                                      (float)(fxRng.NextDouble() * 2 - 1) * sd.Z);
            float rot = inst.Def.Spin ? (float)(fxRng.NextDouble() * MathF.PI * 2) : 0f;
            parts.Add(new FxParticle2 { Pos = inst.World, Vel = vel, Age = 0, Ttl = inst.Def.ParticleTtl, Size0 = inst.Def.Size, Size1 = inst.Def.SizeEnd, Rot = rot });
        }
    }
}

// Draw all live effect particles as camera-facing billboards. Additive emitters (fire/lava glow) drawn with one blend,
// alpha emitters (water/smoke) with another; depth-tested (terrain occludes) but depth-write off (particles don't occlude).
unsafe void DrawEffects()
{
    if (!showEffects || effectProg == 0 || fxInstances.Count == 0) return;
    // camera basis for billboard expansion
    var fwd = cam.Forward;
    var right = cam.Right;
    var up = Vector3.Normalize(Vector3.Cross(right, fwd));
    gl.UseProgram(effectProg);
    var vp = cam.ViewProjection;
    gl.UniformMatrix4(uEMvp, 1, false, (float*)&vp);
    gl.Uniform3(uERight, right.X, right.Y, right.Z);
    gl.Uniform3(uEUp, up.X, up.Y, up.Z);
    gl.Uniform1(uETex, 0);
    SetFogUniforms(effectProg);          // effects fade out across the fog band -> only visible within the view distance
    gl.ActiveTexture(TextureUnit.Texture0);
    gl.Enable(EnableCap.Blend);
    gl.DepthMask(false);
    gl.BindVertexArray(effectVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, effectVbo);
    // beyond the fog end nothing is visible (the shader fades to 0 there) - skip those instances entirely.
    float fogCull = fogEnabled ? fogEnd : float.MaxValue;
    var camp = cam.Position;
    // 6 verts/particle * 8 floats (center3 + corner2 + size + alpha + rot). Reused scratch grows as needed.
    static void Push(float[] a, ref int n, Vector3 c, float cx, float cy, float sz, float al, float rot)
    { a[n++] = c.X; a[n++] = c.Y; a[n++] = c.Z; a[n++] = cx; a[n++] = cy; a[n++] = sz; a[n++] = al; a[n++] = rot; }
    foreach (var inst in fxInstances)
    {
        if (inst.Parts.Count == 0) continue;
        if (Vector3.DistanceSquared(camp, inst.World) > fogCull * fogCull) continue;   // wholly past the view distance
        int need = inst.Parts.Count * 6 * 8;
        if (fxVerts.Length < need) fxVerts = new float[Math.Max(need, fxVerts.Length * 2)];
        int n = 0;
        foreach (var p in inst.Parts)
        {
            float f = p.Ttl > 0 ? p.Age / p.Ttl : 0;
            float size = float.Lerp(p.Size0, p.Size1, f);
            float alpha = MathF.Min(1f, (1f - f) * 1.6f);   // fade out over the last ~60% of life
            float rot = p.Rot;
            Push(fxVerts, ref n, p.Pos, -1, -1, size, alpha, rot); Push(fxVerts, ref n, p.Pos, 1, -1, size, alpha, rot); Push(fxVerts, ref n, p.Pos, 1, 1, size, alpha, rot);
            Push(fxVerts, ref n, p.Pos, -1, -1, size, alpha, rot); Push(fxVerts, ref n, p.Pos, 1, 1, size, alpha, rot); Push(fxVerts, ref n, p.Pos, -1, 1, size, alpha, rot);
        }
        gl.BlendFunc(BlendingFactor.SrcAlpha, inst.Def.Additive ? BlendingFactor.One : BlendingFactor.OneMinusSrcAlpha);
        gl.BindTexture(TextureTarget.Texture2D, inst.Tex);
        gl.Uniform3(uETint, 1f, 1f, 1f);
        fixed (float* pv = fxVerts) gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(n * sizeof(float)), pv, BufferUsageARB.StreamDraw);
        gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(n / 8));
    }
    gl.BindVertexArray(0);
    gl.DepthMask(true);
    gl.Disable(EnableCap.Blend);
    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
}

void DrawWeather()
{
    if (!showWeather || weatherPos.Length == 0 || weatherProg == 0) return;
    var k = WeatherKind();
    if (weatherVerts.Length != weatherPos.Length * 3) weatherVerts = new float[weatherPos.Length * 3];
    int o = 0;
    for (int i = 0; i < weatherPos.Length; i++)
    { var p = weatherPos[i]; weatherVerts[o++] = p.X; weatherVerts[o++] = p.Y; weatherVerts[o++] = p.Z; }
    weatherVertCount = weatherPos.Length;

    Vector3 col = k == RefractorForge.Formats.Con.WeatherType.Rain ? new Vector3(0.78f, 0.85f, 1.0f)
                : k == RefractorForge.Formats.Con.WeatherType.Dust ? new Vector3(0.85f, 0.78f, 0.55f)
                : k == RefractorForge.Formats.Con.WeatherType.DustStorm ? new Vector3(0.80f, 0.70f, 0.48f)
                : new Vector3(1f, 1f, 1f);
    float size = k == RefractorForge.Formats.Con.WeatherType.Rain ? 1.4f
               : k == RefractorForge.Formats.Con.WeatherType.Dust ? 1.1f
               : k == RefractorForge.Formats.Con.WeatherType.DustStorm ? 6f : 1.8f;

    gl.Enable(EnableCap.Blend);
    gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    gl.DepthMask(false);   // soft particles: blend, don't write depth (terrain still occludes via the depth test)
    gl.UseProgram(weatherProg);
    gl.UniformMatrix4(uWMvp, 1, false, ToFloats(cam.ViewProjection));
    gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, WeatherGlTex(k)); gl.Uniform1(uWTex, 0);
    gl.Uniform3(uWColor, col.X, col.Y, col.Z); gl.Uniform1(uWSize, size);
    gl.BindVertexArray(weatherVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, weatherVbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, weatherVerts, BufferUsageARB.DynamicDraw);
    gl.DrawArrays(PrimitiveType.Points, 0, (uint)weatherVertCount);
    gl.DepthMask(true);
    gl.Disable(EnableCap.Blend);
}

// Draw the active tool's gizmo for the selected object (always on top of the scene).
void DrawGizmos()
{
    if (selected < 0 || so is null) return;
    string t = toolNames[tool];
    if (t != "Move" && t != "Rotate" && t != "Scale") return;

    var gp = SelPos();
    float len = GizmoLen(gp);
    var mvp = ToFloats(cam.ViewProjection);
    var fb = window.FramebufferSize;
    var ray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fb.X, fb.Y);

    gl.UseProgram(markerProg);
    gl.UniformMatrix4(uMvpM, 1, false, mvp);
    gl.Disable(EnableCap.DepthTest);
    gl.LineWidth(2f);
    gl.Uniform1(uSize, 1f);

    if (t == "Move")
    {
        if (dragAxis < 0 && !UiWantsMouse()) hoverAxis = Gizmo.PickAxis(ray, gp, len, len * 0.18f);
        int act = dragAxis >= 0 ? dragAxis : hoverAxis;
        float[] g =
        {
            gp.X, gp.Y, gp.Z,  gp.X + len, gp.Y, gp.Z,
            gp.X, gp.Y, gp.Z,  gp.X, gp.Y + len, gp.Z,
            gp.X, gp.Y, gp.Z,  gp.X, gp.Y, gp.Z + len,
        };
        gl.BindVertexArray(gizmoVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gizmoVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, g, BufferUsageARB.DynamicDraw);
        gl.Uniform3(uColor, act == 0 ? 1f : 0.85f, act == 0 ? 0.95f : 0.22f, act == 0 ? 0.30f : 0.22f); gl.DrawArrays(PrimitiveType.Lines, 0, 2);
        gl.Uniform3(uColor, act == 1 ? 0.65f : 0.25f, act == 1 ? 1f : 0.85f, act == 1 ? 0.40f : 0.25f); gl.DrawArrays(PrimitiveType.Lines, 2, 2);
        gl.Uniform3(uColor, act == 2 ? 0.50f : 0.28f, act == 2 ? 0.75f : 0.45f, act == 2 ? 1f : 0.95f); gl.DrawArrays(PrimitiveType.Lines, 4, 2);
        gl.Uniform1(uSize, 11f); gl.Uniform3(uColor, 1f, 1f, 1f);
        gl.DrawArrays(PrimitiveType.Points, 1, 1);
        gl.DrawArrays(PrimitiveType.Points, 3, 1);
        gl.DrawArrays(PrimitiveType.Points, 5, 1);
    }
    else if (t == "Rotate")
    {
        if (rotDragChannel < 0 && !UiWantsMouse()) rotHover = Gizmo.PickRing(ray, gp, len, len * 0.14f);
        int act = rotDragChannel >= 0 ? rotDragChannel : rotHover;
        const int N = 48;
        var rv = new float[3 * N * 3];
        int w = 0;
        for (int c = 0; c < 3; c++)
        {
            var (axis, u, v) = Gizmo.RingFrame(c);
            for (int i = 0; i < N; i++)
            {
                float a = i * (MathF.PI * 2f / N);
                var p = gp + (MathF.Cos(a) * len) * u + (MathF.Sin(a) * len) * v;
                rv[w++] = p.X; rv[w++] = p.Y; rv[w++] = p.Z;
            }
        }
        gl.BindVertexArray(ringVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, ringVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, rv, BufferUsageARB.DynamicDraw);
        gl.Uniform3(uColor, act == 0 ? 0.65f : 0.25f, act == 0 ? 1f : 0.80f, act == 0 ? 0.40f : 0.30f); gl.DrawArrays(PrimitiveType.LineLoop, 0 * N, (uint)N);  // yaw  (Y)
        gl.Uniform3(uColor, act == 1 ? 1f : 0.80f, act == 1 ? 0.95f : 0.25f, act == 1 ? 0.35f : 0.25f); gl.DrawArrays(PrimitiveType.LineLoop, 1 * N, (uint)N);  // pitch(X)
        gl.Uniform3(uColor, act == 2 ? 0.50f : 0.28f, act == 2 ? 0.75f : 0.42f, act == 2 ? 1f : 0.92f); gl.DrawArrays(PrimitiveType.LineLoop, 2 * N, (uint)N);  // roll (Z)
    }
    else // Scale - single uniform handle at the object (grab near it on screen, drag radially)
    {
        float[] one = { gp.X, gp.Y, gp.Z };
        gl.BindVertexArray(gizmoVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, gizmoVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, one, BufferUsageARB.DynamicDraw);
        gl.Uniform1(uSize, scaleDragging ? 18f : 14f);
        gl.Uniform3(uColor, 1f, 0.85f, 0.25f);
        gl.DrawArrays(PrimitiveType.Points, 0, 1);
    }
    gl.Enable(EnableCap.DepthTest);
}

void DoUndo() { if (hist is null || so is null) return; hist.Undo(); selected = -1; multi.Clear(); gpIndex = -1; gpDragging = false; gpRotDragging = false; SyncMarkers(); RebuildObjects(); UploadMarkers(); UploadActivePaintTexture(); }
void DoRedo() { if (hist is null || so is null) return; hist.Redo(); selected = -1; multi.Clear(); gpIndex = -1; gpDragging = false; gpRotDragging = false; SyncMarkers(); RebuildObjects(); UploadMarkers(); UploadActivePaintTexture(); }
// When "Write LightmapShadowBits.lsb" is on, bake the sun cast-shadow into the engine's packed shadow format
// so the saved map's in-game lighting updates. gridDim is taken from the level's existing .lsb (the
// authoritative per-map grid: 8x8 or 4x4), defaulting to 8 if there's none. Shared by DoSave + DoSavePatch.
RefractorForge.Formats.Terrain.LightmapShadowBits? BakeShadowLsb()
{
    if (!writeShadowLsb || heightmap is null) return null;
    var es = EffectiveSun(); var sun = new Vec3(es.X, es.Y, es.Z);   // bake from the controllable editor sun
    // Prefer the level's existing .lsb grid (authoritative 8x8 / 4x4); else derive from map size (Irving 512 -> 8x8).
    int gridDim = Math.Clamp(cfg.MaterialSize / 64, 1, 16);
    if (levelDir is not null && System.IO.Directory.Exists(levelDir))
    {
        var existing = RefractorForge.Formats.Terrain.LightmapShadowBits.TryLoadFolder(levelDir);
        if (existing is { GridDim: > 0 }) gridDim = existing.GridDim;
    }
    Console.WriteLine($"Baking LightmapShadowBits.lsb ({gridDim}x{gridDim} grid{(shadowLsbFlipX ? ", flipX" : "")}{(shadowLsbFlipY ? ", flipY" : "")})...");
    return TerrainShadow.BakeToLsb(heightmap, cfg, sun, gridDim, flipX: shadowLsbFlipX, flipY: shadowLsbFlipY);
}

// The painted terrain atlas as in-memory txCxR.dds tile bytes (uncompressed BGRA DDS - the engine form).
// Used to inject painted surfaces into an .rfa save via extraFiles (folder saves write the same tiles straight
// to disk in SaveTextureTiles). Names are the bare leaf (tx{col}x{row}.dds); LevelSaver.FindEntry matches them
// to the archive's existing Textures/ entries. SplitToTiles only re-emits tiles that already exist, so every
// name resolves on a real level. Empty when nothing was texture-painted.
List<(string Name, byte[] Bytes)> PaintedTileBytes()
{
    var list = new List<(string Name, byte[] Bytes)>();
    if (!atlasPainted || atlasCpu is null || terrainTex is null) return list;
    foreach (var (fileName, tile) in terrainTex.SplitToTiles(atlasCpu))
        list.Add((fileName, DdsTexture.EncodeUncompressed(tile)));
    return list;
}

// Save the level, then launch BF1942 / BF Vietnam so the edits can be tested in the real engine. The client can't be
// CLI-jumped straight onto a map, so it launches the game (with the mod pre-selected via +game when detectable) and
// the user picks this map from the in-game list. Closes the loop on everything that's editor-only-verified.
void DoTestLevel()
{
    if (so is null || levelDir is null) { Toast(Loc.T("Load a level first.")); return; }
    DoSave();   // write the edits back so the game reads them
    string? gameRoot = null, modName = null;
    bool isBf1942 = gameIsBf1942;
    if (activeRfProject is { } proj)
    {
        // Project workflow: pack the extracted folder into a patch .rfa in the mod (or the project's GameTestDir),
        // then launch that game. The base .rfa (if any) stays as-is; the packed <Map>(_Patch).rfa mounts over it.
        gameRoot = !string.IsNullOrEmpty(proj.GameTestDir) ? proj.GameTestDir : proj.GameRoot;
        modName = proj.Mod;
        isBf1942 = !proj.Game.Equals("BFVietnam", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(gameRoot)) { Toast(Loc.T("Set the project's game install (Default) or GameTestDir (Custom) to test in-game.")); return; }
        try
        {
            string baseSub = isBf1942 ? "bf1942" : "BfVietnam";
            string map = proj.EffectiveMapName;
            string rfaName = map + (string.IsNullOrEmpty(proj.PatchNumber) ? "" : "_" + proj.PatchNumber) + ".rfa";
            string destDir = Path.Combine(gameRoot, "Mods", modName, "Archives", baseSub, "levels");
            Directory.CreateDirectory(destDir);
            string outRfa = Path.Combine(destDir, rfaName);
            int n = RefractorForge.Formats.LevelSaver.PackFolder(levelDir, outRfa, $"{baseSub}/levels/{map}/");
            Console.WriteLine($"Test This Level: packed {n} file(s) -> {outRfa}");
        }
        catch (Exception ex) { Toast(Loc.T("Pack for test failed: ") + ex.Message); return; }
    }
    else
    {
        // Classic (non-project): derive the game install by walking up for a Mods\ ancestor.
        try
        {
            var start = LevelArchive.IsRfa(levelDir) ? Path.GetDirectoryName(Path.GetFullPath(levelDir)) : Path.GetFullPath(levelDir);
            var d = start is null ? null : new DirectoryInfo(start);
            for (int i = 0; i < 12 && d is not null; i++)
            {
                if (d.Parent is not null && d.Parent.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
                { modName = d.Name; gameRoot = d.Parent.Parent?.FullName; break; }
                d = d.Parent;
            }
        }
        catch { }
    }
    if (gameRoot is null) { Toast(Loc.T("Couldn't find the game install (no Mods\\ ancestor) - launch the game yourself.")); return; }
    string exe = Path.Combine(gameRoot, isBf1942 ? "BF1942.exe" : "BfVietnam.exe");
    if (!File.Exists(exe)) { Toast($"Game exe not found: {Path.GetFileName(exe)} in {gameRoot}."); return; }
    try
    {
        string launchArgs = modName is not null ? $"+restart 1 +game mods/{modName}" : "+restart 1";
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, launchArgs) { WorkingDirectory = gameRoot, UseShellExecute = true });
        Toast($"Saved + launched {Path.GetFileName(exe)} (mod: {modName ?? "base"}). Pick this map in-game.");
        Console.WriteLine($"Test This Level: launched {exe} {launchArgs}");
    }
    catch (Exception ex) { Toast(Loc.T("Launch failed: ") + ex.Message); }
}

// Auto-backup: before a save overwrites the level, copy the editable level files (or the whole .rfa) to a
// timestamped folder under %APPDATA%\RefractorForge\Backups. CRITICAL: backups must NOT live inside the game's
// levels folder -- the game would try to mount the Backups dir as a level and fail to start. Folder levels skip
// bulky textures (.dds). Best-effort; a backup failure never blocks the save.
void AutoBackup()
{
    if (!autoBackup || string.IsNullOrEmpty(levelDir)) return;
    try
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RefractorForge", "Backups");
        if (LevelArchive.IsRfa(levelDir))
        {
            Directory.CreateDirectory(backupRoot);
            var dst = Path.Combine(backupRoot, $"{Path.GetFileNameWithoutExtension(levelDir)}_{stamp}{Path.GetExtension(levelDir)}");
            if (!File.Exists(dst)) File.Copy(levelDir, dst);
            Console.WriteLine($"Auto-backup -> {dst}");
            Toast($"Backed up {Path.GetFileName(levelDir)}");
        }
        else if (Directory.Exists(levelDir))
        {
            var name = new DirectoryInfo(levelDir.TrimEnd('\\', '/')).Name;
            var bdir = Path.Combine(backupRoot, $"{name}_{stamp}");
            int n = 0;
            foreach (var src in Directory.EnumerateFiles(levelDir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(src).ToLowerInvariant();
                if (ext is not (".con" or ".raw" or ".wst" or ".lsb" or ".ssc" or ".pal")) continue;   // editable level data only
                var rel = Path.GetRelativePath(levelDir, src);
                var dst = Path.Combine(bdir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true); n++;
            }
            if (n > 0) { Console.WriteLine($"Auto-backup: {n} level file(s) -> {bdir}"); Toast($"Backed up {n} level file(s)"); }
        }
    }
    catch (Exception ex) { Console.WriteLine($"Auto-backup skipped: {ex.Message}"); }
}

void DoSave()
{
    if (so is null) return;
    AutoBackup();
    try { DoSaveCore(); } catch (Exception ex) { Console.Error.WriteLine($"Save failed: {ex.Message}"); showLog = true; }
    // Project workflow: keep the .rfproj manifest + Recent Projects list current on every save.
    if (activeRfProject is not null)
        try { activeRfProject.Save(); RecentProjects.Touch(activeRfProject); } catch { }
}
void DoSaveCore()
{

    // Re-emit the painted terrain texture as txCxR.dds tiles (split the atlas back into the level's tile grid,
    // uncompressed DDS - the form the engine reads, same as the generated minimap). Folder levels only for now.
    void SaveTextureTiles()
    {
        if (!atlasPainted || atlasCpu is null || terrainTex is null) return;
        var dir = texturesDir ?? (levelDir is not null && System.IO.Directory.Exists(levelDir) ? System.IO.Path.Combine(levelDir, "Textures") : null);
        if (dir is null) { Console.WriteLine("   (texture paint not baked: .rfa level has no Textures dir -- save to a folder level)"); return; }
        try
        {
            System.IO.Directory.CreateDirectory(dir);
            int n = 0;
            foreach (var (fileName, tile) in terrainTex.SplitToTiles(atlasCpu))
            { System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, fileName), DdsTexture.EncodeUncompressed(tile)); n++; }
            Console.WriteLine($"   Baked {n} terrain texture tile(s) -> {dir}");
            atlasPainted = false;
        }
        catch (Exception ex) { Console.WriteLine($"   texture-tile save failed: {ex.Message}"); }
    }

    // Loaded from a folder: write the whole edited level back to disk (objects + terrain + material + gameplay).
    if (levelDir is not null && System.IO.Directory.Exists(levelDir))
    {
        var written = RefractorForge.Formats.LevelSaver.SaveFolder(levelDir, so, soPath, heightmap, materialMap, gameplayEdit, growth, BakeShadowLsb(), waterLevelEdited ? cfg : null);
        Console.WriteLine($"Saved level to {levelDir} ({written.Count} files):");
        foreach (var w in written) Console.WriteLine("   " + w);
        SaveTextureTiles();
        if (bakedObjectLightmaps.Count > 0)
        {
            try
            {
                var odir = System.IO.Directory.EnumerateDirectories(levelDir, "ObjectLightMaps", System.IO.SearchOption.AllDirectories).FirstOrDefault()
                        ?? System.IO.Path.Combine(levelDir, "ObjectLightMaps");
                System.IO.Directory.CreateDirectory(odir);
                foreach (var (name, bytes) in bakedObjectLightmaps) System.IO.File.WriteAllBytes(System.IO.Path.Combine(odir, name), bytes);
                Console.WriteLine($"   Wrote {bakedObjectLightmaps.Count} baked object lightmap(s) -> ObjectLightMaps/");
            }
            catch (Exception ex) { Console.WriteLine($"   object-lightmap save failed: {ex.Message}"); }
        }
        if (DetailDdsBytes() is { } detd) { try { var tdir = texturesDir ?? System.IO.Path.Combine(levelDir, "Textures"); System.IO.Directory.CreateDirectory(tdir); System.IO.File.WriteAllBytes(System.IO.Path.Combine(tdir, detd.Name), detd.Bytes); Console.WriteLine("   Wrote Textures/detail.dds (imported detail texture)."); } catch (Exception ex) { Console.WriteLine($"   detail.dds save failed: {ex.Message}"); } }
        ApplyWeatherToLevel();   // write Effects/RF_Weather.con + texture + Init run-include if weather is enabled
        SaveCloudsFolder();      // patch the animated-cloud block into SkyAndSun.con if clouds were edited
        // Write every vehicle whose navmap was painted (each buffer is held independently in aiNavBufs - no
        // save-first / no reseed loss). Each buffer carries its own side, robust to a mid-session map resize.
        // Target the level's REAL Pathfinding dir (it may sit in a sub-folder) so the edits land where the engine
        // loads them, not in a stray top-level Pathfinding/.
        string navParent = System.IO.Directory.EnumerateDirectories(levelDir, "Pathfinding", System.IO.SearchOption.AllDirectories).FirstOrDefault() is string pfDir
            ? (System.IO.Path.GetDirectoryName(pfDir) ?? levelDir) : levelDir;
        int navVeh = 0, navFiles = 0;
        for (int v = 0; v < aiNavBufs.Length; v++)
            if (aiNavBufDirty[v] && aiNavBufs[v] is not null)
            {
                var vp = RefractorForge.Formats.Terrain.SearchMapParams.Standard[Math.Clamp(v, 0, RefractorForge.Formats.Terrain.SearchMapParams.Standard.Count - 1)];
                int side = (int)Math.Round(Math.Sqrt(aiNavBufs[v]!.Length));
                navFiles += RefractorForge.Formats.Terrain.SearchMapGenerator.WriteVehicleEditedFolder(navParent, vp, aiNavBufs[v]!, side);
                aiNavBufDirty[v] = false; navVeh++;
            }
        if (navVeh > 0)
        {
            Console.WriteLine($"   Wrote {navFiles} edited AI navmap file(s) for {navVeh} vehicle(s) -> Pathfinding/");
            Toast($"Saved AI navmaps: {navVeh} vehicle(s), {navFiles} files.");
            aiNavDirty = false;
            PreviewSavedNav();   // re-read + show the saved map for a few seconds (verify)
        }
        // Skybox faces: same-named .dds overrides / the override .rs, written straight into the level folder.
        foreach (var (rel, bytes) in SkyFacePieces())
        {
            var pth = System.IO.Path.Combine(levelDir, rel.Replace('/', System.IO.Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(pth)!);
            System.IO.File.WriteAllBytes(pth, bytes);
            Console.WriteLine($"   Skybox face -> {rel}");
        }
        if (skyFaceAssign.Count > 0) skyFacesDirty = false;
        var sw = sounds.SaveDirty();
        if (sw.Count > 0) Console.WriteLine($"   Saved {sw.Count} sound script(s) (.ssc).");
        waterLevelEdited = false; waterLevelLoaded = cfg.WaterLevel;
        return;
    }
    // Loaded from a packed .rfa: PATCH-FIRST SAVE. The base archive is NEVER modified — repacking edits into the
    // base while _NNN patches stay mounted meant the patches kept overriding the user's saved files in-game (the
    // classic "my saved map is corrupted" trap: half the edits show, half silently revert), and it permanently
    // altered retail archives. Instead the save goes to <stem>_NNN.rfa beside the base: a NEW number when the
    // highest patch is retail/foreign, or REWRITING our own working patch (header fingerprint) on repeat saves.
    // The engine and the editor's auto-mount both layer it on top, so the saved state is exactly what loads.
    if (levelDir is not null && LevelArchive.IsRfa(levelDir))
    {
        string baseRfa = rfaList.Length > 0 ? rfaList[0] : levelDir;
        var sndScripts = sounds.DirtyScripts();
        var extras = new List<(string Name, byte[] Bytes)>(sndScripts);
        var tiles = PaintedTileBytes();                 // painted surface tiles -> into the patch
        extras.AddRange(tiles);
        if (DetailDdsBytes() is { } detd) extras.Add(detd);   // imported detail texture -> Textures/detail.dds (override)
        var navFiles = DirtyNavFiles();                 // painted AI navmaps -> into the patch
        extras.AddRange(navFiles);
        var (wxFiles, wxInit) = WeatherRfaPieces(baseRfa);    // weather: new Effects files + Init run-include
        if (wxInit is { } we) extras.Add(we);
        if (CloudMeshNewEntry() is { } cme) wxFiles.Add(cme);   // ship the imported cloud mesh
        if (CloudRfaExtra(baseRfa) is { } cx) { extras.Add(cx); cloudsDirty = false; }   // clouds -> patched SkyAndSun.con
        foreach (var (name, bytes) in bakedObjectLightmaps) wxFiles.Add(($"ObjectLightMaps/{name}", bytes));   // baked object lightmaps -> upsert (override existing OR add new)
        foreach (var sp in SkyFacePieces()) wxFiles.Add(sp);   // skybox face overrides: same-named .dds / override .rs (+ .bik copied beside the game)

        string outPatch = RefractorForge.Formats.LevelSaver.NextPatchPath(baseRfa);
        var names = RefractorForge.Formats.LevelSaver.WritePatchRfa(baseRfa, outPatch, so, heightmap, materialMap, gameplayEdit, growth, BakeShadowLsb(), waterLevelEdited ? cfg : null, extras, wxFiles);
        if (names.Count == 0) { Toast(Loc.T("Nothing changed - no patch written.")); return; }
        if (wxFiles.Count > 0) Console.WriteLine($"   Weather: added {wxFiles.Count} Effects file(s) to the patch (test in-game).");

        // POST-SAVE VALIDATION: decode every entry of the written patch with the independent engine-validated
        // decoder. A save that fails never reports success, and the dirty flags stay set.
        var verr = RefractorForge.Formats.Rfa.RefractorFlatArchive.Validate(outPatch);
        if (verr is not null)
        {
            Console.WriteLine($"SAVE VALIDATION FAILED for {outPatch}: {verr}");
            Toast(Loc.T("SAVE FAILED VALIDATION - do not use this file. See Log / Errors.")); showLog = true;
            return;
        }

        Console.WriteLine($"Saved patch {outPatch} ({names.Count} file(s); base archive untouched, verified OK):");
        foreach (var nm in names) Console.WriteLine("   " + nm);
        if (sndScripts.Count > 0) sounds.MarkAllSaved();
        // Only clear the dirty flag / report success for assets that ACTUALLY matched a base archive entry
        // (extraFiles silently drops names with no Textures//Pathfinding/ entry to override) -- so the editor
        // never claims a save it didn't make, and unmatched edits stay dirty for a folder save.
        int tileOk = tiles.Count > 0 ? tiles.Count(t => names.Any(n => n.EndsWith(t.Name, StringComparison.OrdinalIgnoreCase))) : 0;
        if (tileOk > 0) { atlasPainted = false; Console.WriteLine($"   Baked {tileOk} terrain texture tile(s) into the patch."); }
        else if (tiles.Count > 0) Console.WriteLine("   (painted surface tiles NOT saved: the base .rfa has no Textures/ tiles to override -- save to a folder level)");
        int navOk = navFiles.Count > 0 ? navFiles.Count(nf => names.Any(n => n.EndsWith(nf.Name, StringComparison.OrdinalIgnoreCase))) : 0;
        if (navOk > 0) { for (int v = 0; v < aiNavBufDirty.Length; v++) aiNavBufDirty[v] = false; aiNavDirty = false; Console.WriteLine($"   Baked {navOk} AI navmap file(s) into the patch."); PreviewSavedNav(); }
        else if (navFiles.Count > 0) Console.WriteLine("   (painted AI navmaps NOT saved: the base .rfa has no Pathfinding/ entries to override -- save to a folder level)");
        if (skyFaceAssign.Count > 0) skyFacesDirty = false;
        waterLevelEdited = false; waterLevelLoaded = cfg.WaterLevel;
        Toast(string.Format(Loc.T("Saved patch {0} ({1} files) - verified OK, base untouched."), Path.GetFileName(outPatch), names.Count));
        return;
    }
    // Otherwise (explicit path / no folder): fall back to a loose StaticObjects.con beside the source.
    if (soPath is not null) { so.Save(soPath); Console.WriteLine($"Saved {so.Objects.Count} objects -> {soPath}"); }
}

// Save edits as a PATCH .rfa to a user-chosen path: a small archive of only the changed files, named with the
// base's exact entry paths so the engine mounts it OVER the base (later archives win). The base archive is left
// untouched. serverSideOnly = an SSM (server-side mod) patch: client-only content (textures/sounds/movies/baked
// light) is stripped so the patch carries only what a dedicated server needs — clients never download it.
void DoSavePatch(bool serverSideOnly = false)
{
    if (so is null) return;
    string? baseRfa = rfaList.Length > 0 ? rfaList[0]
                    : (levelDir is not null && LevelArchive.IsRfa(levelDir) ? levelDir : null);
    if (baseRfa is null) { Toast(Loc.T("Save as Patch needs an .rfa-loaded level (folder levels save in place with Ctrl+S).")); return; }
    var dir = Path.GetDirectoryName(Path.GetFullPath(baseRfa)) ?? ".";
    var defName = Path.GetFileName(RefractorForge.Formats.LevelSaver.NextPatchPath(baseRfa));
    var outPath = Picker.Save(serverSideOnly ? Loc.T("Save SSM Patch .rfa (server-side files only)") : Loc.T("Save Patch .rfa (only edited files)"),
                              "RFA archives|*.rfa|All files|*.*", defName, dir);
    if (outPath is null) return;
    try
    {
        var sndScripts = sounds.DirtyScripts();
        var extras = new List<(string Name, byte[] Bytes)>(sndScripts);
        var tiles = PaintedTileBytes();                 // painted surface tiles -> into the patch
        extras.AddRange(tiles);
        if (DetailDdsBytes() is { } detd) extras.Add(detd);   // imported detail texture -> Textures/detail.dds (override)
        var navFiles = DirtyNavFiles();                 // painted AI navmaps -> into the patch
        extras.AddRange(navFiles);
        var (wxFiles, wxInit) = WeatherRfaPieces(baseRfa);   // weather: new Effects files + Init run-include
        if (wxInit is { } we) extras.Add(we);
        if (CloudMeshNewEntry() is { } cme) wxFiles.Add(cme);   // ship the imported cloud mesh
        if (CloudRfaExtra(baseRfa) is { } cx) { extras.Add(cx); cloudsDirty = false; }   // clouds -> patched SkyAndSun.con
        foreach (var sp in SkyFacePieces()) wxFiles.Add(sp);   // skybox face overrides
        var names = RefractorForge.Formats.LevelSaver.WritePatchRfa(baseRfa, outPath, so, heightmap, materialMap, gameplayEdit, growth, BakeShadowLsb(), waterLevelEdited ? cfg : null, extras, wxFiles, serverSideOnly: serverSideOnly);
        if (wxFiles.Count > 0) Console.WriteLine($"   Weather: added {wxFiles.Count} Effects file(s) to the patch (test in-game).");
        if (names.Count == 0) { Toast(Loc.T("Nothing edited yet -- no patch written.")); return; }
        var verr = RefractorForge.Formats.Rfa.RefractorFlatArchive.Validate(outPath);
        if (verr is not null)
        {
            Console.WriteLine($"SAVE VALIDATION FAILED for {outPath}: {verr}");
            Toast(Loc.T("SAVE FAILED VALIDATION - do not use this file. See Log / Errors.")); showLog = true;
            return;
        }
        if (sndScripts.Count > 0) sounds.MarkAllSaved();
        // Clear dirty only for assets that actually matched a base entry (see DoSave); unmatched stay dirty.
        if (tiles.Count > 0 && tiles.Any(t => names.Any(n => n.EndsWith(t.Name, StringComparison.OrdinalIgnoreCase)))) atlasPainted = false;
        if (navFiles.Count > 0 && navFiles.Any(nf => names.Any(n => n.EndsWith(nf.Name, StringComparison.OrdinalIgnoreCase)))) { for (int v = 0; v < aiNavBufDirty.Length; v++) aiNavBufDirty[v] = false; aiNavDirty = false; }
        Toast(string.Format(Loc.T("{0} patch: {1} file(s) -> {2} (verified OK)"), serverSideOnly ? "SSM" : "Map", names.Count, Path.GetFileName(outPath)));
        Console.WriteLine($"Patch {outPath} ({(serverSideOnly ? "server-side only" : "full")}, verified OK):");
        foreach (var nm in names) Console.WriteLine("   " + nm);
        waterLevelEdited = false; waterLevelLoaded = cfg.WaterLevel;
    }
    catch (Exception ex) { Toast(Loc.T("Patch save failed: ") + ex.Message); }
}

// ADVANCED, explicit-only: repack the edited files INTO the base archive itself. Ctrl+S deliberately never does
// this any more — an edited base under mounted _NNN patches gets overridden in-game, and retail archives should
// stay pristine. This remains for the rare "I really want a single self-contained .rfa" case.
void DoRepackBaseInPlace()
{
    if (so is null || levelDir is null || !LevelArchive.IsRfa(levelDir)) { Toast(Loc.T("Only for .rfa-loaded levels.")); return; }
    if (rfaList.Length > 1)
        Toast(Loc.T("Warning: patches are mounted over this base - they will keep overriding the repacked files in-game."));
    try
    {
        var sndScripts = sounds.DirtyScripts();
        var extras = new List<(string Name, byte[] Bytes)>(sndScripts);
        extras.AddRange(PaintedTileBytes());
        if (DetailDdsBytes() is { } detd) extras.Add(detd);
        extras.AddRange(DirtyNavFiles());
        var (wxFiles, wxInit) = WeatherRfaPieces(levelDir);
        if (wxInit is { } we) extras.Add(we);
        foreach (var (name, bytes) in bakedObjectLightmaps) wxFiles.Add(($"ObjectLightMaps/{name}", bytes));
        var names = RefractorForge.Formats.LevelSaver.RepackToRfa(levelDir, levelDir, so, heightmap, materialMap, gameplayEdit, growth, BakeShadowLsb(), waterLevelEdited ? cfg : null, extras, wxFiles);
        var verr = RefractorForge.Formats.Rfa.RefractorFlatArchive.Validate(levelDir);
        if (verr is not null) { Console.WriteLine($"REPACK VALIDATION FAILED: {verr}"); Toast(Loc.T("SAVE FAILED VALIDATION - do not use this file. See Log / Errors.")); showLog = true; return; }
        if (sndScripts.Count > 0) sounds.MarkAllSaved();
        Toast(string.Format(Loc.T("Repacked {0} file(s) into the base archive (verified OK)."), names.Count));
        Console.WriteLine($"Re-packed {names.Count} edited file(s) into {levelDir} (verified OK).");
    }
    catch (Exception ex) { Toast(Loc.T("Repack failed: ") + ex.Message); }
}

// Generate the level's minimap (ingame HUD map) + menu thumbnail from the current heightmap/terrain.
// Folder levels: written into Textures/InGameMap.dds + Menu/Thumbnail.dds. Packed .rfa: written loose
// beside the archive (matching the StaticObjects.con fallback convention) since editing the archive in
// place for image assets isn't wired yet.
void DoGenerateMinimap()
{
    if (heightmap is null) return;
    var ingame = Minimap.Render(512, heightmap, cfg, terrainTex, materialMap);
    var thumb = Minimap.Render(256, heightmap, cfg, terrainTex, materialMap);
    string? a = null, b = null, c = null;
    if (levelDir is not null && System.IO.Directory.Exists(levelDir))
    {
        var texDir = System.IO.Directory.EnumerateDirectories(levelDir, "Textures", System.IO.SearchOption.AllDirectories).FirstOrDefault()
                     ?? System.IO.Path.Combine(levelDir, "Textures");
        var menuDir = System.IO.Directory.EnumerateDirectories(levelDir, "Menu", System.IO.SearchOption.AllDirectories).FirstOrDefault()
                     ?? System.IO.Path.Combine(levelDir, "Menu");
        System.IO.Directory.CreateDirectory(texDir);
        System.IO.Directory.CreateDirectory(menuDir);
        a = System.IO.Path.Combine(texDir, "InGameMap.dds");
        b = System.IO.Path.Combine(menuDir, "Thumbnail.dds");
        // Menu/Briefing.dds is the map picture on the briefing screen. Battlecraft writes it alongside the
        // thumbnail (retail ships 256x256 uncompressed BGRA here, 64x64 for the thumbnail); we had never written
        // it, so an edited map kept the original author's briefing art showing the pre-edit terrain.
        c = System.IO.Path.Combine(menuDir, "Briefing.dds");
    }
    else if (levelDir is not null)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(levelDir)) ?? ".";
        var name = System.IO.Path.GetFileNameWithoutExtension(levelDir);
        a = System.IO.Path.Combine(dir, name + ".InGameMap.dds");
        b = System.IO.Path.Combine(dir, name + ".Thumbnail.dds");
        c = System.IO.Path.Combine(dir, name + ".Briefing.dds");
    }
    if (a is not null && b is not null)
    {
        DdsTexture.Save(ingame, a);
        DdsTexture.Save(thumb, b);
        Console.WriteLine($"Minimap -> {a}");
        Console.WriteLine($"        -> {b}");
        if (c is not null) { DdsTexture.Save(thumb, c); Console.WriteLine($"        -> {c}"); }
    }
}

// Save the level's AI navmaps to where the ENGINE actually loads them. The user's PAINTED maps (aiNavBufs) win:
// every painted vehicle is written; if nothing is painted yet, every vehicle is generated from terrain+objects
// (the fresh-map case). Writes BOTH forms (engine compressed <Veh>Level<L>Map.raw + editor 8Bit
// <Veh>Level<L>Map8Bit.raw). Folder levels -> the level's real Pathfinding/ dir; packed .rfa -> INTO the archive
// (overriding the base navmaps the engine reads). PREVIOUSLY this always regenerated from terrain (discarding
// hand-painted edits) and, for an .rfa, wrote a loose <name>_Pathfinding folder the game never reads -- so
// painted AI paths never reached the game. That is the bug this fixes.
// NOTE: the per-vehicle companions <Veh>.raw (128^2 region map) + <Veh>Info.raw (SAI region graph) are NOT yet
// written (see pathfinding-re-map memory) - pathfinding A* uses the Level maps; companions are SAI-only.
void DoGenerateNavmaps()
{
    if (heightmap is null) return;
    var foots = meshLib is not null && so is not null
        ? RefractorForge.Render.SearchMapBuilder.Footprints(so.Objects, meshLib)
        : null;
    var std = RefractorForge.Formats.Terrain.SearchMapParams.Standard;

    // Per vehicle, in strict priority order. The rule is that the editor NEVER invents a navmap over one the level
    // already ships: retail and hand-tuned maps carry designer intent (bridges, ramps, deliberately blocked alleys)
    // that a terrain-derived regeneration silently destroys, and the generator is a from-scratch slope/height
    // approximation, not the engine's. So generation is the last resort, for a vehicle the level has NO map for.
    //   1. painted in this session -> write the painted buffer
    //   2. the level ships one     -> leave it completely alone (not even re-encoded; byte-identical is the point)
    //   3. neither                 -> generate from terrain (a fresh/new map, which genuinely has none)
    int want = RefractorForge.Formats.Terrain.SearchMapGenerator.FinestSide(cfg.MaterialSize);
    var files = new List<(string Name, byte[] Bytes)>();
    int painted = 0, generated = 0, kept = 0;
    for (int v = 0; v < std.Count; v++)
    {
        var vp = std[v];
        if (v < aiNavBufs.Length && aiNavBufDirty[v] && aiNavBufs[v] is not null)
        {
            int side = (int)Math.Round(Math.Sqrt(aiNavBufs[v]!.Length));
            foreach (var f in RefractorForge.Formats.Terrain.SearchMapGenerator.EncodeVehicleLevels(vp, aiNavBufs[v]!, side))
                files.Add((f.FileName, f.Data));
            painted++;
            continue;
        }
        if (RefractorForge.Formats.Terrain.PathmapRaw.LoadVehicleWorldGrid(ReadLevelNavFile, vp, want) is not null) { kept++; continue; }
        var grid = RefractorForge.Formats.Terrain.SearchMapGenerator.GenerateGrid(cfg, heightmap, vp, 0, foots);
        foreach (var f in RefractorForge.Formats.Terrain.SearchMapGenerator.EncodeVehicleLevels(vp, grid, want))
            files.Add((f.FileName, f.Data));
        generated++;
    }
    if (files.Count == 0)
    {
        // Nothing to do is the NORMAL outcome on a finished map: every vehicle already has a navmap and none was
        // painted. Say so plainly rather than writing a regenerated set nobody asked for.
        Console.WriteLine($"AI navmaps: nothing to write - the level already ships maps for all {kept} vehicle(s) and none were painted.");
        Toast(string.Format(Loc.T("The level already ships AI navmaps for all {0} vehicles - nothing regenerated. Paint one to change it."), kept));
        return;
    }
    var parts = new List<string>();
    if (painted > 0) parts.Add($"{painted} painted");
    if (generated > 0) parts.Add($"{generated} generated (level shipped none)");
    if (kept > 0) parts.Add($"{kept} left as shipped");
    string what = string.Join(", ", parts);

    // Packed .rfa: write the navmaps INTO the archive (override the base entries the engine loads), exactly like
    // Ctrl+S. extraFiles silently drops names the base doesn't ship, so a map without Pathfinding/ entries needs
    // a folder save -- report that honestly instead of claiming success.
    if (levelDir is not null && LevelArchive.IsRfa(levelDir))
    {
        var names = RefractorForge.Formats.LevelSaver.RepackToRfa(levelDir, levelDir, null, null, null, null, extraFiles: files);
        int navOk = files.Count(nf => names.Any(n => n.EndsWith(nf.Name, StringComparison.OrdinalIgnoreCase)));
        if (navOk > 0)
        {
            for (int v = 0; v < aiNavBufDirty.Length; v++) aiNavBufDirty[v] = false; aiNavDirty = false;
            Console.WriteLine($"Saved {navOk} AI navmap file(s) into {levelDir} ({what}).");
            Toast($"Saved AI navmaps into the .rfa: {navOk} files ({what}).");
            PreviewSavedNav();
        }
        else
        {
            Console.WriteLine("   (AI navmaps NOT saved: this .rfa ships no Pathfinding/ entries to override -- save to a folder level).");
            Toast(Loc.T("AI navmaps NOT saved: this .rfa has no Pathfinding entries. Extract to a folder level and save there."));
        }
        return;
    }

    // Folder level: write into the level's REAL Pathfinding/ dir (it may be in a sub-folder).
    string? navDir = (levelDir is not null && System.IO.Directory.Exists(levelDir))
        ? (System.IO.Directory.EnumerateDirectories(levelDir, "Pathfinding", System.IO.SearchOption.AllDirectories).FirstOrDefault()
           ?? System.IO.Path.Combine(levelDir, "Pathfinding"))
        : null;
    if (navDir is null) { Toast(Loc.T("Open a level first.")); return; }
    System.IO.Directory.CreateDirectory(navDir);
    foreach (var (file, data) in files) System.IO.File.WriteAllBytes(System.IO.Path.Combine(navDir, file), data);
    for (int v = 0; v < aiNavBufDirty.Length; v++) aiNavBufDirty[v] = false; aiNavDirty = false;
    Console.WriteLine($"Saved {files.Count} AI navmap file(s) -> {navDir} ({what}).");
    Toast($"Saved {files.Count} AI navmaps ({what}).");
    PreviewSavedNav();
}

// ---- AI Pathmap preview: decode a saved/opened Pathfinding .raw to an image so a save can be verified natively. ----

// Upload a WORLD-GRID map (0x00 pass / 0xFF block) as a grayscale RGBA texture (black=passable, white=blocked,
// matching the AI Path painter + raw2tga) and open the preview window. seconds>0 auto-closes after that many.
unsafe void ShowPathmap(byte[] worldGrid, int side, string label, float seconds)
{
    if (side <= 0 || worldGrid.Length != side * side) return;
    byte[] g = worldGrid; int ts = side;   // downsample big maps (up to 4096 sq) so the preview texture stays modest
    while (ts > 1024 && ts % 2 == 0) { g = RefractorForge.Formats.Terrain.SearchMapGenerator.DownsampleBlocked(g, ts, ts / 2); ts /= 2; }
    var rgba = new byte[ts * ts * 4];
    for (int i = 0; i < ts * ts; i++) { byte v = g[i] == 0xFF ? (byte)230 : (byte)32; rgba[i * 4] = v; rgba[i * 4 + 1] = v; rgba[i * 4 + 2] = v; rgba[i * 4 + 3] = 255; }
    if (pathmapTex == 0) pathmapTex = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, pathmapTex);
    gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    fixed (byte* p = rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)ts, (uint)ts, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    gl.BindTexture(TextureTarget.Texture2D, 0);
    pathmapPreviewSide = side; pathmapPreviewLabel = label; pathmapPreviewOpen = true; pathmapPreviewT = seconds;
}

// Decode a pathmap .raw's bytes (compressed engine form or 8Bit form) to a WORLD-GRID map for display (un-rotating
// the on-disk nav orientation back to the painter's map orientation). Null if unrecognizable.
byte[]? DecodePathmapToWorld(byte[] data, string nameHint, out int side)
{
    side = 0;
    try
    {
        var navOriented = RefractorForge.Formats.Terrain.PathmapRaw.Load(data, nameHint, out side);
        return RefractorForge.Formats.Terrain.SearchMapGenerator.UnemitNav(navOriented, side);
    }
    catch { return null; }
}

// File > Open AI Pathmap: pick a Pathfinding .raw (compressed or 8Bit) and show it natively -- the native
// equivalent of the community Import_Pathfind + raw2tga tools.
void OpenPathmapFile()
{
    var path = Picker.File("Open AI Pathmap (.raw)", "AI pathmaps (*.raw)|*.raw|All files (*.*)|*.*", levelDir);
    if (path is null) return;
    try
    {
        var world = DecodePathmapToWorld(System.IO.File.ReadAllBytes(path), System.IO.Path.GetFileName(path), out int side);
        if (world is null) { Toast(Loc.T("Not a recognizable AI pathmap .raw.")); return; }
        ShowPathmap(world, side, System.IO.Path.GetFileName(path), 0f);
    }
    catch (Exception ex) { Toast(Loc.T("Open pathmap failed: ") + ex.Message); }
}

// After a nav save, RE-READ the active vehicle's just-saved map (from the .rfa or the Pathfinding folder) and show
// it for a few seconds so the save can be verified against what was painted. Best-effort (never throws into save).
void PreviewSavedNav()
{
    try
    {
        if (levelDir is null) return;
        int vi = Math.Clamp(aiPathVeh, 0, RefractorForge.Formats.Terrain.SearchMapParams.Standard.Count - 1);
        var vp = RefractorForge.Formats.Terrain.SearchMapParams.Standard[vi];
        int finest = vp.LevelSet.Min();
        string comp = $"{vp.Name}Level{finest}Map.raw", eight = $"{vp.Name}Level{finest}Map8Bit.raw";
        byte[]? data = null; string used = comp;

        if (LevelArchive.IsRfa(levelDir))
        {
            var a = new RefractorForge.Formats.Rfa.RefractorFlatArchive(levelDir);
            var ce = a.Entries.FirstOrDefault(x => x.Name.EndsWith(comp, StringComparison.OrdinalIgnoreCase) && !x.Name.EndsWith("8Bit.raw", StringComparison.OrdinalIgnoreCase));
            if (ce is not null) { data = a.Read(ce); used = comp; }
            else { var ee = a.Entries.FirstOrDefault(x => x.Name.EndsWith(eight, StringComparison.OrdinalIgnoreCase)); if (ee is not null) { data = a.Read(ee); used = eight; } }
        }
        else if (System.IO.Directory.Exists(levelDir))
        {
            var navDir = System.IO.Directory.EnumerateDirectories(levelDir, "Pathfinding", System.IO.SearchOption.AllDirectories).FirstOrDefault()
                         ?? System.IO.Path.Combine(levelDir, "Pathfinding");
            var pe = System.IO.Path.Combine(navDir, eight); var pc = System.IO.Path.Combine(navDir, comp);
            if (System.IO.File.Exists(pe)) { data = System.IO.File.ReadAllBytes(pe); used = eight; }
            else if (System.IO.File.Exists(pc)) { data = System.IO.File.ReadAllBytes(pc); used = comp; }
        }
        if (data is null) return;
        var world = DecodePathmapToWorld(data, used, out int side);
        if (world is not null) ShowPathmap(world, side, $"{vp.Name} L{finest}  -  saved & re-read from disk", 6f);
    }
    catch { }
}

// The preview window itself (drawn each frame from BuildUi while open).
void PathmapPreviewWindow()
{
    if (!pathmapPreviewOpen || pathmapTex == 0) return;
    ImGui.SetNextWindowSize(new Vector2(560, 640), ImGuiCond.FirstUseEver);
    if (ImGui.Begin(Loc.TL("AI Pathmap Preview"), ref pathmapPreviewOpen, ImGuiWindowFlags.NoScrollbar))
    {
        ImGui.TextColored(new Vector4(0.86f, 0.55f, 0.55f, 1f), pathmapPreviewLabel);
        ImGui.Text($"{pathmapPreviewSide} x {pathmapPreviewSide} cells   black = passable, white = blocked");
        if (pathmapPreviewT > 0f) { ImGui.SameLine(); ImGui.TextDisabled($"(auto-close {pathmapPreviewT:0.#}s)"); }
        float sz = MathF.Min(ImGui.GetContentRegionAvail().X, 512f);
        // North (+Z = high world-grid row) at the TOP, matching the minimap + the in-game map (V-flip the texture).
        ImGui.Image((IntPtr)pathmapTex, new Vector2(sz, sz), new Vector2(0f, 1f), new Vector2(1f, 0f));
    }
    ImGui.End();
}

// Auto material map from terrain (water line / slope / altitude) - the editor's "Generate Material Map".
void DoGenerateMaterialMap()
{
    if (heightmap is null) { Toast(Loc.T("No terrain loaded.")); return; }
    materialMap = RefractorForge.Formats.Terrain.MaterialMapGenerator.FromTerrain(cfg, heightmap);
    matPainter = new MaterialPainter(materialMap, cfg);
    if (paintLayer == 0) UploadActivePaintTexture();
    Console.WriteLine($"Generated material map {materialMap.Width}^2 from terrain (slope/height/water).");
    Toast($"Generated material map ({materialMap.Width}^2) from terrain.");
}

// Bake the surface atlas from the material map + the 16-slot texture set - the editor's "Generate Surface Maps".
void DoGenerateSurfaceMaps()
{
    if (materialMap is null) { Toast(Loc.T("Generate or load a material map first.")); return; }
    if (atlasCpu is null) { Toast(Loc.T("This level has no terrain texture atlas to bake into.")); return; }
    Console.WriteLine("Baking surface atlas from the material map + texture set...");
    atlasCpu = RefractorForge.Render.TerrainTexture.BakeAtlasFromMaterial(materialMap, texPalette, matToSurf, atlasCpu.Width, cfg.WorldSize, texTileMeters);
    atlasPainted = true;
    UploadAtlasRectMips(0, 0, atlasCpu.Width, atlasCpu.Height);
    Toast(Loc.T("Baked surface atlas from the material map + set. Ctrl+S writes the tiles."));
}

// Convert a single TGA to an uncompressed BGRA DDS the game reads - a built-in modern TGA->DDS converter (BF texture
// work historically needed an external tool). Reuses the proven TgaTexture decoder + DdsTexture uncompressed writer.
void DoConvertTgaToDds()
{
    var src = Picker.File("Choose a TGA image to convert", "TGA images (*.tga)|*.tga|All files|*.*", levelDir);
    if (src is null) return;
    Texture2D? tex;
    try { tex = TgaTexture.Decode(File.ReadAllBytes(src)); }
    catch (Exception ex) { Toast(Loc.T("TGA read failed: ") + ex.Message); return; }
    if (tex is null) { Toast(Loc.T("Unsupported TGA (colour-mapped or unreadable).")); return; }
    var dst = Picker.Save("Save DDS", "DDS texture (*.dds)|*.dds", Path.GetFileNameWithoutExtension(src) + ".dds", Path.GetDirectoryName(src));
    if (dst is null) return;
    try { DdsTexture.Save(tex, dst); Toast($"Converted -> {Path.GetFileName(dst)} ({tex.Width}x{tex.Height})."); }
    catch (Exception ex) { Toast(Loc.T("DDS write failed: ") + ex.Message); }
}

// Batch the whole folder: every .tga (recursive) -> a sibling .dds. For converting a dropped-in texture pack at once.
void DoBatchTgaToDds()
{
    var folder = Picker.Folder("Choose a folder of TGAs to convert to DDS (recursive)", levelDir);
    if (folder is null) return;
    int ok = 0, fail = 0;
    foreach (var f in Directory.EnumerateFiles(folder, "*.tga", SearchOption.AllDirectories))
    {
        try { var t = TgaTexture.Decode(File.ReadAllBytes(f)); if (t is null) { fail++; continue; } DdsTexture.Save(t, Path.ChangeExtension(f, ".dds")); ok++; }
        catch { fail++; }
    }
    Toast($"TGA->DDS: {ok} converted{(fail > 0 ? $", {fail} skipped" : "")} under {Path.GetFileName(folder)}.");
}

// Build a GPU shadow texture from the level's stored LightmapShadowBits (its baked terrain sun-shadow). The .lsb is a
// GridDim x GridDim grid of 1024px tiles (up to ~8192 sq); downsample to <=2048 for display. Same UV orientation as the
// terrain shader's uShadow (texel (x,y) -> world (x/size*ws, y/size*ws)), so it lines up with the ground. Honours the
// File-menu .lsb flip X/Y toggles so a mirrored map can be corrected.
Texture2D? ShadowTextureFromLsb(LightmapShadowBits lsb)
{
    int side; byte[] vis;
    try { vis = lsb.ToVisibility(out side); } catch { return null; }
    if (side <= 0 || vis.Length < (long)side * side) return null;
    int target = Math.Min(side, 2048);
    var rgba = new byte[target * target * 4];
    for (int y = 0; y < target; y++)
        for (int x = 0; x < target; x++)
        {
            int sx = (int)((long)x * side / target), sy = (int)((long)y * side / target);
            if (shadowLsbFlipX) sx = side - 1 - sx;
            if (shadowLsbFlipY) sy = side - 1 - sy;
            byte v = vis[sy * side + sx];
            int o = (y * target + x) * 4;
            rgba[o] = v; rgba[o + 1] = v; rgba[o + 2] = v; rgba[o + 3] = 255;
        }
    return new Texture2D(target, target, rgba);
}

// The effective sun direction (points TOWARD the sun): the user's azimuth/elevation override when on, else the level's
// SkyAndSun.con direction (default fallback if absent/zero). Drives terrain + object lighting AND the shadow map.
Vector3 EffectiveSun()
{
    if (sunOverride)
    {
        float az = sunAzimuthDeg * MathF.PI / 180f, el = sunElevationDeg * MathF.PI / 180f;
        var d = new Vector3(MathF.Cos(el) * MathF.Sin(az), MathF.Sin(el), MathF.Cos(el) * MathF.Cos(az));
        return d.LengthSquared() < 1e-6f ? new Vector3(0, 1, 0) : Vector3.Normalize(d);
    }
    var s = env is not null ? new Vector3(env.SunDirection.X, env.SunDirection.Y, env.SunDirection.Z) : new Vector3(-0.5f, 0.8f, -0.35f);
    return s.LengthSquared() < 1e-6f ? Vector3.Normalize(new Vector3(-0.5f, 0.8f, -0.35f)) : Vector3.Normalize(s);
}

// Lazily create the sun shadow-map depth target (a depth-only FBO).
unsafe void EnsureShadowMap()
{
    if (shadowMapFbo != 0) return;
    shadowMapDepthTex = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, shadowMapDepthTex);
    gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, shadowMapSize, shadowMapSize, 0, PixelFormat.DepthComponent, PixelType.Float, (void*)null);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    shadowMapFbo = gl.GenFramebuffer();
    gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowMapFbo);
    gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, shadowMapDepthTex, 0);
    gl.DrawBuffer(DrawBufferMode.None);   // depth-only: no colour attachment
    gl.ReadBuffer(ReadBufferMode.None);
    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
}

// Where to centre the shadow map: the ground point the camera is looking at (forward ray to terrain height), else
// straight under the camera. Centring the (limited-resolution) shadow frustum on the working area is what keeps the
// shadows SHARP where you're editing instead of blocky over the whole map.
Vector3 ShadowFocus()
{
    var p = cam.Position; var f = cam.Forward;
    float gy = terrainPick is not null ? terrainPick.HeightAt(p.X, p.Z) : (minH + maxH) * 0.5f;
    float t = MathF.Abs(f.Y) > 0.05f ? (gy - p.Y) / f.Y : -1f;
    var g = (t > 1f && t < cfg.WorldSize) ? p + f * t : new Vector3(p.X, gy, p.Z);
    return new Vector3(g.X, gy, g.Z);
}

// Half-extent of the shadow frustum, adapted to zoom: tight (sharp) when low/close, wide (whole map) when high up.
float ShadowRadius() => Math.Clamp(Altitude() * 1.1f, 150f, cfg.WorldSize * 0.72f);

// Orthographic light-space matrix for a focus box of half-extent `radius` from the sun direction. A tight box around
// the camera gives far more shadow-map texels per metre than covering the whole terrain at once.
Matrix4x4 ComputeLightSpace(Vector3 sun, Vector3 focus, float radius)
{
    var sd = Vector3.Normalize(sun);
    var up = MathF.Abs(sd.Y) > 0.97f ? Vector3.UnitZ : Vector3.UnitY;   // avoid degenerate LookAt near the zenith
    float dist = radius * 3f + (maxH - minH) + 100f;                    // place the light outside the focus box
    var view = Matrix4x4.CreateLookAt(focus + sd * dist, focus, up);
    var proj = Matrix4x4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 1f, dist + radius + (maxH - minH) + 100f);
    return view * proj;
}

// Render the terrain + nearby objects into the shadow map from the sun's POV (depth only), centred on `focus` with
// half-extent `radius`. Re-run only when the sun, geometry, or the focus/zoom changed - not every frame.
unsafe void RenderShadowMap(Vector3 sun, Vector3 focus, float radius)
{
    if (heightmap is null || terrainVao == 0 || depthProg == 0) return;
    EnsureShadowMap();
    lightSpace = ComputeLightSpace(sun, focus, radius);
    gl.BindFramebuffer(FramebufferTarget.Framebuffer, shadowMapFbo);
    gl.Viewport(0, 0, shadowMapSize, shadowMapSize);
    gl.Clear(ClearBufferMask.DepthBufferBit);
    gl.Enable(EnableCap.DepthTest);
    gl.Disable(EnableCap.CullFace);
    gl.UseProgram(depthProg);
    var lsp = lightSpace; gl.UniformMatrix4(uLightSpaceD, 1, false, (float*)&lsp);
    var id = Matrix4x4.Identity; gl.UniformMatrix4(uModelD, 1, false, (float*)&id);
    gl.BindVertexArray(terrainVao);
    gl.DrawElements(PrimitiveType.Triangles, (uint)terrainIndexCount, DrawElementsType.UnsignedInt, (void*)0);
    // Objects: record BACK-face depth only (cull front faces). A solid building's lit FRONT faces then sit in front of
    // the stored shadow depth, so they stop shadowing themselves - this kills the "shadows on top of the building" acne
    // without the heavy bias that would detach shadows. (Terrain keeps both faces above; it's a single-sided surface.)
    gl.Enable(EnableCap.CullFace);
    gl.CullFace(TriangleFace.Front);
    // Only objects within the focus box can cast into it; culling the rest keeps the depth pass fast on dense maps.
    glObjects?.DrawDepth(gl, depthProg, uModelD, new Vector2(focus.X, focus.Z), radius * 1.6f);
    gl.Disable(EnableCap.CullFace);
    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    gl.Viewport(0, 0, (uint)Math.Max(1, appliedFbSize.X), (uint)Math.Max(1, appliedFbSize.Y));   // restore the screen viewport
    shadowMapDirty = false;
}

// On load, populate the terrain sun-shadow the "Shadows" checkbox toggles: prefer the level's stored lightmap
// (LightmapShadowBits.lsb -> faithful, what the game shipped), else bake one from the heightmap + sun so the toggle is
// still meaningful on maps without a stored lightmap. Previously the checkbox did nothing until you clicked Bake.
void InitTerrainShadowOnLoad()
{
    if (heightmap is null) return;
    if (shadowTexId != 0) { gl.DeleteTexture(shadowTexId); shadowTexId = 0; }
    if (loadedShadowBits is not null && ShadowTextureFromLsb(loadedShadowBits) is { } lm)
    {
        shadowTexId = UploadTexture(lm);
        Console.WriteLine($"Loaded level lightmap (LightmapShadowBits.lsb) -> {lm.Width}^2 terrain sun-shadow. Toggle with 'Shadows'.");
    }
    else
    {
        var sun = env?.SunDirection ?? new Vec3(-0.5f, 0.8f, -0.35f);
        try { shadowTexId = UploadTexture(TerrainShadow.Bake(1024, heightmap, cfg, sun)); } catch { return; }
        Console.WriteLine("Baked terrain sun-shadow on load (no level lightmap present). Toggle with 'Shadows'.");
    }
}

// Bake the terrain sun cast-shadow from the heightmap + sun direction, upload it for the terrain
// shader to sample, and export an inspectable TerrainShadow.dds. (This is the editor preview/export;
// the engine's packed LightmapShadowBits.lsb is a separate format and isn't written here.)
void DoBakeShadows()
{
    if (heightmap is null) return;
    var sun = env?.SunDirection ?? new Vec3(-0.5f, 0.8f, -0.35f);
    var shadow = TerrainShadow.Bake(1024, heightmap, cfg, sun);
    if (shadowTexId != 0) { gl.DeleteTexture(shadowTexId); shadowTexId = 0; }
    shadowTexId = UploadTexture(shadow);
    showShadows = true;
    try
    {
        string? dir = (levelDir is not null && System.IO.Directory.Exists(levelDir))
            ? (System.IO.Directory.EnumerateDirectories(levelDir, "Textures", System.IO.SearchOption.AllDirectories).FirstOrDefault() ?? levelDir)
            : (levelDir is not null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(levelDir)) : null);
        if (dir is not null)
        {
            var p = System.IO.Path.Combine(dir, "TerrainShadow.dds");
            DdsTexture.Save(shadow, p);
            Console.WriteLine($"Baked sun shadows -> {p}");
        }
    }
    catch { }
}
// Re-sync GPU buffers after a transform change (no geometry change).
void SyncTransformEdit() { SyncMarkers(); glObjects?.Sync(so!); UploadMarkers(); }
// Soldier-spawn marker mesh: a simple soldier-sized box (0.6 w x 1.8 h x 0.4 d), base on the ground, built once.
// 24 verts (per-face) so normals come out flat/crisp; depth slightly less than width so its yaw reads.
MeshLibrary.Mesh SoldierBoxMesh()
{
    if (soldierBoxMesh is not null) return soldierBoxMesh;
    float hx = 0.30f, hz = 0.20f, hy = 1.80f;   // half-width, half-depth, full height (base at local y=0)
    // 6 faces, each 4 corners (CCW seen from outside), so each face gets a clean flat normal.
    var faces = new (Vector3 a, Vector3 b, Vector3 c, Vector3 d)[]
    {
        (new(-hx,0, hz), new( hx,0, hz), new( hx,hy, hz), new(-hx,hy, hz)),   // +Z (front)
        (new( hx,0,-hz), new(-hx,0,-hz), new(-hx,hy,-hz), new( hx,hy,-hz)),   // -Z (back)
        (new( hx,0, hz), new( hx,0,-hz), new( hx,hy,-hz), new( hx,hy, hz)),   // +X
        (new(-hx,0,-hz), new(-hx,0, hz), new(-hx,hy, hz), new(-hx,hy,-hz)),   // -X
        (new(-hx,hy, hz), new( hx,hy, hz), new( hx,hy,-hz), new(-hx,hy,-hz)), // +Y (top)
        (new(-hx,0,-hz), new( hx,0,-hz), new( hx,0, hz), new(-hx,0, hz)),     // -Y (bottom)
    };
    var pos = new Vector3[faces.Length * 4];
    var uvs = new System.Numerics.Vector2[faces.Length * 4];
    var idx = new int[faces.Length * 6];
    for (int fi = 0; fi < faces.Length; fi++)
    {
        int v = fi * 4;
        pos[v] = faces[fi].a; pos[v + 1] = faces[fi].b; pos[v + 2] = faces[fi].c; pos[v + 3] = faces[fi].d;
        int t = fi * 6;
        idx[t] = v; idx[t + 1] = v + 1; idx[t + 2] = v + 2; idx[t + 3] = v; idx[t + 4] = v + 2; idx[t + 5] = v + 3;
    }
    var part = new MeshLibrary.MaterialPart(idx, new Vector3(0.40f, 0.85f, 0.48f), null, false);
    soldierBoxMesh = new MeshLibrary.Mesh(pos, uvs, new[] { part });
    return soldierBoxMesh;
}

// Grid snap (toolbar "Snap" toggle): round a single coordinate to the snap step. A no-op when Snap is off.
float Snap1(float v) => snapOn && snapStep > 0f ? MathF.Round(v / snapStep) * snapStep : v;
// Snap a world position's X/Z to the grid (Y is left to the terrain), used by object move/place.
Vec3 SnapXZ(Vec3 p) => snapOn ? new Vec3(Snap1(p.X), p.Y, Snap1(p.Z)) : p;

// Snapshot every selected object's transform at the start of a gizmo drag (for group edits).
void CaptureDragSnapshot()
{
    dragSnap.Clear();
    if (so is null) return;
    foreach (var i in multi)
        if (i >= 0 && i < so.Objects.Count)
        { var o = so.Objects[i]; dragSnap.Add((i, o.Position, o.Rotation, o.Scale ?? 1f)); }
}

// Re-derive the terrain mesh from the (edited) heightmap and re-upload the terrain VBO in place.
void RebuildTerrain()
{
    if (heightmap is null) return;
    mesh = TerrainMesh.FromHeightmap(heightmap, cfg, 1);
    float ws2 = cfg.WorldSize <= 0 ? 1f : cfg.WorldSize;
    var v = new float[mesh.Positions.Length * 8];
    for (int i = 0; i < mesh.Positions.Length; i++)
    {
        var p = mesh.Positions[i]; var n = mesh.Normals[i]; int o = i * 8;
        v[o] = p.X; v[o + 1] = p.Y; v[o + 2] = p.Z;
        v[o + 3] = n.X; v[o + 4] = n.Y; v[o + 5] = n.Z;
        v[o + 6] = p.X / ws2; v[o + 7] = p.Z / ws2;
    }
    gl.BindVertexArray(terrainVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, terrainVbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, v, BufferUsageARB.DynamicDraw);
    if (stroke is null) gridDirty = true;   // terrain settled (sculpt finish / undo / redo / new map) -> re-drape the grid
}

// Transient confirmation shown in the status bar (and echoed to the console). Fades out over a few seconds.
void Toast(string msg) { toastText = msg; toastT = 4.5f; Console.WriteLine(msg); }

// Import a Heightmap.raw (headerless 16-bit LE square grid) over the current terrain. Bilinearly resampled to the
// level's materialSize if it differs, then copied IN PLACE so the existing TerrainPick / TerrainEditor keep working.
// Not part of object undo - it's a fresh terrain baseline (re-import the original .raw to revert).
// The side length of a headerless 16-bit square .raw (sqrt of its sample count), or null if the file isn't a
// perfect square of 16-bit samples. Used by the New Map import to auto-match the grid size and validate the pick.
int? RawSquareSide(string path)
{
    try
    {
        long len = new FileInfo(path).Length;
        if (len <= 0 || (len & 1) != 0) return null;            // must be a whole number of 16-bit samples
        long samples = len / 2;
        int side = (int)Math.Round(Math.Sqrt(samples));
        return side > 0 && (long)side * side == samples ? side : null;
    }
    catch { return null; }
}

void DoImportHeightmap()
{
    if (heightmap is null) return;
    var path = Picker.File("Import Heightmap.raw (16-bit LE, square)", "Raw heightmap|*.raw|All files|*.*", texturesDir ?? levelDir);
    if (path is null) return;
    try
    {
        var imported = Heightmap.LoadRawSquare(path);
        int srcSide = imported.Width;
        if (srcSide != cfg.MaterialSize) imported = imported.Resample(cfg.MaterialSize, cfg.MaterialSize);
        heightmap.CopyFrom(imported);
        RebuildTerrain();
        BroadcastFullTerrain();   // collab: push the imported terrain to peers (whole heightmap as one rect)
        Toast(srcSide == cfg.MaterialSize
            ? $"Imported {Path.GetFileName(path)} ({srcSide}^2)."
            : $"Imported {Path.GetFileName(path)} ({srcSide}^2 -> {cfg.MaterialSize}^2 resampled).");
    }
    catch (Exception ex) { Toast(Loc.T("Import failed: ") + ex.Message); }
}

// Export the current terrain as a raw Heightmap.raw (headerless 16-bit LE, side == materialSize).
void DoExportHeightmap()
{
    if (heightmap is null) return;
    var path = Picker.Save("Export Heightmap.raw", "Raw heightmap|*.raw|All files|*.*", "Heightmap.raw", texturesDir ?? levelDir);
    if (path is null) return;
    try { heightmap.SaveRaw(path); Toast($"Exported {heightmap.Width}^2 heightmap -> {Path.GetFileName(path)}."); }
    catch (Exception ex) { Toast(Loc.T("Export failed: ") + ex.Message); }
}

// Import a Wavefront .obj as a placeable mesh: parse -> inject into the mesh library (so it renders + places like
// an archive object) -> surface it in the "Imported" library category. Kept in importedObjs for .sm export.
void DoImportObj()
{
    if (meshLib is null || so is null) { Toast(Loc.T("Import .obj needs a level with mesh archives loaded.")); return; }
    var path = Picker.File("Import Wavefront .obj", "OBJ models|*.obj|All files|*.*", levelDir);
    if (path is null) return;
    try
    {
        var obj = ObjMesh.Load(path);
        if (obj.TotalFaces == 0) { Toast(Loc.T("That .obj has no triangles.")); return; }
        string name = SanitizeTemplate(Path.GetFileNameWithoutExtension(path));

        // Per-material colours + textures from the .obj's .mtl (resolved relative to the .obj's folder).
        var dir = Path.GetDirectoryName(path) ?? ".";
        var mtl = new System.Collections.Generic.Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
        foreach (var lib in obj.MtlLibs)
        {
            var mp = Path.Combine(dir, lib);
            if (File.Exists(mp)) foreach (var kv in ObjMtl.Load(mp)) mtl[kv.Key] = kv.Value;
        }
        var texCache = new System.Collections.Generic.Dictionary<string, Texture2D?>(StringComparer.OrdinalIgnoreCase);
        var matList = new System.Collections.Generic.List<(string Mat, string? TexName, Vector3 Diffuse)>();
        var seenMat = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (Vector3, Texture2D?) Resolve(string m)
        {
            mtl.TryGetValue(m, out var mm);
            var col = mm is not null ? new Vector3(mm.Diffuse.X, mm.Diffuse.Y, mm.Diffuse.Z) : new Vector3(0.72f, 0.74f, 0.78f);
            Texture2D? tex = null;
            if (mm?.TextureFile is { Length: > 0 } tf && !texCache.TryGetValue(tf, out tex))
            { tex = LoadImageAsTexture(Path.Combine(dir, tf)); texCache[tf] = tex; }
            if (seenMat.Add(m)) matList.Add((m, mm?.TextureName, col));
            return (col, tex);
        }

        meshLib.AddMesh(name, MeshLibrary.MeshFromObj(obj, Resolve));
        importedObjs[name] = obj;
        importMaterials[name] = matList;
        BroadcastObjMesh(name);   // collab: ship the render geometry so peers can show objects placed from this import
        RebuildCatalog();
        browserTemplate = name; gpPlaceKind = null; tool = Array.IndexOf(toolNames, "Place"); mapper = 2;
        int texCount = texCache.Values.Count(t => t is not null);
        Toast($"Imported '{name}' ({obj.TotalVertices} v, {obj.TotalFaces} tris, {obj.SubMeshes.Count} mat, {texCount} tex) - place from Imported.");
    }
    catch (Exception ex) { Toast(Loc.T("OBJ import failed: ") + ex.Message); }
}

// Import a Battlefield 1942 treeMesh.rfa: parse every .tm (BfMeshView-verified format), convert to render meshes,
// resolve trunk/leaf textures from the loaded texture archive, and surface them in a "Trees" library category so
// they render + place through the normal object pipeline (leaf/sprite groups are alpha-tested cutouts).
void DoImportTreeMesh()
{
    if (meshLib is null || so is null) { Toast(Loc.T("Import treeMesh needs a level with mesh archives loaded.")); return; }
    var path = Picker.File("Import a Battlefield 1942 treeMesh.rfa", "RFA archives|*.rfa|All files|*.*", levelDir);
    if (path is null) return;
    try
    {
        var arc = new RefractorFlatArchive(path);
        (Vector3, Texture2D?) Resolve(string texName)
        {
            Texture2D? tex = meshLib.Textures?.Resolve(Path.GetFileNameWithoutExtension(texName));
            return (tex is not null ? new Vector3(1f, 1f, 1f) : new Vector3(0.36f, 0.55f, 0.30f), tex);
        }
        int n = 0, withTex = 0;
        foreach (var e in arc.Entries)
        {
            if (!e.Name.EndsWith(".tm", StringComparison.OrdinalIgnoreCase)) continue;
            if (!RefractorForge.Formats.Rfa.TreeMesh.TryParse(arc.Read(e), out var tm) || tm is null) continue;
            var mesh = MeshLibrary.MeshFromTreeMesh(tm, Resolve);
            if (mesh.Positions.Length == 0 || mesh.Parts.Length == 0) continue;
            string name = Path.GetFileNameWithoutExtension(e.Name);
            meshLib.AddMesh(name, mesh);
            if (!treeMeshNames.Contains(name)) treeMeshNames.Add(name);
            if (mesh.Parts.Any(pt => pt.Texture is not null)) withTex++;
            n++;
        }
        RebuildCatalog();
        Toast($"Imported {n} tree meshes ({withTex} textured) - place them from the Trees category.");
        Console.WriteLine($"Imported {n} treeMesh meshes from {path} ({withTex} with resolved textures).");
    }
    catch (Exception ex) { Toast(Loc.T("treeMesh import failed: ") + ex.Message); }
}

// Load an image (.dds/.bmp/.png/.jpg/.tga/...) into a Texture2D for in-editor material preview; null on failure.
// png/jpg/etc go through System.Drawing (Viewer is net8.0-windows); .dds + .bmp use the engine decoders. Output
// is RGBA to match the GL upload (PixelFormat.Rgba).
// ---- Surface-texture set: each of the 16 slots is the bundled default or a user-imported texture
// (.dds/.tga/.bmp/.png). texSource[i] != null marks an override. The whole set saves/loads as a folder of
// surfNN.dds so a custom palette (e.g. a better sand) is reusable across maps. ----
Vector4 SurfaceSwatch(Texture2D? tx)
{
    if (tx is null) return new Vector4(0.5f, 0.5f, 0.5f, 1f);
    double r = 0, g = 0, b = 0; int px = tx.Rgba.Length / 4;
    for (int p = 0; p < tx.Rgba.Length; p += 4) { r += tx.Rgba[p]; g += tx.Rgba[p + 1]; b += tx.Rgba[p + 2]; }
    return px > 0 ? new Vector4((float)(r / px / 255), (float)(g / px / 255), (float)(b / px / 255), 1f) : new Vector4(0.5f, 0.5f, 0.5f, 1f);
}
Texture2D? LoadBundledSurface(int i) => Texture2D.LoadBmp(Path.Combine(AppContext.BaseDirectory, "textures", $"surf{i:D2}.bmp"));
void SetSurface(int i, Texture2D? tx, string? source) { if (i < 0 || i > 15) return; texPalette[i] = tx; texSwatch[i] = SurfaceSwatch(tx); texSource[i] = source; }
void ImportSurfaceSlot(int slot)
{
    var f = Picker.File("Import a surface texture (.dds / .tga / .bmp / .png)", "Images|*.dds;*.tga;*.bmp;*.png;*.jpg|All files|*.*", null);
    if (f is null) return;
    var tx = LoadImageAsTexture(f);
    if (tx is null) { Toast($"Couldn't load {Path.GetFileName(f)}."); return; }
    SetSurface(slot, tx, f);
    Toast($"Surface #{slot} ({(slot < surfNames.Length ? surfNames[slot] : "?")}) <- {Path.GetFileName(f)} ({tx.Width}x{tx.Height})");
}
void ResetSurfaceSlot(int slot) { SetSurface(slot, LoadBundledSurface(slot), null); Toast($"Surface #{slot} reset to the bundled default."); }
void ExportSurfaceSet()
{
    var dir = Picker.Folder("Choose a folder to save this surface set into", null);
    if (dir is null) return;
    try
    {
        Directory.CreateDirectory(dir);
        int n = 0;
        for (int i = 0; i < 16; i++)
            if (texPalette[i] is Texture2D tx) { File.WriteAllBytes(Path.Combine(dir, $"surf{i:D2}.dds"), DdsTexture.EncodeUncompressed(tx)); n++; }
        File.WriteAllText(Path.Combine(dir, "surfset.txt"), string.Join("\n", surfNames));
        Toast($"Saved surface set ({n} textures) -> {Path.GetFileName(dir)}");
    }
    catch (Exception ex) { Toast($"Save set failed: {ex.Message}"); }
}
void LoadSurfaceSet()
{
    var dir = Picker.Folder("Choose a surface-set folder to load", null);
    if (dir is null) return;
    int n = 0;
    for (int i = 0; i < 16; i++)
        foreach (var ext in new[] { ".dds", ".tga", ".bmp", ".png" })
        {
            var f = Path.Combine(dir, $"surf{i:D2}{ext}");
            if (File.Exists(f)) { var tx = LoadImageAsTexture(f); if (tx is not null) { SetSurface(i, tx, f); n++; } break; }
        }
    Toast($"Loaded surface set ({n} textures) from {Path.GetFileName(dir)}");
}

// Alt-click eyedropper: pick the surface/material/foliage under the cursor (Paint.NET style).
void EyedropAt(float wx, float wz)
{
    float ws = cfg.WorldSize;
    if (paintLayer == 3 && atlasCpu is not null)   // surface painter: nearest set surface by atlas colour
    {
        var c = atlasCpu.SampleRGBA(wx / ws, wz / ws);
        int best = activeTexture; float bestD = float.MaxValue;
        for (int i = 0; i < texSwatch.Length; i++)
        {
            if (texPalette[i] is null) continue;
            float dr = c.X - texSwatch[i].X, dg = c.Y - texSwatch[i].Y, db = c.Z - texSwatch[i].Z;
            float d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
        }
        activeTexture = (byte)best;
        Toast($"Picked surface #{best} ({(best < surfNames.Length ? surfNames[best] : "?")})");
        return;
    }
    var map = ActivePaintMap();   // material (layer 0) or foliage (1/2): exact per-cell index
    if (map is not null)
    {
        int side = map.Width;
        int gx = Math.Clamp((int)(wx / ws * side), 0, side - 1);
        int gy = Math.Clamp((int)(wz / ws * side), 0, side - 1);
        byte v = map[gx, gy];
        if (paintLayer == 0) { activeMaterial = v; int es = v < matToSurf.Length ? (matToSurf[v] & 15) : (v & 15); Toast($"Picked material #{v} ({(es < surfNames.Length ? surfNames[es] : "?")})"); }
        else { activeFoliage = v; Toast($"Picked foliage #{v}"); }
    }
}

// Capture a square of the painted atlas under the cursor and save it as a reusable .dds surface texture.
void CaptureSurfaceAt(float wx, float wz)
{
    if (atlasCpu is null) return;
    int res = captureSizes[Math.Clamp(captureResIdx, 0, captureSizes.Length - 1)];
    float ws = cfg.WorldSize, u0 = (wx - captureMeters * 0.5f) / ws, v0 = (wz - captureMeters * 0.5f) / ws, span = captureMeters / ws;
    var rgba = new byte[res * res * 4];
    for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            var c = atlasCpu.SampleRGBA(u0 + span * (x + 0.5f) / res, v0 + span * (y + 0.5f) / res);
            int o = (y * res + x) * 4;
            rgba[o] = (byte)Math.Clamp(c.X * 255f, 0, 255); rgba[o + 1] = (byte)Math.Clamp(c.Y * 255f, 0, 255);
            rgba[o + 2] = (byte)Math.Clamp(c.Z * 255f, 0, 255); rgba[o + 3] = 255;
        }
    var cap = new Texture2D(res, res, rgba);
    var f = Picker.Save("Save captured terrain as a surface texture", "DDS texture|*.dds", "captured_surface.dds", null);
    if (f is null) return;
    try
    {
        File.WriteAllBytes(f, DdsTexture.EncodeUncompressed(cap));
        if (captureImport) SetSurface(activeTexture, cap, f);
        Toast($"Captured {captureMeters:0} m -> {res}x{res} -> {Path.GetFileName(f)}{(captureImport ? " (imported)" : "")}");
    }
    catch (Exception ex) { Toast($"Capture save failed: {ex.Message}"); }
}

Texture2D? LoadImageAsTexture(string imgPath)
{
    try
    {
        if (!File.Exists(imgPath)) return null;
        var ext = Path.GetExtension(imgPath).ToLowerInvariant();
        if (ext == ".dds") return DdsTexture.Decode(File.ReadAllBytes(imgPath));
        if (ext == ".tga") return TgaTexture.Decode(File.ReadAllBytes(imgPath));
        if (ext == ".bmp") return Texture2D.LoadBmp(imgPath);
        using var bmp = new System.Drawing.Bitmap(imgPath);
        int w = bmp.Width, h = bmp.Height;
        var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var tmp = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, tmp, 0, tmp.Length);
        bmp.UnlockBits(d);
        var rgba = new byte[w * h * 4];   // System.Drawing 32bppArgb is BGRA in memory -> swap to RGBA
        for (int i = 0; i < w * h; i++) { rgba[i * 4] = tmp[i * 4 + 2]; rgba[i * 4 + 1] = tmp[i * 4 + 1]; rgba[i * 4 + 2] = tmp[i * 4]; rgba[i * 4 + 3] = tmp[i * 4 + 3]; }
        return new Texture2D(w, h, rgba);
    }
    catch { return null; }
}

// ===== Texture Library + Editor42-style Layer Tool =====================================================
// A bundled folder of tileable terrain textures (TerrainTextures\<Category>\*) beside the exe that users add their
// own to. Pick one to paint with (the Surface brush tiles it into the atlas), Fill the whole terrain with it, or
// combine two by height/slope with the Layer Tool (noise-blended, like Editor42). All paths reuse the existing atlas
// paint + .dds tile save, so nothing new is needed on the save side.
TextureLayerSpec BuildLayerSpec() => new()
{
    Selector = layerSelectorIdx == 1 ? LayerSelector.Slope : LayerSelector.Height,
    ThresholdLow = layerThrLow, ThresholdHigh = layerThrHigh,
    NoiseOn = layerNoiseOn, Seed = layerSeed, FirstOctave = layerFirstOctave, OctaveCount = layerOctaveCount,
    ThresholdWidth = layerThrWidth, TileMetersA = layerTileA, TileMetersB = layerTileB,
};

void RefreshTextureLibrary()
{
    texLibEntries.Clear();
    try
    {
        Directory.CreateDirectory(texLibRoot);
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dds", ".tga", ".bmp", ".png", ".jpg", ".jpeg" };
        foreach (var f in Directory.EnumerateFiles(texLibRoot, "*.*", SearchOption.AllDirectories))
        {
            if (!exts.Contains(Path.GetExtension(f))) continue;
            var rel = Path.GetRelativePath(texLibRoot, f);
            var dir = Path.GetDirectoryName(rel);
            string cat = string.IsNullOrEmpty(dir) ? "(root)" : dir!.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            texLibEntries.Add((f, Path.GetFileNameWithoutExtension(f), cat));
        }
        texLibEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        var cats = new List<string> { "All" };
        foreach (var e in texLibEntries) if (!cats.Contains(e.Category)) cats.Add(e.Category);
        texLibCats = cats.ToArray();
        if (texLibCatIdx >= texLibCats.Length) texLibCatIdx = 0;
        Console.WriteLine($"Texture library: {texLibEntries.Count} texture(s) in {texLibCats.Length - 1} categories @ {texLibRoot}");
    }
    catch (Exception ex) { Console.WriteLine($"Texture library scan failed: {ex.Message}"); }
}

Texture2D DownscaleTex(Texture2D src, int size)
{
    var rgba = new byte[size * size * 4];
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var c = src.SampleRGBA((x + 0.5f) / size, (y + 0.5f) / size);
            int o = (y * size + x) * 4;
            rgba[o] = (byte)Math.Clamp(c.X * 255f, 0, 255); rgba[o + 1] = (byte)Math.Clamp(c.Y * 255f, 0, 255);
            rgba[o + 2] = (byte)Math.Clamp(c.Z * 255f, 0, 255); rgba[o + 3] = 255;
        }
    return new Texture2D(size, size, rgba);
}

// Lazy 64px GL thumbnail for a library file (cached). The grid is rate-limited by texLibThumbBudget so opening a
// folder of hundreds of textures fills in over a few frames instead of hitching; force=true for single previews.
uint LibThumb(string path, bool force)
{
    if (texLibThumb.TryGetValue(path, out var id)) return id;
    if (!force && texLibThumbBudget <= 0) return 0;
    texLibThumbBudget--;
    try { var tx = LoadImageAsTexture(path); id = tx is null ? 0 : UploadTexture(DownscaleTex(tx, 64)); }
    catch { id = 0; }
    texLibThumb[path] = id;
    return id;
}

// Pick a library texture: as the active surface paint texture, or (when arming the Layer Tool) as layer A / B.
void PickLibraryTexture(string path)
{
    var tx = LoadImageAsTexture(path);
    if (tx is null) { Toast($"Couldn't load {Path.GetFileName(path)}."); return; }
    if (layerPickTarget == 3) { roadLibTex = tx; roadLibTexPath = path; roadUseLib = true; Toast($"Road texture <- {Path.GetFileName(path)}"); }
    else if (layerPickTarget == 1) { layerTexA = tx; layerTexAPath = path; layerProofDirty = true; Toast($"Layer A <- {Path.GetFileName(path)}"); }
    else if (layerPickTarget == 2) { layerTexB = tx; layerTexBPath = path; layerProofDirty = true; Toast($"Layer B <- {Path.GetFileName(path)}"); }
    else { libTex = tx; libTexPath = path; paintFromLib = true; Toast($"Paint texture <- {Path.GetFileName(path)} ({tx.Width}x{tx.Height})"); }
    layerPickTarget = 0;
}

void ImportToLibrary()
{
    var f = Picker.File("Import a texture into the library", "Images|*.dds;*.tga;*.bmp;*.png;*.jpg;*.jpeg|All files|*.*", null);
    if (f is null) return;
    try
    {
        Directory.CreateDirectory(texLibRoot);
        string cat = (texLibCatIdx > 0 && texLibCatIdx < texLibCats.Length && texLibCats[texLibCatIdx] is var c && c != "All" && c != "(root)") ? c : "";
        var destDir = string.IsNullOrEmpty(cat) ? texLibRoot : Path.Combine(texLibRoot, cat);
        Directory.CreateDirectory(destDir);
        File.Copy(f, Path.Combine(destDir, Path.GetFileName(f)), overwrite: true);
        RefreshTextureLibrary();
        Toast($"Imported {Path.GetFileName(f)} into the library{(string.IsNullOrEmpty(cat) ? "" : " / " + cat)}.");
    }
    catch (Exception ex) { Toast($"Import failed: {ex.Message}"); }
}

void OpenLibraryFolder()
{
    try { Directory.CreateDirectory(texLibRoot); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = texLibRoot, UseShellExecute = true }); }
    catch (Exception ex) { Toast($"Couldn't open folder: {ex.Message}"); }
}

// The active Surface paint source (a chosen library texture, else the selected 16-slot palette surface) + its tiling.
Texture2D? SurfPaintTex() => (paintFromLib && libTex is not null) ? libTex : (activeTexture < texPalette.Length ? texPalette[activeTexture & 15] : null);
float SurfPaintTile() => paintFromLib ? libTileMeters : texTileMeters;

// Run a whole-atlas mutation (Fill / Layer bake) as one undoable edit, then re-upload + flag for save.
void AtlasFullEdit(Action mutate)
{
    if (atlasCpu is null) { Toast(Loc.T("This level has no terrain texture atlas.")); return; }
    var before = (byte[])atlasCpu.Rgba.Clone();
    mutate();
    var after = (byte[])atlasCpu.Rgba.Clone();
    var cmd = new AtlasStrokeCommand(atlasCpu, 0, 0, atlasCpu.Width, atlasCpu.Height, before, after, UploadAtlasRectMips);
    atlasPainted = true;
    if (hist is not null) hist.Do(cmd);
    else UploadAtlasRectMips(0, 0, atlasCpu.Width, atlasCpu.Height);
}

void FillTerrainWith(Texture2D tex, float tile)
{
    AtlasFullEdit(() => TerrainTextureLayer.FillAtlas(atlasCpu!, tex, cfg.WorldSize, tile));
    Toast(Loc.T("Filled the terrain. Ctrl+S bakes it into the level's tiles."));
}

void ApplyLayerToTerrain()
{
    if (atlasCpu is null) { Toast(Loc.T("No terrain texture atlas in this level.")); return; }
    if (heightmap is null) { Toast(Loc.T("This level has no heightmap (height/slope blend needs one).")); return; }
    if (layerTexA is null && layerTexB is null) { Toast(Loc.T("Pick at least one layer texture first.")); return; }
    var spec = BuildLayerSpec();
    Console.WriteLine($"Baking texture layer ({(layerSelectorIdx == 1 ? "slope" : "height")} {layerThrLow:0}..{layerThrHigh:0}, noise {(layerNoiseOn ? "on" : "off")}) into the atlas...");
    AtlasFullEdit(() => TerrainTextureLayer.BakeLayerToAtlas(atlasCpu!, heightmap, cfg, layerTexA, layerTexB, spec));
    Toast(Loc.T("Applied the layer. Ctrl+S bakes it into the level's tiles."));
}

void UpdateLayerProof()
{
    var prev = TerrainTextureLayer.ProofPreview(160, layerTexA, layerTexB, BuildLayerSpec());
    if (layerProofGl != 0) { gl.DeleteTexture(layerProofGl); layerProofGl = 0; }
    layerProofGl = UploadTexture(prev);
    layerProofDirty = false;
}

void SaveLayerPreset()
{
    try
    {
        var dir = Path.Combine(texLibRoot, "Layers");
        Directory.CreateDirectory(dir);
        string Rel(string? p) => p is null ? "" : (p.StartsWith(texLibRoot, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(texLibRoot, p) : p);
        var dto = new Dictionary<string, object?>
        {
            ["selector"] = layerSelectorIdx, ["thrLow"] = layerThrLow, ["thrHigh"] = layerThrHigh,
            ["noiseOn"] = layerNoiseOn, ["seed"] = layerSeed, ["firstOctave"] = layerFirstOctave,
            ["octaveCount"] = layerOctaveCount, ["thrWidth"] = layerThrWidth,
            ["tileA"] = layerTileA, ["tileB"] = layerTileB, ["texA"] = Rel(layerTexAPath), ["texB"] = Rel(layerTexBPath),
        };
        var name = string.IsNullOrWhiteSpace(layerPresetName) ? "layer" : layerPresetName.Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        File.WriteAllText(Path.Combine(dir, name + ".layer.json"),
            System.Text.Json.JsonSerializer.Serialize(dto, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Toast($"Saved layer preset -> {name}.layer.json");
    }
    catch (Exception ex) { Toast($"Save preset failed: {ex.Message}"); }
}

void LoadLayerPreset()
{
    var start = Path.Combine(texLibRoot, "Layers");
    var f = Picker.File("Load a layer preset", "Layer preset|*.layer.json;*.json|All files|*.*", Directory.Exists(start) ? start : texLibRoot);
    if (f is null) return;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
        var r = doc.RootElement;
        int GI(string k, int d) => r.TryGetProperty(k, out var e) && e.TryGetInt32(out var v) ? v : d;
        float GF(string k, float d) => r.TryGetProperty(k, out var e) && e.TryGetSingle(out var v) ? v : d;
        bool GB(string k, bool d) => r.TryGetProperty(k, out var e) ? e.GetBoolean() : d;
        string GS(string k) => r.TryGetProperty(k, out var e) ? (e.GetString() ?? "") : "";
        layerSelectorIdx = GI("selector", layerSelectorIdx); layerThrLow = GF("thrLow", layerThrLow); layerThrHigh = GF("thrHigh", layerThrHigh);
        layerNoiseOn = GB("noiseOn", layerNoiseOn); layerSeed = GI("seed", layerSeed); layerFirstOctave = GI("firstOctave", layerFirstOctave);
        layerOctaveCount = GI("octaveCount", layerOctaveCount); layerThrWidth = GF("thrWidth", layerThrWidth);
        layerTileA = GF("tileA", layerTileA); layerTileB = GF("tileB", layerTileB);
        string AbsOf(string rel) => string.IsNullOrEmpty(rel) ? "" : (Path.IsPathRooted(rel) ? rel : Path.Combine(texLibRoot, rel));
        var ta = AbsOf(GS("texA")); var tb = AbsOf(GS("texB"));
        if (File.Exists(ta)) { layerTexA = LoadImageAsTexture(ta); layerTexAPath = ta; }
        if (File.Exists(tb)) { layerTexB = LoadImageAsTexture(tb); layerTexBPath = tb; }
        layerProofDirty = true;
        layerPresetName = Path.GetFileNameWithoutExtension(f).Replace(".layer", "");
        Toast($"Loaded layer preset {Path.GetFileName(f)}");
    }
    catch (Exception ex) { Toast($"Load preset failed: {ex.Message}"); }
}

// One clickable thumbnail tile (button for hover/click + the thumbnail drawn over it).
bool LibTile(string id, uint tex, Vector2 size, bool selected)
{
    if (selected) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.43f, 0.69f, 1f));
    bool clicked = ImGui.Button(id, size);
    if (selected) ImGui.PopStyleColor();
    var mn = ImGui.GetItemRectMin(); var mx = ImGui.GetItemRectMax();
    if (tex != 0) ImGui.GetWindowDrawList().AddImage((IntPtr)tex, new Vector2(mn.X + 2, mn.Y + 2), new Vector2(mx.X - 2, mx.Y - 2));
    return clicked;
}

// The texture-library browser: category + search row, then a thumbnail grid. height 0 = fill the rest of the window.
void DrawTextureBrowser(float height)
{
    ImGui.SetNextItemWidth(140f); ImGui.Combo(Loc.TL("Category"), ref texLibCatIdx, texLibCats, texLibCats.Length);
    ImGui.SameLine(); ImGui.SetNextItemWidth(130f); ImGui.InputTextWithHint("##texsearch", Loc.T("search"), ref texLibSearch, 64);
    ImGui.SameLine(); if (ImGui.Button(Loc.TL("Refresh"))) RefreshTextureLibrary();
    ImGui.SameLine(); if (ImGui.Button(Loc.TL("Import..."))) ImportToLibrary();
    ImGui.SameLine(); if (ImGui.Button(Loc.TL("Folder"))) OpenLibraryFolder();
    if (texLibEntries.Count == 0)
    {
        ImGui.Spacing();
        ImGui.TextWrapped(Loc.T("No textures yet. Click \"Folder\" (or \"Import...\") and drop tileable .bmp/.dds/.tga/.png/.jpg files into the library, then Refresh."));
        return;
    }
    string cat = texLibCats[Math.Clamp(texLibCatIdx, 0, texLibCats.Length - 1)];
    string search = texLibSearch ?? "";
    ImGui.BeginChild("texgrid", new Vector2(0, height), ImGuiChildFlags.Border, ImGuiWindowFlags.None);
    float cell = 72f;
    int cols = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / cell));
    int col = 0;
    foreach (var e in texLibEntries)
    {
        if (cat != "All" && e.Category != cat) continue;
        if (search.Length > 0 && e.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
        bool sel = (layerPickTarget == 0 && string.Equals(libTexPath, e.Path, StringComparison.OrdinalIgnoreCase))
                || (layerPickTarget == 1 && string.Equals(layerTexAPath, e.Path, StringComparison.OrdinalIgnoreCase))
                || (layerPickTarget == 2 && string.Equals(layerTexBPath, e.Path, StringComparison.OrdinalIgnoreCase));
        if (LibTile($"##lib_{e.Path}", LibThumb(e.Path, false), new Vector2(64, 64), sel)) PickLibraryTexture(e.Path);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{e.Name}\n[{e.Category}]");
        col++;
        if (col % cols != 0) ImGui.SameLine();
    }
    ImGui.EndChild();
}

void TextureLibraryWindow()
{
    if (!showTexLibrary) return;
    ImGui.SetNextWindowSize(new Vector2(440, 470), ImGuiCond.FirstUseEver);
    if (ImGui.Begin(Loc.TL("Texture Library"), ref showTexLibrary))
    {
        if (layerPickTarget == 1) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("Click a texture to set LAYER A"));
        else if (layerPickTarget == 2) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("Click a texture to set LAYER B"));
        else if (layerPickTarget == 3) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("Click a texture to set the ROAD texture"));
        else ImGui.TextDisabled(Loc.T("Click a texture to paint the terrain with it."));
        DrawTextureBrowser(0f);
    }
    ImGui.End();
}

void LayerToolWindow()
{
    if (!showLayerTool) return;
    ImGui.SetNextWindowSize(new Vector2(380, 580), ImGuiCond.FirstUseEver);
    if (ImGui.Begin(Loc.TL("Layer Tool"), ref showLayerTool))
    {
        ImGui.TextWrapped(Loc.T("Blend two tileable textures across the terrain by height or slope, with noise breaking up the seam (Editor42-style)."));
        ImGui.Separator();
        // Layer A
        if (LibTile("##layerAtile", layerTexAPath is not null ? LibThumb(layerTexAPath, true) : 0, new Vector2(48, 48), layerPickTarget == 1)) { layerPickTarget = 1; showTexLibrary = true; }
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(Loc.T("Layer A (low / flat): ") + (layerTexAPath is not null ? Path.GetFileName(layerTexAPath) : "(none)"));
        if (ImGui.Button(Loc.TL("Pick A..."))) { layerPickTarget = 1; showTexLibrary = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(130f); if (SldF(Loc.TL("Tile A (m)"), ref layerTileA, 1f, 64f, "%.0f")) layerProofDirty = true;
        ImGui.EndGroup();
        // Layer B
        if (LibTile("##layerBtile", layerTexBPath is not null ? LibThumb(layerTexBPath, true) : 0, new Vector2(48, 48), layerPickTarget == 2)) { layerPickTarget = 2; showTexLibrary = true; }
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(Loc.T("Layer B (high / steep): ") + (layerTexBPath is not null ? Path.GetFileName(layerTexBPath) : "(none)"));
        if (ImGui.Button(Loc.TL("Pick B..."))) { layerPickTarget = 2; showTexLibrary = true; }
        ImGui.SameLine(); ImGui.SetNextItemWidth(130f); if (SldF(Loc.TL("Tile B (m)"), ref layerTileB, 1f, 64f, "%.0f")) layerProofDirty = true;
        ImGui.EndGroup();
        ImGui.Separator();
        string[] selNames = { "Height", "Slope" };
        if (ImGui.Combo(Loc.TL("Selector"), ref layerSelectorIdx, selNames, selNames.Length)) layerProofDirty = true;
        if (layerSelectorIdx == 1)
        {
            if (SldF(Loc.TL("Slope low (deg)"), ref layerThrLow, 0f, 90f, "%.0f")) layerProofDirty = true;
            if (SldF(Loc.TL("Slope high (deg)"), ref layerThrHigh, 0f, 90f, "%.0f")) layerProofDirty = true;
        }
        else
        {
            if (SldF(Loc.TL("Height low (m)"), ref layerThrLow, -50f, 400f, "%.0f")) layerProofDirty = true;
            if (SldF(Loc.TL("Height high (m)"), ref layerThrHigh, -50f, 400f, "%.0f")) layerProofDirty = true;
        }
        if (ImGui.Checkbox(Loc.TL("Use noise gradation"), ref layerNoiseOn)) layerProofDirty = true;
        if (layerNoiseOn)
        {
            if (SldI(Loc.TL("Seed"), ref layerSeed, 0, 99999)) layerProofDirty = true;
            if (SldI(Loc.TL("First octave"), ref layerFirstOctave, 0, 10)) layerProofDirty = true;
            if (SldI(Loc.TL("Octave count"), ref layerOctaveCount, 1, 10)) layerProofDirty = true;
            if (SldF(Loc.TL("Threshold width"), ref layerThrWidth, 0f, 1.5f, "%.2f")) layerProofDirty = true;
        }
        ImGui.Separator();
        if (ImGui.Button(Loc.TL("Proof"))) UpdateLayerProof();
        else if (layerProofDirty && layerProofGl == 0) UpdateLayerProof();
        if (layerProofGl != 0) ImGui.Image((IntPtr)layerProofGl, new Vector2(160, 160));
        if (layerProofDirty) { ImGui.SameLine(); ImGui.TextDisabled(Loc.T("(stale)")); }
        ImGui.Separator();
        if (ImGui.Button(Loc.TL("Apply to terrain"))) ApplyLayerToTerrain();
        ImGui.SameLine(); if (ImGui.Button(Loc.TL("Library..."))) { layerPickTarget = 0; showTexLibrary = true; }
        ImGui.Spacing();
        ImGui.SetNextItemWidth(150f); ImGui.InputText("##presetname", ref layerPresetName, 48);
        ImGui.SameLine(); if (ImGui.Button(Loc.TL("Save preset"))) SaveLayerPreset();
        ImGui.SameLine(); if (ImGui.Button(Loc.TL("Load preset"))) LoadLayerPreset();
        ImGui.TextDisabled(Loc.T("Apply bakes into the terrain texture; Ctrl+S writes the .dds tiles."));
    }
    ImGui.End();
}

// ---- Detail texture (BF detailTexName): a fine tiling overlay multiplied over the base atlas up close. There was
// no import path before - only levels that already shipped Textures/detail.dds got one. ----
void ImportDetailTexture()
{
    if (terrainTex is null) { Toast(Loc.T("No terrain texture in this level to attach detail to.")); return; }
    var f = Picker.File("Import a tiling detail texture (.dds / .tga / .bmp / .png)", "Images|*.dds;*.tga;*.bmp;*.png;*.jpg|All files|*.*", null);
    if (f is null) return;
    var tx = LoadImageAsTexture(f);
    if (tx is null) { Toast($"Couldn't load {Path.GetFileName(f)}."); return; }
    terrainTex.Detail = tx;
    terrainTex.DetailRepeatMeters = detailRepeatM;
    if (detailTexId != 0) { gl.DeleteTexture(detailTexId); detailTexId = 0; }
    detailTexId = UploadDetailTexture(tx);
    gl.UseProgram(terrainProg);
    gl.Uniform1(uHasDetail, 1);
    gl.Uniform1(uDetailScale, terrainTex.DetailScale);
    detailImported = true;
    Toast($"Detail texture <- {Path.GetFileName(f)} ({tx.Width}x{tx.Height})");
}
// Let the user supply the scrolling water textures manually - for maps that REFERENCE water.texLayer1/2 but don't ship
// the files (most stock BF1942 maps use engine-built-in water07/08 absent from the .rfa). Picks diffuse layer 1, then
// optional layer 2 + normal map, uploads them tiled, and flips the water plane to the textured path.
void ImportWaterTextures()
{
    // The water draw only takes the textured path when env is non-null (it reads scroll/tile from env). env is null only
    // on the demo-terrain fallback (no level loaded), where imported water textures would silently never render - guard it.
    if (env is null) { Toast(Loc.T("Load a level first, then import water textures.")); return; }
    var f1 = Picker.File("Water DIFFUSE layer 1 (.dds / .tga / .png / .bmp)", "Images|*.dds;*.tga;*.bmp;*.png;*.jpg|All files|*.*", null);
    if (f1 is null) return;
    var t1 = LoadImageAsTexture(f1);
    if (t1 is null) { Toast($"Couldn't load {Path.GetFileName(f1)}."); return; }
    var f2 = Picker.File("Water DIFFUSE layer 2 (optional - Cancel to reuse layer 1)", "Images|*.dds;*.tga;*.bmp;*.png;*.jpg|All files|*.*", null);
    var t2 = f2 is not null ? LoadImageAsTexture(f2) : null;
    var fn = Picker.File("Water NORMAL map (optional - Cancel for none)", "Images|*.dds;*.tga;*.bmp;*.png;*.jpg|All files|*.*", null);
    var tn = fn is not null ? LoadImageAsTexture(fn) : null;
    if (waterTex1 != 0) { gl.DeleteTexture(waterTex1); waterTex1 = 0; }
    if (waterTex2 != 0) { gl.DeleteTexture(waterTex2); waterTex2 = 0; }
    if (waterNorm != 0) { gl.DeleteTexture(waterNorm); waterNorm = 0; }
    waterTex1 = UploadTiledTexture(t1);
    waterTex2 = UploadTiledTexture(t2 ?? t1);
    waterNorm = UploadTiledTexture(tn ?? t1);
    haveWaterTex = true; useWaterTextures = true;
    Toast($"Water textures imported (layer1 {t1.Width}x{t1.Height}{(t2 is not null ? ", layer2" : "")}{(tn is not null ? ", normal" : "")}).");
}
void SetDetailRepeat(float m)
{
    detailRepeatM = MathF.Max(0.5f, m);
    if (terrainTex?.Detail is not null) { terrainTex.DetailRepeatMeters = detailRepeatM; gl.UseProgram(terrainProg); gl.Uniform1(uDetailScale, terrainTex.DetailScale); }
}
// The imported detail texture as Textures/detail.dds bytes (uncompressed BGRA DDS) for save, else null.
(string Name, byte[] Bytes)? DetailDdsBytes() => (detailImported && terrainTex?.Detail is not null)
    ? ("detail.dds", DdsTexture.EncodeUncompressed(terrainTex.Detail)) : null;

// Export an imported mesh as a Refractor standard mesh (.sm) + a minimal template .con stub, so it can be packed
// into a mesh archive and used in-game. Geometry round-trips byte-exact vs the .sm reader; collision + textured
// materials are a later pass.
void DoExportObjSm(string template)
{
    if (!importedObjs.TryGetValue(template, out var obj)) { Toast(Loc.T("Select an imported mesh to export.")); return; }
    var path = Picker.Save("Export standard mesh (.sm)", "Standard mesh|*.sm|All files|*.*", template + ".sm", levelDir);
    if (path is null) return;
    try
    {
        var col = expCollision ? RefractorForge.Formats.Rfa.StandardMeshWriter.BuildObjCollision(obj) : null;
        File.WriteAllBytes(path, RefractorForge.Formats.Rfa.StandardMeshWriter.Write(obj, col));
        // .rs shader: bind each material's texture + diffuse so the exported mesh is textured in-game.
        bool wroteRs = false;
        if (importMaterials.TryGetValue(template, out var mats) && mats.Count > 0)
        {
            File.WriteAllText(Path.ChangeExtension(path, ".rs"), RsShaderSet.Write(mats.Select(m => (m.Mat, m.TexName, m.Diffuse))));
            wroteRs = true;
        }
        var con = Path.ChangeExtension(path, ".con");
        var stub = $"GeometryTemplate.create StandardMesh {template}\r\nGeometryTemplate.file {template}\r\n\r\n" +
                   $"ObjectTemplate.create SimpleObject {template}\r\nObjectTemplate.geometry {template}\r\n" +
                   $"ObjectTemplate.hasCollisionPhysics 1\r\n" +
                   "rem NOTE: in-game collision needs col data inside the .sm (a serialized DShape). Refractor has no\r\n" +
                   "rem .con-level collision primitive for static objects -- author COL01/COL02 meshes + 3dsToSm, or wait\r\n" +
                   "rem for a RefractorForge .sm col writer (see docs/SM_Collision_RE.md). Until then this object is solid-less.\r\n";
        File.WriteAllText(con, stub);
        Toast($"Exported {Path.GetFileName(path)}{(wroteRs ? " + .rs" : "")}{(col is not null ? " + EXPERIMENTAL collision" : "")} + .con stub.");
    }
    catch (Exception ex) { Toast(Loc.T("Export .sm failed: ") + ex.Message); }
}

// A safe object-template / .sm filename from an arbitrary .obj filename, de-duplicated against existing templates.
string SanitizeTemplate(string raw)
{
    var sb = new System.Text.StringBuilder();
    foreach (var ch in raw) sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
    var s = sb.ToString().Trim('_');
    if (s.Length == 0) s = "ImportedMesh";
    if (char.IsDigit(s[0])) s = "m_" + s;
    string baseName = s; int n = 1;
    while (importedObjs.ContainsKey(s) || (meshLib is not null && meshLib.TryGet(s, out _))) s = baseName + "_" + (++n);
    return s;
}

// Sculpt mode for the current tool: the Smooth tool always averages; the Sculpt tool uses the chosen
// mode (Raise/Lower/Flatten/Set), with Shift still flipping Raise<->Lower for muscle memory.
BrushMode CurBrushMode()
{
    if (toolNames[tool] == "Smooth") return BrushMode.Smooth;
    if (activeStrokeDir > 0) return BrushMode.Raise;     // L/R-button sculpt stroke overrides the Mode combo
    if (activeStrokeDir < 0) return BrushMode.Lower;
    var m = sculptModes[Math.Clamp(sculptModeIdx, 0, sculptModes.Length - 1)];
    bool shift = kb is not null && (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight));
    if (shift) { if (m == BrushMode.Raise) m = BrushMode.Lower; else if (m == BrushMode.Lower) m = BrushMode.Raise; }
    return m;
}

// The brush for the current tool/modifier: mode-appropriate strength, the chosen falloff curve, and a
// Flatten/Set target (null => lock to the height under the brush centre at the start of the stroke).
TerrainBrush MakeBrush()
{
    var m = CurBrushMode();
    float st = (m == BrushMode.Smooth || m == BrushMode.Flatten) ? smoothStrength : brushStrength;
    var fo = (BrushFalloff)Math.Clamp(falloffIdx, 0, 3);
    float? target = (m == BrushMode.Flatten || m == BrushMode.Set) && !flattenLockGround ? flattenTarget : null;
    var shape = brushShapes[Math.Clamp(brushShapeIdx, 0, brushShapes.Count - 1)].Mask;
    return new TerrainBrush(m, brushRadius, st, fo, target, shape, Square: squareBrush && shape is null);
}

// The material/foliage paint brush: shares the brush Shape selector + Square toggle with the terrain tools.
MaterialBrush MakeMatBrush()
{
    var shape = brushShapes[Math.Clamp(brushShapeIdx, 0, brushShapes.Count - 1)].Mask;
    return new MaterialBrush(ActivePaintValue(), brushRadius, matHardness, BrushFalloff.Smooth, shape, squareBrush && shape is null);
}

// Mod-aware vehicle list for the spawn dropdown: the vehicles actually present in the loaded mod (meshLib's assembled
// vehicle folders), cached per mesh library; falls back to the built-in catalog when nothing's loaded.
string[] VehicleChoices()
{
    if (meshLib is null) return vehicleCatalog;
    if (!ReferenceEquals(vehCacheFor, meshLib))
    {
        vehCacheFor = meshLib;
        try { var v = meshLib.AssembledTemplateNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray(); vehCacheList = v.Length > 0 ? v : vehicleCatalog; }
        catch { vehCacheList = vehicleCatalog; }
    }
    return vehCacheList ?? vehicleCatalog;
}

// Geometry is unchanged by a move, so just recompute the placement matrices (cheap, no re-upload).
void NudgeSelected(float dx, float dy, float dz)
{
    if (so is null || hist is null || multi.Count == 0) return;
    var cmds = new List<IEditCommand>();
    foreach (var i in multi)
    {
        if (i < 0 || i >= so.Objects.Count) continue;
        var o = so.Objects[i];
        cmds.Add(new MoveObject(o.Id, new Vec3(o.Position.X + dx, o.Position.Y + dy, o.Position.Z + dz)));
    }
    if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
    SyncMarkers();
    glObjects?.Sync(so);
    UploadMarkers();
}

// Rotate the selected object(s) about yaw (the editor's X rotation axis) by `deg` — keyboard rotate (Alt+Left/Right).
void RotateSelectedYaw(float deg)
{
    if (so is null || hist is null || multi.Count == 0) return;
    var cmds = new List<IEditCommand>();
    foreach (var i in multi)
    {
        if (i < 0 || i >= so.Objects.Count) continue;
        var o = so.Objects[i];
        cmds.Add(new RotateObject(o.Id, new Vec3(o.Rotation.X + deg, o.Rotation.Y, o.Rotation.Z)));
    }
    if (cmds.Count > 0) hist.Do(new CompositeCommand(cmds));
    SyncMarkers();
    glObjects?.Sync(so);
    UploadMarkers();
}

// Clone the selected object(s) a few metres away (same template/rotation/scale), one undo step; select the clones.
void DuplicateSelected()
{
    if (so is null || hist is null || multi.Count == 0) return;
    var cmds = new List<IEditCommand>(); var newIds = new List<string>();
    foreach (var i in multi)
    {
        if (i < 0 || i >= so.Objects.Count) continue;
        var o = so.Objects[i];
        var id = Guid.NewGuid().ToString("N"); newIds.Add(id);
        cmds.Add(new AddObject(id, o.Template, new Vec3(o.Position.X + 4f, o.Position.Y, o.Position.Z + 4f), o.Rotation));
        if (o.Scale is float sc && MathF.Abs(sc - 1f) > 1e-3f) cmds.Add(new ScaleObject(id, sc));
    }
    if (cmds.Count == 0) return;
    hist.Do(new CompositeCommand(cmds));
    SyncMarkers(); RebuildObjects(); UploadMarkers();
    multi.Clear(); selected = -1;
    foreach (var id in newIds) { int idx = so.Objects.FindIndex(x => x.Id == id); if (idx >= 0) { multi.Add(idx); selected = idx; } }
}

// Drop the selected object(s) straight onto the terrain (Y = ground height at their XZ), one undo step.
void DropSelectedToGround()
{
    if (so is null || hist is null || terrainPick is null || multi.Count == 0) return;
    var cmds = new List<IEditCommand>();
    foreach (var i in multi)
    {
        if (i < 0 || i >= so.Objects.Count) continue;
        var o = so.Objects[i];
        cmds.Add(new MoveObject(o.Id, new Vec3(o.Position.X, terrainPick.HeightAt(o.Position.X, o.Position.Z), o.Position.Z)));
    }
    if (cmds.Count > 0) { hist.Do(new CompositeCommand(cmds)); SyncMarkers(); glObjects?.Sync(so); UploadMarkers(); }
}

// Add/delete shifts object indices, so the per-template instance lists are re-resolved. First time = a full Build;
// thereafter rebuild IN PLACE (reusing cached GPU templates) so an edit doesn't re-upload every mesh - this is what
// kept collaborative edits (and their local echo through the relay) from stalling the editor for seconds.
void RebuildObjects()
{
    if (meshLib is null || so is null) return;
    if (glObjects is null) glObjects = GlObjects.Build(gl, so, meshLib);
    else glObjects.Rebuild(gl, so, meshLib);
    if (objectLightmapsLoaded) glObjects.SetObjectLightmaps(gl, objectLightmaps, so, meshLib);   // re-match (only if already loaded)
    collisionDirty = true;   // object set/placement changed -> rebuild the collision overlay next time it's shown
    shadowMapDirty = true;   // object moved/added/deleted -> re-render the sun shadow map
}

// Bake a per-object lightmap for every placed object that carries lightmap UVs, from the CURRENT editor sun: ambient +
// sun N-L Ã— terrain cast-shadow, rendered into each object's lightmap-UV atlas (ObjectLightmapBaker). Shows the result
// immediately AND queues the .tga files so a Save writes them into the level (the engine then reads them). This is the
// object half of "bake lighting to the game"; the terrain half is the .lsb (File > Write LightmapShadowBits on Save).
void BakeObjectLightmaps()
{
    if (so is null || meshLib is null || heightmap is null) { Toast(Loc.T("Load a level with terrain first.")); return; }
    var es = EffectiveSun(); var sunV = new Vec3(es.X, es.Y, es.Z);
    // Resolve meshes serially (MeshLibrary's cache isn't thread-safe), then bake in parallel (the baker is pure).
    var jobs = new List<(StaticObject O, MeshLibrary.Mesh Mesh)>();
    foreach (var o in so.Objects)
        if (meshLib.TryGetRenderMesh(o.Template, out var mesh) && mesh.LightmapUvs is not null)
            jobs.Add((o, mesh));
    if (jobs.Count == 0) { Toast(Loc.T("No placed objects with bakeable lightmap UVs on this map.")); return; }
    var results = new Texture2D?[jobs.Count];
    System.Threading.Tasks.Parallel.For(0, jobs.Count, i =>
        results[i] = ObjectLightmapBaker.Bake(jobs[i].Mesh, LevelScene.MeshWorld(jobs[i].O), heightmap, cfg, sunV, 256));
    var olm = new ObjectLightmaps();
    bakedObjectLightmaps.Clear();
    int baked = 0;
    for (int i = 0; i < jobs.Count; i++)
    {
        if (results[i] is not { } tex) continue;
        var o = jobs[i].O;
        int x = (int)o.Position.X, y = (int)o.Position.Y, z = (int)o.Position.Z;
        olm.AddBaked(o.Template, x, y, z, tex);
        bakedObjectLightmaps[$"{o.Template}_{x}-{y}-{z}.tga"] = TgaTexture.EncodeGrayColormapped(tex);
        baked++;
    }
    objectLightmaps = olm; objectLightmapsLoaded = true;
    sunOverride = false; showObjectLightmaps = true;     // turn off the dynamic-sun preview so the baked result shows
    glObjects?.SetObjectLightmaps(gl, objectLightmaps, so, meshLib);
    Console.WriteLine($"Baked {baked} object lightmap(s) (256^2) from the editor sun.");
    Toast($"Baked {baked} object lightmap(s) from the current sun. Save (Ctrl+S) writes them to the level.");
}

// Lazily decode + match the level's per-object lightmaps the first time the layer is enabled. The decode re-opens the
// level .rfa (or scans the folder), so it's deliberately kept OFF the load path. No-op after the first successful call.
void EnsureObjectLightmaps()
{
    if (objectLightmapsLoaded || glObjects is null || so is null) return;
    objectLightmapsLoaded = true;
    objectLightmaps = rfaList.Length > 0 ? ObjectLightmaps.FromRfaPaths(rfaList)
                    : (levelDir is not null && Directory.Exists(levelDir) ? ObjectLightmaps.FromFolder(levelDir) : null);
    glObjects.SetObjectLightmaps(gl, objectLightmaps, so, meshLib);
    if (glObjects.LightmapInstanceCount > 0)
        Console.WriteLine($"Object lightmaps: {objectLightmaps?.Count ?? 0} baked tga(s), {glObjects.LightmapInstanceCount} object(s) lit.");
}

void UploadMarkers()
{
    if (pointMarkers.Length == 0) { gl.BindVertexArray(markerVao); return; }
    var mv = new float[pointMarkers.Length * 3];
    for (int i = 0; i < pointMarkers.Length; i++) { mv[i * 3] = pointMarkers[i].X; mv[i * 3 + 1] = pointMarkers[i].Y; mv[i * 3 + 2] = pointMarkers[i].Z; }
    gl.BindVertexArray(markerVao);
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, markerVbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, mv, BufferUsageARB.DynamicDraw);
}

// Push the current fog state to a shader program (set by name; cheap enough per-frame).
void SetFogUniforms(uint prog)
{
    gl.Uniform1(gl.GetUniformLocation(prog, "uFogEnable"), fogEnabled ? 1 : 0);
    gl.Uniform3(gl.GetUniformLocation(prog, "uFogColor"), fogColor.X, fogColor.Y, fogColor.Z);
    gl.Uniform1(gl.GetUniformLocation(prog, "uFogStart"), fogStart);
    gl.Uniform1(gl.GetUniformLocation(prog, "uFogEnd"), fogEnd);
    gl.Uniform3(gl.GetUniformLocation(prog, "uCamPos"), cam.Position.X, cam.Position.Y, cam.Position.Z);
}

void OnRender(double dt)
{
    // Minimized (incl. when the launching CMD window is minimized and takes the app down with it): the framebuffer
    // collapses to 0x0, which makes the GL viewport / aspect-ratio math degenerate and crashes. Skip the frame.
    var fbSize = window.FramebufferSize;
    if (fbSize.X <= 0 || fbSize.Y <= 0) return;
    // Sync the GL viewport + camera aspect to the live framebuffer size when it changes. The initial Maximized open
    // doesn't reliably fire FramebufferResize, so without this the first frames render at the old 1280x800 size
    // (content in the lower-left, rest uncleared) until you minimize/maximize. Apply-on-change covers that.
    if (fbSize != appliedFbSize)
    {
        gl.Viewport(0, 0, (uint)fbSize.X, (uint)fbSize.Y);
        cam.Aspect = fbSize.X / (float)Math.Max(1, fbSize.Y);
        appliedFbSize = fbSize;
    }
    lastFps = dt > 0 ? 1.0 / dt : lastFps;
    if (toastT > 0f) toastT -= (float)dt;   // fade out the transient status-bar confirmation
    if (pathmapPreviewT > 0f) { pathmapPreviewT -= (float)dt; if (pathmapPreviewT <= 0f) pathmapPreviewOpen = false; }   // auto-close the post-save pathmap preview
    appClock += dt;              // advance the water-ripple animation
    if (meshViewerOpen && meshViewerAutoRotate) meshViewerYaw += (float)dt * 0.6f;   // spin the model viewer
    imgui.Update((float)dt);     // begin a new ImGui frame
    BuildUi();                   // record the editor panels' draw data

    // 3D scene. ImGui's previous Render() leaves blend on / depth off / scissor set, so re-assert.
    gl.Disable(EnableCap.ScissorTest);
    gl.Disable(EnableCap.Blend);
    gl.Enable(EnableCap.DepthTest);
    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

    // Sky background. Order of preference: the level's CUBEMAP faces (a map's AltTex override, e.g. Immersed's hi-res
    // underwater Sky_Bocage_0N) > the real skybox MESH (when the level doesn't override via cubemap faces) > procedural.
    // The cubemap wins over the mesh because a map that ships AltTex faces intends THOSE, not the base mesh's own
    // (daytime) textures. Drawn first with depth off so all geometry overpaints it.
    if (showSky && skyMeshOk && skyMeshProg != 0 && !(skyCubeTex != 0 && skyUseCubemap))
    {
        DrawSkyMesh(skyMeshVao, skyMeshParts, Vector2.Zero, opaque: true);
    }
    else if (showSky && skyProg != 0)
    {
        gl.Disable(EnableCap.DepthTest);
        gl.UseProgram(skyProg);
        // Reconstruct world view rays from the inverse view-projection (same matrix convention as the terrain MVP).
        Matrix4x4.Invert(cam.ViewProjection, out var invVP);
        unsafe { gl.UniformMatrix4(uInvVPS, 1, false, (float*)&invVP); }
        gl.Uniform3(uCamPosS, cam.Position.X, cam.Position.Y, cam.Position.Z);
        var sky_sun = EffectiveSun();   // honour the manual sun control (sky glow follows the sun too)
        gl.Uniform3(uSunDirS, sky_sun.X, sky_sun.Y, sky_sun.Z);
        gl.Uniform3(uFogColorS, fogColor.X, fogColor.Y, fogColor.Z);
        gl.Uniform1(uRotS, ((env?.SkyRotationAngle ?? 0f) + skyRotDeg) * MathF.PI / 180f);
        bool useCube = skyUseCubemap && skyCubeTex != 0;
        gl.Uniform1(uHasCubeS, useCube ? 1 : 0);
        if (useCube) { gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.TextureCubeMap, skyCubeTex); gl.Uniform1(uCubeS, 0); }
        // Animated clouds: scroll the cloud texture by appClock * speed. Keep the cloud sampler on unit 1 always
        // (distinct from the cubemap on unit 0) so the two sampler types never share a unit.
        if (cloudTex != 0) { gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, cloudTex); gl.Uniform1(uCloudTexS, 1); gl.ActiveTexture(TextureUnit.Texture0); }
        gl.Uniform1(uHasCloudS, (cloudsOn && cloudTex != 0) ? 1 : 0);
        if (cloudsOn && cloudTex != 0)
        {
            gl.Uniform3(uCloudColorS, cloudColor.X, cloudColor.Y, cloudColor.Z);
            gl.Uniform2(uCloudScrollS, (float)(appClock * cloudSpeedX), (float)(appClock * cloudSpeedY));
            gl.Uniform1(uCloudScaleS, cloudScale);
            gl.Uniform1(uCloudOpacityS, cloudOpacity);
        }
        gl.BindVertexArray(skyVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.Enable(EnableCap.DepthTest);
    }

    // Real cloud-layer meshes (the scrolling clouds / underwater bubbles) over either sky path.
    if (showSky && cloudMeshOk && showCloudMesh && skyMeshProg != 0 && env is not null)
        for (int ci = 0; ci < env.Clouds.Count; ci++)
        {
            var cl = env.Clouds[ci];
            DrawSkyMesh(cloudMeshVao, cloudMeshParts,
                        new Vector2((float)(appClock * cl.SpeedX), (float)(appClock * cl.SpeedY)), opaque: false);
        }

    var mvp = ToFloats(cam.ViewProjection);
    // Sun/key-light direction: the manual azimuth/elevation control, else the level's SkyAndSun.con. Drives terrain +
    // object shading AND the real-time shadow map.
    var ld = EffectiveSun();
    bool painting = toolNames[tool] == "Paint";   // hide objects/markers for a clear terrain view while painting (Battlecraft-style)

    if (terrainDirty) { RebuildTerrain(); terrainDirty = false; shadowMapDirty = true; }   // re-upload after this frame's sculpt dabs

    // Real-time sun shadow map: re-render terrain + nearby objects from the sun, centred on the camera's view, when the
    // sun, geometry, or the focus/zoom changed enough (camera panning across the map keeps the sharp area under you).
    bool shadowsOn = showShadows && heightmap is not null;
    if (shadowsOn)
    {
        var focus = ShadowFocus(); float radius = ShadowRadius();
        bool moved = (focus - lastShadowFocus).Length() > radius * 0.25f || MathF.Abs(radius - lastShadowRadius) > radius * 0.2f;
        if (shadowMapDirty || moved)
            try { RenderShadowMap(ld, focus, radius); lastShadowFocus = focus; lastShadowRadius = radius; }
            catch (Exception ex) { Console.WriteLine("Shadow map render failed, disabling: " + ex.Message); showShadows = false; shadowMapDirty = false; shadowsOn = false; }
    }

    if (gridOn && !gridPrevOn) gridDirty = true;                    // re-drape when the user re-enables Grid
    gridPrevOn = gridOn;
    if (gridOn && gridDirty && terrainPick is not null) { BuildGrid(); gridDirty = false; }

    if (showTerrain)
    {
        gl.UseProgram(terrainProg);
        gl.UniformMatrix4(uMvp, 1, false, mvp);
        gl.Uniform3(uLight, ld.X, ld.Y, ld.Z);
        gl.Uniform1(uWater, cfg.WaterLevel);
        gl.Uniform1(uMaxH, maxH);
        gl.Uniform3(uDeepColor, deepColor.X, deepColor.Y, deepColor.Z);
        SetFogUniforms(terrainProg);
        if (terrainTexId != 0) { gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, terrainTexId); }
        if (detailTexId != 0) { gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, detailTexId); gl.ActiveTexture(TextureUnit.Texture0); }
        gl.Uniform1(uUseShadowMapT, (shadowsOn && shadowMapDepthTex != 0) ? 1 : 0);
        unsafe { var ls = lightSpace; gl.UniformMatrix4(uLightSpaceT, 1, false, (float*)&ls); }
        if (shadowMapDepthTex != 0) { gl.ActiveTexture(TextureUnit.Texture3); gl.BindTexture(TextureTarget.Texture2D, shadowMapDepthTex); gl.ActiveTexture(TextureUnit.Texture0); }
        bool showPaint = toolNames[tool] == "Paint" && paintLayer != 3 && matTexId != 0;   // Texture layer shows the real atlas, no tint
        bool showNav = toolNames[tool] == "AIPath";
        if (showNav) { EnsureAiNav(); if (aiNavTexDirty) UploadAiNavTexture(); if (aiNav is null || aiNavTexId == 0) showNav = false; }
        gl.Uniform1(uShowMat, showPaint ? (paintLayer == 0 ? 1 : 2) : (showNav ? 3 : 0));
        if (showPaint) { gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, matTexId); gl.ActiveTexture(TextureUnit.Texture0); }
        else if (showNav) { gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, aiNavTexId); gl.ActiveTexture(TextureUnit.Texture0); }
        gl.BindVertexArray(terrainVao);
        unsafe { gl.DrawElements(PrimitiveType.Triangles, (uint)terrainIndexCount, DrawElementsType.UnsignedInt, (void*)0); }
    }

    DrawGrid();   // draped world-grid overlay (depth-tested, on the ground), if enabled

    // Texture transparency: blend object + foliage alpha (the shader's alpha-test discard does the hard cutout,
    // blending softens the edges). Opaque parts output alpha=1.0 so they're unaffected.
    if (alphaTransparency) { gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
    gl.UseProgram(objProg); gl.Uniform1(uAlphaEnableO, alphaTransparency ? 1 : 0);   // toggle off -> no discard + opaque output (objects revert to solid)

    // Real object geometry (GPU). Selected object is tinted via the highlight colour.
    if (glObjects is not null && showObjects && !painting)
    {
        gl.UseProgram(objProg);
        gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
        SetFogUniforms(objProg);
        gl.Uniform1(uUseShadowMapO, (shadowsOn && shadowMapDepthTex != 0) ? 1 : 0);
        unsafe { var lso = lightSpace; gl.UniformMatrix4(uLightSpaceO, 1, false, (float*)&lso); }
        if (shadowMapDepthTex != 0) { gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, shadowMapDepthTex); gl.ActiveTexture(TextureUnit.Texture0); }
        // Baked object lighting unless you're driving the sun manually - then objects light DYNAMICALLY (N-L + the
        // real-time shadow map) so they respond to the sun in real time (a static baked lightmap can't move with it).
        bool wantLm = showObjectLightmaps && !sunOverride;
        if (wantLm) EnsureObjectLightmaps();   // lazy: only decode the lightmaps when they're actually about to be shown
        glObjects.ShowLightmaps = wantLm;
        glObjects.Draw(gl, objProg, uMvpO, uModelO, uColorO, uUseTexO, uAlphaTestO, uTintO,
                       cam.ViewProjection, multi, selected, new Vector3(1.5f, 1.4f, 0.4f));

        // Continuously-rotating object parts (BF1942 RotationalBundle: windmill blades, watermill wheel, mod fans/rotors).
        // These are kept OUT of the static flattened mesh, so they're drawn here (always — else the part vanishes when
        // the toggle's off); the "Animate objects" toggle only controls whether they SPIN (else they sit at rest angle).
        if (meshLib is not null)
            foreach (var (tmpl, world, _) in glObjects.Placements)
            {
                if (!meshLib.TryGetAnimatedParts(tmpl, out var aparts)) continue;
                float t = showAnimations ? (float)appClock : 0f;
                for (int ai = 0; ai < aparts.Length; ai++)
                {
                    var ap = aparts[ai];
                    // Spin about the pivot using the editor's own (X,Y,Z)->yaw/pitch/roll convention (same as static
                    // placement rotations) — this matched the in-game windmill; the earlier "weird" look was the doubled
                    // static+spinning blades, now fixed by excluding rotating parts from the static mesh.
                    var spin = Matrix4x4.CreateTranslation(-ap.Pivot)
                             * Matrix4x4.CreateFromYawPitchRoll(ap.SpeedDeg.X * t * MathF.PI / 180f,
                                                                ap.SpeedDeg.Y * t * MathF.PI / 180f,
                                                                ap.SpeedDeg.Z * t * MathF.PI / 180f)
                             * Matrix4x4.CreateTranslation(ap.Pivot);
                    var model = spin * ap.StaticLocal * world;
                    glObjects.DrawMesh(gl, objProg, uMvpO, uModelO, uColorO, uUseTexO, uAlphaTestO, uTintO,
                                       cam.ViewProjection, $"anim::{tmpl}::{ai}", ap.Mesh, model, Vector3.One);
                }
            }

        // Vehicle spawns: draw each one's FULL assembled mesh (hull + turret + barrel + wheels + treads).
        if (showVehicles && meshLib is not null && glObjects is not null)
        {
            gl.UseProgram(objProg);
            gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
            SetFogUniforms(objProg);
            foreach (var v in gameplayEdit.VehicleSpawns)
            {
                // Show the spawner's owning-team vehicle (SpawnVehicleName), not the team-2-preferred display fallback,
                // so enemy-team spawners render their real vehicle. NO tint — render exactly as a placed object (the
                // distinct team models are the faction cue; a colour cast looked wrong on the vehicle textures).
                string veh = SpawnVehicleName(v);
                // TryGetRenderMesh (assembled vehicle -> single mesh -> generic Bundle) resolves custom map vehicles in any
                // folder (interstate le_mans Big_Ear/P71_Marked...), not just /Vehicles/.
                if (string.IsNullOrWhiteSpace(veh) || !meshLib.TryGetRenderMesh(veh, out var vmesh) || vmesh is null) continue;
                var spawnWorld = Matrix4x4.CreateFromYawPitchRoll(
                                     v.Rotation.X * MathF.PI / 180f, v.Rotation.Y * MathF.PI / 180f, v.Rotation.Z * MathF.PI / 180f)
                               * Matrix4x4.CreateTranslation(v.Position.X, v.Position.Y, v.Position.Z);
                glObjects.DrawMesh(gl, objProg, uMvpO, uModelO, uColorO, uUseTexO, uAlphaTestO, uTintO,
                                   cam.ViewProjection, $"veh::{veh}", vmesh, spawnWorld, Vector3.One);
            }
        }

        // Control points get a flagpole; soldier spawns get the engine's spawn-marker mesh.
        if (meshLib is not null && glObjects is not null)
        {
            gl.UseProgram(objProg);
            gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
            SetFogUniforms(objProg);
            void DrawGp(string key, MeshLibrary.Mesh mesh, Vec3 pos, Vec3 rot, Vector3? solidColor = null)
            {
                var w = Matrix4x4.CreateFromYawPitchRoll(rot.X * MathF.PI / 180f, rot.Y * MathF.PI / 180f, rot.Z * MathF.PI / 180f)
                      * Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z);
                glObjects.DrawMesh(gl, objProg, uMvpO, uModelO, uColorO, uUseTexO, uAlphaTestO, uTintO,
                                   cam.ViewProjection, key, mesh, w, Vector3.One, solidColor);
            }
            // Soldier spawn marker: a simple soldier-sized box (NOT the engine's 3-arrow spawn mesh), oriented to yaw.
            if (showSpawns)
                foreach (var s in gameplayEdit.SoldierSpawns) DrawGp("gp::soldbox", SoldierBoxMesh(), s.Position, s.Rotation);
            // Control point: the flag-pole/base mesh (flagbase_m1) + the owning team's flag cloth at the mount height.
            // Mesh names come from the CP template (geometry + setTeamGeometry), defaulting to the stock BF1942 flags.
            // Both meshes are textured (no tint needed); each is drawn only when it resolves, so a BFV map that names a
            // mesh it doesn't ship simply shows no pole (graceful).
            if (showControlPoints)
                foreach (var cp in gameplayEdit.ControlPoints)
                {
                    var poleName = string.IsNullOrEmpty(cp.PoleGeometry) ? "flagbase_m1" : cp.PoleGeometry;
                    if (meshLib.TryGet(poleName, out var pole) && pole is not null)
                        DrawGp($"gp::cp::{poleName}", pole, cp.Position, Vec3.Zero);
                    var flagName = cp.Team == 1 ? cp.FlagGeometry1 : cp.FlagGeometry2;   // team1=axis, team2=allied cloth; neutral(0) uses this shape but rendered WHITE (below)
                    if (!string.IsNullOrEmpty(flagName) && meshLib.TryGet(flagName, out var flag) && flag is not null)
                    {
                        float fy = cp.FlagHeight > 0 ? cp.FlagHeight : 8.2f;
                        // Hang the flag from near the TOP of the actual pole mesh (a touch below the finial), not the fixed
                        // mount height, so it sits high on poles of any height instead of leaving bare pole above it.
                        float poleTopY = fy;
                        if (pole is not null && pole.Positions.Length > 0)
                        { float mxY = pole.Positions[0].Y; foreach (var pp in pole.Positions) if (pp.Y > mxY) mxY = pp.Y; poleTopY = mxY - 0.5f; }
                        // Re-anchor the flag cloth onto the pole. The 180° roll rights it (local (x,y)->(-x,-y)); from the
                        // mesh's own AABB we then place its TOP at the pole top and its near (hoist) edge AT the pole so it
                        // flies out. (If it flies the wrong way or the canton ends up at the free end, flip fhi.X -> flo.X.)
                        var fpos0 = flag.Positions;
                        if (fpos0.Length > 0)
                        {
                            Vector3 flo = fpos0[0], fhi = fpos0[0];
                            foreach (var p in fpos0) { flo = Vector3.Min(flo, p); fhi = Vector3.Max(fhi, p); }
                            const float hoistInset = 0.05f;   // forward: hoist edge sits just in front of the pole
                            const float flagLateral = 0.4f;   // "right": shift the flag laterally (Z) to line up with the pole (flip sign if it goes the wrong way)
                            const float flagRise = 0.8f;      // lift the cloth ~2.5 ft up the pole (user request)
                            var fpos = new Vec3(cp.Position.X + fhi.X - hoistInset, cp.Position.Y + poleTopY + flo.Y + flagRise, cp.Position.Z - (flo.Z + fhi.Z) * 0.5f + flagLateral);
                            DrawGp($"gp::cpflag::{flagName}", flag, fpos, new Vec3(0f, 0f, 180f), cp.Team == 0 ? new Vector3(0.9f, 0.9f, 0.9f) : (Vector3?)null);   // neutral CP -> white flag
                        }
                    }
                }
        }
    }

    // Overgrowth foliage overlay (a VIEW of the .wst trees; ephemeral, never saved). Distance-culled so a dense
    // map stays interactive. Rebuilt lazily when toggled on / the spacing changed (foliageDirty).
    if (glObjects is not null && showFoliage && !painting)
    {
        if (foliageDirty) BuildOvergrowthFoliage();
        if (glObjects.FoliageInstanceCount > 0)
        {
            gl.UseProgram(objProg);
            gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
            SetFogUniforms(objProg);
            float vd = growth?.OverPalette?.ViewDistance ?? 0f;
            float cull = vd > 0f ? vd : (fogEnabled ? fogEnd : (float)cfg.WorldSize * 0.4f);
            cull = Math.Clamp(cull, 100f, (float)cfg.WorldSize);
            glObjects.DrawFoliage(gl, objProg, uMvpO, uModelO, uColorO, uUseTexO, uAlphaTestO, uTintO,
                                  cam.ViewProjection, cam.Position, cull);
        }
    }

    if (alphaTransparency) gl.Disable(EnableCap.Blend);   // restore opaque state; water sets its own blend below

    // Water surface: translucent plane at the level's water height, blended over terrain + objects
    // (depth-test on so submerged terrain still occludes it, depth-write off so it doesn't occlude).
    if (showWater && waterProg != 0)
    {
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.DepthMask(false);
        gl.UseProgram(waterProg);
        gl.UniformMatrix4(uMvpW, 1, false, mvp);
        gl.Uniform3(uLightW, ld.X, ld.Y, ld.Z);
        gl.Uniform3(uCamW, cam.Position.X, cam.Position.Y, cam.Position.Z);
        gl.Uniform1(uTimeW, (float)appClock);
        gl.Uniform1(uWaterYW, cfg.WaterLevel);          // live: follows the Water Level slider
        gl.Uniform3(uWaterColorW, waterColor.X, waterColor.Y, waterColor.Z);   // level's water.color
        gl.Uniform1(uWaterAlphaW, waterAlpha);                                  // level's transparency
        bool wtex = haveWaterTex && useWaterTextures && env is not null;
        gl.Uniform1(uHasWaterTexW, wtex ? 1 : 0);
        if (wtex)
        {
            gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, waterTex1); gl.Uniform1(uTexL1W, 0);
            gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, waterTex2); gl.Uniform1(uTexL2W, 1);
            gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, waterNorm); gl.Uniform1(uNormalW, 2);
            gl.Uniform2(uScroll1W, env.ScrollDir1X * env.ScrollSpeed1, env.ScrollDir1Y * env.ScrollSpeed1);   // dir * speed
            gl.Uniform2(uScroll2W, env.ScrollDir2X * env.ScrollSpeed2, env.ScrollDir2Y * env.ScrollSpeed2);
            gl.Uniform2(uScrollNW, env.ScrollDirNX * env.ScrollSpeedN, env.ScrollDirNY * env.ScrollSpeedN);
            gl.Uniform1(uTile1W, env.TileLayer1); gl.Uniform1(uTile2W, env.TileLayer2); gl.Uniform1(uTileNW, env.TileNormal);
            gl.Uniform3(uSpecColW, env.WaterSpecularColor.X, env.WaterSpecularColor.Y, env.WaterSpecularColor.Z);
            gl.ActiveTexture(TextureUnit.Texture0);
        }
        SetFogUniforms(waterProg);                      // fade distant water into the fog like the terrain
        gl.BindVertexArray(waterVao);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        gl.DepthMask(true);
        gl.Disable(EnableCap.Blend);
    }

    // Markers: mesh-less objects (sound/effect emitters, logical points) get a visible 3D indicator
    // diamond so their location is clear. When no mesh library is loaded, every object is a point.
    if (pointMarkers.Length > 0 && showObjects && !painting)
    {
        if (glObjects is not null && indicatorVao != 0)
        {
            // Lit 3D diamonds via the object shader, sized in world-space but kept legible at distance.
            gl.UseProgram(objProg);
            gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
            gl.Uniform1(uUseTexO, 0);
            gl.Uniform1(uAlphaTestO, 0);
            gl.Uniform3(uTintO, 1f, 1f, 1f);
            gl.Uniform3(uColorO, 1f, 0.85f, 0.15f);   // amber
            gl.BindVertexArray(indicatorVao);
            foreach (var m in pointMarkers)
            {
                float s = Math.Clamp(Vector3.Distance(cam.Position, m) * 0.006f, 0.3f, 4f);   // ~constant on-screen size
                var world = Matrix4x4.CreateScale(s, s * 1.6f, s) * Matrix4x4.CreateTranslation(m.X, m.Y + s * 1.6f, m.Z);
                var mvpM = world * cam.ViewProjection; var modelM = world;
                unsafe   // upload matrices from memory; no per-marker float[] alloc (GC-stutter on 170-marker maps)
                {
                    gl.UniformMatrix4(uMvpO, 1, false, (float*)&mvpM);
                    gl.UniformMatrix4(uModelO, 1, false, (float*)&modelM);
                    gl.DrawElements(PrimitiveType.Triangles, (uint)indicatorCount, DrawElementsType.UnsignedInt, (void*)0);
                }
            }
        }
        else
        {
            gl.UseProgram(markerProg);
            gl.UniformMatrix4(uMvpM, 1, false, mvp);
            gl.BindVertexArray(markerVao);
            gl.Uniform3(uColor, 1f, 0.9f, 0.2f); gl.Uniform1(uSize, 5f);
            gl.DrawArrays(PrimitiveType.Points, 0, (uint)pointMarkers.Length);
            // Legacy point-highlight only applies when markers map 1:1 to object indices (no mesh library).
            if (selected >= 0 && selected < pointMarkers.Length)
            {
                gl.Disable(EnableCap.DepthTest);
                gl.Uniform3(uColor, 1f, 0.15f, 0.1f); gl.Uniform1(uSize, 13f);
                gl.DrawArrays(PrimitiveType.Points, selected, 1);
                gl.Enable(EnableCap.DepthTest);
            }
        }
    }

    // Collaboration presence: a coloured diamond at each peer's camera, and one over the object they have selected.
    if (collab is not null && collab.Peers.Count > 0 && indicatorVao != 0)
    {
        gl.UseProgram(objProg);
        gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
        gl.Uniform1(uUseTexO, 0); gl.Uniform1(uAlphaTestO, 0); gl.Uniform3(uTintO, 1f, 1f, 1f);
        gl.BindVertexArray(indicatorVao);
        int pidx = 0;
        // Collect each peer's diamond centre + heading so we can draw the look-direction pointers in one line pass after.
        var pointers = new System.Collections.Generic.List<(Vector3 At, float Heading, Vector3 Col, float Scale)>();
        foreach (var peer in collab.Peers.Values)
        {
            var col = peerColors[pidx++ % peerColors.Length];
            gl.Uniform3(uColorO, col.X, col.Y, col.Z);
            // The diamond is yaw-rotated so its long (forward) axis aims along the peer's heading - paired with the
            // pointer line below this makes the look direction obvious to everyone collaborating.
            void PeerDiamond(Vector3 at, float scale, float heading)
            {
                var world = Matrix4x4.CreateScale(scale * 0.78f, scale * 1.6f, scale * 1.25f)
                          * Matrix4x4.CreateRotationY(heading)
                          * Matrix4x4.CreateTranslation(at.X, at.Y, at.Z);
                var mvpM = world * cam.ViewProjection; var modelM = world;
                unsafe { gl.UniformMatrix4(uMvpO, 1, false, (float*)&mvpM); gl.UniformMatrix4(uModelO, 1, false, (float*)&modelM); gl.DrawElements(PrimitiveType.Triangles, (uint)indicatorCount, DrawElementsType.UnsignedInt, (void*)0); }
            }
            // The "person" diamond floating at the peer's camera - screen-constant size (this one's the keeper).
            var cur = new Vector3(peer.Cursor.X, peer.Cursor.Y, peer.Cursor.Z);
            float ds = Math.Clamp(Vector3.Distance(cam.Position, cur) * 0.012f, 1.5f, 14f);
            PeerDiamond(cur, ds, peer.Heading);
            pointers.Add((cur, peer.Heading, col, ds));
            // The marker over the object they have selected - same screen-constant sizing as the person, 20% bigger.
            if (peer.SelectionId != "-" && so is not null && so.FindById(peer.SelectionId) is { } po)
            {
                var pp = new Vector3(po.Position.X, po.Position.Y + 4f, po.Position.Z);
                PeerDiamond(pp, Math.Clamp(Vector3.Distance(cam.Position, pp) * 0.012f, 1.5f, 14f) * 1.2f, 0f);
            }
        }

        // Look-direction pointer: a short coloured line from each peer's diamond along their heading, so the way
        // they're facing reads unambiguously (the rotated diamond alone is near-symmetric front/back).
        if (pointers.Count > 0)
        {
            gl.UseProgram(markerProg);
            gl.UniformMatrix4(uMvpM, 1, false, ToFloats(cam.ViewProjection));
            gl.BindVertexArray(gizmoVao);
            foreach (var (at, heading, col, scale) in pointers)
            {
                var dir = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
                var tip = at + dir * (scale * 3.0f);
                float[] line = { at.X, at.Y, at.Z, tip.X, tip.Y, tip.Z };
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, gizmoVbo);
                gl.BufferData<float>(BufferTargetARB.ArrayBuffer, line, BufferUsageARB.DynamicDraw);
                gl.Uniform3(uColor, col.X, col.Y, col.Z); gl.Uniform1(uSize, 1f);
                gl.DrawArrays(PrimitiveType.Lines, 0, 2);
            }
        }
    }

    // Placement preview: a bright marker on the terrain under the cursor while Place is armed.
    if (terrainPick is not null && toolNames[tool] == "Place" && browserTemplate is not null && !UiWantsMouse())
    {
        var fbp = window.FramebufferSize;
        var pray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fbp.X, fbp.Y);
        if (terrainPick.Raycast(pray, out var pp))
        {
            float[] one = { pp.X, pp.Y, pp.Z };
            gl.BindVertexArray(previewVao);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, previewVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, one, BufferUsageARB.DynamicDraw);
            gl.UseProgram(markerProg);
            gl.UniformMatrix4(uMvpM, 1, false, mvp);
            gl.Uniform3(uColor, 0.3f, 1f, 0.4f); gl.Uniform1(uSize, 16f);
            gl.Disable(EnableCap.DepthTest);
            gl.DrawArrays(PrimitiveType.Points, 0, 1);
            gl.Enable(EnableCap.DepthTest);
        }
    }

    // Terrain brush: ring on the ground at the cursor showing the brush radius.
    if (terrainPick is not null && toolNames[tool] is "Sculpt" or "Smooth" or "Paint" or "AIPath" && !UiWantsMouse())
    {
        var fbb = window.FramebufferSize;
        var bray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fbb.X, fbb.Y);
        if (terrainPick.Raycast(bray, out var bp))
        {
            gl.UseProgram(markerProg);
            gl.UniformMatrix4(uMvpM, 1, false, ToFloats(cam.ViewProjection));   // the draped outline is built in world space
            bool lower = kb is not null && (kb.IsKeyPressed(Key.ShiftLeft) || kb.IsKeyPressed(Key.ShiftRight));
            if (toolNames[tool] == "Paint") { var pc = paintLayer == 3 ? texSwatch[activeTexture & 15] : matPalette[activeMaterial & 15]; gl.Uniform3(uColor, pc.X, pc.Y, pc.Z); }
            else if (toolNames[tool] == "Smooth") gl.Uniform3(uColor, 0.4f, 0.8f, 1f);
            else if (toolNames[tool] == "AIPath") { if (aiPathBlock) gl.Uniform3(uColor, 1f, 0.4f, 0.4f); else gl.Uniform3(uColor, 0.4f, 0.7f, 1f); }   // red=blocked, blue=passable
            else if (lower) gl.Uniform3(uColor, 1f, 0.5f, 0.3f);
            else gl.Uniform3(uColor, 0.4f, 1f, 0.5f);
            gl.Uniform1(uSize, 1f);
            gl.Disable(EnableCap.DepthTest);
            // Square footprint only when the procedural box brush is actually active. Terrain/material honour a
            // bitmap shape (index > 0) which carries its own footprint and overrides Square; the SURFACE/texture
            // painter (paintLayer 3) has no bitmap shapes (its stroke uses squareBrush directly), so there the box
            // brush is square purely on squareBrush -- a stale brushShapeIdx from another mapper must not force a
            // circle cursor over a square paint.
            bool surfacePaint = toolNames[tool] == "Paint" && paintLayer == 3;
            bool aiPath = toolNames[tool] == "AIPath";   // AI nav paint: no bitmap shapes, square purely on squareBrush
            // The cursor is a square when a square FOOTPRINT is active: the procedural box brush (squareBrush, no
            // bitmap shape; the surface/AI-path painters have no shapes) OR the "Square" bitmap shape (its mask paints
            // a hard square). A non-square bitmap (Round, etc.) keeps the radius ring.
            bool squareBitmap = !surfacePaint && !aiPath && brushShapeIdx > 0 && brushShapeIdx < brushShapeNames.Length
                                && brushShapeNames[brushShapeIdx].IndexOf("square", StringComparison.OrdinalIgnoreCase) >= 0;
            bool sqPrev = squareBitmap || (squareBrush && (surfacePaint || aiPath || brushShapeIdx == 0));
            DrawDrapedBrushOutline(bp.X, bp.Z, brushRadius, sqPrev);   // follows the terrain + hovers above it, like the grid
            gl.Enable(EnableCap.DepthTest);

            // Identify the active material/foliage as a floating label on the ground at the brush cursor.
            if (toolNames[tool] == "Paint")
            {
                var sp = Gizmo.Project(new Vector3(bp.X, bp.Y, bp.Z), cam.ViewProjection, fbb.X, fbb.Y);
                if (!float.IsNaN(sp.X))
                {
                    int mslot = matToSurf[activeMaterial & 15] & 15;   // material index -> its surface (real name + colour)
                    string lbl = paintLayer == 3
                        ? $"{(activeTexture < surfNames.Length ? surfNames[activeTexture] : "?")}  #{activeTexture}"
                        : paintLayer == 0
                            ? $"{(mslot < surfNames.Length ? surfNames[mslot] : "?")}  #{activeMaterial}"
                            : $"{(paintLayer == 1 ? "Undergrowth" : "Overgrowth")}  #{activeFoliage}{(activeFoliage == 0 ? " (clear)" : "")}";
                    var sw = paintLayer == 3 ? texSwatch[activeTexture & 15]
                           : paintLayer == 0 ? texSwatch[mslot] : matPalette[activeFoliage & 15];
                    uint tc = ImGui.GetColorU32(new Vector4(sw.X, sw.Y, sw.Z, 1f));
                    var fgl = ImGui.GetForegroundDrawList();
                    var at = new Vector2(sp.X + 14f, sp.Y + 12f);
                    fgl.AddText(at + new Vector2(1f, 1f), 0xFF000000, lbl);   // shadow for legibility over any terrain
                    fgl.AddText(at, tc, lbl);
                }
            }
        }
    }

    // Paint cursor: over the viewport while painting, hide the OS arrow and draw a paintbrush glyph in the
    // active colour at the pointer, so it's obvious you're painting (the ground ring shows the radius).
    if (mouse is not null)
    {
        bool brushCursor = painting && !UiWantsMouse();
        mouse.Cursor.CursorMode = brushCursor ? CursorMode.Hidden : CursorMode.Normal;
        if (brushCursor)
        {
            var mp = ImGui.GetMousePos();
            var sw = paintLayer == 3 ? texSwatch[activeTexture & 15]
                   : paintLayer == 0 ? matPalette[activeMaterial & 15] : matPalette[activeFoliage & 15];
            uint pcol = ImGui.GetColorU32(new Vector4(sw.X, sw.Y, sw.Z, 1f));
            uint handleCol = ImGui.GetColorU32(new Vector4(0.45f, 0.30f, 0.15f, 1f));
            uint ferruleCol = ImGui.GetColorU32(new Vector4(0.72f, 0.72f, 0.78f, 1f));
            var fg = ImGui.GetForegroundDrawList();
            var fer = mp + new Vector2(9f, -11f);
            fg.AddLine(fer, mp + new Vector2(21f, -25f), handleCol, 4f);                                 // wooden handle
            fg.AddTriangleFilled(mp, mp + new Vector2(11f, -6f), mp + new Vector2(5f, -13f), pcol);      // bristles
            fg.AddCircleFilled(fer, 4f, ferruleCol);                                                     // metal ferrule
            fg.AddCircleFilled(mp, 2f, pcol);                                                            // paint dab at the tip
        }
    }

    if (!painting) DrawGameplay();
    if (!painting) DrawSounds();
    if (!painting) DrawCollision();
    DrawWeather();   // weather preview particles (depth-tested so terrain occludes them)
    DrawEffects();   // the level's particle effects (waterfalls/lava/fire/smoke), billboards, depth-tested
    DrawGridLabels();
    DrawMeasure();
    DrawRoad();
    DrawGizmos();

    fpsFrames++; fpsTimer += dt;
    if (fpsTimer >= 0.5)
    {
        window.Title = levelDir is null ? "RefractorForge"
            : $"RefractorForge  -  {System.IO.Path.GetFileNameWithoutExtension(levelDir.TrimEnd('\\', '/'))}";
        fpsFrames = 0; fpsTimer = 0;
    }

    RenderMeshPreview();   // draw the model-viewer mesh into its FBO before ImGui samples it (restores the viewport)
    imgui.Render();   // editor panels, drawn over the 3D viewport
}

// ===========================================================================
// Editor UI (Dear ImGui). Panels are pinned to the window edges each frame; the
// 3D viewport shows through the central gap. Matches the approved mockup layout.
// ===========================================================================
void ApplyTheme()
{
    ImGui.StyleColorsDark();
    var s = ImGui.GetStyle();
    s.WindowRounding = 0f; s.FrameRounding = 4f; s.GrabRounding = 4f; s.TabRounding = 4f;
    s.WindowBorderSize = 1f; s.FrameBorderSize = 0f;
    s.WindowPadding = new Vector2(10, 8);
    s.FramePadding = new Vector2(8, 4);
    s.ItemSpacing = new Vector2(8, 6);
    s.ScrollbarSize = 12f;
    static Vector4 C(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);
    var c = s.Colors;
    c[(int)ImGuiCol.WindowBg]       = C(32, 37, 44);
    c[(int)ImGuiCol.ChildBg]        = C(28, 33, 40);
    c[(int)ImGuiCol.PopupBg]        = C(28, 33, 40);
    c[(int)ImGuiCol.Border]         = C(17, 20, 26);
    c[(int)ImGuiCol.TitleBg]        = C(31, 36, 43);
    c[(int)ImGuiCol.TitleBgActive]  = C(39, 45, 53);
    c[(int)ImGuiCol.MenuBarBg]      = C(28, 33, 40);
    c[(int)ImGuiCol.FrameBg]        = C(23, 27, 33);
    c[(int)ImGuiCol.FrameBgHovered] = C(33, 39, 47);
    c[(int)ImGuiCol.FrameBgActive]  = C(40, 48, 58);
    c[(int)ImGuiCol.Button]         = C(42, 49, 58);
    c[(int)ImGuiCol.ButtonHovered]  = C(58, 109, 176);
    c[(int)ImGuiCol.ButtonActive]   = C(49, 95, 156);
    c[(int)ImGuiCol.Header]         = C(58, 109, 176);
    c[(int)ImGuiCol.HeaderHovered]  = C(67, 120, 190);
    c[(int)ImGuiCol.HeaderActive]   = C(49, 95, 156);
    c[(int)ImGuiCol.CheckMark]      = C(120, 180, 230);
    c[(int)ImGuiCol.Text]           = C(201, 208, 217);
    c[(int)ImGuiCol.TextDisabled]   = C(110, 119, 130);
    c[(int)ImGuiCol.Separator]      = C(44, 51, 61);
    c[(int)ImGuiCol.ScrollbarBg]    = C(23, 27, 33);
    c[(int)ImGuiCol.ScrollbarGrab]  = C(58, 66, 77);
}

List<(string label, string[] items)> LoadCatalog()
{
    var result = new List<(string, string[])>();

    // The curated BFV category catalog (CATEGORY -> template names). Used to GROUP whatever archive is
    // loaded; it is NOT shown verbatim unless no mesh library is open.
    Dictionary<string, string[]>? dict = null;
    (string key, string label)[] order =
    {
        ("STRUCTURES", "Structures"), ("VEGETATION", "Vegetation"), ("OVERGROWTH", "Overgrowth"),
        ("UNDERGROWTH", "Undergrowth"), ("LAND_VEHICLES", "Land Vehicles"), ("WATER_VEHICLES", "Water Vehicles"),
        ("AIR_VEHICLES", "Air Vehicles"), ("STATIONARY_WEAPONS", "Stationary Weapons"), ("PROPS_HIGH", "Props"),
        ("PROPS_LOW", "Props (Low)"), ("USABLE_ITEMS", "Pickups"), ("EFFECTS", "Effects"),
        ("TUNNEL_OBJECTS", "Tunnels"), ("C99_MESHES", "Destructibles"),
    };
    try
    {
        var asm = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("objcatalog.json", StringComparison.OrdinalIgnoreCase));
        if (resName is not null)
        {
            using var st = asm.GetManifestResourceStream(resName)!;
            using var rd = new StreamReader(st);
            dict = JsonSerializer.Deserialize<Dictionary<string, string[]>>(rd.ReadToEnd());
        }
    }
    catch { /* no catalog -> flat list from the archive */ }

    // Normalize a mesh/template name to a grouping key: drop ".sm" + a trailing LOD suffix, lowercased.
    static string Stem(string n)
    {
        var s = n.EndsWith(".sm", StringComparison.OrdinalIgnoreCase) ? n[..^3] : n;
        s = System.Text.RegularExpressions.Regex.Replace(s, @"_(?:m\d+|l\d+|lod\d+)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return s;
    }

    // (1) A mesh library is open -> build the list from the ACTUAL loaded archives, so BF1942 / mod
    // objects.rfa shows ITS objects (not the bundled BFV list), grouped by the BFV catalog where names match.
    if (meshLib is not null && meshLib.MeshCount > 0)
    {
        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // normKey -> display name
        void Add(string name) { var k = Stem(name).ToLowerInvariant(); if (!present.ContainsKey(k)) present[k] = Stem(name); }
        foreach (var bn in meshLib.MeshBaseNames) Add(bn);
        // Vehicles/weapons by their real name (folder). List them ALL - some BFV stationary weapons (Browning,
        // Coaxial_Browning, StationaryFreePosition) have no standalone body mesh in the archives and render as a
        // marker, but the user still wants them in the list. (AI/ sub-folder phantoms are excluded upstream in
        // AssembledTemplateNames.)
        foreach (var v in meshLib.AssembledTemplateNames) present[v.ToLowerInvariant()] = v;

        // BFV objcatalog reverse map - fallback for objects with no archive-folder category.
        var labelOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (dict is not null)
            foreach (var (key, label) in order)
                if (dict.TryGetValue(key, out var items))
                    foreach (var t in items) labelOf[Stem(t).ToLowerInvariant()] = label;

        // Category for each object: PRIMARY = the archive's own folder structure (auto, any mod);
        // fallback = the BFV catalog; else "Other".
        var cats = meshLib.CategoryOf;
        var byLabel = new Dictionary<string, List<string>>();
        foreach (var kv in present)
        {
            // Assembled names keep their LOD suffix in the present-key, but the category maps are keyed by the
            // STEMMED name - so try the stem too, else e.g. "Stationary_M60" misses its "Stationary Weapons" slot.
            var lk = Stem(kv.Value).ToLowerInvariant();
            string label = cats.TryGetValue(kv.Key, out var c) ? c
                         : cats.TryGetValue(lk, out var c2) ? c2
                         : labelOf.TryGetValue(kv.Key, out var l) ? l
                         : labelOf.TryGetValue(lk, out var l2) ? l2 : "Other";
            if (!byLabel.TryGetValue(label, out var list)) byLabel[label] = list = new();
            list.Add(kv.Value);
        }

        // A friendly order first, then any extra (mod-specific) categories alphabetically, then Other last.
        string[] pref =
        {
            "Structures", "Vegetation", "Overgrowth", "Undergrowth", "Land Vehicles", "Water Vehicles",
            "Air Vehicles", "Vehicles", "Stationary Weapons", "Hand Weapons", "Soldiers", "Props",
            "Props (Low)", "Pickups", "Effects", "Tunnels", "Destructibles", "Misc",
        };
        void Emit(string label)
        {
            if (byLabel.TryGetValue(label, out var items) && items.Count > 0)
            { result.Add((label, items.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray())); byLabel.Remove(label); }
        }
        foreach (var label in pref) Emit(label);
        foreach (var label in byLabel.Keys.Where(k => k != "Other").OrderBy(k => k).ToArray()) Emit(label);
        Emit("Other");
        if (result.Count > 0) return result;
    }

    // (2) No mesh library (terrain-only view): show the bundled BFV catalog verbatim.
    if (dict is not null)
    {
        foreach (var (key, label) in order)
            if (dict.TryGetValue(key, out var items) && items.Length > 0) result.Add((label, items));
        foreach (var kv in dict)
            if (!order.Any(o => o.key == kv.Key) && kv.Value.Length > 0) result.Add((kv.Key, kv.Value));
    }

    // (3) Last resort: the templates actually placed in this level's StaticObjects.con.
    if (result.Count == 0 && so is not null)
    {
        var items = so.Objects.Select(o => o.Template).Distinct().OrderBy(t => t).ToArray();
        if (items.Length > 0) result.Add((Loc.T("Level Objects"), items));
    }
    return result;
}

string ShortName(string n)
{
    var s = System.Text.RegularExpressions.Regex.Replace(n, @"^(o_|ID_|C01F_|F_)", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    s = System.Text.RegularExpressions.Regex.Replace(s, @"(_c99)?(_m\d)?$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return s.Length == 0 ? n : s;
}

void Sep() { ImGui.TextDisabled("|"); ImGui.SameLine(); }

// ---- Icon toolbar glyphs: each draws into a box of half-extent r around centre c, in the button's
// text colour, using the window draw list. Vector-drawn so no icon font is needed (ASCII-font rule). ----
void GiSelect(ImDrawListPtr dl, Vector2 c, float r, uint col)
{
    dl.AddTriangleFilled(new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r * 0.05f, c.Y - r * 0.35f), new Vector2(c.X - r * 0.35f, c.Y + r * 0.05f), col);
    dl.AddLine(new Vector2(c.X - r * 0.1f, c.Y - r * 0.1f), new Vector2(c.X + r, c.Y + r), col, 2.2f);
}
void GiMove(ImDrawListPtr dl, Vector2 c, float r, uint col)
{
    dl.AddLine(new Vector2(c.X - r, c.Y), new Vector2(c.X + r, c.Y), col, 1.6f);
    dl.AddLine(new Vector2(c.X, c.Y - r), new Vector2(c.X, c.Y + r), col, 1.6f);
    float a = r * 0.5f;
    dl.AddTriangleFilled(new Vector2(c.X - r, c.Y), new Vector2(c.X - r + a, c.Y - a * 0.7f), new Vector2(c.X - r + a, c.Y + a * 0.7f), col);
    dl.AddTriangleFilled(new Vector2(c.X + r, c.Y), new Vector2(c.X + r - a, c.Y - a * 0.7f), new Vector2(c.X + r - a, c.Y + a * 0.7f), col);
    dl.AddTriangleFilled(new Vector2(c.X, c.Y - r), new Vector2(c.X - a * 0.7f, c.Y - r + a), new Vector2(c.X + a * 0.7f, c.Y - r + a), col);
    dl.AddTriangleFilled(new Vector2(c.X, c.Y + r), new Vector2(c.X - a * 0.7f, c.Y + r - a), new Vector2(c.X + a * 0.7f, c.Y + r - a), col);
}
void GiRotate(ImDrawListPtr dl, Vector2 c, float r, uint col)
{
    dl.PathArcTo(c, r, 0.7f, 0.7f + 4.9f, 20);
    dl.PathStroke(col, ImDrawFlags.None, 1.8f);
    var e = new Vector2(c.X + MathF.Cos(0.7f) * r, c.Y + MathF.Sin(0.7f) * r);
    dl.AddTriangleFilled(e, new Vector2(e.X - r * 0.55f, e.Y + r * 0.05f), new Vector2(e.X - r * 0.05f, e.Y - r * 0.55f), col);
}
void GiScale(ImDrawListPtr dl, Vector2 c, float r, uint col)
{
    dl.AddRect(new Vector2(c.X - r, c.Y - r), new Vector2(c.X + r, c.Y + r), col, 0f, ImDrawFlags.None, 1.6f);
    dl.AddLine(c, new Vector2(c.X + r, c.Y + r), col, 1.6f);
    dl.AddTriangleFilled(new Vector2(c.X + r, c.Y + r), new Vector2(c.X + r - r * 0.55f, c.Y + r), new Vector2(c.X + r, c.Y + r - r * 0.55f), col);
}
void GiPlace(ImDrawListPtr dl, Vector2 c, float r, uint col)
{
    dl.AddCircleFilled(new Vector2(c.X, c.Y - r * 0.3f), r * 0.55f, col, 16);
    dl.AddTriangleFilled(new Vector2(c.X - r * 0.5f, c.Y - r * 0.05f), new Vector2(c.X + r * 0.5f, c.Y - r * 0.05f), new Vector2(c.X, c.Y + r), col);
}
// (GiPaint/GiSculpt/GiSmooth glyphs removed with the old flat tool row; the mapper bar uses text labels.)

// One square icon tool button: draws the frame, overlays the glyph, shows a tooltip, selects on click.
bool IconTool(int idx, System.Action<ImDrawListPtr, Vector2, float, uint> glyph, string tip)
{
    const float sz = 30f;
    bool active = tool == idx;
    if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.43f, 0.69f, 1f));
    bool clicked = ImGui.Button($"##tool{idx}", new Vector2(sz, sz));
    if (active) ImGui.PopStyleColor();
    var mn = ImGui.GetItemRectMin(); var mx = ImGui.GetItemRectMax();
    var ctr = new Vector2((mn.X + mx.X) * 0.5f, (mn.Y + mx.Y) * 0.5f);
    glyph(ImGui.GetWindowDrawList(), ctr, sz * 0.5f - 8f, ImGui.GetColorU32(ImGuiCol.Text));
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T(tip));
    if (clicked) tool = idx;
    return clicked;
}

// Switch the top-level mapper mode. Drives the underlying tool + paint layer so every existing input
// handler keeps working unchanged; only the active mapper's controls show.
void SetMapper(int m)
{
    mapper = m;
    roadMode = false; measureMode = false;
    switch (m)
    {
        case 0: tool = Array.IndexOf(toolNames, "Sculpt"); break;                                                  // Terrain
        case 1: paintLayer = 0; tool = Array.IndexOf(toolNames, "Paint"); UploadActivePaintTexture(); break;       // Material
        case 2: if (tool > 4) tool = 1; break;                                                                     // Object
        case 3: paintLayer = 3; tool = Array.IndexOf(toolNames, "Paint"); UploadActivePaintTexture(); break;       // Surface
        case 4: paintLayer = (overPainter is not null && underPainter is null) ? 2 : 1;                            // Growth
                tool = Array.IndexOf(toolNames, "Paint"); UploadActivePaintTexture(); break;
        case 5: tool = Array.IndexOf(toolNames, "AIPath"); break;                                                  // AI Path
    }
}

// One mapper-mode toggle button (text label + tooltip), highlighted when active.
void MapperButton(int m, string tip)
{
    bool active = mapper == m;
    if (active) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.43f, 0.69f, 1f));
    bool clicked = ImGui.Button($"{Loc.T(mapperNames[m])}###mapper{m}");
    if (active) ImGui.PopStyleColor();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T(tip));
    if (clicked) SetMapper(m);
}

// Context strip under the mapper row: only the active mapper's quick tools.
void MapperSubToolbar()
{
    ImGui.TextDisabled(string.Format(Loc.T("{0} mapper:"), Loc.T(mapperNames[mapper]))); ImGui.SameLine();
    if (mapper == 0)
    {
        for (int i = 0; i < sculptModeLabels.Length; i++)
        {
            bool on = tool == Array.IndexOf(toolNames, "Sculpt") && sculptModeIdx == i;
            if (on) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.43f, 0.69f, 1f));
            if (ImGui.Button(sculptModeLabels[i])) { tool = Array.IndexOf(toolNames, "Sculpt"); sculptModeIdx = i; }
            if (on) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        bool sm = tool == Array.IndexOf(toolNames, "Smooth");
        if (sm) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.23f, 0.43f, 0.69f, 1f));
        if (ImGui.Button(Loc.TL("Smooth"))) tool = Array.IndexOf(toolNames, "Smooth");
        if (sm) ImGui.PopStyleColor();
    }
    else if (mapper == 2)
    {
        IconTool(0, GiSelect, "Select"); ImGui.SameLine();
        IconTool(1, GiMove,   "Move");   ImGui.SameLine();
        IconTool(2, GiRotate, "Rotate"); ImGui.SameLine();
        IconTool(3, GiScale,  "Scale");  ImGui.SameLine();
        IconTool(4, GiPlace,  "Place");
    }
    else if (mapper == 5)
    {
        if (ImGui.RadioButton(Loc.TL("Passable"), !aiPathBlock)) aiPathBlock = false; ImGui.SameLine();
        if (ImGui.RadioButton(Loc.TL("Blocked"), aiPathBlock)) aiPathBlock = true; ImGui.SameLine();
        ImGui.SetNextItemWidth(130f); ImGui.Combo(Loc.TL("Vehicle##sub"), ref aiPathVeh, aiPathVehNames, aiPathVehNames.Length);
        ImGui.SameLine(); ImGui.SetNextItemWidth(110f); SldF(Loc.TL("Radius##ai"), ref brushRadius, 1f, 200f, "%.0f");
        ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Square brush##ai"), ref squareBrush);   // square footprint + cursor (like terrain)
    }
    else
        ImGui.TextDisabled(Loc.T("brush + palette in the inspector ->"));
}

void ToolButtons()
{
    if (ImGui.Button(Loc.TL("New"))) OpenNewMap();
    ImGui.SameLine(); if (ImGui.Button(Loc.TL("Open"))) OpenLevel();
    ImGui.SameLine(); if (ImGui.Button(Loc.TL("Save"))) DoSave();
    ImGui.SameLine(); Sep();
    MapperButton(0, "Sculpt & smooth the heightmap (F1)");      ImGui.SameLine();
    MapperButton(1, "Paint the ground material type (F2)");     ImGui.SameLine();
    MapperButton(2, "Place & edit objects / spawns (F3)");      ImGui.SameLine();
    MapperButton(3, "Paint the visual surface textures (F4)");  ImGui.SameLine();
    MapperButton(4, "Paint under/overgrowth foliage (F5)");     ImGui.SameLine();
    MapperButton(5, "Paint AI pathfinding pass/block (F6)");    ImGui.SameLine(); Sep();
    if (ImGui.Button(Loc.TL("Undo"))) DoUndo();
    ImGui.SameLine(); if (ImGui.Button(Loc.TL("Redo"))) DoRedo();
    ImGui.SameLine(); Sep();
    ImGui.Checkbox(Loc.TL("Grid"), ref gridOn);
    ImGui.SameLine(); ImGui.ColorEdit3("##gridcol", ref gridColor, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel);
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Grid line colour"));
    ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Labels"), ref gridLabels);
    ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Snap"), ref snapOn);
    if (snapOn)   // grid step: object move/place snaps X/Z to this many metres
    {
        ImGui.SameLine(); ImGui.SetNextItemWidth(56f);
        if (ImGui.DragFloat("##snapStep", ref snapStep, 0.25f, 0.25f, 64f, "%.2fm")) snapStep = Math.Clamp(snapStep, 0.25f, 64f);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Snap grid step (m): placed/moved objects round to this."));
    }
    ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Map"), ref showMinimap);
    MapperSubToolbar();
}

void Inspector()
{
    if (roadMode)
    {
        ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T("Road Tool (spline)"));
        ImGui.Separator();
        ImGui.TextDisabled($"{roadPts.Count} point(s) -- click to add, drag a point to move it.");
        SldF(Loc.TL("Width (m)"), ref roadWidth, 2f, 60f, "%.0f");
        if (roadSelIdx >= 0 && roadSelIdx < roadPtW.Count)
        {
            float pw = roadPtW[roadSelIdx];
            if (SldF($"Point {roadSelIdx + 1} width (m)", ref pw, 0f, 60f, pw <= 0f ? "default" : "%.0f"))
                roadPtW[roadSelIdx] = pw < 2f ? 0f : pw;   // below 2 m snaps back to "use the road width"
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Per-point override; widths blend smoothly along the curve. Drag to 0 for the default."));
        }
        SldF(Loc.TL("Edge softness (m)"), ref roadEdge, 0f, 16f, "%.1f");
        SldF(Loc.TL("Intensity"), ref roadIntensity, 0.05f, 1f, "%.2f");
        ImGui.Separator();
        // Texture: oriented along the road (lane markings follow curves) or classic world tiling; the image comes
        // from the Texture Library pick or the chosen surface slot. The Surface combo always sets the gameplay
        // material painted under the road (and the fallback texture).
        ImGui.Checkbox(Loc.TL("Orient texture along road"), ref roadOrient);
        if (roadOrient)
        {
            SldF(Loc.TL("Tile length (m)"), ref roadTileAlong, 2f, 64f, "%.0f");
            ImGui.Checkbox(Loc.TL("Texture runs horizontally"), ref roadTexRotate);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("On = road strip drawn left-right in the image (BF/ED42 default). Off if your strip is drawn top-to-bottom."));
        }
        if (roadUseLib && roadLibTexPath is not null)
        {
            LibTile("##roadlibtex", LibThumb(roadLibTexPath, true), new Vector2(28, 28), true);
            ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.TextWrapped(Path.GetFileName(roadLibTexPath));
            if (ImGui.SmallButton(Loc.TL("Use surface slot instead"))) roadUseLib = false;
            ImGui.EndGroup();
        }
        if (ImGui.Button(Loc.TL("Pick road texture..."))) { layerPickTarget = 3; showTexLibrary = true; RefreshTextureLibrary(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Pick a road strip from the Texture Library (Road category). Oriented mode runs it lengthwise down the road."));
        int rs = roadSurface; if (ImGui.Combo(Loc.TL("Surface/material"), ref rs, surfNames, surfNames.Length)) roadSurface = (byte)Math.Clamp(rs, 0, 15);
        ImGui.Separator();
        ImGui.Checkbox(Loc.TL("Flatten terrain"), ref roadFlatten);
        if (roadFlatten) SldF(Loc.TL("Shoulder (m)"), ref roadShoulder, 0f, 16f, "%.1f");
        if (roadFlatten && ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Extra flattened ground outside each road edge (the embankment)."));
        ImGui.Spacing();
        if (ImGui.Button(Loc.TL("Stamp Road"), new Vector2(150, 0))) StampRoad();
        ImGui.SameLine(); if (ImGui.Button(Loc.TL("Undo point")) && roadPts.Count > 0)
        { roadPts.RemoveAt(roadPts.Count - 1); if (roadPtW.Count > roadPts.Count) roadPtW.RemoveAt(roadPtW.Count - 1); if (roadSelIdx >= roadPts.Count) roadSelIdx = roadPts.Count - 1; }
        ImGui.SameLine(); if (ImGui.Button(Loc.TL("Clear"))) { roadPts.Clear(); roadPtW.Clear(); roadSelIdx = -1; roadDragIdx = -1; }
        ImGui.Spacing();
        ImGui.BulletText(Loc.T("Points form a smooth curve; heights grade along it."));
        ImGui.BulletText(Loc.T("Click a handle to select it; drag to move."));
        ImGui.BulletText(Loc.T("Points stay after Stamp -- tweak and re-stamp."));
        ImGui.BulletText(Loc.T("Esc clears the points / exits the tool."));
        return;
    }
    string tn = toolNames[tool];
    if (tn is "Sculpt" or "Smooth" or "Paint")
    {
        ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), string.Format(Loc.T("{0} Mapper"), Loc.T(mapperNames[mapper])));
        ImGui.Separator();
        if (tn == "Paint")
        {
            // The mapper already chose the paint target. The Growth mapper still picks Under vs Over here.
            if (paintLayer == 1 || paintLayer == 2)
            {
                if (underPainter is not null && ImGui.RadioButton(Loc.TL("Undergrowth"), paintLayer == 1)) { paintLayer = 1; UploadActivePaintTexture(); }
                if (overPainter is not null) { if (underPainter is not null) ImGui.SameLine(); if (ImGui.RadioButton(Loc.TL("Overgrowth"), paintLayer == 2)) { paintLayer = 2; UploadActivePaintTexture(); } }
                ImGui.Separator();
            }

            if (paintLayer == 3)
            {
                if (atlasCpu is null) { ImGui.TextWrapped(Loc.T("No terrain texture in this level.")); return; }
                if (paintFromLib && libTexPath is not null) ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), $"Library: {Path.GetFileName(libTexPath)}");
                else ImGui.Text($"{(activeTexture < surfNames.Length ? surfNames[activeTexture] : "?")}  #{activeTexture}");
                ImGui.TextDisabled(Loc.T("Painted live; baked to .dds on Ctrl+S."));
                // 16 surface swatches showing each texture's average colour; click selects the surface.
                for (int i = 0; i < texSwatch.Length; i++)
                {
                    bool sel = i == activeTexture;
                    bool has = i < texPalette.Length && texPalette[i] is not null;
                    if (sel) { ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 1f)); ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f); }
                    if (ImGui.ColorButton($"tex{i}", texSwatch[i], ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(20, 20)) && has)
                        { activeTexture = (byte)i; paintFromLib = false; }   // picking a palette slot leaves library-paint mode
                    if (sel) { ImGui.PopStyleVar(); ImGui.PopStyleColor(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"{(i < surfNames.Length ? Loc.T(surfNames[i]) : "?")}  #{i}{(has ? "" : Loc.T("  (missing)"))}{(texSource[i] is not null ? Loc.T("  (custom)") : "")}");
                    if (i % 8 != 7 && i != texSwatch.Length - 1) ImGui.SameLine();
                }
                SldF(Loc.TL("Radius (m)"), ref brushRadius, 0.5f, 100f, "%.1f");
                SldF(Loc.TL("Hardness"), ref matHardness, 0.05f, 1f, "%.2f");
                SldF(Loc.TL("Intensity"), ref texIntensity, 0.02f, 1f, "%.2f");
                SldF(Loc.TL("Tile size (m)"), ref texTileMeters, 1f, 64f, "%.0f");
                ImGui.Checkbox(Loc.TL("Square brush"), ref squareBrush);
                ImGui.Spacing();
                if (ImGui.Button(Loc.TL("Import into slot"))) ImportSurfaceSlot(activeTexture);
                if (texSource[activeTexture] is not null) { ImGui.SameLine(); if (ImGui.Button(Loc.TL("Reset slot"))) ResetSurfaceSlot(activeTexture); ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("(custom)")); }
                if (ImGui.Button(Loc.TL("Save set..."))) ExportSurfaceSet();
                ImGui.SameLine(); if (ImGui.Button(Loc.TL("Load set..."))) LoadSurfaceSet();
                // ---- Texture Library (Editor42-style: your own tileable textures from the TerrainTextures folder) ----
                ImGui.Separator();
                ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T("Texture Library"));
                if (paintFromLib && libTexPath is not null)
                {
                    LibTile("##activelib", LibThumb(libTexPath, true), new Vector2(28, 28), true);
                    ImGui.SameLine();
                    ImGui.BeginGroup();
                    ImGui.TextWrapped(Path.GetFileName(libTexPath));
                    if (ImGui.SmallButton(Loc.TL("Use palette slot instead"))) paintFromLib = false;
                    ImGui.EndGroup();
                    SldF(Loc.TL("Tile size (m)##lib"), ref libTileMeters, 1f, 64f, "%.0f");
                }
                else ImGui.TextDisabled($"Painting palette slot #{activeTexture}. Open the library to paint your own texture.");
                if (ImGui.Button(Loc.TL("Texture Library..."))) { layerPickTarget = 0; showTexLibrary = true; RefreshTextureLibrary(); }
                ImGui.SameLine(); if (ImGui.Button(Loc.TL("Layer Tool..."))) { showLayerTool = true; RefreshTextureLibrary(); }
                if (ImGui.Button(Loc.TL("Fill terrain with this texture")) && SurfPaintTex() is Texture2D ftx) FillTerrainWith(ftx, SurfPaintTile());
                ImGui.Checkbox(Loc.TL("Respect texture alpha (decal/splat)"), ref surfUseAlpha);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Paint only where the source texture is opaque - for cut-out decals/splats."));
                // ---- Detail texture (close-up tiling overlay, BF detailTexName) ----
                ImGui.Separator();
                if (ImGui.Button(Loc.TL("Import detail texture..."))) ImportDetailTexture();
                if (terrainTex?.Detail is not null)
                {
                    ImGui.SameLine(); ImGui.TextColored(new Vector4(0.5f, 0.85f, 0.5f, 1f), detailImported ? "(custom)" : "(level)");
                    float dr = (terrainTex.DetailRepeatMeters > 0 ? terrainTex.DetailRepeatMeters : detailRepeatM);
                    if (SldF(Loc.TL("Detail repeat (m)"), ref dr, 0.5f, 32f, "%.1f")) SetDetailRepeat(dr);
                }
                else ImGui.TextDisabled(Loc.T("No detail texture (adds crisp close-up tiling)."));
                ImGui.Spacing();
                ImGui.Checkbox(Loc.TL("Capture mode"), ref captureMode);
                if (captureMode)
                {
                    SldF(Loc.TL("Capture size (m)"), ref captureMeters, 8f, 256f, "%.0f");
                    ImGui.Combo(Loc.TL("Capture res"), ref captureResIdx, captureSizeNames, captureSizeNames.Length);
                    ImGui.Checkbox(Loc.TL("Also import into this slot"), ref captureImport);
                    ImGui.TextDisabled(Loc.T("Click the terrain to save that square as a .dds."));
                }
                ImGui.Spacing();
                ImGui.BulletText(Loc.T(captureMode ? "Click terrain to capture a square as a texture." : "Drag on terrain to paint the surface."));
                ImGui.BulletText(Loc.T("Alt-click picks the surface under the cursor."));
                ImGui.BulletText(Loc.T("Wheel resizes; Z / Y undo and redo."));
                ImGui.BulletText(Loc.T("Ctrl+S bakes it into the level's terrain tiles."));
                return;
            }

            if (paintLayer == 0)
            {
                if (matPainter is null) { ImGui.TextWrapped(Loc.T("No material map in this level.")); return; }
                // Each material INDEX maps to a surface (matToSurf -> texPalette). Show the swatch as the ACTUAL
                // surface colour + the surface name (in the editor's surfNames order) so the grid matches the ground
                // and the on-map labels - the old matNames order was wrong (it mislabelled jungle grass as Wet Sand).
                int hSlot = activeMaterial < matToSurf.Length ? (matToSurf[activeMaterial] & 15) : (activeMaterial & 15);
                ImGui.Text($"Material #{activeMaterial}  {(hSlot < surfNames.Length ? surfNames[hSlot] : "")}");
                // 16-swatch material palette (8 per row); click selects the active material index.
                for (int i = 0; i < 16; i++)
                {
                    int slot = i < matToSurf.Length ? (matToSurf[i] & 15) : (i & 15);
                    bool sel = i == activeMaterial;
                    if (sel) { ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 1f)); ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f); }
                    if (ImGui.ColorButton($"mat{i}", texSwatch[slot], ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(20, 20)))
                        activeMaterial = (byte)i;
                    if (sel) { ImGui.PopStyleVar(); ImGui.PopStyleColor(); }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip($"#{i}  {(slot < surfNames.Length ? Loc.T(surfNames[slot]) : "?")}");
                    if (i % 8 != 7 && i != 15) ImGui.SameLine();
                }
                SldF(Loc.TL("Radius (m)"), ref brushRadius, 0.5f, 100f, "%.1f");
                SldF(Loc.TL("Hardness"), ref matHardness, 0.05f, 1f, "%.2f");
                if (brushShapeNames.Length > 1) ImGui.Combo(Loc.TL("Shape"), ref brushShapeIdx, brushShapeNames, brushShapeNames.Length);
                if (brushShapeIdx == 0) ImGui.Checkbox(Loc.TL("Square brush"), ref squareBrush);
                ImGui.Spacing();
                ImGui.BulletText(Loc.T("Drag on terrain to paint the material."));
                ImGui.BulletText(Loc.T("Alt-click picks the material under the cursor."));
                ImGui.BulletText(Loc.T("Wheel resizes; Z / Y undo and redo."));
                return;
            }

            // Foliage layer (undergrowth/overgrowth): paint a discrete foliage value (0 clears).
            var pal = paintLayer == 1 ? growth?.UnderPalette : growth?.OverPalette;
            int gside = paintLayer == 1 ? (growth?.UnderSide ?? 0) : (growth?.OverSide ?? 0);
            ImGui.TextColored(new Vector4(0.49f, 0.86f, 0.55f, 1f), $"{(paintLayer == 1 ? "Undergrowth" : "Overgrowth")} - {gside}x{gside}");
            ImGui.Text($"Foliage value #{activeFoliage}{(activeFoliage == 0 ? "  (clear)" : "")}");
            for (int i = 0; i < 16; i++)
            {
                bool sel = i == activeFoliage;
                var col = i == 0 ? new Vector4(0.12f, 0.12f, 0.13f, 1f) : matPalette[i];   // value 0 = dark "clear" swatch
                if (sel) { ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1f, 1f, 1f, 1f)); ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 2f); }
                if (ImGui.ColorButton($"fol{i}", col, ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoPicker, new Vector2(20, 20)))
                    activeFoliage = (byte)i;
                if (sel) { ImGui.PopStyleVar(); ImGui.PopStyleColor(); }
                if (i % 8 != 7 && i != 15) ImGui.SameLine();
            }
            SldF(Loc.TL("Radius (m)"), ref brushRadius, 0.5f, 100f, "%.1f");
            SldF(Loc.TL("Hardness"), ref matHardness, 0.05f, 1f, "%.2f");
            if (brushShapeNames.Length > 1) ImGui.Combo(Loc.TL("Shape"), ref brushShapeIdx, brushShapeNames, brushShapeNames.Length);
            if (brushShapeIdx == 0) ImGui.Checkbox(Loc.TL("Square brush"), ref squareBrush);
            ImGui.Spacing();
            if (pal is not null && pal.DistinctGeometries.Count > 0)
            {
                ImGui.TextDisabled($"{pal.TypeCount} foliage types defined:");
                foreach (var gname in pal.DistinctGeometries) ImGui.BulletText(gname);
                ImGui.Spacing();
            }
            ImGui.BulletText(Loc.T("Value 0 clears; 1+ paint foliage."));
            ImGui.BulletText(Loc.T("The type per cell comes from its material + this map."));
            ImGui.BulletText(Loc.T("Wheel resizes; Z / Y undo and redo."));
            return;
        }
        if (terrainEd is null) { ImGui.TextDisabled(Loc.T("No terrain loaded.")); return; }
        if (tn == "Sculpt") ImGui.Combo(Loc.TL("Mode"), ref sculptModeIdx, sculptModeLabels, sculptModeLabels.Length);
        if (tn == "Sculpt") { ImGui.Checkbox(Loc.TL("L/R mouse = raise / lower"), ref lrSculpt); if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Left-drag raises, right-drag lowers the terrain (overrides the Mode above).\nWhile sculpting, right-drag won't orbit the camera.")); }
        if (brushShapeNames.Length > 1) ImGui.Combo(Loc.TL("Shape"), ref brushShapeIdx, brushShapeNames, brushShapeNames.Length);
        // Falloff + the procedural square only apply to the radial brush; bitmap shapes carry their own edge.
        if (brushShapeIdx == 0) ImGui.Combo(Loc.TL("Falloff"), ref falloffIdx, falloffLabels, falloffLabels.Length);
        if (brushShapeIdx == 0) ImGui.Checkbox(Loc.TL("Square brush"), ref squareBrush);
        SldF(Loc.TL("Radius (m)"), ref brushRadius, 0.5f, 100f, "%.1f");

        var modeNow = CurBrushMode();
        if (modeNow is BrushMode.Raise or BrushMode.Lower)
            SldF(Loc.TL("Strength (m/dab)"), ref brushStrength, 0.1f, 12f, "%.2f");
        else if (modeNow is BrushMode.Smooth or BrushMode.Flatten)
            SldF(Loc.TL("Strength"), ref smoothStrength, 0.05f, 1f, "%.2f");

        if (tn == "Sculpt" && modeNow is BrushMode.Flatten or BrushMode.Set)
        {
            ImGui.Checkbox(Loc.TL("Lock to ground under cursor"), ref flattenLockGround);
            if (!flattenLockGround) SldF(Loc.TL("Target height (m)"), ref flattenTarget, -100f, 500f, "%.1f");
        }

        ImGui.Spacing();
        ImGui.BulletText(tn == "Smooth" ? Loc.T("Drag to average out bumps.") : modeNow switch
        {
            BrushMode.Raise => "Drag to raise; hold Shift to lower.",
            BrushMode.Lower => "Drag to lower; hold Shift to raise.",
            BrushMode.Flatten => Loc.T("Drag to ease terrain toward the target height."),
            BrushMode.Set => Loc.T("Drag to set terrain to the target height."),
            _ => Loc.T("Drag on the terrain."),
        });
        ImGui.BulletText(Loc.T("Mouse wheel resizes the brush."));
        ImGui.BulletText(Loc.T("Z / Y undo and redo each stroke."));
        return;
    }
    if (tn == "AIPath")
    {
        ImGui.TextColored(new Vector4(0.86f, 0.55f, 0.55f, 1f), Loc.T("AI Pathmapping"));
        ImGui.Separator();
        ImGui.TextWrapped(Loc.T("Paint where AI bots can and cannot go. Black = passable, white = blocked (matches the engine navmap)."));
        if (ImGui.RadioButton(Loc.TL("Passable (black)"), !aiPathBlock)) aiPathBlock = false;
        ImGui.SameLine(); if (ImGui.RadioButton(Loc.TL("Blocked (white)"), aiPathBlock)) aiPathBlock = true;
        ImGui.Combo(Loc.TL("Vehicle"), ref aiPathVeh, aiPathVehNames, aiPathVehNames.Length);
        SldF(Loc.TL("Radius (m)"), ref brushRadius, 0.5f, 100f, "%.1f");
        ImGui.Checkbox(Loc.TL("Square brush"), ref squareBrush);
        // Re-seed ONLY this vehicle's buffer (others keep their edits): from the level's shipped navmap, or
        // regenerated from terrain (the old always-generate behaviour, now an explicit choice).
        if (ImGui.Button(Loc.TL("Reload level navmap")))
        { int rv = Math.Clamp(aiPathVeh, 0, aiNavBufs.Length - 1); aiNavBufs[rv] = null; aiNavBufDirty[rv] = false; aiNav = null; aiNavVehLoaded = -1; EnsureAiNav(); }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Discard edits for this vehicle and reload the navmap the level ships (or generate if it has none)."));
        ImGui.SameLine();
        if (ImGui.Button(Loc.TL("Generate from terrain")))
        {
            int rv = Math.Clamp(aiPathVeh, 0, aiNavBufs.Length - 1);
            var vp = RefractorForge.Formats.Terrain.SearchMapParams.Standard[rv];
            var foots2 = (meshLib is not null && so is not null) ? RefractorForge.Render.SearchMapBuilder.Footprints(so.Objects, meshLib) : null;
            aiNavBufs[rv] = heightmap is null ? null : RefractorForge.Formats.Terrain.SearchMapGenerator.GenerateGrid(cfg, heightmap, vp, 0, foots2);
            aiNavBufDirty[rv] = aiNavBufs[rv] is not null;   // generated over an existing map = an edit worth saving
            aiNav = null; aiNavVehLoaded = -1; EnsureAiNav();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Replace this vehicle's navmap with a fresh terrain-derived one (slopes + water + object footprints)."));
        if (aiNavDirty) { ImGui.SameLine(); ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("unsaved")); }
        ImGui.Spacing();
        ImGui.BulletText(Loc.T("Drag to paint (terrain-seeded)."));
        ImGui.BulletText(Loc.T("Z / Y undo & redo each stroke."));
        ImGui.BulletText(Loc.T("Ctrl+S saves all painted vehicles"));
        ImGui.BulletText(Loc.T("   (folder + .rfa)."));
        ImGui.BulletText(Loc.T("Each vehicle keeps its own edits."));
        return;
    }
    // Place tool: choose what to drop, then click the terrain. Always shown while Place is active.
    if (toolNames[tool] == "Place")
    {
        var accent = new Vector4(0.49f, 0.70f, 0.92f, 1f);
        ImGui.TextColored(accent, Loc.T("Place mode"));
        ImGui.TextDisabled(Loc.T("What to place:"));
        if (ImGui.RadioButton(Loc.TL("Static object"), gpPlaceKind is null)) gpPlaceKind = null;
        if (ImGui.RadioButton(Loc.TL("Control Point"), gpPlaceKind == GpKind.ControlPoint)) gpPlaceKind = GpKind.ControlPoint;
        if (ImGui.RadioButton(Loc.TL("Vehicle Spawn"), gpPlaceKind == GpKind.Vehicle)) gpPlaceKind = GpKind.Vehicle;
        if (ImGui.RadioButton(Loc.TL("Soldier Spawn"), gpPlaceKind == GpKind.Soldier)) gpPlaceKind = GpKind.Soldier;
        ImGui.Separator();
        if (gpPlaceKind is GpKind gk)
            ImGui.TextWrapped($"Click the terrain to place a {(gk == GpKind.ControlPoint ? "control point" : gk == GpKind.Vehicle ? "vehicle spawn" : "soldier spawn")}, then tune it with Move / Rotate.");
        else if (browserTemplate is not null)
        {
            ImGui.TextWrapped(Loc.T("Click the terrain to drop:"));
            ImGui.Text(ShortName(browserTemplate));
            ImGui.TextDisabled(Loc.T("(a green marker shows where it will land)"));
        }
        else
            ImGui.TextWrapped(Loc.T("Pick an object in the Object Library, or choose a gameplay type above."));
        return;
    }
    // Selected gameplay handle (control point / vehicle spawn / soldier spawn).
    if (gpIndex >= 0 && gpIndex < gameplayEdit.CountOf(gpKind))
    {
        var (label, colr) = gpKind switch
        {
            GpKind.ControlPoint => ("Control Point", new Vector4(0.45f, 0.9f, 1f, 1f)),
            GpKind.Vehicle => ("Vehicle Spawn", new Vector4(1f, 0.62f, 0.25f, 1f)),
            _ => ("Soldier Spawn", new Vector4(0.45f, 1f, 0.5f, 1f)),
        };
        ImGui.TextColored(colr, Loc.T(label));   // "Control Point" / "Vehicle Spawn" / "Soldier Spawn"
        ImGui.Separator();

        if (!ImGui.IsAnyItemActive())   // don't clobber a field that is mid-edit
        {
            var p = gameplayEdit.GetPos(gpKind, gpIndex);
            gpInsPos = new Vector3(p.X, p.Y, p.Z);
            gpNameBuf = gameplayEdit.GetName(gpKind, gpIndex);
            if (gpKind == GpKind.ControlPoint) gpInsRad = gameplayEdit.GetRadius(gpIndex);
            if (gpKind == GpKind.Vehicle) gpVehBuf = gameplayEdit.GetDetail(gpKind, gpIndex);
            if (gpKind != GpKind.ControlPoint) { var r = gameplayEdit.GetRotation(gpKind, gpIndex); gpInsRot = new Vector3(r.X, r.Y, r.Z); }
        }
        ImGui.InputText(Loc.TL("Name"), ref gpNameBuf, 64u);
        if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
        {
            object cur = gameplayEdit.GetItem(gpKind, gpIndex);
            object nu = gpKind switch
            {
                GpKind.ControlPoint => ((ControlPointDef)cur) with { Name = gpNameBuf },
                GpKind.Vehicle => ((VehicleSpawnDef)cur) with { Name = gpNameBuf },
                _ => ((SoldierSpawnDef)cur) with { Name = gpNameBuf },
            };
            hist.Do(new GameplaySetItemCommand(gameplayEdit, gpKind, gpIndex, nu, null));
        }
        if (gpKind == GpKind.Vehicle)
        {
            // Pick the vehicle from the catalog (current value appended if it's a custom/unlisted template),
            // or type a custom name in the field below.
            var cat = VehicleChoices();
            var choices = cat.Contains(gpVehBuf) || string.IsNullOrEmpty(gpVehBuf)
                ? cat : cat.Append(gpVehBuf).ToArray();
            int sel = Array.IndexOf(choices, gpVehBuf);
            if (ImGui.Combo(Loc.TL("Vehicle"), ref sel, choices, choices.Length) && sel >= 0 && hist is not null)
            {
                gpVehBuf = choices[sel];
                var v = (VehicleSpawnDef)gameplayEdit.GetItem(GpKind.Vehicle, gpIndex);
                hist.Do(new GameplaySetItemCommand(gameplayEdit, GpKind.Vehicle, gpIndex, v with { Vehicle = gpVehBuf }, null));
            }
            ImGui.InputText(Loc.TL("Custom##veh"), ref gpVehBuf, 64u);
            if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
            {
                var v = (VehicleSpawnDef)gameplayEdit.GetItem(GpKind.Vehicle, gpIndex);
                hist.Do(new GameplaySetItemCommand(gameplayEdit, GpKind.Vehicle, gpIndex, v with { Vehicle = gpVehBuf }, null));
            }
        }
        ImGui.DragFloat3(Loc.TL("Position"), ref gpInsPos, 0.25f);
        if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
            hist.Do(new GameplayMoveCommand(gameplayEdit, gpKind, gpIndex, new Vec3(gpInsPos.X, gpInsPos.Y, gpInsPos.Z), null));
        if (gpKind == GpKind.ControlPoint)
        {
            SldF(Loc.TL("Capture radius (m)"), ref gpInsRad, 5f, 150f, "%.0f");
            if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
                hist.Do(new GameplayRadiusCommand(gameplayEdit, gpIndex, gpInsRad, null));
            // Full Battlecraft-style editor: team / area value / conversion time / control-point name + position.
            if (ImGui.Button(Loc.TL("Edit Control Point...")))
            {
                var c = (ControlPointDef)gameplayEdit.GetItem(GpKind.ControlPoint, gpIndex);
                editCpRequest = true; ecpIndex = gpIndex;
                ecpName = c.Name; ecpCpName = string.IsNullOrEmpty(c.ControlPointName) ? c.Name : c.ControlPointName;
                ecpRadius = c.Radius; ecpTeam = c.Team; ecpArea = c.AreaValue; ecpConv = c.ConversionTime; ecpGroup = c.SpawnGroupId;
                ecpOsId = c.ObjectSpawnerId; ecpTimeGet = c.TimeToGetControl; ecpTimeLose = c.TimeToLoseControl;
                ecpDisEnemy = c.DisableIfEnemyInside; ecpDisLosing = c.DisableWhenLosing; ecpLoseClose = c.LoseControlWhenEnemyClose;
                ecpLoseNot = c.LoseControlWhenNotClose; ecpUnable = c.UnableToChangeTeam; ecpOnlyTeam = c.OnlyTakableByTeam; ecpCollision = c.HasCollisionPhysics;
                ecpPos = new Vector3(c.Position.X, c.Position.Y, c.Position.Z);
            }
        }
        else
        {
            ImGui.DragFloat3(Loc.TL("Rotation yaw/pitch/roll"), ref gpInsRot, 1f);
            if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
                hist.Do(new GameplayRotateCommand(gameplayEdit, gpKind, gpIndex, new Vec3(gpInsRot.X, gpInsRot.Y, gpInsRot.Z), null));
            // Full Battlecraft-style spawn editors (team / OS id for vehicles; group / spawn id / paratrooper for soldiers).
            if (gpKind == GpKind.Vehicle && ImGui.Button(Loc.TL("Edit Object Spawn...")))
            {
                var v = (VehicleSpawnDef)gameplayEdit.GetItem(GpKind.Vehicle, gpIndex);
                editVehRequest = true; evIndex = gpIndex; evName = v.Name;
                evPos = new Vector3(v.Position.X, v.Position.Y, v.Position.Z); evRot = new Vector3(v.Rotation.X, v.Rotation.Y, v.Rotation.Z);
                evTeam = v.Team; evOsId = v.OsId;
            }
            if (gpKind == GpKind.Soldier && ImGui.Button(Loc.TL("Edit Soldier Spawn...")))
            {
                var s = (SoldierSpawnDef)gameplayEdit.GetItem(GpKind.Soldier, gpIndex);
                editSolRequest = true; esIndex = gpIndex; esName = s.Name;
                esPos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z); esRot = new Vector3(s.Rotation.X, s.Rotation.Y, s.Rotation.Z);
                esGroup = s.Group; esSpawnId = s.SpawnId; esPara = s.SpawnAsParaTrooper != 0;
            }
        }
        ImGui.Spacing();
        ImGui.BulletText(Loc.T("Move: drag on terrain. Rotate: drag to spin yaw."));
        ImGui.BulletText(Loc.T("Or set values above; Del removes; Z / Y undo."));
        return;
    }
    if (so is null || selected < 0 || selected >= so.Objects.Count)
    {
        ImGui.TextDisabled(Loc.T("No selection."));
        ImGui.TextWrapped(Loc.T("Click to select; Shift-click adds. Arrows nudge X/Z (hold Shift = coarse); Alt+Up/Down raise/lower; Alt+Left/Right rotate. Del removes, Z/Y undo/redo, F focuses."));
        if (browserTemplate is not null)
        {
            ImGui.Separator();
            ImGui.TextDisabled(Loc.T("Library selection:"));
            ImGui.Text(ShortName(browserTemplate));
        }
        return;
    }

    var o = so.Objects[selected];
    ImGui.Text(ShortName(o.Template));
    ImGui.TextDisabled(Loc.T("Template: ") + o.Template);
    // Flag objects that render as a diamond (no mesh resolved) + WHY, so a missing platform etc. is diagnosable.
    if (meshLib is not null && !meshLib.TryGet(o.Template, out _) && !meshLib.TryGetAssembledMesh(o.Template, out _))
        ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f),
            meshLib.HasMeshEntry(o.Template) ? "mesh not shown: .sm present but failed to parse" : "mesh not found in loaded archives");
    if (multi.Count > 1) ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), string.Format(Loc.T("{0} selected - editing primary (gizmo moves all)"), multi.Count));
    ImGui.Separator();

    if (!ImGui.IsAnyItemActive())   // don't clobber a field that's mid-drag
    {
        insPos = new Vector3(o.Position.X, o.Position.Y, o.Position.Z);
        insRot = new Vector3(o.Rotation.X, o.Rotation.Y, o.Rotation.Z);
        insScale = o.Scale ?? 1f;
    }

    ImGui.TextDisabled(Loc.T("TRANSFORM"));
    ImGui.PushItemWidth(-80f);   // fields fill the row but leave room for the Position/Rotation/Scale labels (not -1, which clips them)

    if (ImGui.DragFloat3(Loc.TL("Position"), ref insPos, 0.25f, 0f, 0f))
    { o.Position = new Vec3(insPos.X, insPos.Y, insPos.Z); SyncTransformEdit(); }
    if (ImGui.IsItemActivated()) dragFromV3 = o.Position;
    if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
    { var to = new Vec3(insPos.X, insPos.Y, insPos.Z); o.Position = dragFromV3; hist.Do(new MoveObject(o.Id, to)); SyncTransformEdit(); }

    if (ImGui.DragFloat3(Loc.TL("Rotation"), ref insRot, 0.5f, 0f, 0f))
    { o.Rotation = new Vec3(insRot.X, insRot.Y, insRot.Z); SyncTransformEdit(); }
    if (ImGui.IsItemActivated()) dragFromV3 = o.Rotation;
    if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
    { var to = new Vec3(insRot.X, insRot.Y, insRot.Z); o.Rotation = dragFromV3; hist.Do(new RotateObject(o.Id, to)); SyncTransformEdit(); }

    if (ImGui.DragFloat(Loc.TL("Scale##objscale"), ref insScale, 0.01f, 0.01f, 100f))
    { o.Scale = insScale; SyncTransformEdit(); }
    if (ImGui.IsItemActivated()) dragFromScale = o.Scale ?? 1f;
    if (ImGui.IsItemDeactivatedAfterEdit() && hist is not null)
    { var to = insScale; o.Scale = dragFromScale; hist.Do(new ScaleObject(o.Id, to)); SyncTransformEdit(); }

    ImGui.PopItemWidth();

    // Sound emitter: edit the .ssc script this object loads (shared by every placement of the template).
    if (sounds.IsSound(o.Template))
    {
        var em = sounds.Get(o.Template);
        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("SOUND  -  ") + o.Template + ".ssc");
        if (em?.Script is null)
            ImGui.TextWrapped(Loc.T(".ssc not found (sound editing is for folder levels)."));
        else
        {
            var sc = em.Script;
            ImGui.PushItemWidth(-128f);   // leave room for the "Min distance (m)" / "Volume" labels (not -1, which clips them)
            if (!ImGui.IsAnyItemActive()) sndWavBuf = sc.Wav ?? "";
            ImGui.TextDisabled(sc.SourceMode + " wav:");
            ImGui.SetNextItemWidth(-1f);   // the wav path field has no label, so let it use the full row width
            if (ImGui.InputText("##sndwav", ref sndWavBuf, 200)) { sc.SetWav(sndWavBuf.Trim()); em.Dirty = true; }
            float vol = sc.Volume;
            if (ImGui.DragFloat(Loc.TL("Volume"), ref vol, 0.01f, 0f, 4f)) { sc.SetVolume(MathF.Max(0f, vol)); em.Dirty = true; }
            float md = sc.MinDistance;
            if (ImGui.DragFloat(Loc.TL("Min distance (m)"), ref md, 0.25f, 0f, 2000f)) { sc.SetMinDistance(MathF.Max(0f, md)); em.Dirty = true; }
            bool loop = sc.Loop;
            if (ImGui.Checkbox(Loc.TL("Loop"), ref loop)) { sc.SetLoop(loop); em.Dirty = true; }
            ImGui.SameLine();
            bool stereo = sc.Stereo;
            if (ImGui.Checkbox(Loc.TL("Stereo"), ref stereo)) { sc.SetStereo(stereo); em.Dirty = true; }
            ImGui.PopItemWidth();
            int placements = so.Objects.Count(x => string.Equals(x.Template, o.Template, StringComparison.OrdinalIgnoreCase));
            ImGui.TextDisabled($"shared script - {placements} placement{(placements == 1 ? "" : "s")}");
            if (em.Dirty) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("unsaved - Ctrl+S writes the .ssc"));
        }
    }

    // Imported .obj: show counts + a one-click standard-mesh export (so it can be packed + used in-game).
    if (importedObjs.TryGetValue(o.Template, out var imp))
    {
        ImGui.Separator();
        ImGui.TextDisabled(Loc.T("IMPORTED MESH  -  ") + o.Template);
        int withTex = importMaterials.TryGetValue(o.Template, out var ml) ? ml.Count(m => !string.IsNullOrEmpty(m.TexName)) : 0;
        ImGui.TextDisabled($"{imp.TotalVertices} verts, {imp.TotalFaces} tris, {imp.SubMeshes.Count} material(s), {withTex} textured");
        ImGui.Checkbox(Loc.TL("include collision (experimental)"), ref expCollision);
        if (expCollision) ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), Loc.T("empty-BSP col - TEST IN-GAME (may not collide yet)"));
        if (ImGui.Button(Loc.TL("Export as .sm (+ .rs)..."))) DoExportObjSm(o.Template);
    }
}

// Layer visibility toggles - always shown at the bottom of the Inspector panel.
// The resolved overgrowth scatter: the .wst geometry scattered per cell (OvergrowthFoliage.Scatter), each dropped
// to ground height (skipping underwater) and kept only if its mesh resolves in the loaded library. SHARED by the
// GL overlay AND the bake-to-.con export so they're identical. Empty if no overgrowth / mesh lib / terrain.
List<(string Tmpl, float X, float Y, float Z, float Yaw, float Scale)> ScatterOvergrowthResolved()
{
    var outp = new List<(string, float, float, float, float, float)>();
    if (meshLib is null || growth?.Over is null || growth.OverPalette is null || terrainPick is null) return outp;
    foreach (var fi in RefractorForge.Formats.Terrain.OvergrowthFoliage.Scatter(growth, cfg, foliageSpacing, foliageDensity, over: true))
    {
        if (!meshLib.TryGet(fi.Geometry, out _)) continue;             // skip geometries with no mesh in the library
        float y = terrainPick.HeightAt(fi.WorldX, fi.WorldZ);
        if (y < cfg.WaterLevel) continue;                              // no foliage underwater
        outp.Add((fi.Geometry, fi.WorldX, y, fi.WorldZ, fi.YawDeg, fi.Scale));
    }
    return outp;
}

// Rebuild the overgrowth foliage overlay (a VIEW only - never saved as part of the level). Builds GL instances
// from the shared resolved scatter and hands them to GlObjects.
void BuildOvergrowthFoliage()
{
    foliageDirty = false;
    foliageCount = 0;
    if (glObjects is null) return;
    var inst = ScatterOvergrowthResolved();
    var gi = new List<(string, Matrix4x4)>(inst.Count);
    foreach (var (t, x, y, z, yaw, s) in inst)
        gi.Add((t, Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationY(yaw * MathF.PI / 180f) * Matrix4x4.CreateTranslation(x, y, z)));
    glObjects.SetFoliage(gl, gi, meshLib!);
    foliageCount = glObjects.FoliageInstanceCount;
}

// Per-map overlay settings (spacing + on/off): a tiny JSON sidecar beside the level (in the folder, or next to the
// .rfa keyed by its base name), so the overgrowth view + bake are reproducible across sessions.
string? OvergrowthSettingsPath()
{
    if (levelDir is null) return null;
    if (Directory.Exists(levelDir)) return Path.Combine(levelDir, "refractorforge.overgrowth.json");
    var full = Path.GetFullPath(levelDir);
    var d = Path.GetDirectoryName(full) ?? ".";
    return Path.Combine(d, Path.GetFileNameWithoutExtension(full) + ".overgrowth.json");
}

void SaveOvergrowthSettings()
{
    var p = OvergrowthSettingsPath();
    if (p is null) { Toast(Loc.T("Open a level first.")); return; }
    try
    {
        File.WriteAllText(p, System.Text.Json.JsonSerializer.Serialize(new { show = showFoliage, spacing = foliageSpacing, density = foliageDensity }));
        Toast($"Saved overgrowth settings -> {Path.GetFileName(p)}");
    }
    catch (Exception ex) { Toast(Loc.T("Save overgrowth settings failed: ") + ex.Message); }
}

void LoadOvergrowthSettings()
{
    var p = OvergrowthSettingsPath();
    if (p is null || !File.Exists(p)) return;
    try
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(p));
        var root = doc.RootElement;
        if (root.TryGetProperty("spacing", out var sp) && sp.TryGetSingle(out var spv)) foliageSpacing = Math.Clamp(spv, 6f, 32f);
        if (root.TryGetProperty("density", out var dn) && dn.TryGetSingle(out var dnv)) foliageDensity = Math.Clamp(dnv, 0.25f, 1.5f);
        if (root.TryGetProperty("show", out var sh)) showFoliage = sh.GetBoolean();
        foliageDirty = true;
    }
    catch { }
}

// Export the level's native overgrowth definition (the painted OverGrowthMap.raw + the overGrowth.wst palette,
// preserved verbatim) to a folder the user picks. Undergrowth is exported too when present.
void DoExportOvergrowthFiles()
{
    if (growth?.Over is null || growth.OverPalette is null) { Toast(Loc.T("No overgrowth loaded.")); return; }
    var dir = Picker.Folder("Choose a folder to export the overgrowth files to", levelDir);
    if (dir is null) return;
    try
    {
        File.WriteAllBytes(Path.Combine(dir, "OverGrowthMap.raw"), growth.Over.Samples);
        File.WriteAllText(Path.Combine(dir, "overGrowth.wst"), growth.OverPalette.RawXml);
        int n = 2;
        if (growth.Under is not null && growth.UnderPalette is not null)
        {
            File.WriteAllBytes(Path.Combine(dir, "UnderGrowthMap.raw"), growth.Under.Samples);
            File.WriteAllText(Path.Combine(dir, "underGrowth.wst"), growth.UnderPalette.RawXml);
            n += 2;
        }
        Toast($"Exported {n} overgrowth file(s) -> {dir}");
    }
    catch (Exception ex) { Toast(Loc.T("Overgrowth export failed: ") + ex.Message); }
}

// Bake the editor's overgrowth scatter (exactly what the overlay shows, at the current spacing) into a standalone
// StaticObjects.con: each tree/rock becomes an object.create + absolutePosition + rotation. The file holds ONLY the
// overgrowth, separate from the level's own StaticObjects.con. (The engine also generates foliage from the maps at
// load, so mounting this in-game ON TOP of the overgrowth would double it -- use on a map whose overgrowth you've
// cleared, or as a one-off prop layer.)
void DoBakeOvergrowthToCon()
{
    if (growth?.Over is null || meshLib is null || terrainPick is null) { Toast(Loc.T("No overgrowth / mesh archive loaded.")); return; }
    var inst = ScatterOvergrowthResolved();
    if (inst.Count == 0) { Toast(Loc.T("No overgrowth to bake (paint overgrowth + load the mesh archive).")); return; }
    var sof = new RefractorForge.Formats.Con.StaticObjectsFile();
    sof.Header.Add($"rem *** {inst.Count} overgrowth objects baked by RefractorForge (spacing {foliageSpacing:0.#} m) ***");
    foreach (var (t, x, y, z, yaw, s) in inst)
    {
        var o = new RefractorForge.Formats.Con.StaticObject(t)
        { Position = new RefractorForge.Formats.Geometry.Vec3(x, y, z), Rotation = new RefractorForge.Formats.Geometry.Vec3(yaw, 0f, 0f) };
        if (MathF.Abs(s - 1f) > 1e-3f) o.Scale = s;
        sof.Objects.Add(o);
    }
    string baseDir = Directory.Exists(levelDir) ? levelDir!
                   : (levelDir is not null ? (Path.GetDirectoryName(Path.GetFullPath(levelDir)) ?? ".") : ".");
    var outPath = Picker.Save("Save overgrowth as StaticObjects.con", "CON files|*.con|All files|*.*", "Overgrowth_StaticObjects.con", baseDir);
    if (outPath is null) return;
    try { sof.Save(outPath); Toast($"Baked {inst.Count} overgrowth objects -> {Path.GetFileName(outPath)}"); }
    catch (Exception ex) { Toast(Loc.T("Overgrowth bake failed: ") + ex.Message); }
}

void LayersPanel()
{
    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("LAYERS"));
    ImGui.Checkbox(Loc.TL("Terrain"), ref showTerrain);
    ImGui.Checkbox(Loc.TL("Static Objects"), ref showObjects);
    ImGui.Checkbox(Loc.TL("Texture transparency"), ref alphaTransparency);
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Show texture alpha as transparency (foliage cards, fences, windows, decals).\nOff = everything renders opaque."));
    if (ImGui.Checkbox(Loc.TL("Collision (wireframe)"), ref showCollision) && showCollision) collisionDirty = true;
    ImGui.Checkbox(string.Format(Loc.T("Vehicles ({0})"), gameplayEdit.VehicleSpawns.Count) + "###vehLayer", ref showVehicles);
    ImGui.Checkbox(string.Format(Loc.T("Control Points ({0})"), gameplayEdit.ControlPoints.Count) + "###cpLayer", ref showControlPoints);
    ImGui.Checkbox(string.Format(Loc.T("Spawn Points ({0})"), gameplayEdit.SoldierSpawns.Count) + "###spawnLayer", ref showSpawns);
    ImGui.Checkbox(Loc.TL("Spawn Links"), ref showSpawnLinks);
    if (sounds.Count > 0)
    {
        ImGui.Checkbox(string.Format(Loc.T("Sounds ({0})"), sounds.Count) + "###soundLayer", ref showSounds);
        ImGui.SameLine();
        if (ImGui.Checkbox(Loc.TL("Play##sounds"), ref playSounds))
        {
            try { soundPlayback ??= new SoundPlayback(ResolveSoundWav); soundPlayback.SetEnabled(playSounds); }   // lazy: spin up audio on first enable
            catch (System.Exception ex) { playSounds = false; Console.WriteLine($"Sound playback unavailable: {ex.GetType().Name} {ex.Message}"); }
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Play placed LOOPING ambient sounds (frogs, crickets, water...) while the\ncamera is inside their ring, fading with distance. Needs the level's .wav\n(in the level .rfa's Sound/ or a shared sound*.rfa)."));
    }
    // Overgrowth-trees overlay: instance the .wst foliage geometry on the map (a view of the in-game foliage).
    // BFV-only feature (BF1942 has no overgrowth system), so hide it for a BF1942 target.
    if (!gameIsBf1942 && growth?.Over is not null && growth.OverPalette is not null)
    {
        if (ImGui.Checkbox($"Overgrowth Trees ({foliageCount})###foliageLayer", ref showFoliage)) { if (showFoliage) foliageDirty = true; BroadcastOvergrowth(); }
        if (showFoliage)
        {
            // Patch size + density use the game's patch model (default 12.5 m / x1.0 = the density BfVietnam generates).
            ImGui.SetNextItemWidth(150f);
            if (SldF(Loc.TL("Patch size (m)"), ref foliageSpacing, 6f, 32f, "%.1f")) { foliageDirty = true; BroadcastOvergrowth(); }
            ImGui.SetNextItemWidth(150f);
            // Density tops out at 1.5x: x1.0 is already the game-matched density, so the useful range is a small over/under.
            if (SldF(Loc.TL("Density x"), ref foliageDensity, 0.25f, 1.5f, "%.2f")) { foliageDirty = true; BroadcastOvergrowth(); }
        }
    }
    ImGui.Checkbox(Loc.TL("Water"), ref showWater);
    if (showWater && haveWaterTex)
    {
        ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Textured##water"), ref useWaterTextures);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("The level's scrolling water textures (water.texLayer1/2 + normalMap)\nare loaded. Uncheck for the plain procedural water."));
    }
    ImGui.Checkbox(Loc.TL("Sky"), ref showSky);
    if (ImGui.Checkbox(Loc.TL("Sun Shadows (real-time)"), ref showShadows) && showShadows) shadowMapDirty = true;
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Real-time sun cast-shadows on terrain + objects, from the current sun position.\nOFF by default. Move the sun in the Environment panel to recast them live."));
    if (ImGui.Checkbox(Loc.TL("Object Lightmaps"), ref showObjectLightmaps) && showObjectLightmaps)
    {
        // Enabling it: decode now so we can tell the user when a level simply HAS no baked object lightmaps (e.g. the
        // FHSW Tigerpass ships none - the base BF1942 Tigerpass has 80; the FHSW mapper just didn't bake them). Without
        // this, the toggle silently does nothing and reads as broken. Point them at the bake tool.
        EnsureObjectLightmaps();
        if ((objectLightmaps?.Count ?? 0) == 0)
            Toast(Loc.T("This level has no baked object lightmaps. Use Tools > \"Bake Object Lightmaps (from sun)\" to generate them."));
    }
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Show the level's BAKED per-object lighting (ObjectLightMaps/*.tga or *.dds). Loaded on first\nenable (kept off the load path). If the level ships none (some custom/FHSW maps don't), bake them\nwith Tools > Bake Object Lightmaps. Ignored while you control the sun manually (objects then light\ndynamically: real-time N-L + sun shadows, following the sun)."));

    // Weather (rain/snow/dust): a preview overlay + PLACEABLE emitters generated into the level on save.
    if (ImGui.Checkbox(Loc.TL("Effects"), ref showEffects) && showEffects)
    {
        EnsureEffects();   // lazy-parse the level's FX particle effects on first enable
        if (fxInstances.Count == 0) Toast(Loc.T("This level has no placed particle effects (or their textures aren't loaded)."));
    }
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Animate the level's placed particle EFFECTS (FX/*.con: waterfalls, lava,\nfire, smoke, steam...) as billboards. Loaded on first enable. A preview\napproximation of the in-game particle systems."));
    ImGui.Checkbox(Loc.TL("Animate objects"), ref showAnimations);
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Spin continuously-rotating object parts (BF1942 RotationalBundle: windmill\nblades, watermill wheel, and any mod object using setContinousRotationSpeed),\nlike in-game. View-only."));
    ImGui.Checkbox(Loc.TL("Weather (preview)"), ref showWeather);
    // Weather the LEVEL itself defines (FH winter maps etc.): announce it + one click arms the matching preview.
    if (!levelWeatherScanned) ScanLevelWeather();
    if (detectedLevelWeather.Count > 0)
    {
        ImGui.TextColored(new Vector4(0.55f, 0.85f, 1f, 1f),
            string.Format(Loc.T("This level defines weather: {0}"), string.Join(", ", detectedLevelWeather.Select(w => w.Name).Take(4))));
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.TL("Preview it")))
        { showWeather = true; weatherTypeIdx = detectedLevelWeather[0].TypeIdx; }
    }
    if (showWeather)
    {
        ImGui.SetNextItemWidth(120f);
        ImGui.Combo(Loc.TL("Type"), ref weatherTypeIdx, "Snow\0Rain\0Dust\0Dust Storm\0");
        ImGui.SetNextItemWidth(150f);
        SldI(Loc.TL("Intensity/s"), ref weatherIntensity, 20, 600);
        ImGui.SetNextItemWidth(150f);
        SldF(Loc.TL("Wind"), ref weatherWind, -10f, 10f, "%.1f");
        // Place a weather emitter on the map (the normal Refractor way) - arms the Place tool with the bundle.
        if (ImGui.Button(Loc.TL("Place emitter"))) ArmWeatherPlace();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Arms the Place tool with this weather emitter - click the map to drop it (shows as a marker; saves into StaticObjects.con)."));
        ImGui.SameLine();
        if (ImGui.Button(Loc.TL("Import texture..."))) ImportWeatherTexture();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Use a custom particle image (.dds/.tga/.png) for this weather type - shown in the preview and shipped to the level."));
        int placed = so?.Objects.Count(o => RefractorForge.Formats.Con.WeatherEffect.TypeOfBundle(o.Template) is not null) ?? 0;
        ImGui.Checkbox(Loc.TL("Also auto-place one at map centre"), ref weatherApply);
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), $"{placed} placed - saves Effects/RF_Weather.con + texture (TEST IN-GAME).");
    }
}

// Arm the Place tool with the current weather type's emitter bundle, so a click drops it on the map.
void ArmWeatherPlace()
{
    browserTemplate = RefractorForge.Formats.Con.WeatherEffect.BundleName(WeatherKind());
    gpPlaceKind = null;
    int pi = Array.IndexOf(toolNames, "Place"); if (pi >= 0) tool = pi;
    mapper = 2;   // Object mapper
    Toast($"Click the map to place a {WeatherKind()} emitter.");
}

// Import a custom particle image for the current weather type (preview + ship).
void ImportWeatherTexture()
{
    var path = Picker.File("Import particle image (rain/snow/dust)", "Images|*.dds;*.tga;*.bmp;*.png|All files|*.*", levelDir);
    if (path is null) return;
    var tex = LoadImageAsTexture(path);
    if (tex is null) { Toast(Loc.T("Could not load that image.")); return; }
    int idx = (int)WeatherKind();
    weatherTexImg[idx] = tex;
    if (weatherTexGl[idx] != 0) { gl.DeleteTexture(weatherTexGl[idx]); weatherTexGl[idx] = 0; }   // rebuild preview tex
    Toast($"{WeatherKind()} particle texture imported ({tex.Width}x{tex.Height}).");
}

// The distinct weather types to ship = the types of placed weather emitters, plus the panel type if "auto-place".
System.Collections.Generic.List<RefractorForge.Formats.Con.WeatherType> WeatherTypesToShip()
{
    var set = new System.Collections.Generic.List<RefractorForge.Formats.Con.WeatherType>();
    void Add(RefractorForge.Formats.Con.WeatherType t) { if (!set.Contains(t)) set.Add(t); }
    if (so is not null)
        foreach (var o in so.Objects)
            if (RefractorForge.Formats.Con.WeatherEffect.TypeOfBundle(o.Template) is RefractorForge.Formats.Con.WeatherType t) Add(t);
    if (weatherApply) Add(WeatherKind());
    return set;
}

// The particle texture (.dds bytes) for a weather type: the imported image if any, else the generated particle.
byte[] WeatherTextureDds(RefractorForge.Formats.Con.WeatherType t)
{
    var img = weatherTexImg[(int)t] ?? new Texture2D(32, 32, RefractorForge.Formats.Con.WeatherEffect.BuildParticleRgba(t, 32));
    return DdsTexture.EncodeUncompressed(img);
}

// The Effects/RF_Weather.con text for the shipped types (templates) + an auto-placed instance when "auto-place" is on.
byte[] WeatherConBytes(System.Collections.Generic.List<RefractorForge.Formats.Con.WeatherType> types)
{
    var con = RefractorForge.Formats.Con.WeatherEffect.BuildTemplatesCon(types, weatherIntensity, weatherWind, (float)cfg.WorldSize);
    if (weatherApply)
    {
        var p = RefractorForge.Formats.Con.WeatherEffect.InstancePosition((float)cfg.WorldSize, cfg.WaterLevel + 5f);
        con += $"rem auto-placed emitter (map centre)\r\nObject.create {RefractorForge.Formats.Con.WeatherEffect.BundleName(WeatherKind())}\r\n" +
               $"Object.absolutePosition {p.X.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{p.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)}/{p.Z.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n";
    }
    return System.Text.Encoding.Latin1.GetBytes(con);
}

// Write the weather effect (Effects/RF_Weather.con + a particle texture per shipped type) into a FOLDER level and
// wire the Init run-include. Driven by PLACED weather emitters (+ the auto-place option). Test in-game.
void ApplyWeatherToLevel()
{
    if (levelDir is null || !System.IO.Directory.Exists(levelDir)) return;
    var types = WeatherTypesToShip();
    if (types.Count == 0) return;
    try
    {
        var fxDir = System.IO.Path.Combine(levelDir, "Effects");
        System.IO.Directory.CreateDirectory(fxDir);
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(fxDir, RefractorForge.Formats.Con.WeatherEffect.ConFileName), WeatherConBytes(types));
        foreach (var t in types)
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(fxDir, RefractorForge.Formats.Con.WeatherEffect.TextureName(t) + ".dds"), WeatherTextureDds(t));
        var initPath = System.IO.Directory.EnumerateFiles(levelDir, "Init.con", System.IO.SearchOption.AllDirectories)
                           .Where(p => !p.Replace('\\', '/').ToLowerInvariant().Contains("/menu/"))
                           .OrderBy(p => p.Length).FirstOrDefault()
                       ?? System.IO.Path.Combine(levelDir, "Init.con");
        var initText = System.IO.File.Exists(initPath) ? System.IO.File.ReadAllText(initPath) : "";
        if (!initText.Contains(RefractorForge.Formats.Con.WeatherEffect.ConFileName, StringComparison.OrdinalIgnoreCase))
            System.IO.File.WriteAllText(initPath, initText.TrimEnd() + "\r\n\r\nrem RefractorForge weather\r\n" + RefractorForge.Formats.Con.WeatherEffect.RunInclude() + "\r\n");
        Toast($"Weather ({string.Join("/", types)}) written to Effects\\ (test in-game).");
        Console.WriteLine($"   Weather: wrote Effects/RF_Weather.con + {types.Count} texture(s) + Init run-include.");
    }
    catch (Exception ex) { Toast(Loc.T("Weather write failed: ") + ex.Message); }
}

// The new (con + per-type textures) entries + the Init.con run-edit for the .rfa save paths. Empty when no weather.
(System.Collections.Generic.List<(string RelPath, byte[] Bytes)> NewFiles, (string Name, byte[] Bytes)? InitEdit) WeatherRfaPieces(string baseRfaForInit)
{
    var newFiles = new System.Collections.Generic.List<(string, byte[])>();
    var types = WeatherTypesToShip();
    if (types.Count == 0) return (newFiles, null);
    newFiles.Add(("Effects/" + RefractorForge.Formats.Con.WeatherEffect.ConFileName, WeatherConBytes(types)));
    foreach (var t in types)
        newFiles.Add(("Effects/" + RefractorForge.Formats.Con.WeatherEffect.TextureName(t) + ".dds", WeatherTextureDds(t)));
    (string, byte[])? initEdit = null;
    try
    {
        var arch = new RefractorFlatArchive(baseRfaForInit);
        var e = arch.Entries
            .Where(x => x.Name.EndsWith("Init.con", StringComparison.OrdinalIgnoreCase)
                     && !x.Name.Replace('\\', '/').ToLowerInvariant().Contains("/menu/"))
            .OrderBy(x => x.Name.Length).FirstOrDefault();
        if (e is not null)
        {
            var txt = System.Text.Encoding.Latin1.GetString(arch.Read(e));
            if (!txt.Contains(RefractorForge.Formats.Con.WeatherEffect.ConFileName, StringComparison.OrdinalIgnoreCase))
                initEdit = ("Init.con", System.Text.Encoding.Latin1.GetBytes(txt.TrimEnd() + "\r\n\r\nrem RefractorForge weather\r\n" + RefractorForge.Formats.Con.WeatherEffect.RunInclude() + "\r\n"));
        }
    }
    catch { }
    return (newFiles, initEdit);
}

// Patch the animated-cloud block into the level's SkyAndSun.con on a FOLDER save (preserves skybox/sun; strips any
// old cloud block first). Game-compatible Cloud system; needs a 'cloud' StandardMesh in-game to actually render.
void SaveCloudsFolder()
{
    if (!cloudsDirty || env is null || levelDir is null || !System.IO.Directory.Exists(levelDir)) return;
    SaveCloudsToEnv();
    var skyPath = System.IO.Directory.EnumerateFiles(levelDir, "SkyAndSun.con", System.IO.SearchOption.AllDirectories).FirstOrDefault();
    try
    {
        if (skyPath is not null)
            System.IO.File.WriteAllLines(skyPath, env.PatchSkyAndSunConLines(System.IO.File.ReadAllLines(skyPath)));
        else
        {
            // No SkyAndSun.con to patch - write a fresh one under Init/.
            var initDir = System.IO.Path.Combine(levelDir, "Init"); System.IO.Directory.CreateDirectory(initDir);
            System.IO.File.WriteAllLines(System.IO.Path.Combine(initDir, "SkyAndSun.con"), env.ToSkyAndSunConLines());
        }
        // Ship an imported cloud mesh into the level (so the engine has the 'cloud' StandardMesh).
        if (cloudMeshImportPath is not null && System.IO.File.Exists(cloudMeshImportPath))
        {
            var smDir = System.IO.Path.Combine(levelDir, "StandardMesh"); System.IO.Directory.CreateDirectory(smDir);
            System.IO.File.Copy(cloudMeshImportPath, System.IO.Path.Combine(smDir, System.IO.Path.GetFileName(cloudMeshImportPath)), overwrite: true);
        }
        cloudsDirty = false;
        Console.WriteLine("   Clouds: patched SkyAndSun.con (test in-game; needs a 'cloud' mesh).");
        Toast(Loc.T(cloudsOn ? "Clouds written to SkyAndSun.con (test in-game)." : "Clouds removed from SkyAndSun.con."));
    }
    catch (Exception ex) { Toast(Loc.T("Cloud save failed: ") + ex.Message); }
}

// Import a custom cloud image -> the scrolling-cloud preview texture (REPEAT) + shipped to the level on save.
unsafe void ImportCloudTexture()
{
    var path = Picker.File("Import cloud texture (image)", "Images|*.dds;*.tga;*.bmp;*.png|All files|*.*", levelDir);
    if (path is null) return;
    var tex = LoadImageAsTexture(path);
    if (tex is null) { Toast(Loc.T("Could not load that image.")); return; }
    if (cloudTex != 0) gl.DeleteTexture(cloudTex);
    cloudTex = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, cloudTex);
    fixed (byte* p = tex.Rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)tex.Width, (uint)tex.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.GenerateMipmap(TextureTarget.Texture2D);
    cloudsOn = true;
    Toast($"Cloud texture imported ({tex.Width}x{tex.Height}).");
}

// Import a cloud StandardMesh/.obj: reference it (CloudMeshFile) + ship it into the level on save (for in-game).
void ImportCloudMesh()
{
    if (env is null) { Toast(Loc.T("Load a level first.")); return; }
    var path = Picker.File("Import cloud mesh", "Meshes|*.sm;*.obj|All files|*.*", levelDir);
    if (path is null) return;
    cloudMeshImportPath = path;
    env.CloudMeshFile = System.IO.Path.GetFileNameWithoutExtension(path);
    cloudsOn = true; cloudsDirty = true;
    Toast($"Cloud mesh '{env.CloudMeshFile}' set (shipped on save; test in-game).");
}

// The imported cloud mesh as a new .rfa entry under the level's StandardMesh/ (null when none imported).
(string RelPath, byte[] Bytes)? CloudMeshNewEntry()
{
    if (cloudMeshImportPath is null || !System.IO.File.Exists(cloudMeshImportPath)) return null;
    try { return ("StandardMesh/" + System.IO.Path.GetFileName(cloudMeshImportPath), System.IO.File.ReadAllBytes(cloudMeshImportPath)); }
    catch { return null; }
}

// The SkyAndSun.con replacement (base content + patched cloud block) for the .rfa save paths; null when clouds
// aren't being changed or there's no base SkyAndSun.con entry to patch.
(string Name, byte[] Bytes)? CloudRfaExtra(string baseRfa)
{
    if (!cloudsDirty || env is null) return null;
    SaveCloudsToEnv();
    try
    {
        var arch = new RefractorFlatArchive(baseRfa);
        var e = arch.Entries.FirstOrDefault(x => x.Name.EndsWith("SkyAndSun.con", StringComparison.OrdinalIgnoreCase));
        if (e is null) return null;
        var lines = System.Text.Encoding.Latin1.GetString(arch.Read(e)).Replace("\r\n", "\n").Split('\n');
        var patched = string.Join("\r\n", env.PatchSkyAndSunConLines(lines)) + "\r\n";
        return ("SkyAndSun.con", System.Text.Encoding.Latin1.GetBytes(patched));
    }
    catch { return null; }
}

// Environment editor: distance fog (and room for more sky/lighting settings later). Live-applied to the
// terrain + object shaders; seeded from the level's Init.con (renderer.vertexFog* / fog start-end).
void EnvironmentPanel()
{
    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("GAME"));
    int gameIdx = gameIsBf1942 ? 0 : 1;
    ImGui.SetNextItemWidth(180f);
    if (ImGui.Combo(Loc.TL("Target game"), ref gameIdx, "Battlefield 1942\0Battlefield Vietnam\0")) gameIsBf1942 = gameIdx == 0;
    if (gameIsBf1942) ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), Loc.T("BF1942: no overgrowth / tunnels."));
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Auto-detected from the level path; set it here if wrong. Drives team names (Axis/Allies vs NVA/US) and which features show."));

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("TERRAIN"));
    // Water level edits cfg.WaterLevel live: the water plane (uWaterY), terrain water tint (uWater) and ground
    // picking all read it each frame. Saved into Init/Terrain.con on F5. DragFloat = drag or ctrl-click to type.
    float wl = cfg.WaterLevel;
    ImGui.SetNextItemWidth(150f);
    if (ImGui.DragFloat(Loc.TL("Water level (m)"), ref wl, 0.25f, -5000f, 5000f, "%.1f")) { cfg.WaterLevel = wl; waterLevelEdited = true; BroadcastWater(); }
    ImGui.SameLine();
    if (ImGui.SmallButton(Loc.TL("Reset##wl")) && env is not null) { cfg.WaterLevel = waterLevelLoaded; waterLevelEdited = false; BroadcastWater(); }
    // Water surface colour + transparency, and the submerged-terrain (deep) tint - seeded from the level's
    // water.color / water.deepcolor / waterShallowAlpha.
    ImGui.ColorEdit3(Loc.TL("Water colour"), ref waterColor);
    ImGui.ColorEdit3(Loc.TL("Deep colour"), ref deepColor);
    ImGui.SetNextItemWidth(150f);
    SldF(Loc.TL("Water transparency"), ref waterAlpha, 0.08f, 1f, "%.2f");
    if (ImGui.SmallButton(Loc.TL("Reset##water")) && env is not null)
    {
        waterColor = new Vector3(env.WaterColor.X, env.WaterColor.Y, env.WaterColor.Z);
        deepColor = new Vector3(env.DeepColor.X, env.DeepColor.Y, env.DeepColor.Z);
        waterAlpha = env.WaterAlpha;
    }
    // Water TEXTURES (the level's scrolling water.texLayer1/2 + normalMap). Show whether they resolved + let the user
    // supply their own (base BF1942 maps reference engine-built-in water07/08 that aren't shipped in the .rfa).
    if (haveWaterTex) ImGui.TextDisabled(Loc.T("Water textures: loaded (level)"));
    else if (env is not null && env.HasWaterTextures) ImGui.TextDisabled($"Water textures: {env.WaterTexLayer1 ?? env.WaterBaseTex} not in archives");
    else ImGui.TextDisabled(Loc.T("Water textures: none (color only)"));
    if (ImGui.SmallButton(Loc.TL("Import water textures..."))) ImportWaterTextures();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Pick the scrolling water texture(s): diffuse layer 1, then optionally layer 2 + a normal map.\nUse this when the level references water textures that aren't in its archives (most stock BF1942 maps)."));

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("SUN"));
    if (ImGui.Checkbox(Loc.TL("Control sun manually"), ref sunOverride)) shadowMapDirty = true;
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Override the level's SkyAndSun.con direction with the sliders below.\nMoving the sun relights terrain + objects and recasts real-time shadows live."));
    if (sunOverride)
    {
        ImGui.SetNextItemWidth(150f);
        if (SldF(Loc.TL("Sun azimuth"), ref sunAzimuthDeg, -180f, 180f, "%.0f deg")) shadowMapDirty = true;
        ImGui.SetNextItemWidth(150f);
        if (SldF(Loc.TL("Sun elevation"), ref sunElevationDeg, 2f, 89f, "%.0f deg")) shadowMapDirty = true;
        if (ImGui.Button(Loc.TL("Reset sun to level")) && env is not null)
        {
            sunOverride = false;
            var s = EffectiveSun();
            sunElevationDeg = MathF.Asin(Math.Clamp(s.Y, -1f, 1f)) * 180f / MathF.PI;
            sunAzimuthDeg = MathF.Atan2(s.X, s.Z) * 180f / MathF.PI;
            shadowMapDirty = true;
        }
    }

    ImGui.Separator();
    // Terrain paint alignment: move/zoom the ground texture until it sits on the terrain it belongs to.
    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("TERRAIN PAINT ALIGNMENT"));
    ImGui.SetNextItemWidth(150f);
    if (SldF(Loc.TL("Paint scale"), ref terUvScale, 0.25f, 4f, "%.4f")) ApplyTerrainUv();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Zoom of the ground texture about the map's origin corner.\nBelow 1 pulls the paint inward, above 1 pushes it outward."));
    ImGui.SetNextItemWidth(150f);
    if (SldF(Loc.TL("Paint offset X"), ref terUvOffX, -1f, 1f, "%.4f")) ApplyTerrainUv();
    ImGui.SetNextItemWidth(150f);
    if (SldF(Loc.TL("Paint offset Z"), ref terUvOffY, -1f, 1f, "%.4f")) ApplyTerrainUv();
    if (ImGui.Button(Loc.TL("Reset paint alignment"))) { terUvScale = 1f; terUvOffX = 0f; terUvOffY = 0f; ApplyTerrainUv(); }
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Log values")))
        Console.WriteLine($"Terrain paint alignment: scale={terUvScale:0.#####} offsetX={terUvOffX:0.#####} offsetZ={terUvOffY:0.#####}" +
                          $"  (worldSize {cfg.WorldSize}, offset in metres = {terUvOffX * cfg.WorldSize:0.#}, {terUvOffY * cfg.WorldSize:0.#})");

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("CAMERA"));
    ImGui.SetNextItemWidth(150f);
    SldF(Loc.TL("Fly speed"), ref camSpeedMult, 0.1f, 8f, "%.2fx");
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Multiplier on WASD fly speed + scroll-zoom. Hold Shift for a 4x burst.\nRight-click a slider to type an exact value."));
    bool gcam = groundCam;
    if (ImGui.Checkbox(Loc.TL("Battlecraft camera (fly where you look)  [F7]"), ref gcam)) SetGroundCam(gcam);
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("W/S travel toward whatever is in the middle of the screen, so aiming down descends and\naiming up climbs - the height follows your view instead of needing Q/E.\nA/D still strafe level and Q/E still move straight up/down.\nOff = the fly camera, which keeps your altitude until you press Q/E."));

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("ENVIRONMENT"));
    ImGui.Checkbox(Loc.TL("Fog"), ref fogEnabled);
    if (fogEnabled)
    {
        ImGui.ColorEdit3(Loc.TL("Fog colour"), ref fogColor);
        SldF(Loc.TL("Fog start (m)"), ref fogStart, 0f, Math.Max(2000f, fogEnd), "%.0f");
        SldF(Loc.TL("Fog end (m)"), ref fogEnd, fogStart + 1f, Math.Max(4000f, cfg.WorldSize * 2f), "%.0f");
        if (fogStart > fogEnd - 1f) fogStart = fogEnd - 1f;
    }
    if (ImGui.Button(Loc.TL("Reset to level default")) && env is not null)
    {
        fogEnabled = env.FogEnabled;
        fogColor = new Vector3(env.FogColor.X, env.FogColor.Y, env.FogColor.Z);
        fogStart = env.FogStart; fogEnd = env.FogEnd;
    }

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("SKY"));
    if (skyCubeTex != 0)
    {
        ImGui.Checkbox(Loc.TL("Use level cubemap"), ref skyUseCubemap);
        if (!skyUseCubemap && skyMeshOk) ImGui.TextDisabled($"(using level mesh {env?.SkyBoxMesh})");
        else if (!skyUseCubemap) ImGui.TextDisabled(Loc.T("(procedural sun-sky)"));
    }
    else if (skyMeshOk)
        ImGui.TextDisabled($"Skybox: {env?.SkyBoxMesh} (level mesh)");
    else ImGui.TextDisabled(Loc.T("no cubemap faces found - procedural sun-sky"));
    ImGui.SetNextItemWidth(150f);
    SldF(Loc.TL("Sky rotation (deg)"), ref skyRotDeg, -180f, 180f, "%.0f");
    if (ImGui.Button(Loc.TL("Import skybox..."))) ImportSkybox();
    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Folder with 6 faces named *_01 .. *_06 (.dds/.tga/.bmp/.png), any power-of-2 size."));

    // Per-face skybox editor: each sky-mesh material can take a replacement image or a Bink movie.
    if (skyMeshOk && skyMeshTexNames.Length > 0 && ImGui.CollapsingHeader(Loc.TL("Skybox faces (sky mesh materials)")))
    {
        ImGui.TextWrapped(Loc.T("Assign an image or a .bik movie to each face of the sky mesh. Images ship as same-named .dds inside the level (they override the archive texture for this map). A .bik face ships an override .rs pointing at the movie under the mod's Movies folder - the engine plays Bink textures. Test .bik faces in-game."));
        for (int i = 0; i < skyMeshParts.Length && i < skyMeshTexNames.Length; i++)
        {
            var texRef = skyMeshTexNames[i];
            if (string.IsNullOrEmpty(texRef)) continue;
            var shown = texRef.Replace('\\', '/'); shown = shown[(shown.LastIndexOf('/') + 1)..];
            string row = skyFaceAssign.TryGetValue(i, out var asg)
                ? $"{shown}  <-  {Path.GetFileName(asg.Path)}{(asg.Kind == "bik" ? " (movie)" : "")}"
                : shown;
            ImGui.Text(row);
            ImGui.SameLine(300f);
            if (ImGui.SmallButton(Loc.T("Image...") + $"##skyf{i}"))
            {
                var f = Picker.File(Loc.T("Skybox face image"), "Images|*.dds;*.tga;*.bmp;*.png;*.jpg|All files|*.*", null);
                if (f is not null && LoadImageAsTexture(f) is { } timg)
                {
                    skyMeshParts[i] = (skyMeshParts[i].Off, skyMeshParts[i].Count, UploadTexture(timg));
                    skyFaceAssign[i] = ("img", f); skyFacesDirty = true;
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T(".bik movie...") + $"##skyb{i}"))
            {
                var f = Picker.File(Loc.T("Skybox face movie (.bik)"), "Bink movies|*.bik|All files|*.*", null);
                if (f is not null)
                {
                    skyFaceAssign[i] = ("bik", f); skyFacesDirty = true;
                    Toast(Loc.T("Movie assigned - plays animated in-game after saving (shown static here)."));
                }
            }
            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Revert") + $"##skyr{i}"))
            {
                if (skyFaceAssign.Remove(i))
                {
                    LoadSkyboxMesh();               // restore the original textures...
                    ReapplySkyFacePreviews();       // ...then re-apply the previews still assigned
                    skyFacesDirty = skyFaceAssign.Count > 0;
                }
            }
        }
        if (skyFacesDirty) ImGui.TextColored(new Vector4(1f, 0.8f, 0.3f, 1f), Loc.T("unsaved (Ctrl+S)"));
    }

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("ANIMATED CLOUDS (Refractor Cloud system)"));
    if (cloudMeshOk)
    {
        ImGui.Checkbox(Loc.TL("Cloud layers (level mesh)"), ref showCloudMesh);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("This level ships its own scrolling cloud mesh (the bubbles/clouds); shown faithfully.\nThe procedural overlay below is off while the real mesh is used."));
    }
    if (ImGui.Checkbox(Loc.TL("Clouds"), ref cloudsOn)) cloudsDirty = true;
    if (cloudsOn)
    {
        ImGui.SetNextItemWidth(150f);
        if (SldF(Loc.TL("Coverage"), ref cloudOpacity, 0.05f, 1f, "%.2f")) cloudsDirty = true;
        ImGui.SetNextItemWidth(150f);
        if (SldF(Loc.TL("Scale##cloudscale"), ref cloudScale, 0.15f, 2f, "%.2f")) cloudsDirty = true;
        ImGui.SetNextItemWidth(150f);
        if (SldF(Loc.TL("Drift X"), ref cloudSpeedX, -0.2f, 0.2f, "%.3f")) cloudsDirty = true;
        ImGui.SetNextItemWidth(150f);
        if (SldF(Loc.TL("Drift Y"), ref cloudSpeedY, -0.2f, 0.2f, "%.3f")) cloudsDirty = true;
        if (ImGui.ColorEdit3(Loc.TL("Cloud color"), ref cloudColor)) cloudsDirty = true;
        if (ImGui.Button(Loc.TL("Import cloud texture..."))) ImportCloudTexture();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Use a custom cloud image (.dds/.tga/.png) for the scrolling layer - shown here and shipped."));
        ImGui.SameLine();
        if (ImGui.Button(Loc.TL("Import cloud mesh..."))) ImportCloudMesh();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Pick a cloud StandardMesh (.sm) or .obj - referenced + shipped so clouds render in-game."));
        if (cloudMeshImportPath is not null) ImGui.TextDisabled($"mesh: {System.IO.Path.GetFileName(cloudMeshImportPath)}");
        ImGui.TextColored(new Vector4(1f, 0.7f, 0.3f, 1f), Loc.T("Saved to SkyAndSun.con - needs a 'cloud' mesh in-game."));
    }
}

// ---- New Map (the app's first ImGui modal) ----
// Default the output parent to the current level's folder, then arm the popup for next frame.
void OpenNewMap()
{
    if (string.IsNullOrEmpty(nmFolder) && levelDir is not null)
    {
        try
        {
            var baseDir = LevelArchive.IsRfa(levelDir)
                ? Path.GetDirectoryName(Path.GetFullPath(levelDir))
                : Path.GetDirectoryName(Path.GetFullPath(levelDir.TrimEnd('\\', '/')));
            if (!string.IsNullOrEmpty(baseDir)) nmFolder = baseDir;
        }
        catch { /* leave folder blank; the user picks via Browse */ }
    }
    nmError = "";
    newMapRequest = true;
}

// Build the level folder from the dialog's settings, point Settings at it, and relaunch into the
// normal startup load path (no in-process GL teardown - far simpler and can't half-initialise state).
void DoCreateNewMap()
{
    nmError = "";
    try
    {
        var name = nmName.Trim();
        if (name.Length == 0) { nmError = Loc.T("Enter a map name."); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { nmError = "Name has invalid characters."; return; }
        if (string.IsNullOrWhiteSpace(nmFolder) || !Directory.Exists(nmFolder)) { nmError = "Choose a valid output folder."; return; }

        int matSize = nmMatSizes[Math.Clamp(nmMatSizeIdx, 0, nmMatSizes.Length - 1)];
        int worldSize = Math.Clamp(nmWorldSize, 64, 131072);
        var dir = Path.Combine(nmFolder, name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        { nmError = $"'{name}' already exists and isn't empty."; return; }

        // Auto-fit yScale so the requested peak height is actually representable. A 16-bit sample caps at
        // ~yScale*256 m, so with the default yScale 0.5 anything above ~128 m was silently clamped - which is
        // why cranking the height range still looked flat. The user's yScale is kept as a floor.
        // Validate the heightmap pick up front (type 4) so we fail with a clear message before creating anything.
        if (nmTerrainType == 4 && (nmHeightmapPath.Trim().Length == 0 || !File.Exists(nmHeightmapPath.Trim())))
        { nmError = Loc.T("Choose a heightmap .raw file to import."); return; }

        float maxMeters = MathF.Max(MathF.Max(nmMinH, nmMaxH), nmFlatHeight);
        // Flat + imported terrain use the user's yScale verbatim; the fractal types auto-fit it so tall peaks aren't clamped.
        float effYScale = (nmTerrainType == 0 || nmTerrainType == 4) ? nmYScale : MathF.Max(nmYScale, maxMeters * 256f / 60000f);
        var ncfg = new TerrainConfig { MaterialSize = matSize, WorldSize = worldSize, YScale = effYScale, WaterLevel = nmWaterLevel, SeaFloorLevel = 0f, WaveHeight = 1f };

        Heightmap nhm;
        if (nmTerrainType == 4)
        {
            var imp = Heightmap.LoadRawSquare(nmHeightmapPath.Trim());          // throws if not a square 16-bit raw
            nhm = imp.Width == matSize ? imp : imp.Resample(matSize, matSize);  // resample to the chosen grid if needed
        }
        else
        {
            ushort loRaw = ncfg.MetersToRaw(MathF.Min(nmMinH, nmMaxH)), hiRaw = ncfg.MetersToRaw(MathF.Max(nmMinH, nmMaxH));
            float rough = Math.Clamp(nmRoughness, 0.1f, 1f);
            nhm = nmTerrainType switch
            {
                0 => HeightmapGenerator.Flat(matSize, ncfg.MetersToRaw(nmFlatHeight)),                       // flat
                1 => HeightmapGenerator.Fractal(matSize, nmSeed, rough, loRaw, hiRaw),                       // rolling hills
                2 => HeightmapGenerator.Fractal(matSize, nmSeed, rough * 0.8f, loRaw, hiRaw, peak: 2.2f),    // mountains (sharper peaks)
                _ => HeightmapGenerator.Fractal(matSize, nmSeed, rough,                                      // islands (sea-rimmed)
                         ncfg.MetersToRaw(MathF.Min(nmMinH, nmWaterLevel - 10f)), hiRaw, island: true),
            };
        }

        RefractorForge.Formats.LevelSaver.CreateNewLevel(dir, name, ncfg, nhm, new EnvironmentSettings(), null, nmPlayable);
        // Persist the chosen target game beside the level (a tiny sidecar) so it survives the relaunch + future opens.
        try { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "refractorforge.game"), nmGameBf1942 ? "1942" : "vietnam"); } catch { }

        var saved = Settings.Load();   // keep the current mesh/texture archives so the new map has a library
        Settings.Save(new LevelPaths(dir, saved?.StdMesh, saved?.Objects, saved?.Textures));
        ActiveProject.Clear();   // classic in-editor New Map is Settings-based, not a project
        Console.WriteLine($"Created new level '{name}' ({matSize}^2, world {worldSize} m) at {dir}");
        RelaunchAndExit();
    }
    catch (Exception ex) { nmError = ex.Message; }
}

void RelaunchAndExit()
{
    try
    {
        // --resume is what makes the relaunch reopen what was just chosen (Open Mod / Open Level / New Map / a
        // language switch). A plain launch deliberately does NOT resume - see the arg parsing at the top.
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--resume");
            System.Diagnostics.Process.Start(psi);
        }
    }
    catch (Exception ex) { Console.WriteLine($"Relaunch failed: {ex.Message}"); }
    window.Close();
}

// Relaunch straight to the startup screen: clear the active project + force the picker (--pick skips the active-
// project + remembered-level auto-load), so the user lands on Recent Projects + Open/New.
void RelaunchToStartup()
{
    ActiveProject.Clear();
    try
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add("--pick");
            System.Diagnostics.Process.Start(psi);
        }
    }
    catch (Exception ex) { Console.WriteLine($"Relaunch to startup failed: {ex.Message}"); }
    window.Close();
}

// Switch UI language and restart: the ImGui font atlas is built once at startup (Japanese needs a CJK font), so a
// live swap would leave the new script unrenderable. Keeps the active project, so the same map reopens.
void SetLanguageAndRestart(string code)
{
    try { Loc.SetLanguage(code); } catch (Exception ex) { Toast(Loc.T("Language switch failed: ") + ex.Message); return; }
    Console.WriteLine($"UI language -> {code}; restarting...");
    RelaunchAndExit();
}

// Run a project flow from the File ▸ Project menu (native pickers + extract/create), then relaunch to load the new
// active project. ProjectFlows already saved the .rfproj + set it active + added it to recents.
void OpenProjectMenu(Func<RefractorForge.Formats.RfProject?> flow)
{
    RefractorForge.Formats.RfProject? proj;
    try { proj = flow(); } catch (Exception ex) { Toast(Loc.T("Project action failed: ") + ex.Message); return; }
    if (proj is null) return;   // cancelled
    Console.WriteLine("Opening project - restarting...");
    RelaunchAndExit();
}

// Edit the current project's manifest fields (name/game/mod/patch/mode/paths), then save it.
void OpenProjectSettings()
{
    if (activeRfProject is null) { Toast(Loc.T("No active project.")); return; }
    if (ProjectSettingsDialog.Show(activeRfProject))
        try { activeRfProject.Save(); RecentProjects.Touch(activeRfProject); Toast(Loc.T("Project settings saved.")); }
        catch (Exception ex) { Toast(Loc.T("Save failed: ") + ex.Message); }
}

// Open a different level: run the same native pickers the first-run flow uses, remember the choice, and
// relaunch into the proven startup load path (a clean in-process swap would mean rebuilding all GL state).
void OpenLevel()
{
    try
    {
        var saved = Settings.Load();
        string? lvl;
        string[] lvlArchives = Array.Empty<string>();
        var folder = Picker.Folder("Select the level FOLDER (Cancel to choose packed .rfa instead)", saved?.Level);
        if (folder is not null) lvl = folder;
        else
        {
            var rfas = Picker.Files("Select the level .rfa  (base + ANY patch .rfa together - Ctrl/Shift-click)", "RFA archives|*.rfa|All files|*.*", saved?.Level);
            if (rfas.Length == 0) return;   // cancelled - keep the current level
            lvlArchives = rfas; lvl = rfas[0];
        }

        var mesh = Picker.Files("Select ALL mesh/object archives - standardMesh.rfa, objects.rfa, patches (Ctrl/Shift-click). Cancel to skip.",
                                "RFA archives|*.rfa|All files|*.*", (saved?.MeshArchives is { Length: > 0 } sm0 ? sm0[0] : saved?.StdMesh) ?? lvl);
        var tex = Picker.Files("Select ALL texture archives - texture.rfa, texture_001.rfa, patches (Ctrl/Shift-click). Cancel to skip.",
                               "RFA archives|*.rfa|All files|*.*", (saved?.Textures is { Length: > 0 } st ? st[0] : null) ?? lvl);
        Settings.Save(new LevelPaths(lvl, null, null,
            tex.Length > 0 ? tex : saved?.Textures,
            mesh.Length > 0 ? mesh : saved?.MeshArchives,
            lvlArchives.Length > 0 ? lvlArchives : null));
        ActiveProject.Clear();   // the classic Open-Level path is Settings-based, not a project
        Console.WriteLine($"Opening {lvl} - restarting...");
        RelaunchAndExit();
    }
    catch (Exception ex) { Console.WriteLine($"Open level failed: {ex.Message}"); }
}

// Pick a MOD folder (<Game>\Mods\<Mod>), then auto-collect the mod's Archives\*.rfa PLUS the base game's
// Mods\bf1942|BfVietnam\Archives\*.rfa into the mesh + texture lists (mod archives FIRST so the mod wins -
// MeshLibrary/TextureLibrary are first-wins), parse the init.con mount chain, and pick a level .rfa from the mod.
// Shared by File > Open Mod (which then relaunches) and the first-run startup (which loads in place). Pure
// path-gathering (no window/UI state) so it is safe to call before the GL window exists. Returns false if cancelled.
bool GatherModPaths(out string[] lvlRfas, out string[] meshList, out string[] texList)
{
    lvlRfas = Array.Empty<string>(); meshList = Array.Empty<string>(); texList = Array.Empty<string>();
    var saved = Settings.Load();
    var modDir = Picker.Folder("Select the MOD folder  (e.g. ...\\Battlefield 1942\\Mods\\DesertCombat)", saved?.Level);
    if (modDir is null) return false;
    // gameRoot = the install dir (the parent of the Mods\ folder the mod lives under).
    string? gameRoot = null;
    for (var d = new DirectoryInfo(modDir.TrimEnd('\\', '/')); d?.Parent is not null; d = d.Parent)
        if (d.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase)) { gameRoot = d.Parent.FullName; break; }
    if (gameRoot is null) { Console.WriteLine("Open mod: that folder isn't under a Battlefield Mods\\ directory."); return false; }

    // The MOUNT CHAIN: a Refractor mod's init.con lists `game.addModPath Mods/<X>/` lines in precedence order (mod
    // first, its dependency mods next, the base game LAST). ModChain resolves that TRANSITIVELY - it also follows
    // each dependency's own init.con, so a mini-mod that names FHSW but forgets FH still gets FH's objects (the
    // Japanese FHSW community's case). Inherited mounts are appended at the lowest precedence, so they only ever
    // fill gaps and can never outrank what the game itself would mount.
    var chain = RefractorForge.Formats.ModChain.Resolve(gameRoot, modDir, AppPrefs.ResolveInheritedMods);
    var modPaths = chain.Mounts.Select(m => m.Path).ToList();
    if (modPaths.Count == 0) modPaths.Add(modDir);
    Console.WriteLine($"Mod chain ({chain.Mounts.Count}): {chain.Describe()}");
    if (chain.Missing.Count > 0)
        Console.WriteLine($"   WARNING - init.con names {chain.Missing.Count} mod(s) that are NOT installed: {string.Join(", ", chain.Missing)}");

    // Collect each mount's Archives\**\*.rfa in chain order (first = highest precedence; the mesh/texture libraries
    // are first-wins). Level archives and pure audio/menu archives are skipped - on a full FHSW chain that is
    // several GB of files that can hold nothing the editor draws.
    (meshList, texList) = RefractorForge.Formats.ModChain.CollectArchives(chain);

    bool isBfv = gameRoot.ToLowerInvariant().Contains("vietnam") || modPaths.Any(p => Path.GetFileName(p).Equals("BfVietnam", StringComparison.OrdinalIgnoreCase));
    string baseSub = isBfv ? "BfVietnam" : "bf1942";
    var modArc = Directory.Exists(Path.Combine(modDir, "Archives")) ? Path.Combine(modDir, "Archives") : modDir;
    var levelsHint = Path.Combine(modArc, baseSub, "levels");
    if (!Directory.Exists(levelsHint)) levelsHint = modArc;
    lvlRfas = Picker.Files("Select the map .rfa to open from this mod  (base + any patch, Ctrl/Shift-click)",
                           "RFA archives|*.rfa|All files|*.*", levelsHint);
    if (lvlRfas.Length == 0) { Console.WriteLine("Open mod: no level chosen."); return false; }
    Console.WriteLine($"Open mod {Path.GetFileName(modDir)}: chain [{chain.Describe()}], {meshList.Length} mesh + {texList.Length} texture archive(s), level {Path.GetFileName(lvlRfas[0])}.");
    return true;
}

// File > Open Mod: gather the mod's paths, remember them, and relaunch into the standard load path.
void OpenMod()
{
    try
    {
        if (!GatherModPaths(out var lvlRfas, out var meshList, out var texList)) return;
        Settings.Save(new LevelPaths(lvlRfas[0], null, null, texList, meshList, lvlRfas));
        ActiveProject.Clear();   // classic Open-Mod path is Settings-based, not a project
        Console.WriteLine("Opening mod - restarting...");
        RelaunchAndExit();
    }
    catch (Exception ex) { Console.WriteLine($"Open mod failed: {ex.Message}"); Toast(Loc.T("Open mod failed: ") + ex.Message); }
}

// Drop-in replacements for ImGui.SliderFloat / ImGui.SliderInt that let you RIGHT-CLICK the slider to type an exact
// value (it swaps to a focused input box until you press Enter or click away). Every slider in the editor goes through
// these. They honour any SetNextItemWidth the caller set (the input/slider is the single next item).
bool SldF(string label, ref float v, float min, float max, string fmt = "%.3f")
{
    uint id = ImGui.GetID(label);
    if (sliderEditId == id)
    {
        if (sliderEditStart) { ImGui.SetKeyboardFocusHere(); sliderEditStart = false; }
        bool entered = ImGui.InputFloat(label, ref v, 0f, 0f, fmt, ImGuiInputTextFlags.EnterReturnsTrue);
        if (entered || ImGui.IsItemDeactivated()) { sliderEditId = 0; v = Math.Clamp(v, min, max); return true; }
        return false;
    }
    bool changed = ImGui.SliderFloat(label, ref v, min, max, fmt);
    if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { sliderEditId = id; sliderEditStart = true; }
    return changed;
}
bool SldI(string label, ref int v, int min, int max, string fmt = "%d")
{
    uint id = ImGui.GetID(label);
    if (sliderEditId == id)
    {
        if (sliderEditStart) { ImGui.SetKeyboardFocusHere(); sliderEditStart = false; }
        bool entered = ImGui.InputInt(label, ref v, 0, 0, ImGuiInputTextFlags.EnterReturnsTrue);
        if (entered || ImGui.IsItemDeactivated()) { sliderEditId = 0; v = Math.Clamp(v, min, max); return true; }
        return false;
    }
    bool changed = ImGui.SliderInt(label, ref v, min, max, fmt);
    if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right)) { sliderEditId = id; sliderEditStart = true; }
    return changed;
}

// A slider + a typed input box bound to the same value, so a property can be dragged OR typed exactly.
// Returns the (clamped) value; the visible label sits on the input box.
float SliderInput(string label, float v, float min, float max, string sliderFmt, string inputFmt)
{
    ImGui.PushItemWidth(150f);
    SldF("##" + label + "_s", ref v, min, max, sliderFmt);
    ImGui.PopItemWidth();
    ImGui.SameLine();
    ImGui.PushItemWidth(96f);
    ImGui.InputFloat(Loc.TL(label), ref v, 0f, 0f, inputFmt);
    ImGui.PopItemWidth();
    return Math.Clamp(v, min, max);
}

// ---- Scatter Objects: randomly place vegetation/buildings/props across the terrain (one undo step) ----
void DoScatter()
{
    scatterError = "";
    if (so is null || hist is null || terrainPick is null || meshLib is null) { scatterError = Loc.T("No level/library loaded."); return; }

    // Gather candidate templates from the selected library categories; keep only meshes the library can resolve
    // (so scattered objects actually render, not mesh-less markers).
    var wanted = new List<string>();
    void AddGroup(params string[] labels)
    {
        foreach (var (label, items) in catalog)
            if (labels.Any(w => label.Equals(w, StringComparison.OrdinalIgnoreCase)))
                wanted.AddRange(items);
    }
    if (scatterVeg) AddGroup("Vegetation", "Overgrowth", "Undergrowth");
    if (scatterStruct) AddGroup("Structures", "Buildings");   // BFV "Structures" / BF1942 "Buildings"
    if (scatterProps) AddGroup("Props", "Props (Low)");

    var candidates = wanted.Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(t => meshLib.TryGet(t, out _) || meshLib.TryGetAssembledMesh(t, out _))
        .ToList();
    if (candidates.Count == 0) { scatterError = Loc.T("No resolvable objects in the selected categories."); return; }

    var placements = ObjectScatter.Scatter(candidates, cfg, terrainPick.HeightAt,
        scatterCount, 0f, scatterMaxSlope, scatterAvoidWater, scatterClearance, scatterSpacing, scatterSeed,
        edgeMargin: cfg.WorldSize * 0.02f, minScale: scatterScaleMin, maxScale: scatterScaleMax);
    if (placements.Count == 0) { scatterError = Loc.T("No valid spots (loosen slope / water / spacing)."); return; }

    var cmds = new List<IEditCommand>(placements.Count);
    var ids = new List<string>(placements.Count);
    foreach (var pl in placements)
    {
        var id = Guid.NewGuid().ToString("N"); ids.Add(id);
        cmds.Add(new AddObject(id, pl.Template, pl.Position, new Vec3(pl.Yaw, 0f, 0f)));  // BFV Euler: X = yaw
        if (MathF.Abs(pl.Scale - 1f) > 1e-3f) cmds.Add(new ScaleObject(id, pl.Scale));    // per-object size variation
    }
    hist.Do(new CompositeCommand(cmds));
    SyncMarkers(); RebuildObjects(); UploadMarkers();
    multi.Clear(); selected = -1;
    foreach (var id in ids) { int idx = so.Objects.FindIndex(o => o.Id == id); if (idx >= 0) { multi.Add(idx); selected = idx; } }
    Console.WriteLine($"Scattered {placements.Count} objects from {candidates.Count} candidate template(s).");
    scatterSeed++;                       // so a second Scatter gives a fresh layout
}

void ScatterModal()
{
    if (scatterRequest) { ImGui.OpenPopup(Loc.TL("Scatter Objects")); scatterRequest = false; }
    var fbs = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fbs.X * 0.5f, fbs.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new Vector2(380, 0), ImGuiCond.Appearing);
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Scatter Objects"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        return;

    ImGui.TextDisabled(Loc.T("Randomly place objects across the terrain."));
    ImGui.Checkbox(Loc.TL("Vegetation"), ref scatterVeg);
    ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Structures"), ref scatterStruct);
    ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Props"), ref scatterProps);
    ImGui.Separator();
    ImGui.InputInt(Loc.TL("Count"), ref scatterCount); scatterCount = Math.Clamp(scatterCount, 1, 20000);
    scatterMaxSlope = SliderInput("Max slope (deg)", scatterMaxSlope, 0f, 60f, "%.0f", "%.0f");
    ImGui.Checkbox(Loc.TL("Avoid water"), ref scatterAvoidWater);
    if (scatterAvoidWater) scatterClearance = SliderInput("Water clearance (m)", scatterClearance, 0f, 25f, "%.1f", "%.1f");
    scatterSpacing = SliderInput("Min spacing (m)", scatterSpacing, 0f, 100f, "%.1f", "%.1f");
    // Per-object random size variation (e.g. 0.7-1.4 for natural-looking vegetation; 1/1 = uniform).
    scatterScaleMin = SliderInput("Min scale", scatterScaleMin, 0.2f, 3f, "%.2f", "%.2f");
    scatterScaleMax = SliderInput("Max scale", scatterScaleMax, 0.2f, 3f, "%.2f", "%.2f");
    if (scatterScaleMax < scatterScaleMin) scatterScaleMax = scatterScaleMin;
    ImGui.InputInt(Loc.TL("Seed"), ref scatterSeed);
    if (!string.IsNullOrEmpty(scatterError)) ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), scatterError);

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("Adds one undo step (Z to undo all)."));
    if (ImGui.Button(Loc.TL("Scatter"), new Vector2(120, 0))) DoScatter();
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Close"), new Vector2(120, 0))) { scatterError = ""; ImGui.CloseCurrentPopup(); }
    ImGui.EndPopup();
}

void NewMapModal()
{
    if (newMapRequest) { ImGui.OpenPopup(Loc.TL("New Map")); newMapRequest = false; }

    var fb = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fb.X * 0.5f, fb.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    // Lock the width to 500 px (via a size constraint, which is honoured because AlwaysAutoResize triggers a resize each
    // frame) and let the HEIGHT auto-fit the content so there's no empty space at the bottom. (AlwaysAutoResize alone blew
    // the width up on the fill-width Folder field; the width constraint reins it in. A standalone constraint without
    // AlwaysAutoResize is never applied, because nothing triggers a resize.)
    ImGui.SetNextWindowSizeConstraints(new Vector2(500f, 0f), new Vector2(500f, fb.Y * 0.92f));

    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("New Map"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        return;

    ImGui.InputText(Loc.TL("Name"), ref nmName, 64);
    ImGui.PushItemWidth(-140);   // reserve room for the "Folder" label (drawn to the right) AND the Browse button
    ImGui.InputText(Loc.TL("Folder"), ref nmFolder, 512);
    ImGui.PopItemWidth();
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Browse...")))
    {
        var f = Picker.Folder("Choose where to create the level folder", Directory.Exists(nmFolder) ? nmFolder : null);
        if (f is not null) nmFolder = f;
    }

    ImGui.Separator();
    ImGui.Combo(Loc.TL("Material size"), ref nmMatSizeIdx, nmMatSizeLabels, nmMatSizeLabels.Length);
    ImGui.Combo(Loc.TL("World size (m)"), ref nmWorldSizeIdx, nmWorldSizeLabels, nmWorldSizeLabels.Length);
    nmWorldSize = nmWorldSizes[Math.Clamp(nmWorldSizeIdx, 0, nmWorldSizes.Length - 1)];
    nmYScale     = SliderInput("Y scale", nmYScale, 0.05f, 10f, "%.3f", "%.3f");
    nmWaterLevel = SliderInput("Water level (m)", nmWaterLevel, -2000f, 500f, "%.1f", "%.1f");

    ImGui.Separator();
    ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T("Terrain"));
    ImGui.Combo(Loc.TL("Type"), ref nmTerrainType, Array.ConvertAll(nmTerrainTypeLabels, Loc.T), nmTerrainTypeLabels.Length);
    if (nmTerrainType == 0)
        nmFlatHeight = SliderInput("Ground height (m)", nmFlatHeight, -100f, 500f, "%.1f", "%.1f");
    else if (nmTerrainType == 4)
    {
        // Import a headerless 16-bit LE square .raw as the starting terrain (resampled to the grid if sizes differ).
        ImGui.PushItemWidth(-160);   // reserve room for the "Heightmap" label (drawn to the right) AND the Browse button
        ImGui.InputText(Loc.TL("Heightmap"), ref nmHeightmapPath, 512);
        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (ImGui.Button(Loc.TL("Browse...##hm")))
        {
            var hp = Picker.File("Import Heightmap.raw (16-bit LE, square)", "Raw heightmap|*.raw|All files|*.*",
                                 Directory.Exists(nmFolder) ? nmFolder : null);
            if (hp is not null)
            {
                nmHeightmapPath = hp;
                // If the .raw's native side is one of our grid sizes, snap Material size to it (no resample needed).
                if (RawSquareSide(hp) is int sd && Array.IndexOf(nmMatSizes, sd) is int mi && mi >= 0) nmMatSizeIdx = mi;
            }
        }
        if (nmHeightmapPath.Length > 0 && File.Exists(nmHeightmapPath))
        {
            int tms = nmMatSizes[Math.Clamp(nmMatSizeIdx, 0, nmMatSizes.Length - 1)];
            if (RawSquareSide(nmHeightmapPath) is int sd)
                ImGui.TextDisabled(sd == tms ? $"{sd}^2 raw (matches grid)" : $"{sd}^2 raw -> resampled to {tms}^2");
            else ImGui.TextColored(new Vector4(1f, 0.55f, 0.3f, 1f), Loc.T("not a square 16-bit .raw"));
        }
        else ImGui.TextDisabled(Loc.T("Headerless 16-bit LE square .raw (e.g. Terrain -> Export, World Machine, L3DT)."));
    }
    else
    {
        ImGui.InputInt(Loc.TL("Seed"), ref nmSeed);
        nmRoughness = SliderInput("Roughness", nmRoughness, 0.1f, 1f, "%.2f", "%.2f");
        nmMinH      = SliderInput("Min height (m)", nmMinH, -100f, 1500f, "%.1f", "%.1f");
        nmMaxH      = SliderInput("Max height (m)", nmMaxH, -100f, 1500f, "%.1f", "%.1f");
        // effective relief preview, accounting for the auto-fit yScale (so the number matches what you'll see).
        ImGui.TextDisabled($"relief {MathF.Abs(nmMaxH - nmMinH):0} m" + (nmTerrainType == 2 ? "  (peaks sharpened)" : nmTerrainType == 3 ? "  (sea-rimmed)" : ""));
    }

    ImGui.Separator();
    // Target game for the new map: gates BFV-only features (overgrowth, tunnels) and sets team names.
    int nmGameIdx = nmGameBf1942 ? 0 : 1;
    ImGui.SetNextItemWidth(200f);
    if (ImGui.Combo(Loc.TL("Game"), ref nmGameIdx, "Battlefield 1942\0Battlefield Vietnam\0")) nmGameBf1942 = nmGameIdx == 0;
    ImGui.TextDisabled(nmGameBf1942 ? "BF1942: no overgrowth / tunnel features." : "BF Vietnam: full feature set.");
    ImGui.Checkbox(Loc.TL("Playable (Conquest: flags, spawns, kits)"), ref nmPlayable);
    if (nmPlayable) ImGui.TextDisabled(Loc.T(nmGameBf1942 ? "Adds Axis + Allies + neutral flags with spawn points." : "Adds US + NVA + neutral flags with spawn points."));

    int ms = nmMatSizes[Math.Clamp(nmMatSizeIdx, 0, nmMatSizes.Length - 1)];
    ImGui.TextDisabled($"{ms}x{ms} grid, {(float)Math.Clamp(nmWorldSize, 64, 131072) / ms:0.##} m/sample");
    if (!string.IsNullOrEmpty(nmError)) ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), nmError);

    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("Create restarts the editor on the new map."));
    if (ImGui.Button(Loc.TL("Create"), new Vector2(130, 0))) DoCreateNewMap();
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Cancel"), new Vector2(130, 0))) { nmError = ""; ImGui.CloseCurrentPopup(); }

    ImGui.EndPopup();
}

// ---- Object-group prefabs (Battlecraft-style stamps) ----
bool IsPrefab(string t) => prefabByKey.ContainsKey(t);

void LoadPrefabs()
{
    prefabs.Clear(); prefabByKey.Clear();
    try
    {
        var pdir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        if (Directory.Exists(pdir))
            foreach (var f in Directory.EnumerateFiles(pdir, "*.rfprefab").OrderBy(x => x))
                try { var pf = Prefab.Load(f); if (pf.Members.Count > 0) { prefabs.Add(pf); prefabByKey[pf.Name] = pf; } } catch { }
    }
    catch { }
}

void RebuildCatalog()
{
    catalog = LoadCatalog();
    var allImported = importedObjs.Keys.Concat(remoteMeshNames).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    if (allImported.Length > 0) catalog.Insert(0, ("Imported", allImported.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray()));
    if (treeMeshNames.Count > 0) catalog.Insert(0, ("Trees", treeMeshNames.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray()));
    if (sounds.Count > 0) catalog.Insert(0, ("Sounds", sounds.TemplateNames.ToArray()));
    if (prefabs.Count > 0) catalog.Insert(0, ("Prefabs", prefabs.Select(p => p.Name).ToArray()));
    catalog.Insert(0, ("Gameplay", new[] { GpDragControlPoint, GpDragVehicle, GpDragSoldier }));
}

// Stamp every object in a prefab at the terrain hit point as one undo step, then select the new group.
void StampPrefab(string key, Vec3 hit)
{
    if (so is null || hist is null || !prefabByKey.TryGetValue(key, out var pf) || pf.Members.Count == 0) return;
    var cmds = new List<IEditCommand>();
    var ids = new List<string>();
    foreach (var m in pf.Members)
    {
        var id = Guid.NewGuid().ToString("N"); ids.Add(id);
        float wx = hit.X + m.Offset.X, wz = hit.Z + m.Offset.Z;
        float wy = (terrainPick is not null ? terrainPick.HeightAt(wx, wz) : hit.Y) + m.Offset.Y;
        cmds.Add(new AddObject(id, m.Template, new Vec3(wx, wy, wz), m.Rotation));
        if (MathF.Abs(m.Scale - 1f) > 1e-3f) cmds.Add(new ScaleObject(id, m.Scale));   // preserve the member's authored scale
    }
    hist.Do(new CompositeCommand(cmds));
    SyncMarkers(); RebuildObjects(); UploadMarkers();
    multi.Clear(); selected = -1;
    foreach (var id in ids) { int idx = so.Objects.FindIndex(o => o.Id == id); if (idx >= 0) { multi.Add(idx); selected = idx; } }
    Console.WriteLine($"Stamped prefab '{pf.Name}' ({pf.Members.Count} objects) at {hit.X:0.#}, {hit.Z:0.#}");
}

void DoSavePrefab()
{
    spError = "";
    try
    {
        if (so is null || multi.Count == 0) { spError = Loc.T("Select one or more objects first."); return; }
        var name = spName.Trim();
        if (name.Length == 0) { spError = Loc.T("Enter a prefab name."); return; }
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { spError = "Name has invalid characters."; return; }
        var sel = multi.Where(i => i >= 0 && i < so.Objects.Count).Select(i => so.Objects[i]).ToList();
        if (sel.Count == 0) { spError = Loc.T("Selection is empty."); return; }
        var pdir = Path.Combine(AppContext.BaseDirectory, "prefabs");
        Directory.CreateDirectory(pdir);
        Prefab.FromObjects(name, sel).Save(Path.Combine(pdir, name + ".rfprefab"));
        LoadPrefabs(); RebuildCatalog();
        Console.WriteLine($"Saved prefab '{name}' ({sel.Count} objects).");
        ImGui.CloseCurrentPopup();
    }
    catch (Exception ex) { spError = ex.Message; }
}

void SavePrefabModal()
{
    if (savePrefabRequest) { ImGui.OpenPopup(Loc.TL("Save Prefab")); savePrefabRequest = false; }
    var fb = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fb.X * 0.5f, fb.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Save Prefab"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) return;
    ImGui.Text($"{multi.Count} object(s) selected");
    ImGui.InputText(Loc.TL("Name"), ref spName, 64);
    if (!string.IsNullOrEmpty(spError)) ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), spError);
    ImGui.Separator();
    if (ImGui.Button(Loc.TL("Save"), new Vector2(120, 0))) DoSavePrefab();
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Cancel"), new Vector2(120, 0))) { spError = ""; ImGui.CloseCurrentPopup(); }
    ImGui.EndPopup();
}

// ---- Collaboration ----
// One locally-committed edit -> broadcast its object op(s). Composites (prefab stamp, multi-move) split into
// per-object ops; non-object commands (terrain/material/gameplay) are skipped (object sync only for now).
void OnLocalEdit(IEditCommand cmd)
{
    if (collab is null) return;
    var wire = cmd.ToWire();
    int v = wire.IndexOf(' ');
    var verb = v < 0 ? wire : wire[..v];
    // Terrain/material wire forms only carry the rect; attach the actual data (read back from the map the
    // command just modified) so the remote can reproduce the stroke.
    if (verb == "TERRAIN") { collab.SendOp(EncTerrain(wire)); return; }
    if (verb == "MATERIAL") { collab.SendOp(EncMaterial(wire)); return; }
    // Gameplay (control points / vehicle spawns / soldier spawns) is index-addressed, so ship the WHOLE
    // layer as a full-state snapshot - the receiver replaces theirs. Small + can't desync.
    if (verb.StartsWith("GP")) { collab.SendOp("GAMEPLAY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(GameplaySync.Serialize(gameplayEdit)))); return; }
    foreach (var part in wire.Split(" ; "))   // object ops (incl. composites: prefab stamp / multi-move)
    {
        int sp = part.IndexOf(' ');
        var t = sp < 0 ? part : part[..sp];
        if (t is "ADD" or "MOVE" or "ROT" or "SCALE" or "DEL") collab.SendOp(part);
    }
}

// Collaborative undo/redo: after a local undo/redo reverses an edit, broadcast the RESULTING (now-current)
// state so peers converge - otherwise the undo would only happen on this machine. The command's forward wire
// is the wrong direction, so instead we re-broadcast live state: terrain/material/gameplay reuse the same
// live-reading encoders as a normal edit (they read the post-undo maps), and object ops re-send each affected
// object's current transform (or a DEL if it's now gone). All ops are absolute/idempotent, so the echo back
// through the relay is a harmless no-op and never loops.
void OnUndoRedo(IEditCommand cmd)
{
    if (collab is null) return;
    var wire = cmd.ToWire();
    int v = wire.IndexOf(' ');
    var verb = v < 0 ? wire : wire[..v];
    if (verb == "TERRAIN") { collab.SendOp(EncTerrain(wire)); return; }
    if (verb == "MATERIAL") { collab.SendOp(EncMaterial(wire)); return; }
    if (verb.StartsWith("GP")) { collab.SendOp("GAMEPLAY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(GameplaySync.Serialize(gameplayEdit)))); return; }
    foreach (var part in wire.Split(" ; "))
    {
        var toks = part.Split(' ');
        if (toks.Length >= 2 && toks[0] is "ADD" or "MOVE" or "ROT" or "SCALE" or "DEL")
            BroadcastObjectState(toks[1]);
    }
}

// Broadcast an object's current state as absolute ops: ADD recreates it if a peer had deleted it (and sets
// pos/rot on create), then MOVE/ROT/SCALE force the transform if it already existed; if the object is now gone
// locally (undo of an add), broadcast DEL instead.
void BroadcastObjectState(string id)
{
    if (collab is null || so is null) return;
    var o = so.FindById(id);
    if (o is null) { collab.SendOp($"DEL {id}"); return; }
    collab.SendOp(new AddObject(o.Id, o.Template, o.Position, o.Rotation).ToWire());
    collab.SendOp(new MoveObject(o.Id, o.Position).ToWire());
    collab.SendOp(new RotateObject(o.Id, o.Rotation).ToWire());
    collab.SendOp(new ScaleObject(o.Id, o.Scale ?? 1f).ToWire());
}

// Encode the affected height rect (16-bit LE) as base64 onto the TERRAIN wire line.
string EncTerrain(string wire)
{
    var p = wire.Split(' ');
    int x0 = int.Parse(p[1]), y0 = int.Parse(p[2]), w = int.Parse(p[3]), h = int.Parse(p[4]);
    var buf = new byte[w * h * 2];
    if (heightmap is not null)
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int gx = x0 + xx, gy = y0 + yy;
                ushort val = (gx < heightmap.Width && gy < heightmap.Height) ? heightmap[gx, gy] : (ushort)0;
                int o = (yy * w + xx) * 2; buf[o] = (byte)val; buf[o + 1] = (byte)(val >> 8);
            }
    return $"TERRAIN {x0} {y0} {w} {h} {Convert.ToBase64String(buf)}";
}

// Encode the affected material/foliage rect (1 byte/cell) + the layer it belongs to.
string EncMaterial(string wire)
{
    var p = wire.Split(' ');
    int x0 = int.Parse(p[1]), y0 = int.Parse(p[2]), w = int.Parse(p[3]), h = int.Parse(p[4]);
    var map = ActivePaintMap();
    var buf = new byte[w * h];
    if (map is not null)
        for (int yy = 0; yy < h; yy++)
            for (int xx = 0; xx < w; xx++)
            {
                int gx = x0 + xx, gy = y0 + yy;
                buf[yy * w + xx] = (gx < map.Width && gy < map.Height) ? map[gx, gy] : (byte)0;
            }
    return $"MATERIAL {paintLayer} {x0} {y0} {w} {h} {Convert.ToBase64String(buf)}";
}

void ApplyRemoteTerrain(string payload)
{
    if (heightmap is null) return;
    var p = payload.Split(' ');
    int x0 = int.Parse(p[1]), y0 = int.Parse(p[2]), w = int.Parse(p[3]), h = int.Parse(p[4]);
    var buf = Convert.FromBase64String(p[5]);
    for (int yy = 0; yy < h; yy++)
        for (int xx = 0; xx < w; xx++)
        {
            int gx = x0 + xx, gy = y0 + yy; if (gx < 0 || gy < 0 || gx >= heightmap.Width || gy >= heightmap.Height) continue;
            int o = (yy * w + xx) * 2; if (o + 1 >= buf.Length) continue;
            heightmap[gx, gy] = (ushort)(buf[o] | (buf[o + 1] << 8));
        }
    terrainDirty = true;   // OnRender re-uploads the terrain mesh
}

void ApplyRemoteMaterial(string payload)
{
    var p = payload.Split(' ');
    int layer = int.Parse(p[1]), x0 = int.Parse(p[2]), y0 = int.Parse(p[3]), w = int.Parse(p[4]), h = int.Parse(p[5]);
    var buf = Convert.FromBase64String(p[6]);
    var map = layer == 1 ? growth?.Under : layer == 2 ? growth?.Over : materialMap;
    if (map is null) return;
    for (int yy = 0; yy < h; yy++)
        for (int xx = 0; xx < w; xx++)
        {
            int gx = x0 + xx, gy = y0 + yy; if (gx < 0 || gy < 0 || gx >= map.Width || gy >= map.Height) continue;
            int o = yy * w + xx; if (o >= buf.Length) continue;
            map[gx, gy] = buf[o];
        }
    if (map == ActivePaintMap()) UploadActivePaintTexture();
}

void ApplyRemoteGameplay(string payload)
{
    int sp = payload.IndexOf(' ');
    if (sp < 0) return;
    var text = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(payload[(sp + 1)..]));
    GameplaySync.Apply(gameplayEdit, text);            // gameplay renders directly from gameplayEdit each frame
    if (gpIndex >= gameplayEdit.CountOf(gpKind)) gpIndex = -1;   // selection may now be out of range
}

// Water level: not an undoable edit, so it syncs on its own little op. Peers apply it live (the shaders read
// cfg.WaterLevel each frame) and mark it edited so their save keeps it; the relay seeds it to late joiners.
void BroadcastWater() => collab?.SendOp($"WATER {cfg.WaterLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
void ApplyRemoteWater(string payload)
{
    var p = payload.Split(' ');
    if (p.Length >= 2 && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wl))
    { cfg.WaterLevel = wl; waterLevelEdited = true; }
}

// ---- Overgrowth-trees overlay: the tree-generation SETTINGS (on/off + patch size + density) sync over collab so
// every participant sees the same scatter (it's a view of the in-game foliage, derived from the synced OverGrowthMap).
static string Inv(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
string OvergrowthWire() => $"OVERGROWTH {(showFoliage ? 1 : 0)} {Inv(foliageSpacing)} {Inv(foliageDensity)}";
void BroadcastOvergrowth() => collab?.SendOp(OvergrowthWire());
void ApplyRemoteOvergrowth(string payload)
{
    var p = payload.Split(' ');
    if (p.Length >= 4)
    {
        showFoliage = p[1] != "0";
        if (float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sp)) foliageSpacing = Math.Clamp(sp, 6f, 32f);
        if (float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dn)) foliageDensity = Math.Clamp(dn, 0.25f, 1.5f);
        foliageDirty = true;   // OnRender re-scatters the overlay from the (now matching) settings + maps
    }
}

// ---- Imported .obj meshes shared over collab: ship the resolved render geometry (positions + uvs + per-part
// colour, textures dropped) so a peer renders objects placed from an import even though it never saw the .obj file.
string? ObjMeshWire(string name)
{
    if (meshLib is null || !meshLib.TryGet(name, out var mesh) || mesh is null) return null;
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write(mesh.Positions.Length);
    foreach (var v in mesh.Positions) { bw.Write(v.X); bw.Write(v.Y); bw.Write(v.Z); }
    bw.Write(mesh.Uvs.Length);
    foreach (var uv in mesh.Uvs) { bw.Write(uv.X); bw.Write(uv.Y); }
    bw.Write(mesh.Parts.Length);
    foreach (var part in mesh.Parts)
    {
        bw.Write(part.Color.X); bw.Write(part.Color.Y); bw.Write(part.Color.Z);
        bw.Write(part.AlphaTest);
        bw.Write(part.Indices.Length);
        foreach (var i in part.Indices) bw.Write(i);
    }
    bw.Flush();
    return $"OBJMESH {name} {Convert.ToBase64String(ms.ToArray())}";
}
void BroadcastObjMesh(string name) { var w = ObjMeshWire(name); if (w is not null) collab?.SendOp(w); }
void ApplyRemoteObjMesh(string payload)
{
    if (meshLib is null) return;
    var p = payload.Split(' ', 3);
    if (p.Length < 3) return;
    string name = p[1];
    var blob = Convert.FromBase64String(p[2]);
    using var ms = new MemoryStream(blob);
    using var br = new BinaryReader(ms);
    int nv = br.ReadInt32();
    var pos = new Vector3[nv];
    for (int i = 0; i < nv; i++) pos[i] = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
    int nu = br.ReadInt32();
    var uvs = new System.Numerics.Vector2[nu];
    for (int i = 0; i < nu; i++) uvs[i] = new System.Numerics.Vector2(br.ReadSingle(), br.ReadSingle());
    int np = br.ReadInt32();
    var parts = new MeshLibrary.MaterialPart[np];
    for (int k = 0; k < np; k++)
    {
        var col = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
        bool at = br.ReadBoolean();
        int ni = br.ReadInt32();
        var idx = new int[ni];
        for (int i = 0; i < ni; i++) idx[i] = br.ReadInt32();
        parts[k] = new MeshLibrary.MaterialPart(idx, col, null, at);
    }
    meshLib.AddMesh(name, new MeshLibrary.Mesh(pos, uvs, parts));
    remoteMeshNames.Add(name);
    RebuildCatalog();
}

// Seed the full NON-object world to a fresh central relay (the first client uploads it on SeedRequest): terrain,
// every material/foliage layer, gameplay, water, overgrowth settings, and imported meshes.
void SeedWorld()
{
    if (collab is null) return;
    BroadcastFullTerrain();
    if (materialMap is not null) collab.SendOp($"MATERIAL 0 0 0 {materialMap.Width} {materialMap.Height} {Convert.ToBase64String(materialMap.Samples)}");
    if (growth?.Under is not null) collab.SendOp($"MATERIAL 1 0 0 {growth.Under.Width} {growth.Under.Height} {Convert.ToBase64String(growth.Under.Samples)}");
    if (growth?.Over is not null) collab.SendOp($"MATERIAL 2 0 0 {growth.Over.Width} {growth.Over.Height} {Convert.ToBase64String(growth.Over.Samples)}");
    collab.SendOp("GAMEPLAY " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(GameplaySync.Serialize(gameplayEdit))));
    BroadcastWater();
    BroadcastOvergrowth();
    foreach (var name in importedObjs.Keys.Concat(remoteMeshNames).Distinct(StringComparer.OrdinalIgnoreCase))
        BroadcastObjMesh(name);
}

// Broadcast the WHOLE heightmap as one TERRAIN rect (used after a heightmap import, which is not a brush stroke).
void BroadcastFullTerrain()
{
    if (collab is null || heightmap is null) return;
    var buf = new byte[heightmap.Width * heightmap.Height * 2];
    for (int y = 0; y < heightmap.Height; y++)
        for (int x = 0; x < heightmap.Width; x++)
        { ushort val = heightmap[x, y]; int o = (y * heightmap.Width + x) * 2; buf[o] = (byte)val; buf[o + 1] = (byte)(val >> 8); }
    collab.SendOp($"TERRAIN 0 0 {heightmap.Width} {heightmap.Height} {Convert.ToBase64String(buf)}");
}

// Apply queued inbound protocol lines on the GL thread; rebuild render state if the document changed.
void CollabDrain()
{
    if (collab is null) return;
    bool changed = false;
    while (collab is not null && collab.Inbound.TryDequeue(out var line))
    {
        Message m;
        try { m = Message.Decode(line); } catch { continue; }
        switch (m.Type)
        {
            case MsgType.SyncBegin:
                if (!collab.IsHost && so is not null) { so.Objects.Clear(); changed = true; }   // joiner adopts host doc
                break;
            case MsgType.SyncObj:
            case MsgType.Op:
            {
                var payload = m.Payload;
                int pv = payload.IndexOf(' ');
                var pverb = pv < 0 ? payload : payload[..pv];
                if (pverb == "TERRAIN") { try { ApplyRemoteTerrain(payload); } catch { } }
                else if (pverb == "MATERIAL") { try { ApplyRemoteMaterial(payload); } catch { } }
                else if (pverb == "GAMEPLAY") { try { ApplyRemoteGameplay(payload); } catch { } }
                else if (pverb == "WATER") { try { ApplyRemoteWater(payload); } catch { } }
                else if (pverb == "OVERGROWTH") { try { ApplyRemoteOvergrowth(payload); } catch { } }
                else if (pverb == "OBJMESH") { try { ApplyRemoteObjMesh(payload); changed = true; } catch { } }
                else if (so is not null) { try { EditWire.Parse(payload).Apply(so); changed = true; } catch { } }
                break;
            }
            case MsgType.SyncEnd: changed = true; break;
            case MsgType.SeedRequest:
                // We're the first client on a fresh central relay: upload our WHOLE level so the server (and every
                // later joiner) starts from our document - objects + terrain + material/foliage + gameplay + water +
                // overgrowth settings + any imported .obj meshes.
                if (so is not null && collab is not null)
                {
                    foreach (var o in so.Objects)
                    {
                        collab.SendOp(new AddObject(o.Id, o.Template, o.Position, o.Rotation).ToWire());
                        if (o.Scale is float sc) collab.SendOp(new ScaleObject(o.Id, sc).ToWire());
                    }
                    SeedWorld();
                }
                break;
            case MsgType.Presence:
                if (m.Args[0] != collab.ClientId)
                {
                    if (!collab.Peers.TryGetValue(m.Args[0], out var p)) { p = new Peer { ClientId = m.Args[0] }; collab.Peers[m.Args[0]] = p; }
                    p.Name = m.Args[1]; p.SelectionId = m.Args[2]; p.Cursor = Vec3.Parse(m.Args[3]);
                    if (m.Args.Length > 4 && float.TryParse(m.Args[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hd)) p.Heading = hd;
                }
                break;
            case MsgType.Leave: collab.Peers.Remove(m.Args[0]); break;
            case MsgType.Error: collab.Stop(); collab = null; changed = true; break;          // socket dropped
        }
    }
    if (changed && so is not null)
    {
        if (selected >= so.Objects.Count) selected = -1;            // remote add/delete can shift indices
        multi.RemoveWhere(i => i >= so.Objects.Count);
        SyncMarkers(); RebuildObjects(); UploadMarkers();
    }
}

void DoCollabHost()
{
    collabError = "";
    try
    {
        if (so is null) { collabError = Loc.T("Load a level first."); return; }
        collab = CollabSession.StartHost(so, collabPort, string.IsNullOrWhiteSpace(collabName) ? "Host" : collabName.Trim(),
                                         string.IsNullOrEmpty(collabPass) ? null : collabPass, BuildHostWorld());
        Console.WriteLine(collab.Status);
        ImGui.CloseCurrentPopup();
    }
    catch (Exception ex) { collabError = ex.Message; }
}

// Snapshot the host's full NON-object world (CLONES, so the relay's background threads never touch the live maps the
// GL thread renders) so late joiners to a HOST get terrain/material/foliage/gameplay/water/overgrowth/imports too -
// the relay keeps it current by replaying every streamed op onto these clones.
CollabWorldState BuildHostWorld()
{
    var w = new CollabWorldState
    {
        Height = heightmap is not null ? Heightmap.FromBytes(heightmap.ToBytes(), heightmap.Width, heightmap.Height) : null,
        Material = materialMap?.Clone(),
        Under = growth?.Under?.Clone(),
        Over = growth?.Over?.Clone(),
        Gameplay = GameplaySync.Serialize(gameplayEdit),
        Water = cfg.WaterLevel,
        Overgrowth = OvergrowthWire(),
    };
    foreach (var name in importedObjs.Keys.Concat(remoteMeshNames).Distinct(StringComparer.OrdinalIgnoreCase))
    { var op = ObjMeshWire(name); if (op is not null) w.ObjMeshes[name] = op; }
    return w;
}

void DoCollabJoin()
{
    collabError = "";
    try
    {
        if (so is null) { collabError = Loc.T("Load a level first."); return; }
        collab = CollabSession.StartJoin(collabHostAddr.Trim(), collabPort, string.IsNullOrWhiteSpace(collabName) ? "Guest" : collabName.Trim(),
                                         string.IsNullOrEmpty(collabPass) ? null : collabPass);
        Console.WriteLine(collab.Status);
        ImGui.CloseCurrentPopup();
    }
    catch (Exception ex) { collabError = ex.Message; }
}

void CollabModal()
{
    if (collabRequest) { ImGui.OpenPopup(Loc.TL("Collaborate")); collabRequest = false; }
    var fbc = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fbc.X * 0.5f, fbc.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Collaborate"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize)) return;

    ImGui.InputText(Loc.TL("Your name"), ref collabName, 32);
    ImGui.InputText(Loc.TL("Password (optional)"), ref collabPass, 64, ImGuiInputTextFlags.Password);
    ImGui.Spacing();
    ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T("Host a session"));
    ImGui.InputInt(Loc.TL("Port##host"), ref collabPort);
    if (ImGui.Button(Loc.TL("Host"), new Vector2(160, 0))) DoCollabHost();
    ImGui.Separator();
    ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T("Join a session"));
    ImGui.InputText(Loc.TL("Host address"), ref collabHostAddr, 64);
    ImGui.InputInt(Loc.TL("Port##join"), ref collabPort);
    if (ImGui.Button(Loc.TL("Join"), new Vector2(160, 0))) DoCollabJoin();

    if (!string.IsNullOrEmpty(collabError)) ImGui.TextColored(new Vector4(1f, 0.45f, 0.45f, 1f), collabError);
    ImGui.Separator();
    ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T("Central server (no host clobbering)"));
    ImGui.TextDisabled(Loc.T("Run an always-on relay everyone Joins (nobody 'hosts'):"));
    ImGui.TextDisabled(Loc.T("   RefractorForge.Viewer.exe --relay 7777 [levelFolder]"));
    ImGui.TextDisabled(Loc.T("   add  --save serverState  to persist EVERYTHING across restarts"));
    ImGui.TextDisabled(Loc.T("   (objects + terrain + material + gameplay/vehicles)"));
    ImGui.TextDisabled(Loc.T("   add  --pass secret  to require a password; admin: list | kick <name> | quit"));
    ImGui.TextDisabled(Loc.T("The first joiner (or the seed level) sets the shared state;"));
    ImGui.TextDisabled(Loc.T("everyone else adopts it, so no one overwrites on connect."));
    ImGui.Separator();
    ImGui.TextDisabled(Loc.T("Both editors should have the SAME level open."));
    ImGui.TextDisabled(Loc.T("Same PC: Host in one window, Join 127.0.0.1 in the other."));
    ImGui.TextDisabled(Loc.T("LAN: joiner uses the host's LAN IP (shown in the Collab menu once hosting)."));
    ImGui.TextDisabled(Loc.T("Internet: host forwards this port on their router -> joiner uses the public IP,"));
    ImGui.TextDisabled(Loc.T("   or both run a VPN (Tailscale / ZeroTier / Hamachi) and use its IP - no router setup."));
    if (ImGui.Button(Loc.TL("Close"), new Vector2(160, 0))) ImGui.CloseCurrentPopup();
    ImGui.EndPopup();
}

// Battlecraft-style floating Mini-Map: the top-down map image with a live camera marker + facing; click to fly
// the camera there (keeping its height above the ground). Refresh re-renders it after terrain/material edits.
// Render the model-viewer's mesh into an offscreen FBO (shown by MeshViewerWindow via ImGui.Image). Called each
// frame the viewer is open, AFTER the 3D scene + BuildUi but BEFORE imgui.Render(), then restores the default
// framebuffer + the window viewport (OnRender only re-applies the viewport on a size change, so we restore it).
unsafe void RenderMeshPreview()
{
    if (!meshViewerOpen || meshViewerTemplate is null || meshLib is null || glObjects is null) return;
    // Assembled FIRST so a vehicle shows its whole hierarchy (hull+turret+barrel+wheels), not just the root .sm.
    if (!meshLib.TryGetAssembledMesh(meshViewerTemplate, out var m) && !meshLib.TryGet(meshViewerTemplate, out m)) return;
    if (m.Positions.Length == 0) return;
    if (mvFbo == 0)
    {
        mvColorTex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, mvColorTex);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)mvSize, (uint)mvSize, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)null);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        mvDepthRbo = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, mvDepthRbo);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)mvSize, (uint)mvSize);
        mvFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, mvFbo);
        gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, mvColorTex, 0);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, mvDepthRbo);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }
    // Frame the mesh: centre on its bbox, scale into a unit sphere, then orbit (yaw/pitch).
    Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
    foreach (var p in m.Positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
    var centre = (mn + mx) * 0.5f;
    float radius = MathF.Max((mx - mn).Length() * 0.5f, 0.01f);
    var model = Matrix4x4.CreateTranslation(-centre) * Matrix4x4.CreateScale(1f / radius)
              * Matrix4x4.CreateRotationY(meshViewerYaw) * Matrix4x4.CreateRotationX(meshViewerPitch);
    float mvDist = 2.6f / Math.Clamp(meshViewerZoom, 0.25f, 6f);   // scroll/+/- zoom moves the camera in/out
    var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, mvDist), Vector3.Zero, Vector3.UnitY);
    var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1f, 0.02f, 100f);

    gl.BindFramebuffer(FramebufferTarget.Framebuffer, mvFbo);
    gl.Viewport(0, 0, (uint)mvSize, (uint)mvSize);
    gl.Enable(EnableCap.DepthTest);
    gl.Disable(EnableCap.Blend);
    gl.Disable(EnableCap.CullFace);             // show both faces - mesh winding varies
    gl.ClearColor(0.13f, 0.14f, 0.16f, 1f);
    gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
    gl.UseProgram(objProg);
    gl.Uniform1(gl.GetUniformLocation(objProg, "uFogEnable"), 0);   // isolated preview: kill the leftover scene fog
    var ld = Vector3.Normalize(new Vector3(0.4f, 0.85f, 0.45f));    // (else fog saturates to grey over the whole mesh)
    gl.Uniform3(uLightO, ld.X, ld.Y, ld.Z);
    glObjects.DrawMesh(gl, objProg, uMvpO, uModelO, uColorO, uUseTexO, uAlphaTestO, uTintO, view * proj, "mv::" + meshViewerTemplate, m, model, Vector3.One);

    gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    var fb = window.FramebufferSize;
    gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
}

// 3D model viewer window: the offscreen-rendered mesh + auto-rotate / drag-to-orbit (opened by double-clicking a
// model in the Object Library).
// Locate an ffmpeg.exe to decode .bik (Bink) video: bundled next to the editor, on PATH, a common spot, or any
// Overwolf/OBS bundle. Cached. Returns null if none found (then the RAD Bink player is the fallback).
string? FindFfmpeg()
{
    if (ffmpegPath is not null) return ffmpegPath.Length == 0 ? null : ffmpegPath;
    var cands = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"), @"C:\ffmpeg\bin\ffmpeg.exe" };
    try { foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) if (!string.IsNullOrWhiteSpace(d)) cands.Add(Path.Combine(d.Trim(), "ffmpeg.exe")); } catch { }
    foreach (var c in cands) if (File.Exists(c)) { ffmpegPath = c; return c; }
    try   // last resort: any Overwolf extension (OBS) bundle
    {
        var ow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Overwolf", "Extensions");
        if (Directory.Exists(ow)) { var hit = Directory.EnumerateFiles(ow, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault(); if (hit is not null) { ffmpegPath = hit; return hit; } }
    }
    catch { }
    ffmpegPath = ""; return null;
}

string? FindBinkPlay()
{
    foreach (var c in new[] { @"C:\Program Files (x86)\RADVideo\binkplay.exe", @"C:\Program Files\RADVideo\binkplay.exe" }) if (File.Exists(c)) return c;
    return null;
}

// Pick a .bik from disk (starts in the mod's movies/ folder) and play it.
void DoPlayBik()
{
    string? startNear = null;
    if (levelDir is not null) { var g = levelDir; for (int i = 0; i < 6 && g is not null; i++) { var mv = Path.Combine(g, "movies"); if (Directory.Exists(mv)) { startNear = mv; break; } g = Path.GetDirectoryName(g); } }
    var bik = Picker.File("Choose a .bik video", "Bink video (*.bik)|*.bik|All files|*.*", startNear);
    if (bik is not null) PlayBikFile(bik);
}

// Play any .bik videos EMBEDDED in the loaded map .rfa (extract them to temp first). One -> play it; several -> let the
// user pick from the extracted folder.
void DoPlayMapBik()
{
    if (rfaList.Length == 0) { Toast(Loc.T("This level isn't a .rfa (no embedded videos to scan).")); return; }
    var tmp = Path.Combine(Path.GetTempPath(), "rf_mapbik");
    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); Directory.CreateDirectory(tmp); } catch { }
    var biks = new List<string>();
    foreach (var rfa in rfaList)
    {
        if (!File.Exists(rfa)) continue;
        RefractorForge.Formats.Rfa.RefractorFlatArchive a; try { a = new RefractorFlatArchive(rfa); } catch { continue; }
        foreach (var e in a.Entries)
        {
            if (!e.Name.EndsWith(".bik", StringComparison.OrdinalIgnoreCase)) continue;
            var leaf = e.Name.Replace('\\', '/'); leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
            try { var outp = Path.Combine(tmp, leaf); File.WriteAllBytes(outp, a.Read(e)); biks.Add(outp); } catch { }
        }
    }
    if (biks.Count == 0) { Toast(Loc.T("No .bik videos embedded in this map's .rfa.")); return; }
    if (biks.Count == 1) { PlayBikFile(biks[0]); return; }
    var pick = Picker.File($"{biks.Count} video(s) in this map - choose one", "Bink video (*.bik)|*.bik", tmp);
    if (pick is not null) PlayBikFile(pick);
}

// Decode a .bik to a temp PNG frame sequence (FFmpeg) and open it in the playback window. Falls back to the RAD Bink
// player (binkplay.exe) if FFmpeg isn't available.
void PlayBikFile(string bik)
{
    var ff = FindFfmpeg();
    if (ff is null)
    {
        var rad = FindBinkPlay();
        if (rad is not null) { try { System.Diagnostics.Process.Start(rad, $"\"{bik}\""); Toast(Loc.T("Opened in the RAD Bink player (FFmpeg not found for in-editor playback).")); } catch (Exception ex) { Toast(Loc.T("RAD player launch failed: ") + ex.Message); } }
        else Toast(Loc.T("Need FFmpeg to play .bik in-editor. Drop ffmpeg.exe (+ its DLLs) next to RefractorForge, or install RAD Video Tools."));
        return;
    }
    Toast(Loc.T("Decoding video..."));
    var dir = Path.Combine(Path.GetTempPath(), "rf_bik");
    try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    try { Directory.CreateDirectory(dir); } catch { }
    float fps = 15f;
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -y -i \"{bik}\" -vsync 0 \"{Path.Combine(dir, "f_%05d.png")}\"")
        { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        var proc = System.Diagnostics.Process.Start(psi)!;
        string err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        var mm = System.Text.RegularExpressions.Regex.Match(err, @"([\d.]+) fps");
        if (mm.Success && float.TryParse(mm.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f > 0.5f) fps = f;
    }
    catch (Exception ex) { Toast(Loc.T("Decode failed: ") + ex.Message); return; }
    var frames = Directory.Exists(dir) ? Directory.GetFiles(dir, "f_*.png").OrderBy(f => f, StringComparer.Ordinal).ToArray() : Array.Empty<string>();
    if (frames.Length == 0) { Toast(Loc.T("No frames decoded - is this a valid .bik?")); return; }
    bikFrames = frames; bikFps = fps; bikFrameIdx = 0; bikClock = 0; bikLoadedFrame = -1;
    bikPlaying = true; bikLoop = true; bikOpen = true; bikName = Path.GetFileName(bik);
    Console.WriteLine($"Decoded {frames.Length} frame(s) @ {bikFps:0.#} fps from {bikName}.");
}

unsafe Texture2D? LoadPngRgba(string path)
{
    try
    {
        using var bmp = new System.Drawing.Bitmap(path);
        int w = bmp.Width, h = bmp.Height;
        var d = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var rgba = new byte[w * h * 4];
        byte* p = (byte*)d.Scan0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            { int s = y * d.Stride + x * 4, o = (y * w + x) * 4; rgba[o] = p[s + 2]; rgba[o + 1] = p[s + 1]; rgba[o + 2] = p[s]; rgba[o + 3] = p[s + 3]; }
        bmp.UnlockBits(d);
        return new Texture2D(w, h, rgba);
    }
    catch { return null; }
}

// Advance + display the decoded .bik frames in a floating window (play/pause/loop + seek).
void BikWindow()
{
    if (!bikOpen) return;
    ImGui.SetNextWindowSize(new Vector2(480, 380), ImGuiCond.FirstUseEver);
    if (ImGui.Begin($"Video: {bikName}", ref bikOpen, ImGuiWindowFlags.NoScrollbar))
    {
        if (bikFrames.Length == 0) ImGui.TextDisabled(Loc.T("No frames."));
        else
        {
            if (bikLoadedFrame != bikFrameIdx && (uint)bikFrameIdx < (uint)bikFrames.Length)
            {
                if (LoadPngRgba(bikFrames[bikFrameIdx]) is { } t) { if (bikTex != 0) gl.DeleteTexture(bikTex); bikTex = UploadTexture(t); bikW = t.Width; bikH = t.Height; }
                bikLoadedFrame = bikFrameIdx;
            }
            if (ImGui.Button(Loc.T(bikPlaying ? "Pause" : "Play ") + "###bikPlay")) bikPlaying = !bikPlaying;
            ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Loop"), ref bikLoop);
            ImGui.SameLine(); ImGui.TextDisabled($"{bikFrameIdx + 1}/{bikFrames.Length}  {bikFps:0.#}fps");
            int fi = bikFrameIdx; ImGui.SetNextItemWidth(-1f);
            if (ImGui.SliderInt("##bikseek", ref fi, 0, bikFrames.Length - 1)) { bikFrameIdx = Math.Clamp(fi, 0, bikFrames.Length - 1); bikPlaying = false; }
            if (bikTex != 0 && bikW > 0 && bikH > 0)
            {
                var avail = ImGui.GetContentRegionAvail();
                float scale = MathF.Min(avail.X / bikW, MathF.Max(avail.Y, 1f) / bikH);
                if (scale <= 0f) scale = 1f;
                ImGui.Image((IntPtr)bikTex, new Vector2(bikW * scale, bikH * scale));
            }
        }
    }
    ImGui.End();
}

void MeshViewerWindow()
{
    if (!meshViewerOpen) return;
    ImGui.SetNextWindowSize(new Vector2(440, 480), ImGuiCond.FirstUseEver);
    if (ImGui.Begin(Loc.TL("Model Viewer"), ref meshViewerOpen, ImGuiWindowFlags.NoScrollbar))
    {
        if (meshViewerTemplate is null || meshLib is null) ImGui.TextDisabled(Loc.T("No model selected."));
        else
        {
            bool has = meshLib.TryGetAssembledMesh(meshViewerTemplate, out var m) || meshLib.TryGet(meshViewerTemplate, out m);
            ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), ShortName(meshViewerTemplate));
            if (!has) ImGui.TextWrapped(Loc.T("No mesh for this template (it may be a sound/effect emitter or a proxy with no .sm)."));
            else
            {
                ImGui.SameLine(); ImGui.TextDisabled($"  {m.Triangles} tris, {m.Parts.Length} part(s)");
                ImGui.Checkbox(Loc.TL("Auto-rotate"), ref meshViewerAutoRotate);
                ImGui.SameLine(); if (ImGui.SmallButton(Loc.TL("Reset view"))) { meshViewerYaw = 0f; meshViewerPitch = 0.3f; meshViewerZoom = 1f; }
                ImGui.SameLine(); if (ImGui.SmallButton(" - ")) meshViewerZoom = Math.Clamp(meshViewerZoom / 1.2f, 0.25f, 6f);
                ImGui.SameLine(); if (ImGui.SmallButton(" + ")) meshViewerZoom = Math.Clamp(meshViewerZoom * 1.2f, 0.25f, 6f);
                if (mvColorTex != 0)
                {
                    var avail = ImGui.GetContentRegionAvail();
                    float side = MathF.Max(96f, MathF.Min(avail.X, avail.Y));
                    // FBO textures are bottom-up vs ImGui's top-down -> flip V (uv0=(0,1), uv1=(1,0)).
                    ImGui.Image((IntPtr)mvColorTex, new Vector2(side, side), new Vector2(0f, 1f), new Vector2(1f, 0f));
                    bool hovered = ImGui.IsItemHovered();
                    if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                    {
                        var d = ImGui.GetIO().MouseDelta;
                        meshViewerYaw += d.X * 0.01f;
                        meshViewerPitch = Math.Clamp(meshViewerPitch + d.Y * 0.01f, -1.45f, 1.45f);
                    }
                    if (hovered)
                    {
                        float wheel = ImGui.GetIO().MouseWheel;   // scroll over the model to zoom in/out
                        if (wheel != 0f) meshViewerZoom = Math.Clamp(meshViewerZoom * (1f + wheel * 0.12f), 0.25f, 6f);
                        ImGui.SetTooltip(Loc.T("Drag to orbit - scroll / +- to zoom"));
                    }
                }
                else ImGui.TextDisabled(Loc.T("(rendering...)"));
            }
        }
    }
    ImGui.End();
}

void MinimapPanel()
{
    if (!showMinimap || minimapTexId == 0) return;
    ImGui.SetNextWindowPos(new Vector2(uiLeftW + 10f, uiMenuH + uiToolH + 10f), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowSize(new Vector2(232f, 262f), ImGuiCond.FirstUseEver);
    if (ImGui.Begin(Loc.TL("Mini-Map"), ref showMinimap, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings))
    {
        var avail = ImGui.GetContentRegionAvail();
        float side = MathF.Max(48f, MathF.Min(avail.X, avail.Y - 22f));
        var p0 = ImGui.GetCursorScreenPos();
        ImGui.Image((IntPtr)minimapTexId, new Vector2(side, side));
        var dl = ImGui.GetWindowDrawList();
        float ws = cfg.WorldSize <= 0f ? 1f : cfg.WorldSize;
        // Gameplay overlay: control points (cyan), vehicle spawns (orange squares), soldier spawns (green). Same
        // north-up V flip as the camera marker. Click anywhere on the map flies the camera there (handler below).
        Vector2 MmPt(float wx, float wz) => p0 + new Vector2(Math.Clamp(wx / ws, 0f, 1f) * side, (1f - Math.Clamp(wz / ws, 0f, 1f)) * side);
        // Bigger, black-outlined markers so the gameplay (incl. the spread-out SEA flags / carriers far from the
        // island) stand out against the open water - the island is small on big naval maps like Midway.
        uint black = 0xFF000000;
        // Static-object dots (dim) so you can spot and click them; the selected one is highlighted.
        if (showMinimapObjects && so is not null)
        {
            uint odim = ImGui.GetColorU32(new Vector4(0.85f, 0.85f, 0.9f, 0.5f));
            foreach (var o in so.Objects) { var od = MmPt(o.Position.X, o.Position.Z); dl.AddCircleFilled(od, 1.2f, odim); }
            if (selected >= 0 && selected < so.Objects.Count)
            { var sd = MmPt(so.Objects[selected].Position.X, so.Objects[selected].Position.Z); dl.AddCircleFilled(sd, 3.2f, ImGui.GetColorU32(new Vector4(1f, 0.4f, 0.4f, 1f))); dl.AddCircle(sd, 3.2f, black, 0, 1.4f); }
        }
        if (showSpawns)
            foreach (var ss in gameplayEdit.SoldierSpawns)
            { var d = MmPt(ss.Position.X, ss.Position.Z); dl.AddCircleFilled(d, 2.6f, ImGui.GetColorU32(new Vector4(0.4f, 1f, 0.45f, 1f))); dl.AddCircle(d, 2.6f, black, 0, 1f); }
        if (showVehicles)
            foreach (var vs in gameplayEdit.VehicleSpawns)
            { var d = MmPt(vs.Position.X, vs.Position.Z); dl.AddRectFilled(d - new Vector2(3.2f, 3.2f), d + new Vector2(3.2f, 3.2f), ImGui.GetColorU32(new Vector4(1f, 0.6f, 0.15f, 1f))); dl.AddRect(d - new Vector2(3.2f, 3.2f), d + new Vector2(3.2f, 3.2f), black, 0, 0, 1f); }
        if (showControlPoints)
            foreach (var cp in gameplayEdit.ControlPoints)
            { var d = MmPt(cp.Position.X, cp.Position.Z); dl.AddCircleFilled(d, 4.5f, ImGui.GetColorU32(new Vector4(0.3f, 0.85f, 1f, 1f))); dl.AddCircle(d, 4.5f, black, 0, 1.6f); }
        // The minimap image reads with +Z (north) at the top, so the camera marker's vertical axis is flipped
        // from world Z (v = 1 - Z/ws); X maps straight across.
        float u = Math.Clamp(cam.Position.X / ws, 0f, 1f), v = 1f - Math.Clamp(cam.Position.Z / ws, 0f, 1f);
        var mk = p0 + new Vector2(u * side, v * side);
        uint yellow = ImGui.GetColorU32(new Vector4(1f, 0.9f, 0.2f, 1f));
        var fwd = cam.Forward; var fdir = new Vector2(fwd.X, -fwd.Z);   // +Z points up in the image
        if (fdir.LengthSquared() > 1e-4f) dl.AddLine(mk, mk + Vector2.Normalize(fdir) * 11f, yellow, 2f);
        dl.AddCircleFilled(mk, 4f, yellow);
        dl.AddCircle(mk, 4f, 0xFF000000, 0, 1.5f);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            var mp = ImGui.GetMousePos();
            float cu = Math.Clamp((mp.X - p0.X) / side, 0f, 1f), cv = Math.Clamp((mp.Y - p0.Y) / side, 0f, 1f);
            float nx = cu * ws, nz = (1f - cv) * ws;   // invert V back to world Z
            if (terrainPick is not null)
            {
                float above = cam.Position.Y - terrainPick.HeightAt(cam.Position.X, cam.Position.Z);
                cam.Position = new Vector3(nx, terrainPick.HeightAt(nx, nz) + MathF.Max(above, 15f), nz);
            }
            else cam.Position = new Vector3(nx, cam.Position.Y, nz);
            // Also select the nearest static object to the click (within a few px) so you can edit it from here.
            if (so is not null && so.Objects.Count > 0)
            {
                float pickR = (8f / side) * ws, bestD2 = pickR * pickR; int best = -1;
                for (int i = 0; i < so.Objects.Count; i++)
                { var op = so.Objects[i].Position; float dx = op.X - nx, dz = op.Z - nz, d2 = dx * dx + dz * dz; if (d2 < bestD2) { bestD2 = d2; best = i; } }
                if (best >= 0) { selected = best; multi.Clear(); multi.Add(best); }
            }
        }
        if (ImGui.SmallButton(Loc.TL("Refresh"))) BuildMinimap();
        ImGui.SameLine(); ImGui.Checkbox(Loc.TL("Objects"), ref showMinimapObjects);
    }
    ImGui.End();
}

void BuildUi()
{
    var fb = window.FramebufferSize;
    float W = fb.X, H = fb.Y;
    float menuH = 0f;
    texLibThumbBudget = 24;   // cap library thumbnail GL uploads per frame (a big folder fills in over a few frames)

    if (ImGui.BeginMainMenuBar())
    {
        menuH = ImGui.GetWindowSize().Y;
        if (ImGui.BeginMenu(Loc.TL("File")))
        {
            if (ImGui.BeginMenu(Loc.TL("Project")))
            {
                if (ImGui.MenuItem(Loc.TL("New Project (map)..."))) OpenProjectMenu(() => ProjectFlows.NewMapFlow());
                if (ImGui.MenuItem(Loc.TL("Open Project (.rfproj)..."))) OpenProjectMenu(() => ProjectFlows.OpenProjectFlow());
                if (ImGui.MenuItem(Loc.TL("Open Level RFA (extract to folder)..."))) OpenProjectMenu(() => ProjectFlows.OpenRfaFlow());
                if (ImGui.MenuItem(Loc.TL("Open Level Folder..."))) OpenProjectMenu(() => ProjectFlows.OpenFolderFlow());
                ImGui.Separator();
                if (ImGui.MenuItem(Loc.TL("Project Settings..."), null, false, activeRfProject is not null)) OpenProjectSettings();
                if (ImGui.MenuItem(Loc.TL("Startup Screen (Close Project)"))) RelaunchToStartup();
                ImGui.EndMenu();
            }
            ImGui.Separator();
            if (ImGui.MenuItem(Loc.TL("New Map..."))) OpenNewMap();
            if (ImGui.MenuItem(Loc.TL("Open Level / .rfa..."), "Ctrl+O")) OpenLevel();
            if (ImGui.MenuItem(Loc.TL("Open Mod..."))) OpenMod();
            if (ImGui.MenuItem(Loc.TL("Save"), "Ctrl+S", false, so is not null && soPath is not null)) DoSave();
            if (ImGui.MenuItem(Loc.TL("Test This Level (in-game)"), "Ctrl+L", false, so is not null && levelDir is not null)) DoTestLevel();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Save the level, then launch the game so you can test it (lighting, objects, etc.).\nPick this map from the in-game map list once it loads."));
            if (ImGui.MenuItem(Loc.TL("Save as Patch .rfa..."), null, false, so is not null && rfaList.Length > 0)) DoSavePatch();
            if (ImGui.MenuItem(Loc.TL("Save as SSM Patch (server-side only)..."), null, false, so is not null && rfaList.Length > 0)) DoSavePatch(serverSideOnly: true);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("A server-side-mod patch: only gameplay .con files, no textures/sounds. Drop it in the server's levels folder - clients need nothing."));
            if (ImGui.MenuItem(Loc.TL("Repack base .rfa in place (advanced)..."), null, false, so is not null && levelDir is not null && LevelArchive.IsRfa(levelDir))) DoRepackBaseInPlace();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Writes edits INTO the base archive. Normally Ctrl+S saves a _NNN patch instead and never touches the base."));
            if (ImGui.MenuItem(Loc.TL("Auto-backup on save"), null, autoBackup)) autoBackup = !autoBackup;
            if (ImGui.MenuItem(Loc.TL("Import .obj..."), null, false, meshLib is not null && so is not null)) DoImportObj();
            if (ImGui.MenuItem(Loc.TL("Import treeMesh.rfa..."), null, false, meshLib is not null && so is not null)) DoImportTreeMesh();
            if (ImGui.MenuItem(Loc.TL("Play .bik video..."))) DoPlayBik();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Play a Bink (.bik) movie from the mod's movies/ folder (or anywhere) inside the editor.\nDecoded with FFmpeg if present, else opened in the RAD Bink player."));
            if (ImGui.MenuItem(Loc.TL("Play map video (.bik in this map)..."), null, false, rfaList.Length > 0)) DoPlayMapBik();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Find + play any .bik video embedded in the loaded map's .rfa."));
            if (ImGui.MenuItem(Loc.TL("Generate Minimap"), null, false, heightmap is not null)) DoGenerateMinimap();
            if (ImGui.MenuItem(Loc.TL("Bake Sun Shadows"), null, false, heightmap is not null)) DoBakeShadows();
            if (ImGui.MenuItem(Loc.TL("Reload Level Lightmap (display)"), null, false, heightmap is not null)) InitTerrainShadowOnLoad();
            if (ImGui.MenuItem(Loc.TL("Save / Generate AI Navmaps"), null, false, heightmap is not null)) DoGenerateNavmaps();
            if (ImGui.MenuItem(Loc.TL("Open AI Pathmap (.raw)..."))) OpenPathmapFile();
            if (ImGui.MenuItem(Loc.TL("Scatter Objects..."), null, false, so is not null && meshLib is not null && terrainPick is not null)) { scatterError = ""; scatterRequest = true; }
            if (ImGui.MenuItem(Loc.TL("Write LightmapShadowBits.lsb on Save"), null, writeShadowLsb, heightmap is not null)) writeShadowLsb = !writeShadowLsb;
            // Flips correct a mirrored shadow for BOTH the on-screen lightmap display AND the .lsb write-back; toggling
            // re-displays the loaded lightmap immediately so you can see which orientation is right.
            if (ImGui.MenuItem(Loc.TL("   .lsb: flip X (if shadows are mirrored L/R)"), null, shadowLsbFlipX, heightmap is not null)) { shadowLsbFlipX = !shadowLsbFlipX; InitTerrainShadowOnLoad(); }
            if (ImGui.MenuItem(Loc.TL("   .lsb: flip Y (if mirrored top/bottom)"), null, shadowLsbFlipY, heightmap is not null)) { shadowLsbFlipY = !shadowLsbFlipY; InitTerrainShadowOnLoad(); }
            ImGui.Separator();
            if (ImGui.MenuItem(Loc.TL("Exit"))) window.Close();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(Loc.TL("Edit")))
        {
            if (ImGui.MenuItem(Loc.TL("Undo"), "Z")) DoUndo();
            if (ImGui.MenuItem(Loc.TL("Redo"), "Y")) DoRedo();
            ImGui.Separator();
            bool canDelete = selected >= 0 || gpIndex >= 0;
            if (ImGui.MenuItem(Loc.TL("Delete"), "Del", false, canDelete) && hist is not null)
            {
                if (gpIndex >= 0) { hist.Do(new GameplayDeleteCommand(gameplayEdit, gpKind, gpIndex, null)); gpIndex = -1; }
                else if (selected >= 0 && so is not null)
                { hist.Do(new DeleteObject(so.Objects[selected].Id)); selected = -1; SyncMarkers(); RebuildObjects(); UploadMarkers(); }
            }
            ImGui.Separator();
            if (ImGui.MenuItem(Loc.TL("Save Selection as Prefab..."), null, false, multi.Count > 0)) savePrefabRequest = true;
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(Loc.TL("Object")))
        {
            if (ImGui.MenuItem(Loc.TL("Duplicate"), "Ctrl+D", false, multi.Count > 0)) DuplicateSelected();
            if (ImGui.MenuItem(Loc.TL("Drop to ground"), "G", false, multi.Count > 0)) DropSelectedToGround();
            if (ImGui.MenuItem(Loc.TL("Delete"), "Del", false, multi.Count > 0 || gpIndex >= 0)) OnKeyDown(kb!, Key.Delete, 0);
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(Loc.TL("Tools")))
        {
            if (ImGui.MenuItem(Loc.TL("Road tool"), null, roadMode)) { roadMode = !roadMode; if (roadMode) measureMode = false; roadPts.Clear(); roadPtW.Clear(); roadSelIdx = -1; roadDragIdx = -1; }
            if (ImGui.MenuItem(Loc.TL("Measure"), null, measureMode)) { measureMode = !measureMode; if (measureMode) roadMode = false; measurePts.Clear(); }
            if (ImGui.MenuItem(Loc.TL("Validate map..."))) { validateReport = ValidateMap(); validateRequest = true; }
            ImGui.Separator();
            if (ImGui.MenuItem(Loc.TL("Generate Material Map (from terrain)"), null, false, heightmap is not null)) DoGenerateMaterialMap();
            if (ImGui.MenuItem(Loc.TL("Generate Surface Maps (bake from set)"), null, false, materialMap is not null && atlasCpu is not null)) DoGenerateSurfaceMaps();
            ImGui.Separator();
            if (ImGui.MenuItem(Loc.TL("Bake Object Lightmaps (from sun)"), null, false, so is not null && meshLib is not null && heightmap is not null)) BakeObjectLightmaps();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Bake each building/object's lighting (sun + terrain shadow) into its lightmap from the\ncurrent sun, then Save to ship them to the game. Pair with File > 'Write LightmapShadowBits.lsb'\nfor the terrain shadow. Set the sun first in the Environment > Sun panel."));
            ImGui.Separator();
            if (ImGui.MenuItem(Loc.TL("Convert TGA -> DDS..."))) DoConvertTgaToDds();
            if (ImGui.MenuItem(Loc.TL("Batch TGA -> DDS (folder)..."))) DoBatchTgaToDds();
            ImGui.Separator();
            bool haveOver = growth?.Over is not null && growth.OverPalette is not null;
            if (ImGui.MenuItem(Loc.TL("Save Overgrowth Settings"), null, false, levelDir is not null)) SaveOvergrowthSettings();
            if (ImGui.MenuItem(Loc.TL("Export Overgrowth (map + .wst)..."), null, false, haveOver)) DoExportOvergrowthFiles();
            if (ImGui.MenuItem(Loc.TL("Bake Overgrowth -> StaticObjects.con..."), null, false, haveOver && meshLib is not null)) DoBakeOvergrowthToCon();
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(Loc.TL("Terrain")))
        {
            if (ImGui.MenuItem(Loc.TL("Import Heightmap.raw..."), null, false, heightmap is not null)) DoImportHeightmap();
            if (ImGui.MenuItem(Loc.TL("Export Heightmap.raw..."), null, false, heightmap is not null)) DoExportHeightmap();
            ImGui.EndMenu();
        }
        foreach (var m in new[] { "Layer", "Window" })
            if (ImGui.BeginMenu(Loc.TL(m) + "##stub_" + m)) { ImGui.MenuItem(Loc.TL("(coming soon)"), null, false, false); ImGui.EndMenu(); }
        if (ImGui.BeginMenu(Loc.TL("Collab")))
        {
            if (collab is null)
            {
                if (ImGui.MenuItem(Loc.TL("Collaborate..."))) { collabError = ""; collabRequest = true; }
            }
            else
            {
                ImGui.MenuItem(Loc.T(collab.Status), null, false, false);
                if (collab.IsHost)
                {
                    if (!string.IsNullOrEmpty(collab.LocalIp)) ImGui.MenuItem(string.Format(Loc.T("LAN: {0}:{1}"), collab.LocalIp, collab.Port), null, false, false);
                    ImGui.MenuItem(string.Format(Loc.T("Internet: {0}:{1} (forward port)"), collab.PublicIp, collab.Port), null, false, false);
                }
                ImGui.Separator();
                ImGui.MenuItem(string.Format(Loc.T("{0} peer(s) connected"), collab.Peers.Count), null, false, false);
                int pmi = 0;
                foreach (var peer in collab.Peers.Values)
                {
                    var pc = peerColors[pmi % peerColors.Length];
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(pc.X, pc.Y, pc.Z, 1f));
                    if (ImGui.MenuItem(string.Format(Loc.T("  {0}   (jump to)"), peer.Name))) cam.Position = new Vector3(peer.Cursor.X, peer.Cursor.Y, peer.Cursor.Z);
                    ImGui.PopStyleColor();
                    pmi++;
                }
                ImGui.Separator();
                if (ImGui.MenuItem(Loc.TL("Disconnect"))) { collab.Stop(); collab = null; }
            }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(Loc.TL("View")))
        {
            ImGui.MenuItem(Loc.TL("Log / Errors"), null, ref showLog);
            ImGui.Separator();
            // How a level is ASSEMBLED. Both pull in content the opened .rfa doesn't itself contain, which is what
            // the game does - but while authoring it can be confusing, so both can be switched off. Reopen to apply.
            if (ImGui.BeginMenu(Loc.TL("Level assembly")))
            {
                bool inh = AppPrefs.ResolveInheritedMods, layer = AppPrefs.LayerBaseMap;
                if (ImGui.MenuItem(Loc.TL("Resolve inherited mod dependencies"), null, ref inh))
                { AppPrefs.ResolveInheritedMods = inh; AppPrefs.Save(); Toast(Loc.T("Reopen the map to apply.")); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("Follow each dependency's own init.con, so a mod that lists FHSW also gets FH."));
                if (ImGui.MenuItem(Loc.TL("Layer base map under add-on maps"), null, ref layer))
                { AppPrefs.LayerBaseMap = layer; AppPrefs.Save(); Toast(Loc.T("Reopen the map to apply.")); }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(Loc.T("When the opened .rfa has no terrain, load the same-named base map underneath it."));
                ImGui.EndMenu();
            }
            ImGui.Separator();
            // UI language. The font atlas is baked once at startup (Japanese needs CJK glyphs the built-in font
            // lacks), so switching language restarts the editor.
            if (ImGui.BeginMenu(Loc.TL("Language")))
            {
                foreach (var lang in Loc.Available)
                {
                    bool active = string.Equals(lang.Code, Loc.Current, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.MenuItem(lang.DisplayName + "##lang_" + lang.Code, null, active) && !active) SetLanguageAndRestart(lang.Code);
                }
                ImGui.Separator();
                if (ImGui.MenuItem(Loc.TL("Export translation template...")))
                {
                    try { Toast(Loc.T("Template written: ") + Loc.WriteTemplate(Loc.Current == "en" ? "ja" : Loc.Current, Loc.Seen)); }
                    catch (Exception ex) { Toast(Loc.T("Template failed: ") + ex.Message); }
                }
                ImGui.EndMenu();
            }
            ImGui.EndMenu();
        }
        if (ImGui.BeginMenu(Loc.TL("Help"))) { if (ImGui.MenuItem(Loc.TL("User Guide / Controls"))) showHelp = true; ImGui.Separator(); ImGui.MenuItem(Loc.TL("RefractorForge"), null, false, false); ImGui.EndMenu(); }
        ImGui.EndMainMenuBar();
    }
    if (menuH <= 0) menuH = ImGui.GetFrameHeight();
    uiMenuH = menuH;   // hand the menu-bar height to the 3D overlay so it can clip labels below the menu

    const float statusH = uiStatusH, leftW = uiLeftW, rightW = uiRightW;
    float top = menuH;
    const ImGuiWindowFlags fixedFlags = ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
                                      | ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.NoSavedSettings;
    const ImGuiWindowFlags barFlags = fixedFlags | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;

    ImGui.SetNextWindowPos(new Vector2(0, top), ImGuiCond.Always);
    ImGui.SetNextWindowSize(new Vector2(W, 0f), ImGuiCond.Always);   // 0 height = auto-fit both toolbar rows so the sub-toolbar is never clipped
    float toolH = uiToolH;
    if (ImGui.Begin("##toolbar", barFlags)) { ToolButtons(); toolH = ImGui.GetWindowHeight(); }
    ImGui.End();
    uiToolH = toolH;   // remember the measured height for the 3D-overlay viewport clip (uiMenuH + uiToolH)
    top += toolH;

    float bodyH = H - top - statusH;

    ImGui.SetNextWindowPos(new Vector2(0, top), ImGuiCond.Always);
    ImGui.SetNextWindowSize(new Vector2(leftW, bodyH), ImGuiCond.Always);
    if (ImGui.Begin(Loc.TL("Object Library"), fixedFlags))
    {
        ImGui.PushItemWidth(-1);
        ImGui.InputTextWithHint("##search", Loc.T("Search objects..."), ref searchText, 64);
        ImGui.PopItemWidth();
        ImGui.Separator();
        string filter = searchText.Trim();
        for (int ci = 0; ci < catalog.Count; ci++)
        {
            var (label, items) = catalog[ci];
            var shown = string.IsNullOrEmpty(filter) ? items
                : items.Where(t => ShortName(t).Contains(filter, StringComparison.OrdinalIgnoreCase)
                                   || t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (shown.Length == 0) continue;
            var nodeFlags = (ci < 2 || filter.Length > 0) ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (ImGui.TreeNodeEx($"{Loc.T(label)}  ({shown.Length})###cat_{label}", nodeFlags))
            {
                foreach (var t in shown)
                {
                    bool isGp = GpKindForDrag(t) is not null;
                    bool isPf = IsPrefab(t);
                    bool special = isGp || isPf;   // gameplay sentinels + prefabs show their raw name, not ShortName
                    // Clicking a Gameplay entry arms that placement kind; a prefab arms it for stamping (+ Place
                    // tool); a static template selects it for the browser. All are drag sources too.
                    if (ImGui.Selectable(special ? Loc.T(t) + "###gp_" + t : ShortName(t), !special && browserTemplate == t))
                    {
                        if (GpKindForDrag(t) is GpKind k) { gpPlaceKind = k; tool = Array.IndexOf(toolNames, "Place"); mapper = 2; }
                        else { browserTemplate = t; if (isPf) { gpPlaceKind = null; tool = Array.IndexOf(toolNames, "Place"); mapper = 2; } }
                    }
                    // Double-click a real mesh template -> open the 3D model viewer.
                    if (!special && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    { meshViewerTemplate = t; meshViewerOpen = true; meshViewerYaw = 0f; }
                    // Drag a library item onto the map to place it (no need to pick the Place tool first).
                    if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.None))
                    {
                        dragTemplate = t;
                        ImGui.SetDragDropPayload("RF_OBJ", IntPtr.Zero, 0);
                        ImGui.Text(special ? Loc.T(t) : ShortName(t));  // drag preview tooltip
                        ImGui.EndDragDropSource();
                    }
                }
                ImGui.TreePop();
            }
        }
    }
    ImGui.End();

    ImGui.SetNextWindowPos(new Vector2(W - rightW, top), ImGuiCond.Always);
    ImGui.SetNextWindowSize(new Vector2(rightW, bodyH), ImGuiCond.Always);
    if (ImGui.Begin(Loc.TL("Inspector"), fixedFlags)) { Inspector(); LayersPanel(); EnvironmentPanel(); }
    ImGui.End();

    MinimapPanel();

    ImGui.SetNextWindowPos(new Vector2(0, H - statusH), ImGuiCond.Always);
    ImGui.SetNextWindowSize(new Vector2(W, statusH), ImGuiCond.Always);
    if (ImGui.Begin("##status", barFlags))
    {
        var cp = cam.Position;
        ImGui.Text($"Cam  {cp.X:0.0}, {cp.Y:0.0}, {cp.Z:0.0}"); ImGui.SameLine(); Sep();
        // World position of the terrain point under the cursor ("--" when over a panel or pointed at the sky).
        if (terrainPick is not null && !UiWantsMouse())
        {
            var fbh = window.FramebufferSize;
            var hr = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fbh.X, fbh.Y);
            if (terrainPick.Raycast(hr, out var hp)) ImGui.Text($"Cursor  {hp.X:0.0}, {hp.Y:0.0}, {hp.Z:0.0}");
            else ImGui.Text(Loc.T("Cursor  --"));
        }
        else ImGui.Text(Loc.T("Cursor  --"));
        ImGui.SameLine(); Sep();
        ImGui.Text(string.Format(Loc.T("{0} selected"), multi.Count)); ImGui.SameLine(); Sep();
        ImGui.Text(Loc.T("Tool:")); ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.49f, 0.70f, 0.92f, 1f), Loc.T(toolNames[tool])); ImGui.SameLine(); Sep();
        ImGui.Text(string.Format(Loc.T("Snap {0}"), snapOn ? Loc.T("On") : Loc.T("Off"))); ImGui.SameLine(); Sep();
        ImGui.Text(string.Format(Loc.T("world {0} m"), cfg.WorldSize.ToString("0"))); ImGui.SameLine(); Sep();
        ImGui.Text(string.Format(Loc.T("{0} objects"), so?.Objects.Count ?? markers.Length)); ImGui.SameLine(); Sep();
        ImGui.Text($"{lastFps:0} fps");
        if (toastT > 0f && toastText.Length > 0)
        { ImGui.SameLine(); Sep(); ImGui.TextColored(new Vector4(0.55f, 0.95f, 0.6f, MathF.Min(1f, toastT)), toastText); }
    }
    ImGui.End();

    // Complete a drag-and-drop from the Object Library: when the dragged item is released over the 3D
    // viewport (not over a panel), place it on the terrain under the cursor - same as the Place tool.
    if (dragTemplate is not null && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
    {
        var mp = ImGui.GetMousePos();
        bool overViewport = mp.X > leftW && mp.X < W - rightW && mp.Y > toolH && mp.Y < H - statusH;
        if (overViewport && hist is not null && terrainPick is not null)
        {
            var fbv = window.FramebufferSize;
            var ray = Picking.ScreenToRay(cam, lastMouse.X, lastMouse.Y, fbv.X, fbv.Y);
            if (terrainPick.Raycast(ray, out var hit))
            {
                var dhit = SnapXZ(new Vec3(hit.X, hit.Y, hit.Z));
                if (GpKindForDrag(dragTemplate) is GpKind pk)
                {
                    // Gameplay drop: create a control point / vehicle / soldier spawn at the drop point.
                    object item = pk switch
                    {
                        GpKind.ControlPoint => EditableGameplay.NewControlPoint(dhit),
                        GpKind.Vehicle => EditableGameplay.NewVehicleSpawn(dhit),
                        _ => EditableGameplay.NewSoldierSpawn(dhit),
                    };
                    var addCmd = new GameplayAddCommand(gameplayEdit, pk, item, null);
                    hist.Do(addCmd);
                    gpKind = pk; gpIndex = addCmd.Index; selected = -1; multi.Clear();
                    Console.WriteLine($"Dropped {pk} at {hit.X:0.#}, {hit.Z:0.#}");
                }
                else if (IsPrefab(dragTemplate))
                {
                    // Prefab drop: stamp the whole object group at the cursor (one undo step).
                    StampPrefab(dragTemplate, dhit);
                }
                else if (so is not null)
                {
                    // Static object drop.
                    var id = Guid.NewGuid().ToString("N");
                    hist.Do(new AddObject(id, dragTemplate, dhit, Vec3.Zero));
                    browserTemplate = dragTemplate;
                    SyncMarkers(); RebuildObjects(); UploadMarkers();
                    selected = so.Objects.FindIndex(o => o.Id == id);
                    multi.Clear(); if (selected >= 0) multi.Add(selected);
                    Console.WriteLine($"Dropped {dragTemplate} at {hit.X:0.#}, {hit.Y:0.##}, {hit.Z:0.#}");
                }
            }
        }
        dragTemplate = null;
    }

    // Collaboration: floating name labels at each peer's position (projected to screen, peer colour).
    if (collab is not null && collab.Peers.Count > 0)
    {
        var dl = ImGui.GetBackgroundDrawList();   // world overlay: over the 3D scene but UNDER all UI chrome (panels/minimap/modals)
        var vp = cam.ViewProjection;
        var fbp = window.FramebufferSize;
        int pli = 0;
        foreach (var peer in collab.Peers.Values)
        {
            var col = peerColors[pli++ % peerColors.Length];
            uint c32 = ImGui.GetColorU32(new Vector4(col.X, col.Y, col.Z, 1f));
            var clip = Vector4.Transform(new Vector4(peer.Cursor.X, peer.Cursor.Y + 6f, peer.Cursor.Z, 1f), vp);
            if (clip.W <= 1e-4f) continue;                       // behind the camera
            float sx = (clip.X / clip.W * 0.5f + 0.5f) * fbp.X;
            float sy = (1f - (clip.Y / clip.W * 0.5f + 0.5f)) * fbp.Y;
            dl.AddText(new Vector2(sx + 8f, sy - 8f), c32, peer.Name);
        }
    }

    NewMapModal();      // top-level scope here: all panels' Begin/End are balanced, so the popups nest cleanly
    SavePrefabModal();
    CollabModal();
    ScatterModal();
    EditCpModal();
    EditVehModal();
    EditSolModal();
    HelpWindow();
    ValidateModal();
    LogWindow();
    TextureLibraryWindow();
    LayerToolWindow();
    MeshViewerWindow();
    BikWindow();
    PathmapPreviewWindow();
}

// In-app Log / Errors window: shows captured console output (errors highlighted). Auto-pops after a level load that
// produced warnings (missing meshes etc.) - the equivalent of Battlecraft's "Load Errors" box - and is reopenable
// from View -> Log / Errors. Replaces hunting through the background CMD window.
void LogWindow()
{
    if (!showLog) return;
    var fbs = window.FramebufferSize;
    ImGui.SetNextWindowSize(new Vector2(720, 420), ImGuiCond.FirstUseEver);
    ImGui.SetNextWindowPos(new Vector2(fbs.X * 0.5f, fbs.Y * 0.5f), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
    if (!ImGui.Begin(Loc.TL("Log / Errors"), ref showLog, ImGuiWindowFlags.NoCollapse)) { ImGui.End(); return; }
    var all = ConsoleLog.Snapshot();
    int errCount = all.Count(ConsoleLog.LooksLikeError);
    ImGui.Checkbox(Loc.TL("Errors only"), ref logErrorsOnly);
    ImGui.SameLine(); ImGui.TextDisabled($"{all.Count} lines, {errCount} warning(s)");
    ImGui.SameLine();
    if (ImGui.SmallButton(Loc.TL("Copy"))) { try { Win32Clipboard.SetText(string.Join("\r\n", logErrorsOnly ? all.Where(ConsoleLog.LooksLikeError) : all)); } catch { } }
    ImGui.SameLine(); if (ImGui.SmallButton(Loc.TL("Clear"))) ConsoleLog.Clear();
    ImGui.SameLine(); if (ImGui.SmallButton(Loc.TL("Close"))) showLog = false;
    ImGui.Separator();
    ImGui.BeginChild("loglines", new Vector2(0, 0), ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);
    foreach (var line in all)
    {
        bool err = ConsoleLog.LooksLikeError(line);
        if (logErrorsOnly && !err) continue;
        if (err) ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), line);
        else ImGui.TextUnformatted(line);
    }
    if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f) ImGui.SetScrollHereY(1f);   // autoscroll when at the bottom
    ImGui.EndChild();
    ImGui.End();
}

// Read-only map-validation report popup (filled by the Tools -> Validate map command).
void ValidateModal()
{
    if (validateRequest) { ImGui.OpenPopup(Loc.TL("Validate Map")); validateRequest = false; }
    var fbv = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fbv.X * 0.5f, fbv.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new Vector2(420, 0), ImGuiCond.Appearing);
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Validate Map"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        return;
    ImGui.TextUnformatted(validateReport);
    ImGui.Separator();
    if (ImGui.Button(Loc.TL("Close"), new Vector2(160, 0))) ImGui.CloseCurrentPopup();
    ImGui.EndPopup();
}

// Open the right Battlecraft-style edit dialog for a gameplay handle (shared by the inspector "Edit..." buttons and the
// double-click shortcut). Fills the dialog's working copy from the current item, then arms its popup-open request.
void OpenGpEditor(GpKind kind, int idx)
{
    if (idx < 0 || idx >= gameplayEdit.CountOf(kind)) return;
    if (kind == GpKind.ControlPoint)
    {
        var c = (ControlPointDef)gameplayEdit.GetItem(GpKind.ControlPoint, idx);
        editCpRequest = true; ecpIndex = idx;
        ecpName = c.Name; ecpCpName = string.IsNullOrEmpty(c.ControlPointName) ? c.Name : c.ControlPointName;
        ecpRadius = c.Radius; ecpTeam = c.Team; ecpArea = c.AreaValue; ecpConv = c.ConversionTime; ecpGroup = c.SpawnGroupId;
        ecpOsId = c.ObjectSpawnerId; ecpTimeGet = c.TimeToGetControl; ecpTimeLose = c.TimeToLoseControl;
        ecpDisEnemy = c.DisableIfEnemyInside; ecpDisLosing = c.DisableWhenLosing; ecpLoseClose = c.LoseControlWhenEnemyClose;
        ecpLoseNot = c.LoseControlWhenNotClose; ecpUnable = c.UnableToChangeTeam; ecpOnlyTeam = c.OnlyTakableByTeam; ecpCollision = c.HasCollisionPhysics;
        ecpPos = new Vector3(c.Position.X, c.Position.Y, c.Position.Z);
    }
    else if (kind == GpKind.Vehicle)
    {
        var v = (VehicleSpawnDef)gameplayEdit.GetItem(GpKind.Vehicle, idx);
        editVehRequest = true; evIndex = idx; evName = v.Name;
        evPos = new Vector3(v.Position.X, v.Position.Y, v.Position.Z); evRot = new Vector3(v.Rotation.X, v.Rotation.Y, v.Rotation.Z);
        evTeam = v.Team; evOsId = v.OsId;
    }
    else
    {
        var s = (SoldierSpawnDef)gameplayEdit.GetItem(GpKind.Soldier, idx);
        editSolRequest = true; esIndex = idx; esName = s.Name;
        esPos = new Vector3(s.Position.X, s.Position.Y, s.Position.Z); esRot = new Vector3(s.Rotation.X, s.Rotation.Y, s.Rotation.Z);
        esGroup = s.Group; esSpawnId = s.SpawnId; esPara = s.SpawnAsParaTrooper != 0;
    }
}

// Battlecraft-style "Edit Control Point" dialog: edit the selected flag's name, control-point name, position,
// capture radius, team, area value, conversion time and spawn group; OK commits as one undo step (and syncs
// over collaboration). Flag geometry / team flags on the template are preserved verbatim on save.
void EditCpModal()
{
    if (editCpRequest) { ImGui.OpenPopup(Loc.TL("Edit Control Point")); editCpRequest = false; }
    var fbm = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fbm.X * 0.5f, fbm.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Edit Control Point"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        return;
    if (ecpIndex < 0 || ecpIndex >= gameplayEdit.ControlPoints.Count) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }

    ImGui.InputText(Loc.TL("Name"), ref ecpName, 64u);
    ImGui.InputText(Loc.TL("Control point name"), ref ecpCpName, 64u);
    ImGui.DragFloat3(Loc.TL("Position"), ref ecpPos, 0.25f);
    ImGui.DragFloat(Loc.TL("Capture radius (m)"), ref ecpRadius, 0.5f, 1f, 300f, "%.1f");
    // Team labels follow the target game: BF1942 = Axis/Allies, BF Vietnam = NVA/US. (Index 1/2 are the same slots.)
    string[] teams = gameIsBf1942
        ? new[] { Loc.T("Neutral (0)"), Loc.T("Axis (1)"), Loc.T("Allies (2)") }
        : new[] { Loc.T("Neutral (0)"), Loc.T("Vietcong / NVA (1)"), Loc.T("US Army (2)") };
    int teamIdx = Math.Clamp(ecpTeam, 0, 2);
    if (ImGui.Combo(Loc.TL("Team"), ref teamIdx, teams, teams.Length)) ecpTeam = teamIdx;
    ImGui.InputInt(Loc.TL("Area value"), ref ecpArea);
    ImGui.InputInt(Loc.TL("Spawn group id"), ref ecpGroup);
    ImGui.InputInt(Loc.TL("Object spawner id"), ref ecpOsId);
    if (gameIsBf1942)
    {
        ImGui.Separator(); ImGui.TextDisabled(Loc.T("Capture timing / behaviour (BF1942)"));
        ImGui.InputInt(Loc.TL("Time to get control"), ref ecpTimeGet);
        ImGui.InputInt(Loc.TL("Time to lose control"), ref ecpTimeLose);
        bool b;
        b = ecpDisEnemy != 0; if (ImGui.Checkbox(Loc.TL("Disable if enemy inside radius"), ref b)) ecpDisEnemy = b ? 1 : 0;
        b = ecpDisLosing != 0; if (ImGui.Checkbox(Loc.TL("Disable when losing control"), ref b)) ecpDisLosing = b ? 1 : 0;
        b = ecpLoseClose != 0; if (ImGui.Checkbox(Loc.TL("Lose control when enemy close"), ref b)) ecpLoseClose = b ? 1 : 0;
        b = ecpLoseNot != 0; if (ImGui.Checkbox(Loc.TL("Lose control when not close"), ref b)) ecpLoseNot = b ? 1 : 0;
        b = ecpUnable != 0; if (ImGui.Checkbox(Loc.TL("Unable to change team"), ref b)) ecpUnable = b ? 1 : 0;
        b = ecpCollision != 0; if (ImGui.Checkbox(Loc.TL("Has collision physics"), ref b)) ecpCollision = b ? 1 : 0;
        ImGui.InputInt(Loc.TL("Only takable by team"), ref ecpOnlyTeam);
    }
    else
    {
        ImGui.InputInt(Loc.TL("Conversion time"), ref ecpConv);
    }
    ImGui.Spacing();
    ImGui.TextDisabled(Loc.T("Flag geometry + team flags are preserved on save."));
    ImGui.Separator();
    if (ImGui.Button(Loc.TL("OK"), new Vector2(150, 0)))
    {
        if (hist is not null)
        {
            var cur = (ControlPointDef)gameplayEdit.GetItem(GpKind.ControlPoint, ecpIndex);
            var nu = cur with
            {
                Name = ecpName, Position = new Vec3(ecpPos.X, ecpPos.Y, ecpPos.Z), Radius = MathF.Max(1f, ecpRadius),
                SpawnGroupId = ecpGroup, ObjectSpawnerId = ecpOsId, Team = Math.Clamp(ecpTeam, 0, 2), AreaValue = ecpArea,
                ConversionTime = ecpConv, ControlPointName = ecpCpName, TimeToGetControl = ecpTimeGet, TimeToLoseControl = ecpTimeLose,
                DisableIfEnemyInside = ecpDisEnemy, DisableWhenLosing = ecpDisLosing, LoseControlWhenEnemyClose = ecpLoseClose,
                LoseControlWhenNotClose = ecpLoseNot, UnableToChangeTeam = ecpUnable, OnlyTakableByTeam = ecpOnlyTeam, HasCollisionPhysics = ecpCollision,
            };
            hist.Do(new GameplaySetItemCommand(gameplayEdit, GpKind.ControlPoint, ecpIndex, nu, null));
        }
        ImGui.CloseCurrentPopup();
    }
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Cancel"), new Vector2(150, 0))) ImGui.CloseCurrentPopup();
    ImGui.EndPopup();
}

// Battlecraft-style "Edit Object Spawn" dialog: a vehicle spawner's name, position, rotation, owning OS id and team.
void EditVehModal()
{
    if (editVehRequest) { ImGui.OpenPopup(Loc.TL("Edit Object Spawn")); editVehRequest = false; }
    var fbm = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fbm.X * 0.5f, fbm.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new Vector2(340, 0), ImGuiCond.Appearing);
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Edit Object Spawn"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        return;
    if (evIndex < 0 || evIndex >= gameplayEdit.VehicleSpawns.Count) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
    ImGui.InputText(Loc.TL("Name"), ref evName, 64u);
    ImGui.DragFloat3(Loc.TL("Position"), ref evPos, 0.25f);
    ImGui.DragFloat3(Loc.TL("Rotation yaw/pitch/roll"), ref evRot, 1f);
    ImGui.InputInt(Loc.TL("OS id"), ref evOsId);
    string[] vteams = gameIsBf1942
        ? new[] { Loc.T("Neutral (0)"), Loc.T("Axis (1)"), Loc.T("Allies (2)") }
        : new[] { Loc.T("Neutral (0)"), Loc.T("Vietcong / NVA (1)"), Loc.T("US Army (2)") };
    int vteamIdx = Math.Clamp(evTeam, 0, 2);
    if (ImGui.Combo(Loc.TL("Team"), ref vteamIdx, vteams, vteams.Length)) evTeam = vteamIdx;
    ImGui.Spacing();
    ImGui.TextDisabled(Loc.T("OS id links the spawner to its control point."));
    ImGui.Separator();
    if (ImGui.Button(Loc.TL("OK"), new Vector2(150, 0)))
    {
        if (hist is not null)
        {
            var cur = (VehicleSpawnDef)gameplayEdit.GetItem(GpKind.Vehicle, evIndex);
            var nu = cur with { Name = evName, Position = new Vec3(evPos.X, evPos.Y, evPos.Z), Rotation = new Vec3(evRot.X, evRot.Y, evRot.Z), OsId = evOsId, Team = Math.Clamp(evTeam, 0, 2) };
            hist.Do(new GameplaySetItemCommand(gameplayEdit, GpKind.Vehicle, evIndex, nu, null));
        }
        ImGui.CloseCurrentPopup();
    }
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Cancel"), new Vector2(150, 0))) ImGui.CloseCurrentPopup();
    ImGui.EndPopup();
}

// Battlecraft-style "Edit Soldier Spawn" dialog: name, spawn group, spawn id, paratrooper flag, position, rotation.
void EditSolModal()
{
    if (editSolRequest) { ImGui.OpenPopup(Loc.TL("Edit Soldier Spawn")); editSolRequest = false; }
    var fbm = window.FramebufferSize;
    ImGui.SetNextWindowPos(new Vector2(fbm.X * 0.5f, fbm.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    ImGui.SetNextWindowSize(new Vector2(340, 0), ImGuiCond.Appearing);
    bool open = true;
    if (!ImGui.BeginPopupModal(Loc.TL("Edit Soldier Spawn"), ref open, ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        return;
    if (esIndex < 0 || esIndex >= gameplayEdit.SoldierSpawns.Count) { ImGui.CloseCurrentPopup(); ImGui.EndPopup(); return; }
    ImGui.InputText(Loc.TL("Name"), ref esName, 64u);
    ImGui.InputInt(Loc.TL("Spawn group"), ref esGroup);
    ImGui.InputInt(Loc.TL("Spawn id"), ref esSpawnId);
    ImGui.Checkbox(Loc.TL("Spawn as paratrooper"), ref esPara);
    ImGui.DragFloat3(Loc.TL("Position"), ref esPos, 0.25f);
    ImGui.DragFloat3(Loc.TL("Rotation yaw/pitch/roll"), ref esRot, 1f);
    ImGui.Spacing();
    ImGui.TextDisabled(Loc.T("Spawn group ties this to its control point."));
    ImGui.Separator();
    if (ImGui.Button(Loc.TL("OK"), new Vector2(150, 0)))
    {
        if (hist is not null)
        {
            var cur = (SoldierSpawnDef)gameplayEdit.GetItem(GpKind.Soldier, esIndex);
            var nu = cur with { Name = esName, Position = new Vec3(esPos.X, esPos.Y, esPos.Z), Rotation = new Vec3(esRot.X, esRot.Y, esRot.Z), Group = esGroup, SpawnId = esSpawnId, SpawnAsParaTrooper = esPara ? 1 : 0 };
            hist.Do(new GameplaySetItemCommand(gameplayEdit, GpKind.Soldier, esIndex, nu, null));
        }
        ImGui.CloseCurrentPopup();
    }
    ImGui.SameLine();
    if (ImGui.Button(Loc.TL("Cancel"), new Vector2(150, 0))) ImGui.CloseCurrentPopup();
    ImGui.EndPopup();
}

// Help > User Guide window: shows USER_GUIDE.md (shipped next to the exe) as scrollable, wrapped text.
void HelpWindow()
{
    if (!showHelp) return;
    var fbh = window.FramebufferSize;
    ImGui.SetNextWindowSize(new Vector2(720f, MathF.Min(fbh.Y * 0.85f, 820f)), ImGuiCond.Appearing);
    ImGui.SetNextWindowPos(new Vector2(fbh.X * 0.5f, fbh.Y * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
    if (ImGui.Begin(Loc.TL("User Guide / Controls"), ref showHelp, ImGuiWindowFlags.NoSavedSettings))
    {
        if (helpText is null)
        {
            try { var hp = Path.Combine(AppContext.BaseDirectory, "USER_GUIDE.md"); helpText = File.Exists(hp) ? File.ReadAllText(hp) : Loc.T("USER_GUIDE.md was not found next to the editor. See the RefractorForge GitHub repository for the full documentation."); }
            catch (Exception ex) { helpText = Loc.T("Could not load USER_GUIDE.md: ") + ex.Message; }
        }
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(helpText);
        ImGui.PopTextWrapPos();
    }
    ImGui.End();
}

uint MakeMesh(float[] verts, uint[] indices, out uint vbo)
{
    uint vao = gl.GenVertexArray(); gl.BindVertexArray(vao);
    vbo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.DynamicDraw);
    uint stride = 8 * (uint)sizeof(float);          // position(3) + normal(3) + uv(2)
    unsafe
    {
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        gl.EnableVertexAttribArray(2);
    }
    uint ebo = gl.GenBuffer(); gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
    gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);
    return vao;
}

// Load the level's real skybox cubemap from the Sky_<map>_0N.dds faces (skybox mesh Sky_OI_m1 -> textures
// Sky_OI_0N), falling back to the generic env_default cubemap. Best-effort face order (06 = the low-res down
// face); verify in-editor - the Sky inspector has a rotation slider + a toggle to the procedural sun-sky. Leaves
// skyCubeTex 0 (procedural sky) when no faces resolve.
// Build a pos(3)+uv(2) GL mesh from a resolved StandardMesh + upload each part's EMBEDDED texture; returns the VAO and
// a per-part (offset, count, texId) table. Used for the real skybox + cloud meshes. Textures upload at NATIVE size (no
// cap) so hi-res skyboxes show full resolution. `center` re-centres the mesh on its bbox so the camera (placed at the
// mesh origin) sits inside it regardless of how the box was modelled.
unsafe (uint vao, (int Off, int Count, uint Tex)[] parts) BuildSkyMeshVao(MeshLibrary.Mesh m, bool center)
{
    var pos = m.Positions; var uvs = m.Uvs;
    Vector3 c = Vector3.Zero;
    if (center && pos.Length > 0)
    {
        Vector3 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var p in pos) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
        c = (mn + mx) * 0.5f;
    }
    var verts = new float[pos.Length * 5];
    for (int i = 0; i < pos.Length; i++)
    {
        var p = pos[i] - c; var uv = i < uvs.Length ? uvs[i] : default;
        int o = i * 5;
        verts[o] = p.X; verts[o + 1] = p.Y; verts[o + 2] = p.Z; verts[o + 3] = uv.X; verts[o + 4] = uv.Y;
    }
    var allIdx = new System.Collections.Generic.List<uint>();
    var parts = new System.Collections.Generic.List<(int, int, uint)>();
    foreach (var part in m.Parts)
    {
        int off = allIdx.Count;
        foreach (var ix in part.Indices) allIdx.Add((uint)ix);
        uint tex = 0;
        if (part.Texture is { } bmp && bmp.Width > 0 && bmp.Height > 0)
        {
            tex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, tex);
            fixed (byte* pp = bmp.Rgba)
                gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)bmp.Width, (uint)bmp.Height, 0,
                              PixelFormat.Rgba, PixelType.UnsignedByte, pp);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.GenerateMipmap(TextureTarget.Texture2D);
        }
        parts.Add((off, part.Indices.Length, tex));
    }
    uint vao = gl.GenVertexArray();
    gl.BindVertexArray(vao);
    uint vbo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
    gl.BufferData<float>(BufferTargetARB.ArrayBuffer, verts, BufferUsageARB.StaticDraw);
    uint stride = 5 * (uint)sizeof(float);
    gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0); gl.EnableVertexAttribArray(0);
    gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float))); gl.EnableVertexAttribArray(1);
    uint ebo = gl.GenBuffer();
    gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
    gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, allIdx.ToArray(), BufferUsageARB.StaticDraw);
    gl.BindVertexArray(0);
    return (vao, parts.ToArray());
}

// Resolve + upload the level's real skybox mesh (env.SkyBoxMesh, e.g. Sky_Bocage_m1). The mesh's own .rs shaders carry
// the real face-texture names, so this works for any custom skybox regardless of texture naming (unlike the _01..06
// cubemap name-guess). Drawn instead of the procedural gradient when it resolves.
void LoadSkyboxMesh()
{
    skyMeshOk = false;
    if (meshLib is null || string.IsNullOrEmpty(env?.SkyBoxMesh)) return;
    MeshLibrary.Mesh m;
    if ((!meshLib.TryGet(env.SkyBoxMesh, out m) || m.Positions.Length == 0)
        && (!meshLib.TryGetRenderMesh(env.SkyBoxMesh, out m) || m.Positions.Length == 0)) return;
    var (vao, parts) = BuildSkyMeshVao(m, center: true);
    skyMeshVao = vao; skyMeshParts = parts; skyMeshOk = true;
    skyMeshTexNames = m.Parts.Select(p => p.TextureName).ToArray();   // .rs refs, for the skybox face editor
    int sktx = 0; foreach (var pt in parts) if (pt.Tex != 0) sktx++;
    Console.WriteLine($"Skybox mesh '{env.SkyBoxMesh}': {m.Positions.Length} verts, {parts.Length} part(s), {sktx} textured.");
}

// Re-apply the user's image assignments onto the (freshly reloaded) sky mesh parts, so reverting ONE face
// doesn't lose the preview of the others.
void ReapplySkyFacePreviews()
{
    foreach (var kv in skyFaceAssign)
        if (kv.Value.Kind == "img" && kv.Key < skyMeshParts.Length && LoadImageAsTexture(kv.Value.Path) is { } t)
            skyMeshParts[kv.Key] = (skyMeshParts[kv.Key].Off, skyMeshParts[kv.Key].Count, UploadTexture(t));
}

// The pieces a skybox-face save ships, as level-relative entries. Image faces become same-named .dds INSIDE the
// level (the engine prefers the level's copy over the archive one - the mechanism custom maps already use for
// hi-res skyboxes). A .bik face rewrites that material's texture line in an override .rs shipped level-side,
// pointing at the movie under the mod's Movies folder - the Refractor texture loader plays Bink paths (the
// GCMOD/EoD movie-screen trick, 1000+ uses in the wild). The .bik itself is copied beside the game when the
// install is known.
List<(string RelPath, byte[] Bytes)> SkyFacePieces()
{
    var outp = new List<(string RelPath, byte[] Bytes)>();
    if (skyFaceAssign.Count == 0 || meshLib is null || string.IsNullOrEmpty(env?.SkyBoxMesh)) return outp;
    bool haveRs = meshLib.TryGetRsText(env.SkyBoxMesh, out var rsEntry, out var rsText);
    bool rsModified = false;
    foreach (var kv in skyFaceAssign)
    {
        var texRef = kv.Key < skyMeshTexNames.Length ? skyMeshTexNames[kv.Key] : null;
        if (string.IsNullOrEmpty(texRef)) continue;
        if (kv.Value.Kind == "img")
        {
            var timg = LoadImageAsTexture(kv.Value.Path);
            if (timg is null) { Console.WriteLine($"   skybox face: could not read {kv.Value.Path}"); continue; }
            var leaf = texRef.Replace('\\', '/'); leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
            outp.Add(($"Texture/{leaf}.dds", DdsTexture.EncodeUncompressed(timg)));
        }
        else if (haveRs)
        {
            string movieRef = CopyBikToMovies(kv.Value.Path);
            var quoted = "\"" + texRef + "\"";
            int at = rsText.IndexOf(quoted, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                rsText = rsText[..at] + "\"" + movieRef + "\"" + rsText[(at + quoted.Length)..];
                rsModified = true;
            }
            else Console.WriteLine($"   skybox face: texture ref '{texRef}' not found in {rsEntry} - .bik face skipped");
        }
    }
    if (rsModified)
    {
        var leaf = rsEntry.Replace('\\', '/'); leaf = leaf[(leaf.LastIndexOf('/') + 1)..];
        outp.Add(($"StandardMesh/{leaf}", System.Text.Encoding.Latin1.GetBytes(rsText)));
    }
    return outp;
}

// Copy a .bik to <gameRoot>\Mods\<Mod>\Movies so the engine's Bink texture loader finds it (movies load loose
// from disk, not from archives - raised_fist ships movies\background.bik the same way). Returns the texture
// reference to write into the .rs; falls back to a relative Movies/ ref + a toast when no install is found.
string CopyBikToMovies(string bikPath)
{
    var leaf = Path.GetFileName(bikPath);
    try
    {
        var anchor = levelDir is not null && LevelArchive.IsRfa(levelDir) ? Path.GetDirectoryName(Path.GetFullPath(levelDir))
                   : levelDir is not null ? Path.GetFullPath(levelDir)
                   : rfaList.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(rfaList[0])) : null;
        for (var d = anchor is null ? null : new DirectoryInfo(anchor); d?.Parent is not null; d = d.Parent)
            if (d.Parent.Name.Equals("Mods", StringComparison.OrdinalIgnoreCase))
            {
                var dest = Path.Combine(d.FullName, "Movies");
                Directory.CreateDirectory(dest);
                File.Copy(bikPath, Path.Combine(dest, leaf), overwrite: true);
                Console.WriteLine($"   Skybox movie -> {Path.Combine(dest, leaf)}");
                return $"Mods/{d.Name}/Movies/{leaf}";
            }
    }
    catch (Exception ex) { Console.WriteLine($"   Could not copy the .bik beside the game: {ex.Message}"); }
    Toast(Loc.T("Place the .bik in your mod's Movies folder yourself (game install not found)."));
    return $"Movies/{leaf}";
}

// Resolve + upload the level's real cloud mesh (env.CloudMeshFile, e.g. the level-local 'cloud'). Drawn per cloud layer
// with that layer's scroll/blend, instead of the procedural cloud overlay (disabled when this resolves).
void LoadCloudMesh()
{
    cloudMeshOk = false;
    if (meshLib is null || env is null || env.Clouds.Count == 0) return;
    var name = string.IsNullOrEmpty(env.CloudMeshFile) ? "cloud" : env.CloudMeshFile;
    MeshLibrary.Mesh m;
    if ((!meshLib.TryGet(name, out m) || m.Positions.Length == 0)
        && (!meshLib.TryGetRenderMesh(name, out m) || m.Positions.Length == 0)) return;
    var (vao, parts) = BuildSkyMeshVao(m, center: false);
    cloudMeshVao = vao; cloudMeshParts = parts; cloudMeshOk = true;
    cloudsOn = false;   // render the REAL cloud mesh -> turn off the procedural overlay (avoid double clouds)
    int cltx = 0; foreach (var pt in parts) if (pt.Tex != 0) cltx++;
    Console.WriteLine($"Cloud mesh '{name}': {m.Positions.Length} verts, {parts.Length} part(s), {cltx} textured.");
}

// Draw a skybox/cloud mesh centred on the camera, rotated by the level's sky angle, pinned to the far plane (depth off,
// depth-write off, cull off so the box interior shows). opaque=true for the skybox (alpha forced 1 — DXT1 1-bit alpha
// would otherwise punch holes); opaque=false alpha-blends the cloud layers. scroll animates cloud UVs.
unsafe void DrawSkyMesh(uint vao, (int Off, int Count, uint Tex)[] parts, Vector2 scroll, bool opaque)
{
    if (vao == 0 || parts.Length == 0 || skyMeshProg == 0) return;
    bool cull = gl.IsEnabled(EnableCap.CullFace);
    gl.UseProgram(skyMeshProg);
    gl.Disable(EnableCap.DepthTest);
    gl.DepthMask(false);
    gl.Disable(EnableCap.CullFace);
    if (opaque) gl.Disable(EnableCap.Blend);
    else { gl.Enable(EnableCap.Blend); gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha); }
    float rot = ((env?.SkyRotationAngle ?? 0f) + skyRotDeg) * MathF.PI / 180f;
    var model = Matrix4x4.CreateRotationY(rot) * Matrix4x4.CreateTranslation(cam.Position);
    var mvpM = model * cam.ViewProjection;
    gl.UniformMatrix4(uSMmvp, 1, false, (float*)&mvpM);
    gl.Uniform2(uSMscroll, scroll.X, scroll.Y);
    gl.Uniform1(uSMpin, 1);
    gl.Uniform1(uSMopaque, opaque ? 1 : 0);
    gl.Uniform4(uSMtint, 1f, 1f, 1f, 1f);
    gl.ActiveTexture(TextureUnit.Texture0);
    gl.BindVertexArray(vao);
    foreach (var (off, count, tex) in parts)
    {
        gl.Uniform1(uSMhasTex, tex != 0 ? 1 : 0);
        if (tex != 0) { gl.BindTexture(TextureTarget.Texture2D, tex); gl.Uniform1(uSMtex, 0); }
        gl.DrawElements(PrimitiveType.Triangles, (uint)count, DrawElementsType.UnsignedInt, (void*)((nint)off * sizeof(uint)));
    }
    gl.BindVertexArray(0);
    if (!opaque) gl.Disable(EnableCap.Blend);
    gl.DepthMask(true);
    gl.Enable(EnableCap.DepthTest);
    if (cull) gl.Enable(EnableCap.CullFace);
}

unsafe void LoadSkyCubemap()
{
    if (meshLib?.Textures is null) return;
    // SKYBOX faces resolve from the level's OWN archive(s) FIRST (a map ships override faces in Texture/AltTex that must
    // beat base duplicates — e.g. Immersed's hi-res underwater Sky_Bocage_0N over the base 512px daytime faces) then the
    // global lib. This is SURGICAL — done only for the skybox so it does NOT shadow object/tree textures (a global
    // level-first order dropped tree leaves).
    var levelTex = rfaList.Length > 0 ? TextureLibrary.Open(rfaList.Where(File.Exists).ToArray()) : null;
    Texture2D? RFace(string n) => levelTex?.Resolve(n) ?? meshLib.Textures.Resolve(n);
    string? baseName = null;
    if (!string.IsNullOrEmpty(env?.SkyBoxMesh))
    {
        var stem = System.Text.RegularExpressions.Regex.Replace(env.SkyBoxMesh, @"_m\d+$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (RFace(stem + "_01") is not null) baseName = stem;
    }
    if (baseName is null && RFace("env_default_01") is not null) baseName = "env_default";
    if (baseName is null) return;

    var faces = new Texture2D?[6];
    for (int i = 0; i < 6; i++) faces[i] = RFace($"{baseName}_0{i + 1}");
    if (faces.Any(f => f is null)) return;

    // A cubemap is INCOMPLETE (every sample returns black) unless all 6 faces share one size. BFV's down face is
    // often tiny - Sky_OI_06 is 32x32 vs 512 for the rest - so upscale every face to the largest before upload.
    BuildSkyCubemapFromFaces(faces, $"{baseName}_0N");
}

// Build the GL cube-map from 6 faces of ANY (power-of-2) sizes: upscale all to the largest (a cube-map needs one
// size per face), clamp to the GPU limit, upload in GL face order. Shared by the level load + custom import.
// High-res faces (e.g. 2048) upload at native size, so a hi-res custom skybox shows at full resolution.
unsafe void BuildSkyCubemapFromFaces(Texture2D?[] faces, string label)
{
    if (faces.Length < 6 || faces.Take(6).Any(f => f is null)) return;
    int sz = 0; for (int i = 0; i < 6; i++) sz = Math.Max(sz, Math.Max(faces[i]!.Width, faces[i]!.Height));
    Span<int> mcs = stackalloc int[1]; gl.GetInteger(GLEnum.MaxCubeMapTextureSize, mcs);
    if (mcs[0] > 0) sz = Math.Min(sz, mcs[0]);
    Texture2D Fit(Texture2D s)
    {
        if (s.Width == sz && s.Height == sz) return s;
        var rgba = new byte[sz * sz * 4];
        for (int y = 0; y < sz; y++)
            for (int x = 0; x < sz; x++)
            {
                var c = s.SampleRGBA((x + 0.5f) / sz, (y + 0.5f) / sz);
                int o = (y * sz + x) * 4;
                rgba[o] = (byte)(c.X * 255f); rgba[o + 1] = (byte)(c.Y * 255f); rgba[o + 2] = (byte)(c.Z * 255f); rgba[o + 3] = (byte)(c.W * 255f);
            }
        return new Texture2D(sz, sz, rgba);
    }
    var fit = new Texture2D[6]; for (int i = 0; i < 6; i++) fit[i] = Fit(faces[i]!);
    if (skyCubeTex != 0) { gl.DeleteTexture(skyCubeTex); skyCubeTex = 0; }
    skyCubeTex = gl.GenTexture();
    gl.BindTexture(TextureTarget.TextureCubeMap, skyCubeTex);
    int[] order = { 1, 3, 4, 5, 0, 2 };   // GL +X,-X,+Y,-Y,+Z,-Z  <-  faces _02,_04,_05,_06,_01,_03 (06 = down)
    for (int i = 0; i < 6; i++)
    {
        var f = fit[order[i]];
        fixed (byte* p = f.Rgba)
            gl.TexImage2D(TextureTarget.TextureCubeMapPositiveX + i, 0, InternalFormat.Rgba8, (uint)f.Width, (uint)f.Height, 0,
                          PixelFormat.Rgba, PixelType.UnsignedByte, p);
    }
    gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
    gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge);
    Console.WriteLine($"Skybox cube-map '{label}': {sz}px faces.");
}

// Import a custom skybox: a folder with 6 faces named *_01 .. *_06 (.dds/.tga/.bmp/.png), the BF Sky_X_0N order.
void ImportSkybox()
{
    var dir = Picker.Folder("Choose a folder with 6 skybox faces named *_01 .. *_06", null);
    if (dir is null) return;
    var faces = new Texture2D?[6];
    for (int i = 0; i < 6; i++)
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (Path.GetFileNameWithoutExtension(f).EndsWith($"_0{i + 1}", StringComparison.OrdinalIgnoreCase)
                && ext is ".dds" or ".tga" or ".bmp" or ".png" or ".jpg")
            { faces[i] = LoadImageAsTexture(f); break; }
        }
    int got = 0; for (int i = 0; i < 6; i++) if (faces[i] is not null) got++;
    if (got < 6) { Toast($"Found {got}/6 faces (need images named *_01..*_06). Skybox not changed."); return; }
    BuildSkyCubemapFromFaces(faces, Path.GetFileName(dir));
    skyUseCubemap = true;
    Toast($"Imported custom skybox '{Path.GetFileName(dir)}'.");
}

unsafe uint UploadTexture(Texture2D t)
{
    uint id = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, id);
    fixed (byte* p = t.Rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)t.Width, (uint)t.Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.GenerateMipmap(TextureTarget.Texture2D);
    return id;
}

// Same upload but REPEAT-wrapped + mipmapped - for tiling water layers / normal maps.
unsafe uint UploadTiledTexture(Texture2D t)
{
    uint id = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, id);
    fixed (byte* p = t.Rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)t.Width, (uint)t.Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.GenerateMipmap(TextureTarget.Texture2D);
    return id;
}

// Resolve the level's water.texLayer1/2 + normalMap (Init.con) via the object texture library and upload them as tiling
// GL textures, so the water plane renders the game's scrolling textured water instead of flat colour. Falls back to the
// procedural look (haveWaterTex=false) when the level configures no water textures or they aren't in any loaded archive
// (base BF1942 maps reference engine-built-in water07/08 that aren't shipped in the .rfa). Called on load + on import.
void InitWaterTextures()
{
    // free any previous
    if (waterTex1 != 0) { gl.DeleteTexture(waterTex1); waterTex1 = 0; }
    if (waterTex2 != 0) { gl.DeleteTexture(waterTex2); waterTex2 = 0; }
    if (waterNorm != 0) { gl.DeleteTexture(waterNorm); waterNorm = 0; }
    haveWaterTex = false;
    if (env is null || !env.HasWaterTextures || meshLib?.Textures is null) return;
    var lib = meshLib.Textures;
    Texture2D? Res(string? n) => string.IsNullOrEmpty(n) ? null : lib.Resolve(n);
    var t1 = Res(env.WaterTexLayer1) ?? Res(env.WaterBaseTex);
    var t2 = Res(env.WaterTexLayer2) ?? t1;
    var tn = Res(env.WaterNormalMap);
    var primary = t1 ?? tn;
    if (primary is null) return;   // nothing resolved -> keep procedural water
    waterTex1 = UploadTiledTexture(t1 ?? primary);
    waterTex2 = UploadTiledTexture(t2 ?? primary);
    waterNorm = UploadTiledTexture(tn ?? primary);
    haveWaterTex = true;
    Console.WriteLine($"Water textures: layer1={(t1 is not null ? env.WaterTexLayer1 : "(miss)")}, " +
                      $"layer2={(t2 is not null ? env.WaterTexLayer2 : "(miss)")}, normal={(tn is not null ? env.WaterNormalMap : "(miss)")} -> textured water ON.");
}

// Build a tileable procedural cloud-density texture (multi-octave value noise + a coverage curve), grayscale RGBA,
// REPEAT-wrapped, for the animated cloud-layer overlay. Built once; the shader scrolls its UVs over time.
unsafe uint BuildCloudTexture(int size)
{
    var rng = new System.Random(1337);
    int[] periods = { 4, 8, 16, 32 };
    float[] amps = { 0.5f, 0.27f, 0.15f, 0.08f };
    var lats = new float[periods.Length][];
    for (int o = 0; o < periods.Length; o++)
    { int P = periods[o]; var l = new float[P * P]; for (int i = 0; i < l.Length; i++) l[i] = (float)rng.NextDouble(); lats[o] = l; }
    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    float ValueNoise(float u, float v, int P, float[] lat)
    {
        float x = u * P, y = v * P;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        int X0 = ((x0 % P) + P) % P, Y0 = ((y0 % P) + P) % P, X1 = (X0 + 1) % P, Y1 = (Y0 + 1) % P;
        float sx = fx * fx * (3 - 2 * fx), sy = fy * fy * (3 - 2 * fy);
        return Lerp(Lerp(lat[Y0 * P + X0], lat[Y0 * P + X1], sx), Lerp(lat[Y1 * P + X0], lat[Y1 * P + X1], sx), sy);
    }
    var rgba = new byte[size * size * 4];
    for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float u = x / (float)size, v = y / (float)size, f = 0f;
            for (int o = 0; o < periods.Length; o++) f += amps[o] * ValueNoise(u, v, periods[o], lats[o]);
            f = Math.Clamp(f, 0f, 1f);
            float s = Math.Clamp((f - 0.45f) / 0.40f, 0f, 1f); s = s * s * (3 - 2 * s);   // smoothstep coverage -> puffy gaps
            byte b = (byte)Math.Clamp((int)(s * 255f), 0, 255);
            int i = (y * size + x) * 4; rgba[i] = b; rgba[i + 1] = b; rgba[i + 2] = b; rgba[i + 3] = b;
        }
    uint id = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, id);
    fixed (byte* p = rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)size, (uint)size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.GenerateMipmap(TextureTarget.Texture2D);
    return id;
}

// Seed the cloud edit fields from the level's env (so a level that already has a Cloud block shows it).
void LoadCloudsFromEnv()
{
    cloudsOn = false; cloudsDirty = false;
    if (env is null || env.Clouds.Count == 0) return;
    var c = env.Clouds[0];
    cloudsOn = true;
    cloudSpeedX = c.SpeedX; cloudSpeedY = c.SpeedY; cloudHeight = c.Height;
    cloudScale = Math.Clamp(c.TexScale / 16f, 0.1f, 3f);   // map TexScale -> the dome projection scale
}

// Write the cloud edit fields back into env as a single layer (the saved source of truth), or clear them when off.
void SaveCloudsToEnv()
{
    if (env is null) return;
    env.Clouds.Clear();
    if (cloudsOn)
        env.Clouds.Add(new RefractorForge.Formats.Terrain.EnvironmentSettings.CloudLayer
        {
            Name = "cloud_0", SpeedX = cloudSpeedX, SpeedY = cloudSpeedY, Height = cloudHeight,
            TexScale = Math.Clamp(cloudScale * 16f, 1f, 64f),
        });
}

// Upload one painted rectangle of the CPU atlas to mip 0 of the GPU terrain texture (live, no mip regen).
// Uses UNPACK_ROW_LENGTH/SKIP so the sub-rect is read straight out of the full atlas buffer.
unsafe void UploadAtlasRect(int x, int y, int w, int h)
{
    if (atlasCpu is null || terrainTexId == 0 || w <= 0 || h <= 0) return;
    gl.BindTexture(TextureTarget.Texture2D, terrainTexId);
    gl.PixelStore(PixelStoreParameter.UnpackRowLength, atlasCpu.Width);
    gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, x);
    gl.PixelStore(PixelStoreParameter.UnpackSkipRows, y);
    fixed (byte* p = atlasCpu.Rgba)
        gl.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
    gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
    gl.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
}

// Same, plus a full mip regen - for discrete events (stroke finish / undo / redo) where distant mips must refresh.
void UploadAtlasRectMips(int x, int y, int w, int h)
{
    UploadAtlasRect(x, y, w, h);
    if (terrainTexId != 0) { gl.BindTexture(TextureTarget.Texture2D, terrainTexId); gl.GenerateMipmap(TextureTarget.Texture2D); }
}

// ---- AI Path navmap painting (mapper 5). aiNav is the selected vehicle's WORLD-GRID finest map (0x00 pass /
// 0xFF block). Seeding order: the level's EXISTING navmap first (retail/hand-tuned maps carry designer intent a
// regeneration would destroy), terrain generation only when the level ships none. The brush stamps it, the
// terrain shader drapes it (uShowMat==3), and Ctrl+S downsamples it to the vehicle's level set + writes both
// the 8Bit and compressed forms. ----

// Read one Pathfinding file by leaf name (e.g. "Tank0Level0Map8Bit.raw") from wherever the level lives: the
// folder's Pathfinding dir, or the mounted .rfa chain (LAST archive wins — patches override the base).
byte[]? ReadLevelNavFile(string leaf)
{
    try
    {
        if (levelDir is not null && System.IO.Directory.Exists(levelDir))
        {
            var navDir = System.IO.Directory.EnumerateDirectories(levelDir, "Pathfinding", System.IO.SearchOption.AllDirectories).FirstOrDefault();
            if (navDir is not null)
            {
                var f = System.IO.Path.Combine(navDir, leaf);
                if (System.IO.File.Exists(f)) return System.IO.File.ReadAllBytes(f);
            }
            return null;
        }
        for (int i = rfaList.Length - 1; i >= 0; i--)
        {
            if (!File.Exists(rfaList[i])) continue;
            var a = new RefractorForge.Formats.Rfa.RefractorFlatArchive(rfaList[i]);
            var e = a.Entries.FirstOrDefault(x => x.Name.Replace('\\', '/').EndsWith("/Pathfinding/" + leaf, StringComparison.OrdinalIgnoreCase));
            if (e is not null) return a.Read(e);
        }
    }
    catch { }
    return null;
}

// Push the terrain-atlas UV correction to the shader. Cheap, so it is simply re-sent whenever a slider moves.
void ApplyTerrainUv()
{
    if (terrainProg == 0 || uTerUvScaleL < 0) return;
    gl.UseProgram(terrainProg);
    gl.Uniform2(uTerUvScaleL, terUvScale, terUvScale);
    gl.Uniform2(uTerUvOffsetL, terUvOffX, terUvOffY);
}

// ---- Battlecraft-style camera ----------------------------------------------------------------------------------

/// Switch camera mode. Position and orientation are untouched, so the toggle never moves the view - only how
/// W/S is interpreted changes (true view vector vs flattened onto XZ).
void SetGroundCam(bool on)
{
    groundCam = on;
    AppPrefs.GroundCamera = on;
    AppPrefs.Save();
    Toast(on ? Loc.T("Battlecraft camera on: W/S fly where you look (F7).") : Loc.T("Fly camera on: W/S stay level (F7)."));
}

void EnsureAiNav()
{
    if (heightmap is null) return;
    int want = RefractorForge.Formats.Terrain.SearchMapGenerator.FinestSide(cfg.MaterialSize);
    int veh = Math.Clamp(aiPathVeh, 0, RefractorForge.Formats.Terrain.SearchMapParams.Standard.Count - 1);
    if (aiNav is not null && aiNavVehLoaded == veh && aiNavSide == want) return;   // active view already current
    // Swap to this vehicle's buffer. Reuse the cached buffer (preserving unsaved edits); seed only the first time
    // for a vehicle, or when the map size changed (a stale-side buffer is re-seeded).
    var buf = aiNavBufs[veh];
    if (buf is null || buf.Length != want * want)
    {
        var p = RefractorForge.Formats.Terrain.SearchMapParams.Standard[veh];
        // 1) the level's own navmap — what the game actually pathfinds on today.
        buf = RefractorForge.Formats.Terrain.PathmapRaw.LoadVehicleWorldGrid(ReadLevelNavFile, p, want);
        if (buf is not null)
            Console.WriteLine($"AI Path: loaded the level's existing {p.Name} navmap.");
        else
        {
            // 2) no existing map (fresh/new level) — generate the terrain-derived base as before.
            var foots = (meshLib is not null && so is not null) ? RefractorForge.Render.SearchMapBuilder.Footprints(so.Objects, meshLib) : null;
            buf = RefractorForge.Formats.Terrain.SearchMapGenerator.GenerateGrid(cfg, heightmap, p, 0, foots);   // level 0 = finest, world-grid
            Console.WriteLine($"AI Path: level has no {p.Name} navmap - generated one from the terrain.");
        }
        aiNavBufs[veh] = buf;
        aiNavBufDirty[veh] = false;
    }
    aiNav = buf;
    aiNavSide = want;
    aiNavVehLoaded = veh;
    aiNavDirty = aiNavBufDirty[veh];
    aiNavTexDirty = true;
}

// Called by an AiNavStrokeCommand on apply/undo/redo: mark the (possibly background) vehicle's buffer dirty, and
// if it is the one currently shown, refresh the active proxy + overlay texture.
void AiNavStrokeChanged(int veh)
{
    if (veh >= 0 && veh < aiNavBufDirty.Length) aiNavBufDirty[veh] = true;
    if (veh == aiNavVehLoaded) { aiNavDirty = true; aiNavTexDirty = true; }
}

// Every painted vehicle's navmap as (name, bytes) for an .rfa save: 8Bit + compressed per level set. Names are
// the bare leaf (e.g. Tank0Level0Map.raw); LevelSaver.FindEntry matches them to the archive's Pathfinding/ entries
// (an .rfa whose base already ships navmaps updates them; one without them is a nav no-op, like sounds/tiles).
List<(string Name, byte[] Bytes)> DirtyNavFiles()
{
    var list = new List<(string Name, byte[] Bytes)>();
    for (int v = 0; v < aiNavBufs.Length; v++)
        if (aiNavBufDirty[v] && aiNavBufs[v] is not null)
        {
            var vp = RefractorForge.Formats.Terrain.SearchMapParams.Standard[Math.Clamp(v, 0, RefractorForge.Formats.Terrain.SearchMapParams.Standard.Count - 1)];
            int side = (int)Math.Round(Math.Sqrt(aiNavBufs[v]!.Length));
            foreach (var (file, data) in RefractorForge.Formats.Terrain.SearchMapGenerator.EncodeVehicleLevels(vp, aiNavBufs[v]!, side))
                list.Add((file, data));
        }
    return list;
}

void AiNavDab(float wx, float wz)
{
    if (aiNav is null || aiNavSide <= 0) return;
    float mpc = (float)cfg.WorldSize / aiNavSide;   // metres per nav cell
    int cx = (int)(wx / mpc), cy = (int)(wz / mpc);
    int rc = Math.Max(0, (int)MathF.Round(brushRadius / mpc));
    int rc2 = rc * rc;
    byte val = aiPathBlock ? (byte)0xFF : (byte)0x00;
    int minx = aiNavSide, miny = aiNavSide, maxx = -1, maxy = -1;
    for (int dy = -rc; dy <= rc; dy++)
        for (int dx = -rc; dx <= rc; dx++)
        {
            if (!squareBrush && dx * dx + dy * dy > rc2) continue;
            int x = cx + dx, y = cy + dy;
            if (x < 0 || y < 0 || x >= aiNavSide || y >= aiNavSide) continue;
            int i = y * aiNavSide + x;
            if (aiNav[i] != val) { aiNavStroke?.Touch(x, y); aiNav[i] = val; if (x < minx) minx = x; if (y < miny) miny = y; if (x > maxx) maxx = x; if (y > maxy) maxy = y; }
        }
    if (maxx >= minx)
    {
        aiNavDirty = true;
        if (aiNavVehLoaded >= 0 && aiNavVehLoaded < aiNavBufDirty.Length) aiNavBufDirty[aiNavVehLoaded] = true;
        if (aiNavTexId != 0) UploadAiNavRect(minx, miny, maxx - minx + 1, maxy - miny + 1);
        else aiNavTexDirty = true;
    }
}

unsafe void UploadAiNavTexture()
{
    if (aiNav is null || aiNavSide <= 0) return;
    if (aiNavTexId == 0) aiNavTexId = gl.GenTexture();
    gl.ActiveTexture(TextureUnit.Texture1);
    gl.BindTexture(TextureTarget.Texture2D, aiNavTexId);
    gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    fixed (byte* p = aiNav)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)aiNavSide, (uint)aiNavSide, 0, PixelFormat.Red, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    gl.ActiveTexture(TextureUnit.Texture0);
    aiNavTexDirty = false;
}

unsafe void UploadAiNavRect(int x, int y, int w, int h)
{
    if (aiNav is null || aiNavTexId == 0 || w <= 0 || h <= 0) return;
    gl.ActiveTexture(TextureUnit.Texture1);
    gl.BindTexture(TextureTarget.Texture2D, aiNavTexId);
    gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    gl.PixelStore(PixelStoreParameter.UnpackRowLength, aiNavSide);
    gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, x);
    gl.PixelStore(PixelStoreParameter.UnpackSkipRows, y);
    fixed (byte* p = aiNav)
        gl.TexSubImage2D(TextureTarget.Texture2D, 0, x, y, (uint)w, (uint)h, PixelFormat.Red, PixelType.UnsignedByte, p);
    gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
    gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
    gl.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
    gl.ActiveTexture(TextureUnit.Texture0);
}

// (Re)render the top-down minimap (terrain colour + water + hill-shade) and upload it for the Mini-Map panel.
void BuildMinimap()
{
    if (heightmap is null) return;
    try
    {
        var img = Minimap.Render(256, heightmap, cfg, terrainTex, materialMap);
        if (minimapTexId != 0) gl.DeleteTexture(minimapTexId);
        minimapTexId = UploadTexture(img);
    }
    catch { }
}

// Detail texture upload: identical to UploadTexture but REPEAT-wrapped so it tiles across the terrain.
unsafe uint UploadDetailTexture(Texture2D t)
{
    uint id = gl.GenTexture();
    gl.BindTexture(TextureTarget.Texture2D, id);
    fixed (byte* p = t.Rgba)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)t.Width, (uint)t.Height, 0,
                      PixelFormat.Rgba, PixelType.UnsignedByte, p);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
    gl.GenerateMipmap(TextureTarget.Texture2D);
    return id;
}

// (Re)upload an index map as a single-channel R8 texture, sampled NEAREST so each cell reads its
// exact index. Called on load and after every paint stroke (cheap). The same texture slot serves the
// material map and either growth layer - whichever is the active paint target.
unsafe void UploadPaintTexture(MaterialMap? m)
{
    if (m is null) return;
    if (matTexId == 0) matTexId = gl.GenTexture();
    gl.ActiveTexture(TextureUnit.Texture1);
    gl.BindTexture(TextureTarget.Texture2D, matTexId);
    gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    fixed (byte* p = m.Samples)
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, (uint)m.Width, (uint)m.Height, 0,
                      PixelFormat.Red, PixelType.UnsignedByte, p);
    gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
    gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
    gl.ActiveTexture(TextureUnit.Texture0);
}

// Active paint target (Material / Undergrowth / Overgrowth) - painter, map, brush value, and overlay.
MaterialPainter? ActivePainter() => paintLayer == 1 ? underPainter : paintLayer == 2 ? overPainter : matPainter;
MaterialMap? ActivePaintMap() => paintLayer == 1 ? growth?.Under : paintLayer == 2 ? growth?.Over : materialMap;
byte ActivePaintValue() => paintLayer == 0 ? activeMaterial : activeFoliage;
void UploadActivePaintTexture() => UploadPaintTexture(ActivePaintMap());

uint BuildProgram(string vs, string fs)
{
    uint v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
    uint p = gl.CreateProgram(); gl.AttachShader(p, v); gl.AttachShader(p, f); gl.LinkProgram(p);
    gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int ok);
    if (ok == 0) throw new Exception("Link error: " + gl.GetProgramInfoLog(p));
    gl.DeleteShader(v); gl.DeleteShader(f);
    return p;
}

uint Compile(ShaderType type, string src)
{
    uint s = gl.CreateShader(type); gl.ShaderSource(s, src); gl.CompileShader(s);
    gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
    if (ok == 0) throw new Exception($"{type} compile error: " + gl.GetShaderInfoLog(s));
    return s;
}

static float[] ToFloats(Matrix4x4 m) => new[]
{
    m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24,
    m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44,
};


