using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Lumen.App.Diff;
using Lumen.App.Markdown;
using Lumen.Contracts;

namespace Lumen.App.ViewModels;

public sealed class ThreadCommentViewModel(ThreadComment comment, DateTimeOffset now, string? suggestionBase)
{
    public ThreadComment Model { get; } = comment;

    public string Author => Model.Author;

    public string Initial => Model.Author.Length == 0 ? "?" : Model.Author[..1].ToUpperInvariant();

    public string Body => Model.Body;

    public string AgeLabel => InboxItemViewModel.Age(Model.CreatedAt?.ToDateTimeOffset() ?? now, now);

    public bool IsPending => Model.IsPending;

    /// <summary>The lines a suggestion replaces, so it reads as a mini diff.</summary>
    public string? SuggestionBase { get; } = suggestionBase;

    public string Url => Model.Url;
}

/// <summary>An existing review thread, shown inline under its line (plan v2 M4).</summary>
public sealed partial class ThreadViewModel : ObservableObject
{
    public ThreadViewModel(ReviewThread model, DateTimeOffset now, string? suggestionBase)
    {
        Model = model;
        Comments = [.. model.Comments.Select(c => new ThreadCommentViewModel(c, now, suggestionBase))];
        IsCollapsed = model.IsResolved;
    }

    public ReviewThread Model { get; private set; }

    public IReadOnlyList<ThreadCommentViewModel> Comments { get; private set; }

    public string Id => Model.Id;

    public string CardId => $"thread:{Model.Id}";

    public string Path => Model.Path;

    public string FileName => System.IO.Path.GetFileName(Model.Path);

    public LineSide Side => Model.Side == Contracts.DiffSide.Left ? LineSide.Left : LineSide.Right;

    /// <summary>Where it shows at the head; for an outdated thread, where it was written.</summary>
    public int Line => Model.Line > 0 ? Model.Line : Model.OriginalLine;

    public bool IsResolved => Model.IsResolved;

    public bool IsOutdated => Model.IsOutdated;

    /// <summary>Part of the viewer's pending review: a draft only they can see until the review is submitted.</summary>
    public bool IsPendingOnly => Model.Comments.Count > 0 && Model.Comments.All(c => c.IsPending);

    /// <summary>Unresolved and visible to others (a pending draft isn't a conversation yet).</summary>
    public bool IsOpen => !Model.IsResolved && !IsPendingOnly;

    public bool CanResolve => Model.CanResolve && !Model.IsResolved;

    public bool CanUnresolve => Model.CanUnresolve && Model.IsResolved;

    public bool CanReply => Model.CanReply;

    public string StatusLabel => IsPendingOnly ? "Pending" : (IsResolved, IsOutdated) switch
    {
        (true, true) => "Resolved · outdated",
        (true, false) => "Resolved",
        (false, true) => "Outdated",
        _ => "",
    };

    public string LocationLabel => string.Create(CultureInfo.InvariantCulture, $"{FileName}:{Line}") + (Side == LineSide.Left ? " (before)" : "");

    public string Summary => Comments.Count > 0 ? Excerpt(Comments[0].Body) : "";

    public string? FirstSuggestionBase => Comments.Count > 0 ? Comments[0].SuggestionBase : null;

    public string FirstAuthor => Comments.Count > 0 ? Comments[0].Author : "";

    public string ReplyCountLabel => Comments.Count switch
    {
        <= 1 => "",
        2 => "1 reply",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n - 1} replies"),
    };

    [ObservableProperty]
    public partial bool IsCollapsed { get; set; }

    [ObservableProperty]
    public partial bool IsReplying { get; set; }

    [ObservableProperty]
    public partial string ReplyDraft { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>A newer copy from GitHub; the reader's collapse state and reply draft are kept.</summary>
    public void Update(ReviewThread model, DateTimeOffset now, string? suggestionBase)
    {
        var wasResolved = Model.IsResolved;
        Model = model;
        Comments = [.. model.Comments.Select(c => new ThreadCommentViewModel(c, now, suggestionBase))];
        if (wasResolved != model.IsResolved)
        {
            IsCollapsed = model.IsResolved;
        }

        OnPropertyChanged(string.Empty);
    }

    private static string Excerpt(string body)
    {
        var text = string.Join(' ', MarkdownParser.Parse(body).Select(b => b switch
        {
            ParagraphBlock p => p.Text,
            HeadingBlock h => h.Text,
            ListBlock l => string.Join(' ', l.Items),
            QuoteBlock q => q.Text,
            CodeBlock { Language: "suggestion" } => "(suggested change)",
            _ => "",
        }).Where(t => t.Length > 0));
        return text.Length > 140 ? text[..140] + "…" : text;
    }
}

