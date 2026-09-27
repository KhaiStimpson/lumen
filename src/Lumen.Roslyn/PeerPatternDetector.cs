using System.Security.Cryptography;
using System.Text;
using Lumen.Analysis;
using Lumen.Domain;

namespace Lumen.Roslyn;

/// <summary>A changed type that lacks a trait its peers share.</summary>
public sealed record AffectedType(
    TypeFacts Type,
    CodeLocation Anchor,
    IReadOnlyList<TraitOccurrence> Instead);

/// <summary>What <see cref="PeerPatternDetector"/> found; the explainer turns this into words.</summary>
public sealed record PeerDeviationFacts(
    PeerRole Role,
    Trait Missing,
    IReadOnlyList<AffectedType> Affected,
    IReadOnlyList<TypeFacts> Following,
    IReadOnlyList<TypeFacts> NotFollowing,
    PrecedentExample BestExample,
    PrecedentExample CurrentExample);

/// <summary>How peers were grouped: by a shared base type, a shared constructor dependency, or a name suffix.</summary>
public sealed record PeerRole(string Kind, string Subject)
{
    public string Key => $"{Kind}:{Subject}";

    public static PeerRole Of(Trait trait) => trait.Kind switch
    {
        TraitKind.BaseType => new PeerRole("base", trait.Subject),
        TraitKind.Dependency => new PeerRole("dep", trait.Subject),
        _ => throw new ArgumentOutOfRangeException(nameof(trait)),
    };

