using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Lumen.App.ViewModels;

namespace Lumen.App;

/// <summary>
/// Development aid (<c>--capture dir --capture-script slice,fold,flag</c>): drives the real app through a few states
/// and renders each to a PNG, so UI changes can be reviewed without a human at the keyboard.
/// </summary>
internal static class DevCapture
{
    public static async Task RunAsync(Window window, MainWindowViewModel viewModel, string directory, string? script)
    {
        Directory.CreateDirectory(directory);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline && viewModel.PullRequest is not { IsAnalysing: false } && viewModel.PullRequest?.Error is null
               && !(viewModel.IsHome && !viewModel.Home.IsLoading))
        {
            await Task.Delay(200).ConfigureAwait(true);
        }

        await Task.Delay(900).ConfigureAwait(true);
        var steps = (script ?? "files").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < steps.Length; i++)
        {
            await ApplyAsync(viewModel, steps[i]).ConfigureAwait(true);
            await Task.Delay(900).ConfigureAwait(true);

            // "size:" and theme steps only set things up; everything else is a state worth a picture.
            if (!steps[i].StartsWith("size:", StringComparison.Ordinal) && steps[i] is not ("dark" or "light" or "nocapture"))
            {
                Save(window, Path.Combine(directory, $"{i + 1:00}-{Sanitise(steps[i])}.png"));
            }
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static string Sanitise(string step) =>
        string.Concat(step.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-'));

    /// <summary>
    /// Capture steps. Overhaul: <c>slice</c>, <c>slice:N</c> (the Nth slice), <c>slicefile:name</c>, <c>nextslice</c>,
    /// <c>files</c> (the file tree), <c>fold</c> (open the current file's folds), <c>slicefolds</c>, <c>flag</c> (the
    /// next flag), <c>mark</c> (press Mark reviewed), <c>since</c> (since my last look), <c>all</c>, <c>pane</c> (toggle
    /// the right pane), <c>unlinked</c>, <c>shared</c>, <c>mechanical</c>. Review client: <c>inbox</c>,
    /// <c>open</c>, <c>local</c> (open the first local branch), <c>conversation</c>, <c>commits</c>, <c>checks</c>, <c>thread</c>, <c>draft</c>, <c>tray</c>,
    /// <c>split</c>, <c>find</c>, <c>palette</c>, <c>file:name</c>. Setup: <c>dark</c>, <c>light</c>, <c>size:WxH</c>.
    /// </summary>
    private static async Task ApplyAsync(MainWindowViewModel viewModel, string step)
    {
        var pr = viewModel.PullRequest;
        switch (step)
        {
            case "home" or "inbox":
                await viewModel.GoHomeCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "open" when viewModel.Home.AllItems.FirstOrDefault() is { } item:
                await viewModel.OpenAsync(item.Reference).ConfigureAwait(true);
                await Task.Delay(1500).ConfigureAwait(true);
                break;
            case "local" when viewModel.Home.LocalBranches.FirstOrDefault() is { } branch:
                await viewModel.Home.OpenLocalItemCommand.ExecuteAsync(branch).ConfigureAwait(true);
                await Task.Delay(1500).ConfigureAwait(true);
                break;
            case "generated" when pr is not null:
                pr.ToggleGenerated();
                break;

            // Slices, folds, flags and marks.
            case "slice" when pr is { HasSlices: true }:
                pr.ShowSlices();
                break;
            case var s when s.StartsWith("slice:", StringComparison.Ordinal) && pr is not null
                            && int.TryParse(s["slice:".Length..], out var n) && n >= 1 && n <= pr.Slices.Count:
                await pr.SelectSliceAsync(pr.Slices[n - 1]).ConfigureAwait(true);
                break;
            case var s when s.StartsWith("slicefile:", StringComparison.Ordinal) && pr?.CurrentSlice is { } current:
                await pr.SelectSliceFileAsync(current.Files.FirstOrDefault(f => f.Name == s["slicefile:".Length..])).ConfigureAwait(true);
                break;
            case "nextslice" when pr is not null:
                await pr.NextSliceAsync().ConfigureAwait(true);
                break;
            case "nextfile" when pr is not null:
                await pr.NextFileAsync().ConfigureAwait(true);
                break;
            case "shared" when pr?.Slices.FirstOrDefault(x => x.IsShared) is { } shared:
                await pr.SelectSliceAsync(shared).ConfigureAwait(true);
                break;
            case "unlinked" when pr?.Slices.FirstOrDefault(x => x.IsUnlinked) is { } unlinked:
                await pr.SelectSliceAsync(unlinked).ConfigureAwait(true);
                break;
            case "mechanical" when pr?.Slices.FirstOrDefault(x => x.IsMechanicalOnly) is { } mechanical:
                await pr.SelectSliceAsync(mechanical).ConfigureAwait(true);
                break;
            case "fold" when pr is not null:
                pr.ToggleFileFolds();
                break;
            case "slicefolds" when pr is not null:
                pr.OpenSliceFolds();
                break;
            case "flag" when pr is not null:
                await pr.NextFlagAsync().ConfigureAwait(true);
                break;
            case "mark" when pr is not null:
                await pr.MarkSliceReviewedAsync().ConfigureAwait(true);
                break;
            case "since" when pr is not null:
                pr.ShowSinceLastLook = true;
                await Task.Delay(800).ConfigureAwait(true);
                break;
            case "all" when pr is not null:
                pr.ShowSinceLastLook = false;
                await pr.ClearScopeAsync().ConfigureAwait(true);
                break;
            case "pane" when pr is not null:
                pr.ToggleRightPane();
                break;

            // Review client states.
            case "conversation" when pr is not null:
                pr.ShowTab(PullRequestTab.Conversation);
                break;
            case "commits" when pr is not null:
                pr.ShowTab(PullRequestTab.Commits);
                break;
            case "checks" when pr is not null:
                pr.ShowTab(PullRequestTab.Checks);
                break;
            case "files" when pr is not null:
                pr.ShowFiles();
                break;
            case "thread" when pr is not null:
                await pr.NextThreadAsync().ConfigureAwait(true);
                break;
            case "draft" when pr?.CurrentDiff?.Rows.FirstOrDefault(r => r.Kind == Diff.DiffRowKind.Added) is { } row:
                pr.StartLineComment(Diff.LineSide.Right, row.NewNumber)?.Body = "A comment written in the gutter composer.";
                break;
            case "tray" when pr is not null:
                pr.IsTrayOpen = !pr.IsTrayOpen;
                break;
            case "split" when pr is not null:
                pr.ToggleSplitView();
                break;
            case "find" when pr is not null:
                pr.OpenFind();
                pr.FindQuery = "Order";
                break;
            case "palette":
                viewModel.Palette.Open();
                break;
            case "dark" when Application.Current is { } app:
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;
            case "light" when Application.Current is { } app:
                app.RequestedThemeVariant = ThemeVariant.Light;
                break;
            case var s when s.StartsWith("size:", StringComparison.Ordinal)
                            && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } main }:
                var parts = s["size:".Length..].Split('x');
                main.Width = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                main.Height = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                break;
            case var s when s.StartsWith("file:", StringComparison.Ordinal) && pr is not null:
                var name = s["file:".Length..];
                if (pr.Files.FirstOrDefault(f => f.Name == name) is { } file)
                {
                    await pr.SelectFileAsync(file).ConfigureAwait(true);
                }

                break;
        }
    }

    public static void Save(Control control, string path)
    {
        var size = control.Bounds.Size;
        var scale = TopLevel.GetTopLevel(control)?.RenderScaling ?? 1;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)),
            new Vector(96 * scale, 96 * scale));
        bitmap.Render(control);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }
}
