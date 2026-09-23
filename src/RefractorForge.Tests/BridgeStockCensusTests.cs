using System.Text;
using RefractorBridge.Con;
using RefractorBridge.Oracle;
using Xunit;

namespace RefractorForge.Tests;

/// <summary>
/// Gates on the retail-Battlefield-Vietnam census - the oracle the executables cannot be.
///
/// Refractor registers properties PER CLASS and the exe string table never says which class exposes a name, so
/// the exe can be actively misleading: BfVietnam.exe carries <c>setTorque</c>, yet on the Engine class retail
/// uses <c>torque</c> and the set- form zero times. Only a census of shipped content settles it, and getting
/// one wrong fails silently - an unregistered property is discarded and the object keeps its default.
/// </summary>
public class BridgeStockCensusTests
{
    private const string BfvObjects = @"D:\Games\EA GAMES\Battlefield Vietnam\Mods\BfVietnam\Archives\objects.rfa";

    private static StockCensus Synthetic(string con) =>
        StockCensus.Build(new[] { new ConFile("stock.con", Encoding.Latin1.GetBytes(con)) });

    // --- attribution ---------------------------------------------------------------------------------------

    [Fact]
    public void Properties_are_attributed_to_the_class_being_defined()
    {
        // A .con is a stream of "create <Class> <Name>" followed by the properties set on it, so the census
        // has to follow the current class as it walks - that attribution IS the whole point.
        var census = Synthetic("""
            ObjectTemplate.create Engine myEngine
            ObjectTemplate.torque 100
            ObjectTemplate.torque 120
            ObjectTemplate.create Spring mySpring
            ObjectTemplate.strength 5
            """.Replace("\n", "\r\n"));

        Assert.Equal(2, census.Count("Engine", "torque"));
        Assert.Equal(1, census.Count("Spring", "strength"));
        Assert.Equal(0, census.Count("Spring", "torque"));
        Assert.True(census.DefinesTemplate("myEngine"));
        Assert.False(census.DefinesTemplate("somethingElse"));
    }

    [Fact]
    public void ActiveSafe_reopens_a_template_and_counts_like_create()
    {
        // Retail uses activeSafe constantly; missing it would strand every property after one.
        var census = Synthetic("""
            ObjectTemplate.activeSafe Engine myEngine
            ObjectTemplate.torque 100
            """.Replace("\n", "\r\n"));

        Assert.Equal(1, census.Count("Engine", "torque"));
    }

    [Fact]
    public void Commented_out_content_is_not_censused()
    {
        var census = Synthetic("""
            ObjectTemplate.create Engine myEngine
            rem ObjectTemplate.torque 100
            beginRem
            ObjectTemplate.torque 200
            endRem
            """.Replace("\n", "\r\n"));

        Assert.Equal(0, census.Count("Engine", "torque"));
    }

    // --- the setX decision ---------------------------------------------------------------------------------

    [Fact]
    public void A_short_form_retail_uses_and_a_set_form_it_never_uses_is_a_proven_rename()
    {
        var census = Synthetic(string.Join("\r\n",
            new[] { "ObjectTemplate.create Engine e" }
                .Concat(Enumerable.Repeat("ObjectTemplate.torque 100", 5))));

        var v = census.JudgeSetForm("Engine", "setTorque");

        Assert.Equal(SetFormDecision.RenameToShortForm, v.Decision);
        Assert.Equal("torque", v.ShortForm);
        Assert.Equal(5, v.ShortFormUses);
    }

    [Fact]
    public void A_set_form_retail_itself_uses_is_never_renamed()
    {
        // setLodDistance has thousands of retail uses; renaming it breaks working content. This is the guard
        // that stops a blanket "strip set" rule.
        var census = Synthetic(string.Join("\r\n",
            new[] { "GeometryTemplate.create StandardMesh m" }
                .Concat(Enumerable.Repeat("GeometryTemplate.setLodDistance 100", 20))));

        var v = census.JudgeSetForm("StandardMesh", "setLodDistance");

        Assert.Equal(SetFormDecision.KeepSetForm, v.Decision);
    }

    [Fact]
    public void Whole_corpus_evidence_settles_a_class_retail_never_uses()
    {
        // Retail sets neither spelling of networkableInfo on PlayerControlObject, yet uses 'networkableInfo'
        // across other classes and 'setNetworkableInfo' nowhere. Without this tier the census misses a rename
        // that demonstrably matters: no NetworkableInfo means the template never replicates, so the server
        // owns the vehicle and no client is ever told it exists.
        var census = Synthetic(string.Join("\r\n",
            new[] { "ObjectTemplate.create FireArms f" }
                .Concat(Enumerable.Repeat("ObjectTemplate.networkableInfo Some_Info", 8))));

        var v = census.JudgeSetForm("PlayerControlObject", "setNetworkableInfo");

        Assert.Equal(SetFormDecision.RenameToShortForm, v.Decision);
        Assert.Contains("across all of retail", v.Reason);
    }

