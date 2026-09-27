namespace Lumen.Roslyn;

/// <summary>Plain-language renderings of roles and traits for the template explainer.</summary>
public static class Phrases
{
    public static string RolePlural(PeerRole role) => role.Kind switch
    {
        "base" when IsInterface(role.Subject) =>
            $"Implementations of {Display(role.Subject)}",
        "base" => $"Subclasses of {Display(role.Subject)}",
        "dep" => $"Classes that take {Display(role.Subject)}",
        "suffix" => $"*{role.Subject} classes",
        _ => "Similar classes",
    };

    /// <summary>"throw ProviderOperationException" — present-tense verb phrase agreeing with a plural subject.</summary>
    public static string Has(Trait trait) => trait.Kind switch
    {
        TraitKind.Dependency => $"take {Display(trait.Subject)}",
        TraitKind.BaseType when IsInterface(trait.Subject) => $"implement {Display(trait.Subject)}",
        TraitKind.BaseType => $"derive from {Display(trait.Subject)}",
        TraitKind.Throws => $"report failures by throwing {Display(trait.Subject)}",
        TraitKind.SwallowsExceptions => trait.Subject == "Exception"
            ? "catch exceptions broadly without rethrowing"
            : "handle specific exceptions without rethrowing",
        TraitKind.Attribute => $"use [{trait.Subject}]",
        TraitKind.Calls => $"call {trait.Subject}",
        _ => trait.Subject,
    };

    /// <summary>Singular form for one type: "throws ProviderOperationException".</summary>
    public static string HasSingular(Trait trait) => trait.Kind switch
    {
        TraitKind.Dependency => $"takes {Display(trait.Subject)}",
        TraitKind.BaseType when IsInterface(trait.Subject) => $"implements {Display(trait.Subject)}",
        TraitKind.BaseType => $"derives from {Display(trait.Subject)}",
        TraitKind.Throws => $"reports failures by throwing {Display(trait.Subject)}",
        TraitKind.SwallowsExceptions => trait.Subject == "Exception"
            ? "catches exceptions broadly without rethrowing"
            : "handles specific exceptions without rethrowing",
        TraitKind.Attribute => $"uses [{trait.Subject}]",
        TraitKind.Calls => $"calls {trait.Subject}",
        _ => trait.Subject,
    };

    /// <summary>Bare infinitive for negation: "does not throw ProviderOperationException".</summary>
    public static string HasBare(Trait trait) => Has(trait);

    /// <summary>Short title for a missing trait.</summary>
    public static string MissingTitle(Trait trait) => trait.Kind switch
    {
        TraitKind.Dependency => $"Doesn't use {Display(trait.Subject)}",
        TraitKind.BaseType when IsInterface(trait.Subject) => $"Doesn't implement {Display(trait.Subject)}",
        TraitKind.BaseType => $"Doesn't derive from {Display(trait.Subject)}",
        TraitKind.Throws => $"Doesn't surface failures as {Display(trait.Subject)}",
        TraitKind.SwallowsExceptions => "Doesn't handle failures locally like its peers",
        TraitKind.Attribute => $"Missing [{trait.Subject}]",
        TraitKind.Calls => $"Doesn't call {trait.Subject}",
        _ => $"Differs from peers: {trait.Subject}",
    };

    /// <summary>"Classes that take HttpClient" → "classes that take HttpClient" (keeps type names intact).</summary>
    public static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];

    public static bool IsInterface(string name) => name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1]);

    /// <summary>"ILogger&lt;&gt;" reads better as "ILogger&lt;T&gt;".</summary>
    public static string Display(string subject) =>
        subject.EndsWith("<>", StringComparison.Ordinal) ? subject[..^2] + "<T>" :
        subject.EndsWith("<,>", StringComparison.Ordinal) ? subject[..^3] + "<T1, T2>" :
        subject;

    public static string JoinNames(IEnumerable<string> names, int max = 3)
    {
        var list = names.ToList();
        var shown = list.Take(max).ToList();
        var rest = list.Count - shown.Count;
        return rest > 0 ? $"{string.Join(", ", shown)} and {rest} more" :
            shown.Count > 1 ? $"{string.Join(", ", shown.Take(shown.Count - 1))} and {shown[^1]}" :
            shown.FirstOrDefault() ?? "";
    }
}
