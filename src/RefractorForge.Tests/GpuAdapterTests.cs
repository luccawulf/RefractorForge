using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// The GPU bake must only ever run on a dedicated card. These are real OpenGL renderer strings; the integrated ones
/// are the cases that matter most, because a machine with two GPUs runs the editor on the integrated one by default
/// and a sustained compute load there has already blue-screened the project's own test machine.
/// </summary>
public class GpuAdapterTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 3070/PCIe/SSE2")]
    [InlineData("NVIDIA GeForce GTX 1060 6GB/PCIe/SSE2")]
    [InlineData("Quadro RTX 4000/PCIe/SSE2")]
    [InlineData("AMD Radeon RX 6700 XT")]
    [InlineData("Radeon RX 580 Series")]
    [InlineData("AMD Radeon RX 7900M")]
    [InlineData("Radeon RX Vega 64")]
    [InlineData("AMD Radeon Pro W6800")]
    [InlineData("AMD Radeon R9 200 Series")]
    [InlineData("AMD Radeon R9 Fury Series")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics")]
    [InlineData("Intel(R) Arc(TM) B580 Graphics")]
    public void Dedicated_cards_are_recognised(string renderer) => Assert.True(GpuAdapter.LooksDiscrete(renderer));

    [Theory]
    [InlineData("AMD Radeon(TM) Graphics")]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics")]
    [InlineData("AMD Radeon(TM) RX Vega 11 Graphics")]     // a Ryzen APU, despite the "RX"
    [InlineData("AMD Radeon 780M Graphics")]
    [InlineData("AMD Radeon(TM) 890M Graphics")]
    [InlineData("AMD Radeon(TM) R7 Graphics")]               // a Kaveri APU, despite the "R7"
    [InlineData("Intel(R) UHD Graphics 630")]
    [InlineData("Intel(R) Iris(R) Xe Graphics")]
    [InlineData("Intel(R) Arc(TM) Graphics")]                // Meteor Lake's built-in GPU, despite the "Arc"
    [InlineData("GDI Generic")]
    [InlineData("Microsoft Basic Render Driver")]
    [InlineData("llvmpipe (LLVM 15.0.7, 256 bits)")]
    [InlineData("")]
    [InlineData(null)]
    public void Integrated_and_unknown_GPUs_are_refused(string? renderer) => Assert.False(GpuAdapter.LooksDiscrete(renderer));
}
