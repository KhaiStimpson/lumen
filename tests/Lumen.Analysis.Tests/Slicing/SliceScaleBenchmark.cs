using System.Diagnostics;
using System.Text;
using Lumen.Domain;
using Lumen.Roslyn.Slicing;
using Xunit.Abstractions;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>
/// Slicing on a large synthetic repository shaped like a long-lived line-of-business app: many Razor pages and MVC
/// controllers, a service and repository per area, one DbContext naming every entity, shared base classes, helpers and
/// constants named everywhere, page scripts, and tests. The change touches ~130 files across a dozen areas plus the
/// shared files, like a real large PR. Generated in memory; no repository is read.
/// </summary>
public sealed class SliceScaleBenchmark(ITestOutputHelper output)
{
    /// <summary>Opt-in: LUMEN_SLICE_BENCH=&lt;areas&gt; (1500 areas is about 14,000 files) prints each step's time.</summary>
    [Fact]
    public void PrintsEachStepsTimeOnALargeRepository()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("LUMEN_SLICE_BENCH"), out var areas))
        {
            return;
        }

        var (files, facts) = SyntheticRepository.Create(areas, changedAreas: 12);
        output.WriteLine($"{files.Count:N0} files, {facts.Count} changed");
        var (index, graph, result, times) = Run(files, facts);
        output.WriteLine($"index {times.Index.TotalSeconds:0.0} s · links {times.Links.TotalSeconds:0.0} s · slices {times.Slice.TotalSeconds:0.0} s");
        output.WriteLine($"{result.Slices.Count} slices, fell back: {result.FellBack} {result.FallbackReason}");
        Assert.NotNull(index);
    }

    /// <summary>Always on: a mid-sized repository slices well inside the budget, so a regression to quadratic shows.</summary>
    [Fact]
    public void AThousandAreaRepositorySlicesQuickly()
    {
        var (files, facts) = SyntheticRepository.Create(areas: 1000, changedAreas: 12);
        var (_, _, result, times) = Run(files, facts);

        Assert.False(result.FellBack, result.FallbackReason);
        Assert.Contains(result.Slices, s => s.Kind == SliceKind.Feature);
        var total = times.Index + times.Links + times.Slice;
        output.WriteLine($"{files.Count:N0} files: index {times.Index.TotalSeconds:0.0} s · links {times.Links.TotalSeconds:0.0} s · slices {times.Slice.TotalSeconds:0.0} s");
        Assert.True(total < TimeSpan.FromSeconds(20), $"took {total.TotalSeconds:0.0} s");
    }

    private static (RepositoryIndex Index, LinkGraph Graph, SliceResult Result, (TimeSpan Index, TimeSpan Links, TimeSpan Slice) Times) Run(
        Dictionary<string, string> files,
        List<ChangedFileFacts> facts)
    {
        var watch = Stopwatch.StartNew();
        var index = RepositoryIndex.Build(files);
        var indexTime = watch.Elapsed;
        watch.Restart();
        var graph = LinkResolver.Build(index);
        var linkTime = watch.Elapsed;
        watch.Restart();
        var result = Slicer.Slice(graph, facts);
        return (index, graph, result, (indexTime, linkTime, watch.Elapsed));
    }
}

/// <summary>Generates the large repository and its change.</summary>
internal static class SyntheticRepository
{
    public static (Dictionary<string, string> Files, List<ChangedFileFacts> Facts) Create(int areas, int changedAreas)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        const string web = "src/Acme.Web";

        // Shared: one DbContext naming every entity, a base service and page model, constants and helpers used everywhere.
        var db = new StringBuilder("using Microsoft.EntityFrameworkCore;\nnamespace Acme.Web.Data;\npublic class AppDbContext : DbContext\n{\n");
        var program = new StringBuilder("using Acme.Web.Services;\nvar builder = WebApplication.CreateBuilder(args);\n");
        for (var i = 0; i < areas; i++)
        {
            db.Append(System.Globalization.CultureInfo.InvariantCulture, $"    public DbSet<Area{i}Item> Area{i}Items => Set<Area{i}Item>();\n");
            program.Append(System.Globalization.CultureInfo.InvariantCulture, $"builder.Services.AddScoped<IArea{i}Service, Area{i}Service>();\n");
        }

