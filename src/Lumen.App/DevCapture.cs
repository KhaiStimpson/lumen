using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Lumen.App.ViewModels;

namespace Lumen.App;

/// <summary>
/// Development aid (<c>--capture dir --capture-script diff,next,examine</c>): drives the real app through a few
/// states and renders each to a PNG, so UI changes can be reviewed without a human at the keyboard.
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
        var steps = (script ?? "diff").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < steps.Length; i++)
        {
            await ApplyAsync(viewModel, steps[i]).ConfigureAwait(true);
            await Task.Delay(900).ConfigureAwait(true);
            Save(window, Path.Combine(directory, $"{i + 1:00}-{steps[i].Replace(':', '-')}.png"));
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static async Task ApplyAsync(MainWindowViewModel viewModel, string step)
    {
        var pr = viewModel.PullRequest;
        switch (step)
        {
            case "next" when pr is not null:
                await pr.NextPointAsync().ConfigureAwait(true);
                break;
            case "prev" when pr is not null:
                await pr.PreviousPointAsync().ConfigureAwait(true);
                break;
            case "examine" when pr is not null:
                await pr.ExamineAsync(null).ConfigureAwait(true);
                break;
            case "evidence" when pr is not null:
                pr.ExamineTab = "evidence";
                break;
            case "precedent" when pr is not null:
                pr.ExamineTab = "precedent";
                break;
            case "comment" when pr is not null:
                pr.StartComment(null);
                break;
            case "close" when pr is not null:
                pr.CloseExamine();
                break;
            case "dismiss" when pr is not null:
                await pr.DismissAsync(null).ConfigureAwait(true);
                break;
            case "source" when pr?.CurrentPoint?.PrecedentExamples is [var example, ..]:
                await pr.OpenSourceAsync(example).ConfigureAwait(true);
                break;
            case "home":
                await viewModel.GoHomeCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "generated" when pr is not null:
                pr.ToggleGenerated();
                break;

            // Plan v2 states.
            case "inbox":
                await viewModel.GoHomeCommand.ExecuteAsync(null).ConfigureAwait(true);
                break;
            case "open" when viewModel.Home.AllItems.FirstOrDefault() is { } item:
                await viewModel.OpenAsync(item.Reference).ConfigureAwait(true);
                await Task.Delay(1500).ConfigureAwait(true);
                break;
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
                pr.ShowTab(PullRequestTab.Files);
                break;
            case "since" when pr is { CanShowSinceLastReview: true }:
                pr.ShowSinceLastReview = true;
                await Task.Delay(500).ConfigureAwait(true);
                break;
            case "all" when pr is not null:
                await pr.ClearScopeAsync().ConfigureAwait(true);
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
                pr.FindQuery = "Enrichment";
                break;
            case "palette":
                viewModel.Palette.Open();
                break;
            case "risk" when pr?.Walkthrough?.Risks is [var risk, ..]:
                await pr.SelectRiskAsync(risk).ConfigureAwait(true);
                break;
            case "group" when pr is not null:
                await pr.NextGroupAsync().ConfigureAwait(true);
                break;
            case "order" when pr is not null:
                pr.GroupOrder = !pr.GroupOrder;
                break;
            case "dark" when Application.Current is { } app:
                app.RequestedThemeVariant = ThemeVariant.Dark;
                break;
            case "light" when Application.Current is { } app:
                app.RequestedThemeVariant = ThemeVariant.Light;
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
