using System.Numerics;
using RefractorForge.Render;

// Particle-effect runtime types for the editor's effect preview (Program.cs is top-level statements, so named types
// can't live inline among the statements — they go here). One FxInstance2 per placed effect emitter; FxParticle2 is a
// single live billboard particle.
sealed class FxParticle2
{
    public Vector3 Pos;
    public Vector3 Vel;
    public float Age;
    public float Ttl;
    public float Size0;
    public float Size1;
    public float Rot;   // billboard rotation (radians), randomised per particle from the sprite's initRotation
}

sealed class FxInstance2
{
    public Vector3 World;                            // emitter world position
    public EffectsLibrary.EmitterDef Def = null!;   // texture / rate / velocity / size / blend / lod
    public uint Tex;                                 // uploaded GL texture for the particle
    public float Accum;                              // fractional spawn accumulator
    public readonly System.Collections.Generic.List<FxParticle2> Parts = new();
}