        files[$"{web}/Data/AppDbContext.cs"] = db.Append("}\n").ToString();
        files[$"{web}/Program.cs"] = program.Append("var app = builder.Build();\napp.MapRazorPages();\napp.MapControllers();\napp.Run();\n").ToString();
        files[$"{web}/Services/ServiceBase.cs"] = "namespace Acme.Web.Services;\npublic abstract class ServiceBase\n{\n    protected readonly Acme.Web.Data.AppDbContext Db;\n    protected ServiceBase(Acme.Web.Data.AppDbContext db) => Db = db;\n    protected static string Clean(string s) => Helpers.Text.Trim(s);\n}\n";
        files[$"{web}/Pages/AppPageModel.cs"] = "using Microsoft.AspNetCore.Mvc.RazorPages;\nnamespace Acme.Web.Pages;\npublic abstract class AppPageModel : PageModel\n{\n    public string Title { get; set; } = Constants.AppName;\n}\n";
        files[$"{web}/Constants.cs"] = "namespace Acme.Web;\npublic static class Constants\n{\n    public const string AppName = \"Acme\";\n    public const int PageSize = 25;\n}\n";
        files[$"{web}/Helpers/Text.cs"] = "namespace Acme.Web.Helpers;\npublic static class Text\n{\n    public static string Trim(string s) => s.Trim();\n}\n";
        files[$"{web}/Helpers/Grid.cs"] = "namespace Acme.Web.Helpers;\npublic static class Grid\n{\n    public static int Pages(int total) => (total + Constants.PageSize - 1) / Constants.PageSize;\n}\n";
        files[$"{web}/Pages/Shared/_Layout.cshtml"] = "<!DOCTYPE html>\n<html><body>@RenderBody()<script src=\"~/js/site.js\"></script></body></html>\n";
        files[$"{web}/Pages/Shared/_Grid.cshtml"] = "@model Acme.Web.Helpers.GridModel\n<table class=\"grid\"></table>\n";
        files[$"{web}/Helpers/GridModel.cs"] = "namespace Acme.Web.Helpers;\npublic class GridModel\n{\n    public int Total { get; set; }\n}\n";
        files[$"{web}/wwwroot/js/site.js"] = "function grid(url) { return fetch(url); }\n$('#modal').hide();\n";
        files[$"{web}/Pages/Shared/_Toolbar.cshtml"] = "<div id=\"toolbar\"></div>\n<script src=\"~/js/toolbar.js\"></script>\n";
        files[$"{web}/wwwroot/js/toolbar.js"] = "$('#grid').data('kendoGrid');\n$('#save').prop('disabled', false);\n$.ajax({ url: '?handler=Refresh' });\n$('#toolbar').show();\n";

        // Vendored, minified script outside wwwroot/lib (Scripts/kendo is common in older apps): one 3 MB line.
        var vendor = new StringBuilder();
        for (var v = 0; vendor.Length < 3_000_000; v++)
        {
            vendor.Append(System.Globalization.CultureInfo.InvariantCulture, $"var a{v}=function(e){{return $(\"#k{v % 50}\").data(e)||$.ajax({{url:\"/x?handler=h{v}\"}})}};");
        }

        files[$"{web}/Scripts/kendo/kendo.all.min.js"] = vendor.ToString();

