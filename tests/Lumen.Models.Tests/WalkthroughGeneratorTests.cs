using Lumen.Domain;
using Lumen.Models.Walkthrough;

namespace Lumen.Models.Tests;

public sealed class WalkthroughGeneratorTests
{
    private static readonly ChangedFile Provider = Files.File("src/Providers/AbnProvider.cs", ["class AbnProvider", "{", "  void Fetch() { try { } catch { } }", "}"]);
    private static readonly ChangedFile Model = Files.File("src/Models/Contact.cs", ["class Contact", "{", "}"]);
    private static readonly ChangedFile Test = Files.File("tests/AbnProviderTests.cs", ["class AbnProviderTests { }"]);
    private static readonly ChangedFile Migration = Files.File("Migrations/2026_Init.cs", ["// generated"], mechanical: true);

    private static WalkthroughInput Input(params ChangedFile[] files) => new()
    {
        Title = "Add ABN provider",
        Description = "Adds an ABN lookup provider.",
        CommitHeadlines = ["Add provider"],
        Files = files,
        HeadSha = "head",
    };

    private static string Answer(ModelRequest request) => request.Purpose switch
    {
        "walkthrough/intent" => """{"intent":"Adds an ABN registry provider."}""",
        "walkthrough/file" when request.User.Contains("AbnProvider.cs", StringComparison.Ordinal) && !request.User.Contains("Tests", StringComparison.Ordinal) =>
            """{"summary":"New provider.","kind":"logic","risks":[{"line":3,"category":"error-handling","reason":"Swallows every exception."},{"line":99,"category":"other","reason":"Invented line."}]}""",
        "walkthrough/file" => "```json\n{\"summary\":\"Something.\",\"kind\":\"logic\",\"risks\":[]}\n```",
        "walkthrough/groups" => """
            {"groups":[
              {"title":"Contact model","summary":"Data first.","paths":["src/Models/Contact.cs"]},
              {"title":"ABN provider","summary":"The new provider.","paths":["src/Providers/AbnProvider.cs","src/Invented.cs","src/Models/Contact.cs"]},
              {"title":"Ghost","summary":"Nothing real.","paths":["nope.cs"]}
            ],"descriptionMismatch":""}
            """,
        _ => "{}",
    };

    [Fact]
    public async Task ProducesIntentOrderedGroupsAndGroundedRisks()
    {
        var model = new ScriptedModel(Answer);
        var updates = new List<Domain.Walkthrough>();

        var result = await new WalkthroughGenerator(model, new WalkthroughOptions()).RunAsync(
            Input(Provider, Model, Test, Migration), updates.Add, CancellationToken.None);

        Assert.Equal(WalkthroughStatus.Complete, result.Status);
        Assert.Equal("Adds an ABN registry provider.", result.Intent);
        Assert.Equal(["Contact model", "ABN provider", "Other changes", "Generated and mechanical"], result.Groups.Select(g => g.Title));
        Assert.Equal([1, 2, 3, 4], result.Groups.Select(g => g.Order));
        Assert.Equal(["src/Providers/AbnProvider.cs"], result.Groups[1].Paths);
        Assert.Equal(["tests/AbnProviderTests.cs"], result.Groups[2].Paths);
        Assert.True(result.Groups[3].IsMechanical);

        var risk = Assert.Single(result.Risks);
        Assert.Equal(new CodeLocation("src/Providers/AbnProvider.cs", 3, 3), risk.Location);
        Assert.Equal(RiskCategory.ErrorHandling, risk.Category);
        Assert.Equal("glm-test · walkthrough/v1", result.Producer);

        // Running → … → complete, with progress in between.
        Assert.Equal(WalkthroughStatus.Running, updates[0].Status);
        Assert.Contains(updates, u => u.FilesDone == 3 && u.FilesTotal == 3);
        Assert.Contains(updates, u => u.Status == WalkthroughStatus.Running && u.Groups.Count > 0);
    }

