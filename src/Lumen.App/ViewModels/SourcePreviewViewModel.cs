namespace Lumen.App.ViewModels;

/// <summary>A precedent file opened in full, scrolled to the relevant line.</summary>
public sealed class SourcePreviewViewModel(string path, string symbol, string text, int line, int startLine = 1)
{
    public string Path { get; } = path;

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";

    public string Symbol { get; } = symbol;

    public string Text { get; } = text;

    public int Line { get; } = line;

    /// <summary>1 for a whole file; the excerpt's first line when only a snippet is available.</summary>
    public int StartLine { get; } = startLine;

    public IReadOnlyList<int> HighlightLines => [Line];
}
