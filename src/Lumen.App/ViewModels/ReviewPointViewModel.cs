using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public sealed partial class ReviewPointViewModel : ObservableObject
{
    public ReviewPointViewModel(ReviewPoint model, int ordinal)
    {
        Ordinal = ordinal;
        State = model.State;
        CommentDraft = model.SuggestedComment;
        Apply(model);
    }

    public ReviewPoint Model { get; private set; } = null!;

    /// <summary>
    /// Takes a newer version of the same point (e.g. an investigation added evidence). The reviewer's own state,
    /// comment draft and position are kept.
    /// </summary>
    public void Update(ReviewPoint model)
    {
        Apply(model);
        OnPropertyChanged(string.Empty);
    }

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(PrecedentExamples), nameof(Exceptions), nameof(Evidence), nameof(Locations))]
    private void Apply(ReviewPoint model)
    {
        Model = model;
        if (model.Comparison is { } comparison)
        {
            Current = new ComparisonSideViewModel(comparison.Current, isPrecedent: false);
            Precedent = new ComparisonSideViewModel(comparison.Precedent, isPrecedent: true);
        }

        var precedent = model.Evidence.Where(e => e.Examples.Count > 0 && !e.IsCounterEvidence);
        PrecedentExamples = [.. precedent.SelectMany(e => e.Examples).Select(x => new PrecedentExampleViewModel(x))];
        Exceptions = [.. model.Evidence.Where(e => e.IsCounterEvidence).SelectMany(e => e.Examples).Select(x => new PrecedentExampleViewModel(x))];
        Evidence = [.. model.Evidence.Select(e => new EvidenceItemViewModel(e))];
        Locations = [new LocationViewModel(model.Anchor, isPrimary: true), .. model.OtherLocations.Select(l => new LocationViewModel(l, isPrimary: false))];
    }

    /// <summary>Position in the ranked list (1-based); drives the "R1" tag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tag))]
    public partial int Ordinal { get; set; }

    public string Id => Model.Id;

    public string Tag => $"R{Ordinal}";

    public string Title => Model.Title;

    public string Summary => Model.Summary;

    public string WhyItMatters => Model.WhyItMatters;

    public string Kind => Model.Type;

    public string TypeLabel => Model.Type switch
    {
        "PatternDeviation" => "Pattern deviation",
        "CorrectnessRisk" => "Correctness risk",
        "BehaviourChange" => "Behaviour change",
        "ArchitectureDrift" => "Architecture drift",
        "MissingCoverage" => "Missing coverage",
        "ErrorHandling" => "Error handling",
        "SecurityRisk" => "Security risk",
        _ => Model.Type,
    };

    public string Severity => Model.Severity switch
    {
        ReviewSeverity.High => "High",
        ReviewSeverity.Medium => "Medium",
        _ => "Low",
    };

    /// <summary>Evidence described in words, never as an opaque percentage (TDD §51).</summary>
    public string EvidenceLabel => Model.EvidenceState switch
    {
        EvidenceState.Strong => "Strong repository precedent",
        EvidenceState.Conflicting => "Precedent, with exceptions",
        EvidenceState.Verified => "Verified",
        EvidenceState.HumanDecision => "Needs your judgement",
        _ => "No executable evidence",
    };

    public string SupportLabel
    {
        get
        {
            var precedent = Model.Evidence.FirstOrDefault(e => !e.IsCounterEvidence && e.PeerCount > 0);
            return precedent is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{precedent.Supporting} of {precedent.PeerCount} peers");
        }
    }

    public string Path => Model.Anchor.Path;

    public string FileName => System.IO.Path.GetFileName(Model.Anchor.Path);

    public int Line => Model.Anchor.StartLine;

    public string LocationLabel => Model.OtherLocations.Count == 0
        ? string.Create(CultureInfo.InvariantCulture, $"{FileName}:{Line}")
        : string.Create(CultureInfo.InvariantCulture, $"{FileName}:{Line}  ·  +{Model.OtherLocations.Count} more");

    public string SuggestedComment => Model.SuggestedComment;

    public ComparisonSideViewModel? Current { get; private set; }

    public ComparisonSideViewModel? Precedent { get; private set; }

    public IReadOnlyList<PrecedentExampleViewModel> PrecedentExamples { get; private set; }

    public IReadOnlyList<PrecedentExampleViewModel> Exceptions { get; private set; }

    public IReadOnlyList<EvidenceItemViewModel> Evidence { get; private set; }

    public IReadOnlyList<LocationViewModel> Locations { get; private set; }

    public bool HasExceptions => Exceptions.Count > 0;

    public string RoutingReason => Model.RoutingReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDismissed), nameof(IsCommented), nameof(IsOpen), nameof(StateLabel))]
    public partial ReviewPointState State { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial bool IsComposing { get; set; }

    [ObservableProperty]
    public partial string CommentDraft { get; set; }

    [ObservableProperty]
    public partial bool IsPosting { get; set; }

    [ObservableProperty]
    public partial string? PostError { get; set; }

    [ObservableProperty]
    public partial string? PostedUrl { get; set; }

    public bool IsDismissed => State == ReviewPointState.Dismissed;

    public bool IsCommented => State == ReviewPointState.Commented;

    public bool IsOpen => !IsDismissed && !IsCommented;

    public string StateLabel => State switch
    {
        ReviewPointState.Dismissed => "Dismissed",
        ReviewPointState.Commented => "Commented",
        ReviewPointState.Resolved => "Resolved",
        _ => "",
    };

    public bool IsAt(string path, int line) =>
        (Model.Anchor.Path == path && Model.Anchor.StartLine == line) ||
        Model.OtherLocations.Any(l => l.Path == path && l.StartLine == line);
}

