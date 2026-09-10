using System;
using System.Collections.Generic;
using System.IO;

namespace RefractorForge.Formats.Mesh;

/// <summary>The one place that knows which reader a model file needs. OBJ and <c>.3ds</c> are read here; everything
/// Blender can open is converted to OBJ first by <see cref="BlenderBridge"/> and then comes through the OBJ door.</summary>
public static class ModelFile
{
    /// <summary>A model as read, with any materials the file itself carried (a <c>.3ds</c> keeps them inside; an
    /// OBJ points at <c>.mtl</c> files the caller resolves beside it).</summary>
    public sealed record Loaded(ObjMesh Mesh, Dictionary<string, ObjMaterial>? Materials, bool ZUpMaxConvention);

    public static bool IsThreeDs(string path) => ThreeDsMesh.Is(path);

    public static Loaded Load(string path)
    {
        if (ThreeDsMesh.Is(path))
        {
            var r = ThreeDsMesh.Load(path);
            return new Loaded(r.Mesh, r.Materials, ZUpMaxConvention: true);
        }
        return new Loaded(ObjMesh.Load(path), null, ZUpMaxConvention: false);
    }
}