/// <summary>Existing review threads in the diff (plan v2 M4): read, reply, resolve, and N / Shift+N between them.</summary>
public sealed partial class PullRequestViewModel
{
    public ObservableCollection<ThreadViewModel> Threads { get; } = [];

    public IEnumerable<ThreadViewModel> OpenThreads => Threads.Where(t => t.IsOpen);

    public string ThreadsLabel => OpenThreads.Count() switch
    {
        0 => Threads.Count == 0 ? "No review threads" : "All threads resolved",
        1 => "1 open thread",
        var n => string.Create(CultureInfo.InvariantCulture, $"{n} open threads"),
    };

    [ObservableProperty]
    public partial ThreadViewModel? CurrentThread { get; private set; }

    /// <summary>Asks the view to bring a line into view (a thread, a risky area, a pending comment).</summary>
    public event EventHandler<LineRevealRequest>? LineRevealRequested;

    public ThreadViewModel? FindThread(string cardId) => Threads.FirstOrDefault(t => t.CardId == cardId);

    private void ApplyThreads(IEnumerable<ReviewThread> threads)
    {
        var now = Now;
        var incoming = threads.ToList();
        var byId = Threads.ToDictionary(t => t.Id, StringComparer.Ordinal);
        Threads.Clear();
        foreach (var thread in incoming)
        {
            var suggestionBase = thread.Side == Contracts.DiffSide.Right && thread.Line > 0
                ? LinesText(thread.Path, thread.StartLine > 0 ? thread.StartLine : thread.Line, thread.Line)
                : null;
            if (byId.TryGetValue(thread.Id, out var existing))
            {
                existing.Update(thread, now, suggestionBase);
                Threads.Add(existing);
            }
            else
            {
                Threads.Add(new ThreadViewModel(thread, now, suggestionBase));
            }
        }

        foreach (var file in Files.Concat(ScopeFiles))
        {
            file.ThreadCount = Threads.Count(t => t.Path == file.Path && t.IsOpen);
        }

        OnPropertyChanged(nameof(OpenThreads));
        OnPropertyChanged(nameof(ThreadsLabel));
        if (SelectedFile is { } selected && (incoming.Any(t => t.Path == selected.Path) || byId.Values.Any(t => t.Path == selected.Path)))
        {
            RebuildCurrentDiff();
        }
    }

    /// <summary>Threads whose line exists at the head sit under it.</summary>
    private IEnumerable<CardAnchor> ThreadAnchors(string path) =>
        Threads.Where(t => t.Path == path && !t.IsOutdated && t.Model.Line > 0).Select(t => new CardAnchor(t.CardId, t.Side, t.Model.Line));

    /// <summary>Outdated threads have no line at the head: they lead the file.</summary>
    private IEnumerable<CardAnchor> OutdatedAnchors(string path) =>
        Threads.Where(t => t.Path == path && (t.IsOutdated || t.Model.Line == 0)).Select(t => new CardAnchor(t.CardId, t.Side, 0));

    /// <summary>
    /// For a commit or range that ends before the head: outdated threads written on that commit sit at their original
    /// line; nothing else is anchored (review points and current threads refer to head lines).
    /// </summary>
    private IEnumerable<CardAnchor> CommitCardAnchors(string path) =>
        Threads.Where(t => t.Path == path && Scope is { } scope && t.Model.OriginalCommitSha == scope.ToSha && t.Model.OriginalLine > 0)
            .Select(t => new CardAnchor(t.CardId, t.Side, t.Model.OriginalLine));

    [RelayCommand]
    public static void ToggleThread(ThreadViewModel? thread)
    {
        if (thread is not null)
        {
            thread.IsCollapsed = !thread.IsCollapsed;
        }
    }

    [RelayCommand]
    public static void StartReply(ThreadViewModel? thread)
    {
        if (thread is { CanReply: true })
        {
            thread.IsCollapsed = false;
            thread.IsReplying = true;
            thread.Error = null;
        }
    }

    [RelayCommand]
    public static void CancelReply(ThreadViewModel? thread)
    {
        if (thread is not null)
        {
            thread.IsReplying = false;
            thread.ReplyDraft = "";
            thread.Error = null;
        }
    }

