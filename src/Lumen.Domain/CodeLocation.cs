namespace Lumen.Domain;

/// <summary>A span of head-side lines in a file. Lines are 1-based and inclusive.</summary>
public sealed record CodeLocation(string Path, int StartLine, int EndLine, string? Symbol = null);
