using RefractorForge.Formats.Geometry;

namespace RefractorForge.Formats.Validation;

public enum IssueSeverity { Info, Warning, Error }

/// <summary>
/// One finding from any of the level checks. Everything the report window shows is one of these, so a new
/// check only has to produce them - the listing, the jump-to and the select-object come for free.
/// </summary>
public sealed record LevelIssue(
    IssueSeverity Severity,
    string Category,
    string Message,
    Vec3? Position = null,      // where to fly the camera, when the finding is somewhere
    string? ObjectId = null)    // which static object to select, when it is about one
{
    public override string ToString() =>
        $"[{Severity}] {Category}: {Message}" + (Position is { } p ? $" @ ({p.X:0},{p.Y:0},{p.Z:0})" : "");
}

/// <summary>A finished check: what it found, plus a one-line summary for the status bar.</summary>
public sealed class LevelReport
{
    public string Title { get; }
    public List<LevelIssue> Issues { get; } = new();
    public DateTime When { get; } = DateTime.Now;

    public LevelReport(string title) { Title = title; }

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.Error);
    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.Warning);
    public int Infos => Issues.Count(i => i.Severity == IssueSeverity.Info);

    public void Add(IssueSeverity s, string category, string message, Vec3? pos = null, string? objectId = null)
        => Issues.Add(new LevelIssue(s, category, message, pos, objectId));

    public string Summary =>
        Issues.Count == 0 ? $"{Title}: nothing found"
        : $"{Title}: {Errors} error(s), {Warnings} warning(s), {Infos} note(s)";
}