    /// <summary>Posts a reply (into the pending review if there is one, as GitHub requires). Explicit action only.</summary>
    [RelayCommand]
    public async Task ReplyAsync(ThreadViewModel? thread)
    {
        if (thread is null || thread.IsBusy || string.IsNullOrWhiteSpace(thread.ReplyDraft))
        {
            return;
        }

        thread.IsBusy = true;
        thread.Error = null;
        try
        {
            var updated = await _source.ReplyToThreadAsync(Ref, thread.Id, thread.ReplyDraft.Trim(), _lifetime.Token).ConfigureAwait(true);
            thread.Update(updated, Now, thread.FirstSuggestionBase);
            thread.ReplyDraft = "";
            thread.IsReplying = false;
            Toast = updated.Comments.LastOrDefault()?.IsPending == true ? "Reply added to your pending review" : "Reply posted to GitHub";
        }
        catch (RpcException ex)
        {
            thread.Error = ex.Status.Detail;
        }
        finally
        {
            thread.IsBusy = false;
        }
    }

    [RelayCommand]
    public Task ResolveAsync(ThreadViewModel? thread) => SetResolvedAsync(thread, true);

    [RelayCommand]
    public Task UnresolveAsync(ThreadViewModel? thread) => SetResolvedAsync(thread, false);

    private async Task SetResolvedAsync(ThreadViewModel? thread, bool resolved)
    {
        if (thread is null || thread.IsBusy)
        {
            return;
        }

        thread.IsBusy = true;
        thread.Error = null;
        try
        {
            var updated = await _source.SetThreadResolvedAsync(Ref, thread.Id, resolved, _lifetime.Token).ConfigureAwait(true);
            thread.Update(updated, Now, thread.FirstSuggestionBase);
            ApplyThreads([.. Threads.Select(t => t.Model)]);
        }
        catch (RpcException ex)
        {
            thread.Error = ex.Status.Detail;
        }
        finally
        {
            thread.IsBusy = false;
        }
    }

    /// <summary>N / Shift+N: the next unresolved thread, in file-tree order then line.</summary>
    [RelayCommand]
    public Task NextThreadAsync() => StepThreadAsync(+1);

    [RelayCommand]
    public Task PreviousThreadAsync() => StepThreadAsync(-1);

    private Task StepThreadAsync(int direction)
    {
        var order = Files.Select((f, i) => (f.Path, i)).ToDictionary(x => x.Path, x => x.i, StringComparer.Ordinal);
        var open = OpenThreads
            .OrderBy(t => order.GetValueOrDefault(t.Path, int.MaxValue))
            .ThenBy(t => t.IsOutdated ? 0 : t.Line)
            .ToList();
        if (open.Count == 0)
        {
            Toast = "No open threads";
            return Task.CompletedTask;
        }

        var index = CurrentThread is null ? -1 : open.IndexOf(CurrentThread);
        var next = index < 0 ? (direction > 0 ? open[0] : open[^1]) : open[(index + direction + open.Count) % open.Count];
        return GoToThreadAsync(next);
    }

    /// <summary>Shows a thread in the Files tab at its location (from the conversation, N, or the tray).</summary>
    [RelayCommand]
    public async Task GoToThreadAsync(ThreadViewModel? thread)
    {
        if (thread is null)
        {
            return;
        }

        if (CurrentThread is { } previous)
        {
            previous.IsCurrent = false;
        }

        thread.IsCurrent = true;
        thread.IsCollapsed = false;
        CurrentThread = thread;
        await RevealAsync(thread.Path, thread.IsOutdated ? LineSide.Right : thread.Side, thread.IsOutdated ? 0 : thread.Line, thread.CardId).ConfigureAwait(true);
    }

    /// <summary>Opens the Files tab on <paramref name="path"/> and asks the view to bring the line (or card) into view.</summary>
    public async Task RevealAsync(string path, LineSide side, int line, string? cardId = null)
    {
        ShowTab(PullRequestTab.Files);
        if (!ScopeEndsAtHead)
        {
            await ClearScopeAsync().ConfigureAwait(true);
        }

        var file = (Scope is null ? Files : ScopeFiles).FirstOrDefault(f => f.Path == path) ?? Files.FirstOrDefault(f => f.Path == path);
        if (file is null)
        {
            return;
        }

        if (SelectedFile?.Path != path)
        {
            await SelectFileAsync(file).ConfigureAwait(true);
        }

        LineRevealRequested?.Invoke(this, new LineRevealRequest(path, side, line, cardId));
    }
}

/// <summary>Bring a line, or the card under it, into view.</summary>
public sealed record LineRevealRequest(string Path, LineSide Side, int Line, string? CardId);