        for (var i = 0; i < areas; i++)
        {
            var a = $"Area{i}";
            var next = $"Area{(i + 1) % areas}";
            files[$"{web}/Data/{a}Item.cs"] = $"namespace Acme.Web.Data;\npublic class {a}Item\n{{\n    public int Id {{ get; set; }}\n    public string Name {{ get; set; }} = \"\";\n    public {next}Item? Related {{ get; set; }}\n}}\n";
            files[$"{web}/Models/{a}Dto.cs"] = $"namespace Acme.Web.Models;\npublic record {a}Dto(int Id, string Name);\n";
            files[$"{web}/Services/I{a}Service.cs"] = $"using Acme.Web.Models;\nnamespace Acme.Web.Services;\npublic interface I{a}Service\n{{\n    Task<IReadOnlyList<{a}Dto>> ListAsync(int page);\n    Task SaveAsync({a}Dto dto);\n}}\n";
            files[$"{web}/Services/{a}Service.cs"] =
                $"using Acme.Web.Data;\nusing Acme.Web.Models;\nusing Microsoft.EntityFrameworkCore;\nnamespace Acme.Web.Services;\n" +
                $"public class {a}Service : ServiceBase, I{a}Service\n{{\n    public {a}Service(AppDbContext db) : base(db) {{ }}\n" +
                $"    public async Task<IReadOnlyList<{a}Dto>> ListAsync(int page) =>\n        await Db.{a}Items.Skip(page * Constants.PageSize).Take(Constants.PageSize).Select(x => new {a}Dto(x.Id, Clean(x.Name))).ToListAsync();\n" +
                $"    public async Task SaveAsync({a}Dto dto)\n    {{\n        var item = await Db.{a}Items.FindAsync(dto.Id) ?? new {a}Item();\n        item.Name = Helpers.Text.Trim(dto.Name);\n        await Db.SaveChangesAsync();\n    }}\n}}\n";
            files[$"{web}/Pages/{a}/Index.cshtml"] = $"@page\n@model Acme.Web.Pages.{a}.IndexModel\n<h1>@Model.Title</h1>\n<partial name=\"_Grid\" model=\"Model.Grid\" />\n<partial name=\"_Toolbar\" />\n<div id=\"grid\"></div>\n<button id=\"save\">Save</button>\n<div id=\"modal\"></div>\n<form method=\"post\" asp-page-handler=\"Save\"></form>\n<script src=\"~/js/{a.ToLowerInvariant()}.js\"></script>\n";
            files[$"{web}/Pages/{a}/Index.cshtml.cs"] =
                $"using Acme.Web.Helpers;\nusing Acme.Web.Models;\nusing Acme.Web.Services;\nusing Microsoft.AspNetCore.Mvc;\nnamespace Acme.Web.Pages.{a};\n" +
                $"public class IndexModel : AppPageModel\n{{\n    private readonly I{a}Service _service;\n    public IndexModel(I{a}Service service) => _service = service;\n" +
                $"    public IReadOnlyList<{a}Dto> Items {{ get; private set; }} = [];\n    public GridModel Grid {{ get; private set; }} = new();\n" +
                $"    public async Task OnGetAsync(int page = 0)\n    {{\n        Items = await _service.ListAsync(page);\n        Grid = new GridModel {{ Total = Items.Count }};\n    }}\n" +
                $"    public async Task<IActionResult> OnPostSaveAsync({a}Dto dto)\n    {{\n        await _service.SaveAsync(dto);\n        return RedirectToPage();\n    }}\n}}\n";
            files[$"{web}/Controllers/{a}Controller.cs"] =
                $"using Acme.Web.Services;\nusing Microsoft.AspNetCore.Mvc;\nnamespace Acme.Web.Controllers;\n[ApiController]\n[Route(\"api/{a.ToLowerInvariant()}\")]\n" +
                $"public class {a}Controller : ControllerBase\n{{\n    private readonly I{a}Service _service;\n    public {a}Controller(I{a}Service service) => _service = service;\n" +
                $"    [HttpGet]\n    public async Task<IActionResult> Get(int page) => Ok(await _service.ListAsync(page));\n}}\n";
            files[$"{web}/wwwroot/js/{a.ToLowerInvariant()}.js"] =
                $"$('#grid').kendoGrid({{ dataSource: {{ transport: {{ read: '/{a}/Index?handler=Items' }} }} }});\n$('#save').on('click', () => $.ajax({{ url: '?handler=Save', type: 'POST' }}));\n$('#modal').hide();\n";
            files[$"tests/Acme.Web.Tests/{a}ServiceTests.cs"] =
                $"using Acme.Web.Services;\nusing Xunit;\nnamespace Acme.Web.Tests;\npublic class {a}ServiceTests\n{{\n    [Fact]\n    public async Task Lists()\n    {{\n        var service = new {a}Service(TestDb.Create());\n        Assert.Empty(await service.ListAsync(0));\n    }}\n}}\n";
        }

        files["tests/Acme.Web.Tests/TestDb.cs"] = "using Acme.Web.Data;\nnamespace Acme.Web.Tests;\npublic static class TestDb\n{\n    public static AppDbContext Create() => new();\n}\n";

        // The change: every file of a dozen areas, plus the shared files a big PR touches.
        var changed = new List<string>
        {
            $"{web}/Data/AppDbContext.cs", $"{web}/Program.cs", $"{web}/Helpers/Grid.cs", $"{web}/Helpers/GridModel.cs",
            $"{web}/Pages/Shared/_Grid.cshtml", $"{web}/wwwroot/js/site.js", $"{web}/Services/ServiceBase.cs",
        };
        var step = Math.Max(1, areas / changedAreas);
        for (var n = 0; n < changedAreas; n++)
        {
            var a = $"Area{n * step}";
            changed.AddRange(files.Keys.Where(p => p.Contains($"/{a}Item.cs", StringComparison.Ordinal) || p.Contains($"/{a}Dto.cs", StringComparison.Ordinal)
                || p.Contains($"/I{a}Service.cs", StringComparison.Ordinal) || p.Contains($"/{a}Service.cs", StringComparison.Ordinal)
                || p.Contains($"/{a}/", StringComparison.Ordinal) || p.Contains($"/{a}Controller.cs", StringComparison.Ordinal)
                || p.EndsWith($"/{a.ToLowerInvariant()}.js", StringComparison.Ordinal) || p.Contains($"/{a}ServiceTests.cs", StringComparison.Ordinal)));
        }

        var facts = changed.Distinct().Select(p =>
        {
            var lines = files[p].Split('\n').Length;
            return new ChangedFileFacts(p, FileChangeKind.Modified, Enumerable.Range(1, lines).ToHashSet(), lines, AllMechanical: false, OpenLines: lines);
        }).ToList();
        return (files, facts);
    }
}
