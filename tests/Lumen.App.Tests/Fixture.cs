using Avalonia.Headless;
using Avalonia.Threading;
using Lumen.App.Services;
using Lumen.App.ViewModels;
using Lumen.Contracts;

namespace Lumen.App.Tests;

/// <summary>The recorded andrew-crm#58 session, and helpers to drive it on the Avalonia dispatcher.</summary>
internal static class Fixture
{
    public const string Reference = "KhaiStimpson/andrew-crm#58";

    public const string TopPointTitle = "Doesn't surface failures as ProviderOperationException";

    public const string TopPointPath = "src/AndrewCrm.Web/Services/Enrichment/AbrAbnProvider.cs";

    public const int TopPointLine = 60;

    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "fixtures", "andrew-crm-58");

    public static PullRequestRef PullRequest => new() { Owner = "KhaiStimpson", Name = "andrew-crm", Number = 58 };

    public static FixtureReviewSource CreateSource() => new(DirectoryPath);

    /// <summary>Starts a view model over the fixture and waits for the analysis to complete.</summary>
    public static async Task<PullRequestViewModel> LoadAsync(IReviewSource source)
    {
        var pr = new PullRequestViewModel(source, PullRequest);
        pr.Start();
        await WaitUntilAsync(() => !pr.IsAnalysing);
        Assert.Null(pr.Error);
        return pr;
    }

    /// <summary>Runs dispatcher jobs and render ticks until <paramref name="condition"/> holds.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met in time.");
            }

            await PumpAsync(1);
        }
    }

    /// <summary>Lets queued work (async continuations, layout, animations) run for a few frames.</summary>
    public static async Task PumpAsync(int frames = 5)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
