using System;
using System.Text.RegularExpressions;

namespace RefractorForge.Render;

/// <summary>
/// Which graphics card an OpenGL context is running on, judged from its renderer string.
///
/// <para>A lightmap bake is minutes of sustained compute. On a dedicated card that is what it is for; on an integrated
/// GPU it is slower than the processor it shares memory with, and on this project's own test machine a sustained load
/// on the AMD integrated GPU has already ended in a video-scheduler bugcheck (0x119). A machine with both runs a
/// program on the integrated one unless told otherwise, so the editor has to check which one it got rather than
/// assume. Anything not recognised as a dedicated card is treated as integrated - the cost of a wrong "no" is a bake
/// that runs on the CPU as it always has; the cost of a wrong "yes" can be a blue screen.</para>
/// </summary>
public static class GpuAdapter
{
    public static bool LooksDiscrete(string? renderer)
    {
        if (string.IsNullOrWhiteSpace(renderer)) return false;
        string r = " " + renderer.ToUpperInvariant().Replace("(TM)", "").Replace("(R)", "") + " ";

        if (r.Contains("NVIDIA") || r.Contains("GEFORCE") || r.Contains("QUADRO") || r.Contains(" RTX ")) return true;

        // Intel: the dedicated cards are Arc with a model number (A770, B580, Arc Pro A60). A bare "Arc Graphics" is
        // the GPU built into Meteor Lake and later processors, as UHD, Iris and Xe are in earlier ones.
        if (r.Contains("INTEL")) return Regex.IsMatch(r, @" ARC (PRO )?[AB]\d");

        if (r.Contains("RADEON") || r.Contains(" AMD ") || r.Contains(" ATI "))
        {
            // Vega is both: RX Vega 56/64 and Frontier are cards, "RX Vega 11" and "Vega 8" are Ryzen APUs.
            if (r.Contains("VEGA")) return r.Contains("VEGA 56") || r.Contains("VEGA 64") || r.Contains("FRONTIER");
            // Every AMD card since 2016 is an RX or a Pro; R7/R9 with a model number, Fury and Radeon VII are the older
            // ones. An APU reports "Radeon Graphics", "R7 Graphics" (Kaveri) or a model with an M ("Radeon 780M").
            return r.Contains(" RX ") || r.Contains("RADEON PRO") || r.Contains("FIREPRO") || r.Contains("RADEON VII")
                   || Regex.IsMatch(r, @" R[79] (\d|FURY|NANO)");
        }
        return false;   // software renderers (Microsoft Basic Render, GDI Generic, llvmpipe) and anything unknown
    }
}
