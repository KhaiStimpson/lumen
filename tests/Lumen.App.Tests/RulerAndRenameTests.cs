using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Lumen.App.Diff;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>The diff's overview ruler with [ / ] change stepping, and renamed files shown as a path change.</summary>
public sealed class RulerAndRenameTests
{
    private const string Moved = "src/AndrewCrm.Web/Services/RecommendationService.cs";
    private const string MovedFrom = "src/AndrewCrm.Web/Services/Signals/RecommendationService.cs";
    private const string Renamed = "src/AndrewCrm.Web/Services/ImportService.cs";
    private const string RenamedFrom = "src/AndrewCrm.Web/Services/ContactImportService.cs";

    [AvaloniaFact]
    public async Task BracketsStepThroughTheChangesAndOnIntoTheNextFile()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        var file = pr.Files.Single(f => f.Path.EndsWith("CatchMeUpService.cs", StringComparison.Ordinal));
        await pr.SelectFileAsync(file);
        await Fixture.PumpAsync(10);

        var editor = session.Find<DiffEditor>("Editor");
        var count = pr.CurrentDiff!.Changes.Count;
        Assert.True(count > 2);
        Assert.Equal($"{count} changes", pr.ChangePositionLabel);
        Assert.True(session.Find<StackPanel>("ChangeNav").IsEffectivelyVisible);

        await session.PressAsync(PhysicalKey.BracketRight);
        await Fixture.PumpAsync(20);
        Assert.Equal(0, editor.CurrentChange);
        Assert.Equal($"Change 1 of {count}", pr.ChangePositionLabel);

        await session.PressAsync(PhysicalKey.BracketRight);
        await Fixture.PumpAsync(20);
        Assert.Equal(1, editor.CurrentChange);
        session.Capture("40-ruler.png");

        await session.PressAsync(PhysicalKey.BracketLeft);
        await Fixture.PumpAsync(20);
        Assert.Equal(0, editor.CurrentChange);

        // Past the last change, ] opens the next file in the tree and lands on its first change.
        for (var i = 1; i < count; i++)
        {
            pr.NextChange();
            await Fixture.PumpAsync(20);
        }