public sealed class ComparisonSideViewModel(ComparisonSide side, bool isPrecedent)
{
    public bool IsPrecedent { get; } = isPrecedent;

    public string Title => side.Title;

    public string Caption => side.Caption;

    public string Path => side.Source.Path;

    public string FileName => System.IO.Path.GetFileName(side.Source.Path);

    public string Symbol => side.Source.Symbol;

    public string Snippet => side.Snippet;

    public int StartLine => side.SnippetStartLine;

    public IReadOnlyList<int> HighlightLines => [.. side.HighlightLines];

    public string PointsLabel => side.Points.Count switch
    {
        0 => "",
        <= 3 => string.Join(", ", side.Points),
        _ => $"{string.Join(", ", side.Points.Take(3))} +{side.Points.Count - 3}",
    };

    public string HighlightKey => IsPrecedent ? "Diff.Added.Bg" : "Diff.Removed.Bg";
}

public sealed class PrecedentExampleViewModel(PrecedentExample example)
{
    public string TypeName => example.TypeName;

    public string Path => example.Location.Path;

    public string Directory => System.IO.Path.GetDirectoryName(example.Location.Path)?.Replace('\\', '/') ?? "";

    public string FileName => System.IO.Path.GetFileName(example.Location.Path);

    public int Line => example.Location.StartLine;

    public string Snippet => example.Snippet;

    public int StartLine => example.SnippetStartLine;

    public IReadOnlyList<int> HighlightLines => [example.Location.StartLine];
}

public sealed class EvidenceItemViewModel(Evidence evidence)
{
    public string Summary => evidence.Summary;

    public bool IsCounterEvidence => evidence.IsCounterEvidence;

    public string Glyph => evidence.IsCounterEvidence ? "✕" : "✓";

    public string SourceLabel => evidence.Source switch
    {
        "RepositoryPrecedent" => "Repository precedent",
        "StaticAnalysis" => "Static analysis",
        "ReviewHistory" => "Review history",
        "GeneratedTest" => "Generated test",
        "AgentInvestigation" => "Agent investigation",
        "GitHistory" => "Git history",
        _ => evidence.Source,
    };

    /// <summary>Provenance for every claim (TDD §2.3).</summary>
    public string Provenance => $"{SourceLabel} · {evidence.Producer}";
}

public sealed class LocationViewModel(CodeLocation location, bool isPrimary)
{
    public string Path => location.Path;

    public int Line => location.StartLine;

    public bool IsPrimary { get; } = isPrimary;

    public string Symbol => location.Symbol;

    public string Label => string.Create(CultureInfo.InvariantCulture, $"{System.IO.Path.GetFileName(location.Path)}:{location.StartLine}");
}
