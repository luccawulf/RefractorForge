using Xunit;

namespace RefractorForge.Tests;

/// <summary>A test that needs a game install (or other machine-local data). It is reported as SKIPPED, with the
/// reason, when none of the paths exist - instead of the old pattern of <c>if (!Directory.Exists(root)) continue;</c>
/// inside the test, which turned "no D:\ drive" into an all-green run nobody could tell apart from a real pass.</summary>
public sealed class InstallFactAttribute : FactAttribute
{
    public InstallFactAttribute(params string[] anyOfPaths)
    {
        if (!anyOfPaths.Any(p => Directory.Exists(p) || File.Exists(p)))
            Skip = "needs a local install: " + string.Join(" or ", anyOfPaths);
    }
}

/// <summary>Where the install-backed tests look. The clean installs are the retail baselines; the main installs
/// carry HD re-packs and hundreds of community levels.</summary>
public static class Installs
{
    public const string Bf1942Clean = @"D:\Games\EA GAMES\Battlefield 1942 Clean\Battlefield 1942";
    public const string BfvOriginal = @"D:\Games\EA GAMES\Battlefield Vietnam Original Files";
    public const string Bf1942CleanArchives = Bf1942Clean + @"\Mods\bf1942\Archives";
    public const string BfvOriginalArchives = BfvOriginal + @"\Mods\BfVietnam\Archives";
}