    [Fact]
    public async Task MechanicalFilesAreNeverSent()
    {
        var model = new ScriptedModel(Answer);

        await new WalkthroughGenerator(model, new WalkthroughOptions()).RunAsync(Input(Provider, Migration), _ => { }, CancellationToken.None);

        Assert.DoesNotContain(model.Requests, r => r.Purpose == "walkthrough/file" && r.User.Contains("generated", StringComparison.Ordinal));
        var intent = model.Requests.Single(r => r.Purpose == "walkthrough/intent");
        Assert.Contains("Migrations/2026_Init.cs (+1 −0) [generated]", intent.User, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDiffBudgetLimitsWhatIsSent()
    {
        var model = new ScriptedModel(Answer);
        var big = Files.File("src/Big.cs", [.. Enumerable.Range(0, 2000).Select(i => $"line {i} with some code")]);

        var result = await new WalkthroughGenerator(model, new WalkthroughOptions { MaxDiffBytes = 3200, MaxFileBytes = 3000 })
            .RunAsync(Input(big, Model), _ => { }, CancellationToken.None);

        var sent = model.Requests.Where(r => r.Purpose == "walkthrough/file").ToList();
        Assert.Single(sent);
        Assert.Contains("(the rest of this file's diff was cut)", sent[0].User, StringComparison.Ordinal);
        Assert.Contains("summarised by path and size only", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IfEveryFileFailsTheWalkthroughFailsQuietly()
    {
        var model = new ScriptedModel(Answer)
        {
            FailWhen = r => r.Purpose == "walkthrough/file" ? new ModelException(ModelFailure.Unavailable, "down") : null,
        };

        var result = await new WalkthroughGenerator(model, new WalkthroughOptions()).RunAsync(Input(Provider, Model), _ => { }, CancellationToken.None);

        Assert.Equal(WalkthroughStatus.Failed, result.Status);
        Assert.Contains("review works as usual", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedGroupingFallsBackToOneGroup()
    {
        var model = new ScriptedModel(Answer)
        {
            FailWhen = r => r.Purpose == "walkthrough/groups" ? new ModelException(ModelFailure.Timeout, "slow") : null,
        };

        var result = await new WalkthroughGenerator(model, new WalkthroughOptions()).RunAsync(Input(Provider, Model), _ => { }, CancellationToken.None);

        Assert.Equal(WalkthroughStatus.Complete, result.Status);
        Assert.Equal(["Changes"], result.Groups.Select(g => g.Title));
        Assert.Equal(2, result.Groups[0].Paths.Count);
    }

    [Fact]
    public async Task ManyCandidateRisksAreNarrowedByTheModelAndStillGrounded()
    {
        var files = Enumerable.Range(1, 4).Select(i => Files.File($"src/F{i}.cs", ["a", "b", "c"])).ToArray();
        var model = new ScriptedModel(r => r.Purpose switch
        {
            "walkthrough/file" => """{"summary":"x","kind":"logic","risks":[{"line":1,"category":"security","reason":"a"},{"line":2,"category":"concurrency","reason":"b"}]}""",
            "walkthrough/risks" => """{"picks":[{"candidate":7,"reason":"Most important"},{"candidate":0,"reason":""},{"candidate":99,"reason":"bad"}]}""",
            _ => Answer(r),
        });

        var result = await new WalkthroughGenerator(model, new WalkthroughOptions()).RunAsync(Input(files), _ => { }, CancellationToken.None);

        Assert.Equal(2, result.Risks.Count);
        Assert.Equal(("src/F4.cs", 2, "Most important"), (result.Risks[0].Location.Path, result.Risks[0].Location.StartLine, result.Risks[0].Reason));
        Assert.Equal("a", result.Risks[1].Reason);
    }

    [Fact]
    public async Task ASinceLastReviewWalkthroughSaysSo()
    {
        var model = new ScriptedModel(Answer);

        var result = await new WalkthroughGenerator(model, new WalkthroughOptions()).RunAsync(
            Input(Provider) with { SinceSha = "old" }, _ => { }, CancellationToken.None);

        Assert.Equal("old", result.SinceSha);
        Assert.Contains("since they last looked", model.Requests.Single(r => r.Purpose == "walkthrough/intent").System, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsHashFollowsTheModelAndLimits()
    {
        var a = new WalkthroughGenerator(new ScriptedModel(Answer), new WalkthroughOptions()).SettingsHash;
        var b = new WalkthroughGenerator(new ScriptedModel(Answer), new WalkthroughOptions { MaxDiffBytes = 1 }).SettingsHash;
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void RenderedDiffCarriesHeadLineNumbers()
    {
        var text = WalkthroughGenerator.RenderDiff(Provider, 10_000);

        Assert.Contains("+    3|   void Fetch()", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GroundingDropsRisksOutsideTheDiff()
    {
        var risks = WalkthroughGrounding.GroundRisks(
            [("src/Models/Contact.cs", 2, RiskCategory.Other, "ok"), ("src/Models/Contact.cs", 40, RiskCategory.Other, "no line"), ("x.cs", 1, RiskCategory.Other, "no file"), ("src/Models/Contact.cs", 2, RiskCategory.Security, "duplicate")],
            [Model],
            5);

        Assert.Equal("ok", Assert.Single(risks).Reason);
    }
}
