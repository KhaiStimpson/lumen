using Lumen.Analysis;
using Lumen.Domain;
using Lumen.Roslyn.Slicing;

namespace Lumen.Engine.Overhaul;

public sealed partial class OverhaulAnalysis
{
    private static partial List<RedFlag> Flags(PullRequestSnapshot snapshot, TriageResult? triage, ReviewSettings settings, RepositoryIndex? index) => [];
}
