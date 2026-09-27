namespace Lumen.Roslyn;

public enum TraitKind
{
    Dependency,
    BaseType,
    Throws,
    SwallowsExceptions,
    Attribute,
    Calls,
}

/// <summary>
/// A structural fact about a type, such as "takes HttpClient" or "throws ProviderOperationException".
/// Keys are normalised so the same fact compares equal across types.
/// </summary>
public sealed record Trait(TraitKind Kind, string Subject)
{
    public string Key => $"{Kind}:{Subject}";

    public string Category => Kind switch
    {
        TraitKind.Throws or TraitKind.SwallowsExceptions => "error-handling",
        TraitKind.Dependency => "dependency",
        TraitKind.Attribute when Subject.Contains("Concurren", StringComparison.Ordinal) ||
                                 Subject.Contains("Lock", StringComparison.Ordinal) => "concurrency",
        TraitKind.Calls => "usage",
        _ => "structure",
    };

    public override string ToString() => Key;
}

/// <summary>Where a trait appears in a type (1-based line in the type's file).</summary>
public sealed record TraitOccurrence(Trait Trait, int Line);

public sealed record TypeFacts
{
    public required string Name { get; init; }

    public required string FullName { get; init; }

    public required string Path { get; init; }

    /// <summary>1-based line of the type's identifier.</summary>
    public required int DeclarationLine { get; init; }

    public required int StartLine { get; init; }

    public required int EndLine { get; init; }

    public required bool IsTest { get; init; }

    public required bool IsAbstract { get; init; }

    /// <summary>The last PascalCase word of the name, e.g. "Client" for "MailgunClient".</summary>
    public string? Suffix { get; init; }

    public required IReadOnlyDictionary<string, TraitOccurrence> Traits { get; init; }

    /// <summary>1-based line of the constructor (or primary constructor), if any.</summary>
    public int? ConstructorLine { get; init; }

    public bool Has(Trait trait) => Traits.ContainsKey(trait.Key);

    public IEnumerable<Trait> AllTraits => Traits.Values.Select(o => o.Trait);

    public override string ToString() => FullName;
}
