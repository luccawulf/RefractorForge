using RefractorForge.Formats;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// A beta user reported the editor deleting its own installation, leaving only the two files the running process
/// still had open. The one operation that can do that is "Delete project from disk", which recursively deletes
/// whatever folder the project lives in - and nothing checked what that folder was. These pin the guard, because
/// the failure is unrecoverable and the report could not be reproduced on demand.
/// </summary>
public class PathSafetyTests
{
    [Fact]
    public void The_folder_the_application_runs_from_is_never_deletable()
    {
        var app = Path.Combine(Path.GetTempPath(), "rf_app_" + Guid.NewGuid().ToString("N")[..8]);
        Assert.NotNull(PathSafety.ProtectedReason(app, app));
        Assert.Contains("runs from", PathSafety.ProtectedReason(app, app));

        // An ancestor is just as fatal - deleting it takes the install with it.
        Assert.NotNull(PathSafety.ProtectedReason(Path.GetDirectoryName(app), app));

        // Trailing separators and mixed slashes must not slip past the comparison.
        Assert.NotNull(PathSafety.ProtectedReason(app + Path.DirectorySeparatorChar, app));
        Assert.NotNull(PathSafety.ProtectedReason(app.Replace('\\', '/'), app));
    }

    [Fact]
    public void Drive_roots_and_system_folders_are_refused()
    {
        var app = Path.Combine(Path.GetTempPath(), "rf_elsewhere");
        var root = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        Assert.NotNull(PathSafety.ProtectedReason(root, app));

        foreach (var f in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
                                  Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles })
        {
            var p = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(p)) Assert.NotNull(PathSafety.ProtectedReason(p, app));
        }

        Assert.NotNull(PathSafety.ProtectedReason("", app));
        Assert.NotNull(PathSafety.ProtectedReason(null, app));
    }

    [Fact]
    public void A_game_install_is_not_a_project_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_game_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var app = Path.Combine(Path.GetTempPath(), "rf_elsewhere");
            Assert.Null(PathSafety.ProtectedReason(dir, app));       // ordinary until the exe appears

            File.WriteAllBytes(Path.Combine(dir, "BF1942.exe"), new byte[] { 0x4D, 0x5A });
            Assert.Contains("game installation", PathSafety.ProtectedReason(dir, app));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void An_ordinary_project_folder_is_allowed_and_measured()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rf_proj_" + Guid.NewGuid().ToString("N")[..8], "MyMap");
        Directory.CreateDirectory(dir);
        try
        {
            var app = Path.Combine(Path.GetTempPath(), "rf_elsewhere");
            Assert.Null(PathSafety.ProtectedReason(dir, app));
            Assert.True(PathSafety.IsEmpty(dir));

            File.WriteAllBytes(Path.Combine(dir, "Heightmap.raw"), new byte[2048]);
            Directory.CreateDirectory(Path.Combine(dir, "Textures"));
            File.WriteAllBytes(Path.Combine(dir, "Textures", "t.dds"), new byte[1024]);

            Assert.False(PathSafety.IsEmpty(dir));
            var (files, bytes) = PathSafety.Measure(dir);
            Assert.Equal(2, files);
            Assert.Equal(3072, bytes);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(dir)!, true); } catch { } }
    }

    [Fact]
    public void A_sibling_folder_whose_name_merely_starts_the_same_is_not_protected()
    {
        // "…\RefractorForge2" must not be mistaken for a parent of "…\RefractorForge" by a plain StartsWith.
        var app = Path.Combine(Path.GetTempPath(), "rf_guard", "RefractorForge");
        Assert.Null(PathSafety.ProtectedReason(Path.Combine(Path.GetTempPath(), "rf_guard", "RefractorForge2"), app));
        Assert.Null(PathSafety.ProtectedReason(Path.Combine(Path.GetTempPath(), "rf_guard", "RefractorForgeMaps"), app));
    }
}