    public bool Matches(TypeFacts type) => Kind switch
    {
        "base" => type.Has(new Trait(TraitKind.BaseType, Subject)),
        "dep" => type.Has(new Trait(TraitKind.Dependency, Subject)),
        "suffix" => string.Equals(type.Suffix, Subject, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>The trait that defines the role; never reported as "missing".</summary>
    public string? DefiningTraitKey => Kind switch
    {
        "base" => new Trait(TraitKind.BaseType, Subject).Key,
        "dep" => new Trait(TraitKind.Dependency, Subject).Key,
        _ => null,
    };
}

/// <summary>
/// Finds changed classes that differ from how analogous classes in the repository are built (TDD §57: "pattern
/// deviation"). Peers are other classes sharing a base type, constructor dependency, or naming role. A trait
/// most peers share, that is distinctive to the role, and that the changed class lacks becomes a candidate.
/// </summary>
public sealed class PeerPatternDetector : IChangeDetector
{
    public const string DetectorId = "peer-pattern/v1";

    /// <summary>Names that say little about design; comparing them produces noise. Settings can add more.</summary>
    public static readonly IReadOnlySet<string> BuiltInIgnoredNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "CancellationToken", "string", "int", "bool", "Guid", "TimeProvider", "Task", "ValueTask", "Math", "Console",
        "string.IsNullOrWhiteSpace", "string.IsNullOrEmpty", "string.Join", "string.Format", "Task.WhenAll",
        "Task.FromResult", "Task.CompletedTask", "Array.Empty", "Enumerable.Empty", "ArgumentNullException.ThrowIfNull",
        "ArgumentException.ThrowIfNullOrWhiteSpace", "Guid.NewGuid", "DateTime.UtcNow", "DateTimeOffset.UtcNow",
        "Obsolete", "Fact", "Theory", "InlineData",
    };

    private readonly Func<string, CancellationToken, Task<RepositoryTypeIndex>> _indexFactory;

    public PeerPatternDetector()
        : this(RepositoryTypeIndex.BuildAsync)
    {
    }

    public PeerPatternDetector(Func<string, CancellationToken, Task<RepositoryTypeIndex>> indexFactory)
    {
        _indexFactory = indexFactory;
    }

    public string Id => DetectorId;

    public async Task<DetectionResult> DetectAsync(AnalysisContext context, CancellationToken cancellationToken)
    {
        var index = await _indexFactory(context.Checkout.RootPath, cancellationToken).ConfigureAwait(false);
        return await DetectAsync(index, context.Snapshot, context.Checkout.ReadBaseFileAsync, context.Settings, cancellationToken).ConfigureAwait(false);
    }

    public static Task<DetectionResult> DetectAsync(
        RepositoryTypeIndex index,
        PullRequestSnapshot snapshot,
        Func<string, CancellationToken, Task<string?>> readBaseFile,
        CancellationToken cancellationToken) =>
        DetectAsync(index, snapshot, readBaseFile, ReviewSettings.Default, cancellationToken);

    public static async Task<DetectionResult> DetectAsync(
        RepositoryTypeIndex index,
        PullRequestSnapshot snapshot,
        Func<string, CancellationToken, Task<string?>> readBaseFile,
        ReviewSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var sensitivity = settings.Sensitivity;
        var ignored = IgnoredNames(settings);
        var baseFacts = await ReadBaseFactsAsync(snapshot, readBaseFile, cancellationToken).ConfigureAwait(false);
        var changed = FindChangedTypes(index, snapshot, baseFacts, settings);
        if (changed.Count == 0)
        {
            return DetectionResult.Empty;
        }

        // Precedent is the code as it was before this PR: unchanged files at head, changed files at merge-base.
        var changedPaths = snapshot.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var pool = index.Types
            .Where(t => !changedPaths.Contains(t.Path))
            .Concat(baseFacts.Values.SelectMany(t => t))
            .Where(t => !t.IsTest && !t.IsAbstract)
            .ToList();
        if (pool.Count == 0)
        {
            return DetectionResult.Empty;
        }

        var repoTypes = index.DeclaredTypeNames;

        var baseRate = pool
            .SelectMany(t => t.Traits.Keys)
            .GroupBy(k => k, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (double)g.Count() / pool.Count, StringComparer.Ordinal);

        double Lift(string traitKey, double support) =>
            support / Math.Max(baseRate.GetValueOrDefault(traitKey), 1.0 / pool.Count);

        var deviations = new List<Deviation>();
        var conventions = new Dictionary<string, RepositoryConvention>(StringComparer.Ordinal);

        foreach (var change in changed)
        {
            var type = change.Head;
            foreach (var role in RolesOf(type, repoTypes, ignored))
            {
                var peers = pool.Where(p => p.FullName != type.FullName && role.Matches(p)).ToList();
                if (peers.Count < sensitivity.MinimumPeers)
                {
                    continue;
                }

                var traitCounts = peers
                    .SelectMany(p => p.AllTraits)
                    .GroupBy(t => t)
                    .Select(g => (Trait: g.Key, Count: g.Count()))
                    .Where(x => x.Count >= sensitivity.MinimumPeers && x.Trait.Key != role.DefiningTraitKey && IsMeaningful(x.Trait, type, repoTypes, ignored))
                    .Where(x => x.Trait.Kind != TraitKind.BaseType || role.Kind == "suffix");

                foreach (var (trait, count) in traitCounts)
                {
                    var support = (double)count / peers.Count;
                    if (support < sensitivity.CandidateSupport)
                    {
                        continue;
                    }

                    var lift = Lift(trait.Key, support);
                    var category = CategoryOf(trait, repoTypes);
                    AddConvention(conventions, role, trait, peers, count, lift, sensitivity);

                    if (type.Has(trait) || IsImpliedByMissingDependency(trait, type, peers, sensitivity.CandidateSupport))
                    {
                        continue;
                    }

                    var introduced = change.Kind == ChangeKind.TypeAdded || (change.Base?.Has(trait) ?? false);
                    deviations.Add(new Deviation(change, role, trait, category, peers, count, support, lift, introduced));
                }
            }
        }

        var candidates = SelectCandidates(deviations, index, snapshot, sensitivity);
        return new DetectionResult(
            candidates,
            [.. conventions.Values.OrderByDescending(c => (double)c.Supporting / c.PeerCount).ThenBy(c => c.Statement, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Filters traits down to ones that say something about design: calls and dependencies on concepts declared in
    /// this repository (not framework plumbing), error handling and attributes. Never the type itself.
    /// </summary>
    private static bool IsMeaningful(Trait trait, TypeFacts type, IReadOnlySet<string> repoTypes, IReadOnlySet<string> ignored)
    {
        if (ignored.Contains(trait.Subject) || ignored.Contains(StripGeneric(trait.Subject)) ||
            (trait.Kind == TraitKind.Calls && ignored.Contains(StripGeneric(ReceiverOf(trait)))))
        {
            return false;
        }

        return trait.Kind switch
        {
            TraitKind.Calls => StripGeneric(ReceiverOf(trait)) is var r && repoTypes.Contains(r) && r != type.Name,
            TraitKind.Dependency => !trait.Subject.StartsWith("ILogger<", StringComparison.Ordinal) && StripGeneric(trait.Subject) != type.Name,
            TraitKind.Throws => trait.Subject is not ("NotImplementedException" or "NotSupportedException"),
            _ => true,
        };
    }

    /// <summary>Repository concepts matter more than framework plumbing such as IConfiguration.</summary>
    private static IReadOnlySet<string> IgnoredNames(ReviewSettings settings) =>
        settings.IgnoredNames.Count == 0
            ? BuiltInIgnoredNames
            : BuiltInIgnoredNames.Concat(settings.IgnoredNames.Select(n => n.Trim()).Where(n => n.Length > 0)).ToHashSet(StringComparer.Ordinal);

    private static string CategoryOf(Trait trait, IReadOnlySet<string> repoTypes) =>
        trait.Kind == TraitKind.Dependency && !repoTypes.Contains(StripGeneric(trait.Subject))
            ? "framework-dependency"
            : trait.Category;

    private static string ReceiverOf(Trait trait) => trait.Subject[..trait.Subject.LastIndexOf('.')];

    private static string StripGeneric(string name)
    {
        var i = name.IndexOf('<', StringComparison.Ordinal);
        return i < 0 ? name : name[..i];
    }

    /// <summary>
    /// "Calls QueueClaimService.Claim" is implied by "takes QueueClaimService"; report the dependency, not the call.
    /// </summary>
    private static bool IsImpliedByMissingDependency(Trait trait, TypeFacts type, List<TypeFacts> peers, double candidateSupport)
    {
        if (trait.Kind != TraitKind.Calls)
        {
            return false;
        }

        var dependency = new Trait(TraitKind.Dependency, ReceiverOf(trait));
        return !type.Has(dependency) && peers.Count(p => p.Has(dependency)) >= peers.Count * candidateSupport;
    }

    private static IEnumerable<PeerRole> RolesOf(TypeFacts type, IReadOnlySet<string> repoTypes, IReadOnlySet<string> ignored)
    {
        foreach (var trait in type.AllTraits)
        {
            if (trait.Kind is TraitKind.BaseType or TraitKind.Dependency && IsMeaningful(trait, type, repoTypes, ignored))
            {
                yield return PeerRole.Of(trait);
            }
        }

        if (type.Suffix is { Length: > 2 } suffix)
        {
            yield return new PeerRole("suffix", suffix);
        }
    }

    private static void AddConvention(
        Dictionary<string, RepositoryConvention> conventions,
        PeerRole role,
        Trait trait,
        List<TypeFacts> peers,
        int count,
        double lift,
        ReviewSensitivity sensitivity)
    {
        if ((double)count / peers.Count < sensitivity.MinimumSupport || lift < sensitivity.MinimumLift)
        {
            return;
        }

        var statement = $"{Phrases.RolePlural(role)} {Phrases.Has(trait)}";
        if (conventions.TryGetValue(statement, out var existing) && existing.PeerCount >= peers.Count)
        {
            return;
        }

        conventions[statement] = new RepositoryConvention(
            StableId("conv", role.Key, trait.Key),
            statement,
            Phrases.RolePlural(role),
            count,
            peers.Count,
            [.. peers.Where(p => p.Has(trait)).Take(sensitivity.MaxExamples).Select(p => new CodeLocation(p.Path, p.Traits[trait.Key].Line, p.Traits[trait.Key].Line, p.Name))]);
    }

    private static List<Candidate> SelectCandidates(List<Deviation> deviations, RepositoryTypeIndex index, PullRequestSnapshot snapshot, ReviewSensitivity sensitivity)
    {
        // The same missing trait is often visible through several roles; each type is reported for a trait
        // only once, under the most convincing role.
        var claimed = new HashSet<(string Trait, string Type)>();
        var grouped = deviations
            .GroupBy(d => (d.Role.Key, d.Trait.Key))
            .Select(g => new
            {
                First = g.First(),
                Changes = g.Select(d => d.Change).DistinctBy(c => c.Head.FullName).ToList(),
                Score = g.First().Score,
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.First.Role.Key, StringComparer.Ordinal)
            .Select(x => new
            {
                x.First,
                Changes = x.Changes.Where(c => claimed.Add((x.First.Trait.Key, c.Head.FullName))).ToList(),
                x.Score,
            })
            .Where(x => x.Changes.Count > 0)
            .ToList();

        var perType = new Dictionary<string, int>(StringComparer.Ordinal);
        var candidates = new List<Candidate>();

        foreach (var group in grouped)
        {
            var d = group.First;

            // Only surface-worthy candidates consume a type's budget; weaker ones still flow to the policy for diagnostics.
            var budgeted = d.Introduced && d.Support >= sensitivity.MinimumSupport - 0.05 && d.Lift >= sensitivity.MinimumLift;
            var changes = budgeted
                ? group.Changes.Where(c => perType.GetValueOrDefault(c.Head.FullName) < sensitivity.MaxPointsPerType).ToList()
                : group.Changes;
            if (changes.Count == 0)
            {
                continue;
            }

            var affected = changes
                .Select(c => ToAffected(c, d, snapshot))
                .OfType<AffectedType>()
                .OrderBy(a => a.Anchor.Path, StringComparer.Ordinal)
                .ThenBy(a => a.Anchor.StartLine)
                .ToList();
            if (affected.Count == 0)
            {
                continue;
            }

            if (budgeted)
            {
                foreach (var a in affected)
                {
                    perType[a.Type.FullName] = perType.GetValueOrDefault(a.Type.FullName) + 1;
                }
            }

            candidates.Add(BuildCandidate(d, affected, changes, index, sensitivity.MaxExamples));
        }

        return candidates;
    }

    private static AffectedType? ToAffected(ChangedType change, Deviation d, PullRequestSnapshot snapshot)
    {
        var type = change.Head;
        var file = snapshot.FindFile(type.Path);
        if (file is null)
        {
            return null;
        }

        // Only error handling and dependencies have natural alternatives ("catches instead of throwing").
        var instead = type.Traits.Values
            .Where(o => d.Trait.Kind is TraitKind.Throws or TraitKind.SwallowsExceptions or TraitKind.Dependency)
            .Where(o => o.Trait.Category == d.Trait.Category && o.Trait != d.Trait)
            .Where(o => o.Trait.Kind != TraitKind.Dependency || !o.Trait.Subject.StartsWith("ILogger<", StringComparison.Ordinal))
            .Where(o => d.Peers.Count(p => p.Has(o.Trait)) <= d.Peers.Count / 4)
            .OrderBy(o => o.Line)
            .ToList();

        var preferred = instead.FirstOrDefault()?.Line
            ?? (d.Trait.Kind == TraitKind.Dependency ? type.ConstructorLine : null)
            ?? type.DeclarationLine;

        var line = file.ContainsNewLine(preferred)
            ? preferred
            : file.AddedLines().Where(l => l >= type.StartLine && l <= type.EndLine).Cast<int?>().FirstOrDefault();

        return line is null
            ? null
            : new AffectedType(type, new CodeLocation(type.Path, line.Value, line.Value, type.Name), instead);
    }

    private static Candidate BuildCandidate(Deviation d, List<AffectedType> affected, List<ChangedType> changes, RepositoryTypeIndex index, int maxExamples)
    {
        var following = d.Peers.Where(p => p.Has(d.Trait)).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        var notFollowing = d.Peers.Where(p => !p.Has(d.Trait)).OrderBy(p => p.Path, StringComparer.Ordinal).ToList();
        var now = DateTimeOffset.UtcNow;
        var key = StableId("pp", d.Role.Key, d.Trait.Key);
        var convention = $"{Phrases.RolePlural(d.Role)} {Phrases.Has(d.Trait)}";

        var examples = following.Take(maxExamples).Select(p => Example(index, p, p.Traits[d.Trait.Key].Line)).ToList();
        var primary = affected[0];
        var currentLine = (primary.Instead.Count > 0 ? primary.Instead[0].Line : (int?)null) ?? primary.Anchor.StartLine;

        var evidence = new List<Evidence>
        {
            new RepositoryPrecedentEvidence
            {
                Id = $"{key}-precedent",
                Source = EvidenceSource.RepositoryPrecedent,
                CreatedAt = now,
                Producer = DetectorId,
                Summary = $"{following.Count} of {d.Peers.Count} {Phrases.LowerFirst(Phrases.RolePlural(d.Role))} {Phrases.Has(d.Trait)}",
                Convention = convention,
                Supporting = following.Count,
                PeerCount = d.Peers.Count,
                Examples = examples,
            },
        };

        evidence.AddRange(affected.Select((a, i) => new StaticAnalysisEvidence
        {
            Id = $"{key}-syntax-{i}",
            Source = EvidenceSource.StaticAnalysis,
            CreatedAt = now,
            Producer = DetectorId,
            Summary = a.Instead.Count > 0
                ? $"{a.Type.Name} {Phrases.HasSingular(a.Instead[0].Trait)} (line {a.Instead[0].Line})"
                : $"{a.Type.Name} does not {Phrases.HasBare(d.Trait)}",
            Location = a.Anchor,
            RuleId = d.Trait.Key,
        }));

        if (notFollowing.Count > 0)
        {
            evidence.Add(new RepositoryPrecedentEvidence
            {
                Id = $"{key}-exceptions",
                Source = EvidenceSource.RepositoryPrecedent,
                CreatedAt = now,
                Producer = DetectorId,
                IsCounterEvidence = true,
                Summary = $"{notFollowing.Count} peer(s) don't: {string.Join(", ", notFollowing.Take(3).Select(p => p.Name))}{(notFollowing.Count > 3 ? ", …" : "")}",
                Convention = convention,
                Supporting = notFollowing.Count,
                PeerCount = d.Peers.Count,
                Examples = [.. notFollowing.Take(3).Select(p => Example(index, p, p.DeclarationLine))],
            });
        }

        return new Candidate
        {
            Key = key,
            DetectorId = DetectorId,
            Type = ReviewPointType.PatternDeviation,
            ChangeUnits = [.. changes.Select(c => c.Unit)],
            Anchor = primary.Anchor,
            OtherLocations = [.. affected.Skip(1).Select(a => a.Anchor)],
            Evidence = evidence,
            Signals = new AttentionSignals
            {
                ChangeKind = changes.All(c => c.Kind == ChangeKind.TypeAdded) ? "type-added" : "type-modified",
                IntroducedByChange = d.Introduced || changes.Any(c => c.Kind == ChangeKind.TypeAdded),
                PeerCount = d.Peers.Count,
                Supporting = following.Count,
                Lift = Math.Round(d.Lift, 2),
                AffectedLocations = affected.Count,
                Category = d.Category,
            },
            Facts = new PeerDeviationFacts(
                d.Role,
                d.Trait,
                affected,
                following,
                notFollowing,
                examples[0],
                Example(index, primary.Type, currentLine)),
        };
    }

    private static PrecedentExample Example(RepositoryTypeIndex index, TypeFacts type, int line)
    {
        var (snippet, start) = index.Snippet(type.Path, line - 3, line + 5);
        return new PrecedentExample(new CodeLocation(type.Path, line, line, type.Name), type.Name, snippet, start);
    }

    private static bool IsAnalysable(ChangedFile file) =>
        !file.Mechanical.IsMechanical &&
        file.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
        !RepositoryTypeIndex.IsTestPath(file.Path);

    /// <summary>Type facts at merge-base for every modified source file.</summary>
    private static async Task<Dictionary<string, IReadOnlyList<TypeFacts>>> ReadBaseFactsAsync(
        PullRequestSnapshot snapshot,
        Func<string, CancellationToken, Task<string?>> readBaseFile,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<TypeFacts>>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files.Where(f => IsAnalysable(f) && f.Kind is FileChangeKind.Modified or FileChangeKind.Renamed))
        {
            var baseText = await readBaseFile(file.OldPath ?? file.Path, cancellationToken).ConfigureAwait(false);
            if (baseText is not null)
            {
                result[file.Path] = TypeFactsExtractor.Extract(file.Path, baseText, isTest: false);
            }
        }

        return result;
    }

    private static List<ChangedType> FindChangedTypes(
        RepositoryTypeIndex index,
        PullRequestSnapshot snapshot,
        Dictionary<string, IReadOnlyList<TypeFacts>> baseFacts,
        ReviewSettings settings)
    {
        var result = new List<ChangedType>();

        foreach (var file in snapshot.Files.Where(f => IsAnalysable(f) && f.Kind != FileChangeKind.Deleted && !settings.IsSkipped(f.Path)))
        {
            var added = file.AddedLines().ToHashSet();
            var headTypes = index.TypesIn(file.Path).Where(t => !t.IsTest && added.Any(l => l >= t.StartLine && l <= t.EndLine));
            var baseTypes = baseFacts.GetValueOrDefault(file.Path) ?? [];

            foreach (var head in headTypes)
            {
                var before = baseTypes.FirstOrDefault(b => b.FullName == head.FullName);
                var kind = before is null ? ChangeKind.TypeAdded : ChangeKind.TypeModified;
                var unit = new ChangeUnit
                {
                    Id = StableId("cu", head.FullName),
                    Title = $"{(kind == ChangeKind.TypeAdded ? "New" : "Changed")} {head.Name}",
                    Kind = kind,
                    Symbols = [head.FullName],
                    Locations = [new CodeLocation(head.Path, head.StartLine, head.EndLine, head.Name)],
                };
                result.Add(new ChangedType(head, before, kind, unit));
            }
        }

        return result;
    }

    private static string StableId(string prefix, params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return $"{prefix}-{Convert.ToHexStringLower(hash)[..12]}";
    }

    private sealed record ChangedType(TypeFacts Head, TypeFacts? Base, ChangeKind Kind, ChangeUnit Unit);

    private sealed record Deviation(
        ChangedType Change,
        PeerRole Role,
        Trait Trait,
        string Category,
        List<TypeFacts> Peers,
        int Supporting,
        double Support,
        double Lift,
        bool Introduced)
    {
        public double Score => Support * Math.Min(Lift, 5) * Category switch
        {
            "error-handling" or "concurrency" => 1.5,
            "usage" or "dependency" => 1.2,
            "framework-dependency" => 0.6,
            _ => 0.8,
        };
    }
}
