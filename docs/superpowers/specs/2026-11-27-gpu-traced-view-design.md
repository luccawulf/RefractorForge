# Live traced light in the viewport (GPU) — design

## Intent

The Realistic Lighting window describes the light (sun, sky, lamps) and its preview is a click-to-render CPU picture.
The user wants to tune that light by looking, in real time, on the GPU: a view mode of the main viewport that shows
the traced light live, follows the camera and the sliders, and clears up while the camera is still.

## Goals / non-goals

- Goal: the same light the CPU preview (`LightPreview`) traces — sun through its disc, sky dome, two bounces,
  placed lamps with soft shadows, cut-out leaves and fences, the water sheet — drawn live in the main viewport.
- Goal: dedicated GPUs only. On an integrated GPU (refused since the iGPU bugcheck) the mode is not offered and the
  CPU preview stays.
- Non-goal: a live BAKE. The bake still writes lightmaps and tiles as before.
- Non-goal: hardware ray-tracing cores. OpenGL exposes none; this is software BVH traversal in a compute shader, as
  the Ultra lightmap bake already does on the user's RTX 4060. Hardware RT would need Vulkan interop — later.
- Anti-goal: anything that can hang the driver (TDR) or touch an iGPU.

## Approach (chosen)

Hybrid: the rasteriser supplies textures, a compute shader supplies light.

1. **Scene**: `LightScene.Build` (the CPU preview's scene, terrain + objects + cut-out masks) packed by a new
   `LiveTraceScene` into flat GPU arrays — the existing BVH layout plus a cut-out table and mask bits (7 SSBOs).
2. **G-buffer** (each frame, internal resolution): terrain and objects drawn by one small program into an FBO —
   object texture colour (alpha-tested, glass dropped) and distance from the camera.
3. **Compute** (`LiveTraceKernel`, one sample per pixel per frame): primary ray into the BVH (so geometry is exactly
   the CPU preview's), albedo from the G-buffer where it agrees with the hit (else the ground picture for terrain,
   the triangle's mean colour otherwise), then the estimator of `LightTracer.Gather` with one sun ray, one
   hemisphere path of up to two bounces, and the lamps of `LampLight.Display`. Accumulated in a buffer; reset when
   the camera, the light, the lamps or the scene change; stops after a sample cap.
4. **Composite**: albedo x ToDisplay(mean light), sky and water as `LightPreview` draws them, depth written so
   gizmos and markers still sit in the scene.

A C# copy of the kernel (`Render/LiveTraceKernel.cs`) is tested against `RayScene` and `LightTracer`; on first use
the viewer runs the GPU against it on a small view and refuses the mode if they disagree.

Rejected: GPU-render only the existing preview image (fast but not live); drive the game-formula viewport from the
sliders (live, but shadows and bounce would not change until a bake).

## Decided by the user

- Main viewport, as a view mode. Offered only on a capable (dedicated) card.

## Builder's calls

- Resolution Auto (full, dropping to half / quarter when a frame takes too long), Full, Half, Quarter.
- The scene is built when the mode is turned on; after a level edit the status says the trace is stale and offers
  Rebuild (building blocks the editor for a few seconds, as the CPU preview's build does).
- Hidden groups and growth are not in the trace (the CPU preview does not trace them either).