    [Fact]
    public void Whole_corpus_evidence_refuses_when_retail_uses_the_set_form_anywhere()
    {
        // The mirror of the above, and the reason the fallback is safe: a set- form retail uses SOMEWHERE is a
        // spelling BFV keeps, so a class with no local evidence must be left alone.
        var census = Synthetic(string.Join("\r\n",
            new[] { "GeometryTemplate.create StandardMesh m" }
                .Concat(Enumerable.Repeat("GeometryTemplate.setLodDistance 100", 20))));

        var v = census.JudgeSetForm("SomeClassRetailNeverUses", "setLodDistance");

        Assert.Equal(SetFormDecision.NoEvidence, v.Decision);
        Assert.Contains("keeps", v.Reason);
    }

    [Fact]
    public void No_evidence_at_all_means_leave_it_alone()
    {
        var census = Synthetic("ObjectTemplate.create Engine e\r\nObjectTemplate.torque 1\r\n");
        Assert.Equal(SetFormDecision.NoEvidence, census.JudgeSetForm("Wing", "setSomethingNobodyUses").Decision);
    }

    // --- driving the rewriter ------------------------------------------------------------------------------

    [Fact]
    public void The_rewriter_applies_a_proven_rename_in_the_class_being_defined()
    {
        var census = Synthetic(string.Join("\r\n",
            new[] { "ObjectTemplate.create Engine e" }
                .Concat(Enumerable.Repeat("ObjectTemplate.torque 100", 5))));

        const string source = "ObjectTemplate.create Engine dcEngine\r\nObjectTemplate.setTorque 30\r\n";
        var result = ConDialect.Rewrite(source, "Objects.con", new ConDialectOptions { Census = census });

        Assert.Contains("ObjectTemplate.torque 30", Encoding.Latin1.GetString(result.Bytes), StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Reason.Contains("on class Engine"));
    }

    [Fact]
    public void Without_a_census_the_rewriter_changes_nothing_and_only_reports()
    {
        // The converter never guesses a rename. No census, no rewrite.
        const string source = "ObjectTemplate.create Engine dcEngine\r\nObjectTemplate.setTorque 30\r\n";
        var result = ConDialect.Rewrite(source, "Objects.con");

        Assert.Equal(source, Encoding.Latin1.GetString(result.Bytes));
        Assert.Empty(result.Changes);
        Assert.Contains(result.Reviews, r => r.Kind == ReviewKind.PerClassRename);
    }

    [Fact]
    public void A_census_driven_rewrite_is_still_idempotent()
    {
        var census = Synthetic(string.Join("\r\n",
            new[] { "ObjectTemplate.create Engine e" }
                .Concat(Enumerable.Repeat("ObjectTemplate.torque 100", 5))));
        var options = new ConDialectOptions { Census = census };

        const string source = "ObjectTemplate.create Engine dcEngine\r\nObjectTemplate.setTorque 30\r\n";
        var first = ConDialect.Rewrite(source, "Objects.con", options);
        var second = ConDialect.Rewrite(first.Bytes, "Objects.con", options);

        Assert.NotEmpty(first.Changes);
        Assert.Empty(second.Changes);
        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Fact]
    public void A_census_survives_a_round_trip_through_json()
    {
        var census = Synthetic("ObjectTemplate.create Engine e\r\nObjectTemplate.torque 100\r\n");
        string path = Path.Combine(Path.GetTempPath(), $"rbridge_census_{Environment.ProcessId}.json");
        try
        {
            census.Save(path);
            var loaded = StockCensus.Load(path);

            Assert.Equal(census.Count("Engine", "torque"), loaded.Count("Engine", "torque"));
            Assert.True(loaded.DefinesTemplate("e"));
        }
        finally { try { File.Delete(path); } catch { /* ignore */ } }
    }

    // --- against the real game -----------------------------------------------------------------------------

    [Fact]
    public void The_real_retail_library_reproduces_the_documented_conclusions()
    {
        if (!File.Exists(BfvObjects)) return;

        var census = StockCensus.Build(ConSource.Load(BfvObjects));

        // The porting notes' own examples, rediscovered from the shipped content rather than trusted.
        Assert.Equal(SetFormDecision.RenameToShortForm, census.JudgeSetForm("Spring", "setStrength").Decision);
        Assert.Equal(SetFormDecision.RenameToShortForm, census.JudgeSetForm("Engine", "setTorque").Decision);
        Assert.Equal(SetFormDecision.RenameToShortForm, census.JudgeSetForm("Engine", "setEngineType").Decision);
        Assert.Equal(SetFormDecision.RenameToShortForm, census.JudgeSetForm("Engine", "setDifferential").Decision);

        // ... and the ones that must NOT be touched.
        Assert.Equal(SetFormDecision.KeepSetForm, census.JudgeSetForm("StandardMesh", "setLodDistance").Decision);

        Assert.True(census.Count("Spring", "strength") > 50, "retail should set strength on Spring many times");
        Assert.Equal(0, census.Count("Spring", "setStrength"));

        // 'Tracer_Projectile' is retail's - a level defining it would REPLACE stock's for the whole game.
        Assert.True(census.DefinesTemplate("Tracer_Projectile"));
    }
}
