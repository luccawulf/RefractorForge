using System.Text;
using RefractorForge.Formats.Mesh;
using RefractorForge.Formats.Rfa;
using RefractorForge.Render;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// <c>beginrem</c> ... <c>endrem</c> comments a region out for the engine, and retail vehicles use it: KettenKrad's
/// Physics.con comments out two front wheels (<c>KettenKradFrontWheelL/R</c>), the SBD's Objects.con a camera. The
/// assembler read straight through those blocks and drew parts the vehicle never has in game.
/// </summary>
public class MeshLibraryBlockCommentTests
{
    private const string Obj = "o wheel\nv 1 0 0\nv -1 0 0\nv -1 1 0\nvt 0 0\nvt 1 0\nvt 1 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n";

    [Fact]
    public void A_part_added_inside_beginrem_is_not_assembled()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_brem_" + Guid.NewGuid().ToString("N")[..8], "Map");
        try
        {
            Write(dir, "standardmesh/wheel.sm", StandardMeshWriter.Write(ObjMesh.Parse(Obj)));
            Write(dir, "standardmesh/ghost.sm", StandardMeshWriter.Write(ObjMesh.Parse(Obj)));
            Write(dir, "objects/Vehicles/Land/Kart/Geometries.con",
                  "GeometryTemplate.create StandardMesh Kart_Wheel\r\nGeometryTemplate.file wheel\r\n"
                + "beginrem\r\nGeometryTemplate.create StandardMesh Kart_Ghost\r\nGeometryTemplate.file ghost\r\nendrem\r\n");
            Write(dir, "objects/Vehicles/Land/Kart/Objects.con",
                  "ObjectTemplate.create PlayerControlObject Kart\r\n"
                + "ObjectTemplate.addTemplate KartWheel\r\n"
                + "ObjectTemplate.setPosition 1/0/0\r\n"
                + "beginrem\r\n"
                + "ObjectTemplate.addTemplate KartWheel\r\n"
                + "ObjectTemplate.setPosition -1/0/0\r\n"
                + "endrem\r\n"
                + "ObjectTemplate.addTemplate KartWheel\r\n"
                + "ObjectTemplate.setPosition 0/0/2\r\n"
                + "ObjectTemplate.create SimpleObject KartWheel\r\n"
                + "ObjectTemplate.geometry Kart_Wheel\r\n");
            var lib = MeshLibrary.Open(dir);
            Assert.True(lib.TryAssembleVehicle("Kart", out var parts));
            Assert.Equal(new[] { 1f, 0f }, parts.Select(p => p.Local.Translation.X));
            Assert.Equal(new[] { 0f, 2f }, parts.Select(p => p.Local.Translation.Z));
        }
        finally { Directory.Delete(Path.GetDirectoryName(dir)!, true); }
    }

    private static void Write(string dir, string rel, byte[] bytes)
    {
        var p = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, bytes);
    }

    private static void Write(string dir, string rel, string text) => Write(dir, rel, Encoding.ASCII.GetBytes(text));
}
