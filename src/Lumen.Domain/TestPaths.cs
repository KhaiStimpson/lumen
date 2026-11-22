namespace Lumen.Domain;

/// <summary>Whether a repository path is test code, by folder or file name.</summary>
public static class TestPaths
{
    public static bool IsTest(string path)
    {
        var segments = path.Split('/');
        return segments.Any(s =>
                   s.Equals("tests", StringComparison.OrdinalIgnoreCase) ||
                   s.Equals("test", StringComparison.OrdinalIgnoreCase) ||
                   s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
                   s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)) ||
               path.EndsWith("Tests.cs", StringComparison.Ordinal);
    }
}