        Assert.Equal(file.Path, pr.SelectedFile!.Path);
        Assert.Equal(count - 1, editor.CurrentChange);
        pr.NextChange();
        await Fixture.WaitUntilAsync(() => pr.SelectedFile!.Path != file.Path);
        await Fixture.PumpAsync(20);
        Assert.Equal(0, editor.CurrentChange);
        Assert.StartsWith("Change 1 of ", pr.ChangePositionLabel, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task TheRulerAlsoRunsTheFullFileAndSplitViews()
    {
        await using var session = await AppSession.OpenAsync();
        var pr = session.PullRequest;
        await pr.SelectFileAsync(pr.Files.Single(f => f.Path.EndsWith("CatchMeUpService.cs", StringComparison.Ordinal)));
        var hunksOnly = pr.CurrentDiff!.Changes.Count;

        pr.ShowFullFile = true;
        await Fixture.WaitUntilAsync(() => pr.CurrentDiff!.Rows.Any(r => r.IsOutsideDiff));
        Assert.Equal(hunksOnly, pr.CurrentDiff!.Changes.Count);
        pr.NextChange();
        await Fixture.PumpAsync(20);
        Assert.Equal(0, session.Find<DiffEditor>("Editor").CurrentChange);
        session.Capture("41-ruler-full-file.png");

        pr.IsSplitView = true;
        await Fixture.PumpAsync(10);
        var split = session.Find<SplitDiffView>("SplitEditor");
        Assert.Equal(hunksOnly, split.Right.Diff!.Changes.Count);
        Assert.False(split.Left.ShowRuler);
        Assert.True(split.Right.ShowRuler);
        pr.NextChange();
        await Fixture.PumpAsync(20);
        Assert.True(split.Right.CurrentChange >= 0);
        session.Capture("42-ruler-split.png");
    }

    [AvaloniaFact]
    public async Task APureRenameShowsThePathChangeInsteadOfAWholeFileDiff()
    {
        await using var session = await OpenWithRenamesAsync();
        var pr = session.PullRequest;
        var moved = pr.Files.Single(f => f.Path == Moved);
        Assert.True(moved.IsPureRename);
        Assert.False(moved.ShowCounts);
        Assert.Equal("from …/Signals/", moved.RenamedFromLabel);

        await pr.SelectFileAsync(moved);
        await Fixture.PumpAsync(10);
        Assert.True(pr.CurrentDiff!.IsEmpty);
        Assert.True(session.Find<StackPanel>("MovedNotice").IsEffectivelyVisible);
        Assert.True(session.Find<TextBlock>("RenamePath").IsEffectivelyVisible);
        Assert.False(session.Find<StackPanel>("ChangeNav").IsEffectivelyVisible);
        session.Capture("43-moved-file.png");

        // "Show file" shows the content, with nothing marked as changed.
        pr.ShowWholeFileCommand.Execute(null);
        await Fixture.WaitUntilAsync(() => pr.CurrentDiff is { IsEmpty: false });
        Assert.Empty(pr.CurrentDiff!.Changes);
        Assert.All(pr.CurrentDiff.Rows, r => Assert.Equal(DiffRowKind.Context, r.Kind));
    }

    [AvaloniaFact]
    public async Task ARenameWithEditsShowsOnlyTheEditsAndHowSimilarItIs()
    {
        await using var session = await OpenWithRenamesAsync();
        var pr = session.PullRequest;
        var renamed = pr.Files.Single(f => f.Path == Renamed);
        Assert.False(renamed.IsPureRename);
        Assert.Equal("was ContactImportService.cs", renamed.RenamedFromLabel);
        Assert.Equal("88% similar", renamed.SimilarityLabel);
        Assert.Equal(new PathChange("src/AndrewCrm.Web/Services/", "ContactImportService.cs", "ImportService.cs", ""), renamed.PathChange);

        await pr.SelectFileAsync(renamed);
        await Fixture.PumpAsync(10);
        Assert.NotEmpty(pr.CurrentDiff!.Changes);
        Assert.True(session.Find<Border>("SimilarityPill").IsEffectivelyVisible);
        Assert.False(session.Find<StackPanel>("MovedNotice").IsEffectivelyVisible);
        session.Capture("44-renamed-file.png");
    }

    [Theory]
    [InlineData("src/Retry/Policy.cs", "src/Policies/Policy.cs", "src/", "Retry/", "Policies/", "Policy.cs")]
    [InlineData("src/A/Helper.cs", "src/A/Scheduler.cs", "src/A/", "Helper.cs", "Scheduler.cs", "")]
    [InlineData("src/Widget.cs", "src/Parts/Widget.cs", "src/", "", "Parts/", "Widget.cs")]
    [InlineData("Old.md", "New.md", "", "Old.md", "New.md", "")]
    [InlineData("a/b/X.cs", "c/d/Y.cs", "", "a/b/X.cs", "c/d/Y.cs", "")]
    public void PathChangeSplitsAroundTheSegmentsThatDiffer(string oldPath, string newPath, string head, string oldMiddle, string newMiddle, string tail)
    {
        var change = PathChange.Between(oldPath, newPath);

        Assert.Equal(new PathChange(head, oldMiddle, newMiddle, tail), change);
        Assert.Equal(oldPath, change.OldPath);
        Assert.Equal(newPath, change.NewPath);
    }

    /// <summary>The recorded session with one file moved untouched and another renamed with its edits.</summary>
    private static async Task<AppSession> OpenWithRenamesAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lumen-app-tests", $"renames-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        foreach (var path in Directory.GetFiles(Fixture.DirectoryPath))
        {
            File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        }

        var events = Path.Combine(directory, "events.jsonl");
        var lines = File.ReadAllLines(events).Select(line =>
        {
            if (line.Length == 0 || JsonNode.Parse(line) is not JsonObject evt || evt["snapshot"]?["files"] is not JsonArray files)
            {
                return line;
            }

            foreach (var file in files.OfType<JsonObject>())
            {
                switch ((string?)file["path"])
                {
                    case Moved:
                        file["kind"] = "FILE_CHANGE_KIND_RENAMED";
                        file["oldPath"] = MovedFrom;
                        file["similarity"] = 100;
                        file.Remove("additions");
                        file.Remove("deletions");
                        break;
                    case Renamed:
                        file["kind"] = "FILE_CHANGE_KIND_RENAMED";
                        file["oldPath"] = RenamedFrom;
                        file["similarity"] = 88;
                        break;
                }
            }

            return evt.ToJsonString();
        });
        File.WriteAllLines(events, lines);

        var diffs = Path.Combine(directory, "diffs.jsonl");
        File.WriteAllLines(diffs, File.ReadAllLines(diffs).Select(line =>
            line.Contains($"\"path\": \"{Moved}\"", StringComparison.Ordinal) ? $"{{ \"path\": \"{Moved}\", \"hunks\": [] }}" : line));

        return await AppSession.OpenAsync(fixtureDirectory: directory);
    }
}
