using Lumen.Analysis.Tests.Support;
using Lumen.Domain;
using Lumen.Roslyn.Flags;

namespace Lumen.Analysis.Tests.Flags;

/// <summary>Phase 7: one detector per row of the red-flag table, each with the near-misses that must stay quiet.</summary>
public sealed class RedFlagDetectorTests
{
    private static IReadOnlyList<RedFlag> Flags(params TestFile[] files) => Flags(new ReviewSettings(), files);

    private static IReadOnlyList<RedFlag> Flags(ReviewSettings settings, params TestFile[] files)
    {
        var snapshot = TestDiffs.Snapshot(string.Concat(files.Select(f => TriageHarness.Diff(f.Path, f.Base, f.Head))));
        var sources = new TriageSources(
            files.Where(f => f.Base is not null).ToDictionary(f => f.Path, f => f.Base!),
            files.Where(f => f.Head is not null).ToDictionary(f => f.Path, f => f.Head!));
        return RedFlags.Detect(snapshot, sources, settings);
    }

    private static IReadOnlyList<RedFlag> Changed(string path, string before, string after) => Flags(new TestFile(path, before, after));

    private static RedFlag One(IReadOnlyList<RedFlag> flags, FlagKind kind) => Assert.Single(flags, f => f.Kind == kind);

    private static void None(IReadOnlyList<RedFlag> flags, FlagKind kind) => Assert.DoesNotContain(flags, f => f.Kind == kind);

    // ---------------------------------------------------------------- weakened tests

    private const string TestBase = """
        namespace Shop.Tests;

        public class OrderTests
        {
            [Fact]
            public void Saves()
            {
                var order = new Order();
                Assert.True(order.Save());
                Assert.Equal(1, order.Version);
            }

            [Fact]
            public void Rejects()
            {
                Assert.Throws<ArgumentException>(() => new Order(null));
            }
        }
        """;

    [Fact]
    public void ASkipAddedToATestIsFlagged()
    {
        var flag = One(Changed("tests/OrderTests.cs", TestBase, TestBase.Replace("    [Fact]\n    public void Rejects", "    [Fact(Skip = \"flaky\")]\n    public void Rejects", StringComparison.Ordinal)), FlagKind.WeakenedTests);

        Assert.Equal(DiffSide.Right, flag.Side);
        Assert.Equal("OrderTests.Rejects", flag.Member);
        Assert.StartsWith("Test skipped", flag.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeletedTestIsFlaggedButOneMovedToAnotherFileIsNot()
    {
        var without = TestBase.Replace("\n    [Fact]\n    public void Rejects()\n    {\n        Assert.Throws<ArgumentException>(() => new Order(null));\n    }\n", "", StringComparison.Ordinal);
        var deleted = One(Changed("tests/OrderTests.cs", TestBase, without), FlagKind.WeakenedTests);
        Assert.Equal(DiffSide.Left, deleted.Side);
        Assert.Equal("Test `Rejects` deleted.", deleted.Message);

        var moved = "namespace Shop.Tests;\n\npublic class OrderValidationTests\n{\n    [Fact]\n    public void Rejects()\n    {\n        Assert.Throws<ArgumentException>(() => new Order(null));\n    }\n}\n";
        None(Flags(new TestFile("tests/OrderTests.cs", TestBase, without), new TestFile("tests/OrderValidationTests.cs", null, moved)), FlagKind.WeakenedTests);
    }

    [Fact]
    public void AnAssertRemovedWithNoReplacementIsFlaggedButARewrittenAssertIsNot()
    {
        var removed = TestBase.Replace("        Assert.Equal(1, order.Version);\n", "", StringComparison.Ordinal);
        Assert.Contains("1 assert removed from `Saves`", One(Changed("tests/OrderTests.cs", TestBase, removed), FlagKind.WeakenedTests).Message, StringComparison.Ordinal);

        var rewritten = TestBase.Replace("Assert.Equal(1, order.Version);", "Assert.Equal(2, order.Version);", StringComparison.Ordinal);
        None(Changed("tests/OrderTests.cs", TestBase, rewritten), FlagKind.WeakenedTests);
    }

    [Theory]
    [InlineData("Assert.True(true);", true)]
    [InlineData("Assert.Equal(order.Version, order.Version);", true)]
    [InlineData("Assert.Equal(expected, order.Version);", false)]
    [InlineData("Assert.True(order.IsValid);", false)]
    public void AVacuousAssertIsFlagged(string assert, bool flagged)
    {
        var head = TestBase.Replace("        Assert.Equal(1, order.Version);\n", $"        Assert.Equal(1, order.Version);\n        {assert}\n", StringComparison.Ordinal);

        Assert.Equal(flagged, Changed("tests/OrderTests.cs", TestBase, head).Any(f => f.Kind == FlagKind.WeakenedTests));
    }

    [Theory]
    [InlineData("it.only('saves', () => {});", true)]
    [InlineData("xit('saves', () => {});", true)]
    [InlineData("it('only saves once', () => {});", false)]
    public void FocusedOrDisabledJsTestsAreFlagged(string line, bool flagged)
    {
        var flags = Changed("src/js/cart.test.js", "describe('cart', () => {\n});\n", $"describe('cart', () => {{\n  {line}\n}});\n");

        Assert.Equal(flagged, flags.Any(f => f.Kind == FlagKind.WeakenedTests));
    }

    // ---------------------------------------------------------------- antiforgery

    [Fact]
    public void IgnoreAntiforgeryTokenAndAspAntiforgeryFalseAreFlagged()
    {
        var page = "namespace Shop.Pages;\n\npublic class BulkModel : PageModel\n{\n}\n";
        One(Changed("src/Pages/Bulk.cshtml.cs", page, page.Replace("public class BulkModel", "[IgnoreAntiforgeryToken]\npublic class BulkModel", StringComparison.Ordinal)), FlagKind.AntiforgeryOff);

        var view = "@page\n<form method=\"post\">\n</form>\n";
        One(Changed("src/Pages/Bulk.cshtml", view, view.Replace("<form method=\"post\">", "<form method=\"post\" asp-antiforgery=\"false\">", StringComparison.Ordinal)), FlagKind.AntiforgeryOff);
    }

    [Theory]
    [InlineData("$.post('?handler=Apply', { ids: ids });", true)]
    [InlineData("$.post('?handler=Apply', $form.serialize());", false)]
    [InlineData("$.ajax({ url: '?handler=Apply', type: 'POST', data: { id: 1 } });", true)]
    [InlineData("$.ajax({ url: '?handler=Apply', type: 'POST', headers: { RequestVerificationToken: token } });", false)]
    [InlineData("$.ajax({ url: '?handler=Rows', type: 'GET' });", false)]
    public void AJQueryPostWithoutTheTokenIsFlagged(string call, bool flagged)
    {
        var flags = Changed("src/wwwroot/js/bulk.js", "$(function () {\n});\n", $"$(function () {{\n    {call}\n}});\n");

        Assert.Equal(flagged, flags.Any(f => f.Kind == FlagKind.AntiforgeryOff));
    }

    [Fact]
    public void AJQueryPostSendingTheHeaderBuiltElsewhereInTheFileIsNotFlagged()
    {
        var head = "function headers() { return { RequestVerificationToken: $('input[name=__RequestVerificationToken]').val() }; }\n$(function () {\n    $.ajax({ url: '?handler=Add', type: 'POST', headers: headers(), data: {} });\n});\n";

        None(Changed("src/wwwroot/js/notes.js", "$(function () {\n});\n", head), FlagKind.AntiforgeryOff);
    }

    // ---------------------------------------------------------------- authorisation

    private const string ProgramBase = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/Admin");
            options.Conventions.AuthorizeFolder("/Invoices");
        });
        builder.Services.AddAuthorization();

        var app = builder.Build();
        app.Run();
        """;

    [Fact]
    public void ARemovedAuthorizeFolderIsFlaggedButAMovedOneIsNot()
    {
        var flag = One(Changed("src/Program.cs", ProgramBase, ProgramBase.Replace("    options.Conventions.AuthorizeFolder(\"/Invoices\");\n", "", StringComparison.Ordinal)), FlagKind.AuthorisationLoosened);
        Assert.Equal(DiffSide.Left, flag.Side);
        Assert.Equal(5, flag.Line);

        var moved = ProgramBase.Replace("    options.Conventions.AuthorizeFolder(\"/Admin\");\n    options.Conventions.AuthorizeFolder(\"/Invoices\");\n", "    options.Conventions.AuthorizeFolder(\"/Invoices\");\n    options.Conventions.AuthorizeFolder(\"/Admin\");\n", StringComparison.Ordinal);
        None(Changed("src/Program.cs", ProgramBase, moved), FlagKind.AuthorisationLoosened);
    }

    [Theory]
    [InlineData("    options.Conventions.AllowAnonymousToPage(\"/Invoices/Export\");\n")]
    [InlineData("    options.FallbackPolicy = null;\n")]
    public void AnonymousConventionsAndFallbackChangesAreFlagged(string line)
    {
        var head = ProgramBase.Replace("    options.Conventions.AuthorizeFolder(\"/Invoices\");\n", "    options.Conventions.AuthorizeFolder(\"/Invoices\");\n" + line, StringComparison.Ordinal);

        One(Changed("src/Program.cs", ProgramBase, head), FlagKind.AuthorisationLoosened);
    }

    [Fact]
    public void AuthorizeRemovedOrAllowAnonymousAddedOnAPageIsFlagged()
    {
        var page = "namespace Shop.Pages;\n\n[Authorize]\npublic class ExportModel : PageModel\n{\n    public void OnGet() { }\n}\n";
        One(Changed("src/Pages/Export.cshtml.cs", page, page.Replace("[Authorize]\n", "", StringComparison.Ordinal)), FlagKind.AuthorisationLoosened);
        One(Changed("src/Pages/Export.cshtml.cs", page, page.Replace("    public void OnGet() { }", "    [AllowAnonymous]\n    public void OnGet() { }", StringComparison.Ordinal)), FlagKind.AuthorisationLoosened);
    }

    // ---------------------------------------------------------------- over-posting

    private const string EditPage = """
        namespace Shop.Pages;

        public class EditModel : PageModel
        {
            [BindProperty]
            public InputModel Input { get; set; } = new();

            public string? Message { get; set; }

            public class InputModel
            {
                public string Name { get; set; } = "";
            }
        }
        """;

    [Fact]
    public void ANewPropertyOnAnExistingBoundModelIsFlagged()
    {
        var flag = One(Changed("src/Pages/Edit.cshtml.cs", EditPage, EditPage.Replace("        public string Name { get; set; } = \"\";\n", "        public string Name { get; set; } = \"\";\n\n        public bool IsAdmin { get; set; }\n", StringComparison.Ordinal)), FlagKind.OverPosting);

        Assert.Equal("InputModel.IsAdmin", flag.Member);
        Assert.Contains("`IsAdmin` on `InputModel`", flag.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("        [BindNever]\n        public bool IsAdmin { get; set; }\n")]
    [InlineData("        public bool IsAdmin { get; private set; }\n")]
    [InlineData("        public bool IsAdmin => Name == \"admin\";\n")]
    public void ABoundModelPropertyTheRequestCannotSetIsNotFlagged(string property)
    {
        var head = EditPage.Replace("        public string Name { get; set; } = \"\";\n", "        public string Name { get; set; } = \"\";\n\n" + property, StringComparison.Ordinal);

        None(Changed("src/Pages/Edit.cshtml.cs", EditPage, head), FlagKind.OverPosting);
    }

    [Fact]
    public void ANewBindPropertyOnAnExistingPageIsFlaggedButNewPagesAndUnboundTypesAreNot()
    {
        var head = EditPage.Replace("    public string? Message { get; set; }\n", "    public string? Message { get; set; }\n\n    [BindProperty]\n    public int CustomerId { get; set; }\n", StringComparison.Ordinal);
        One(Changed("src/Pages/Edit.cshtml.cs", EditPage, head), FlagKind.OverPosting);

        None(Flags(new TestFile("src/Pages/Edit.cshtml.cs", null, EditPage)), FlagKind.OverPosting);

        var dto = "namespace Shop;\n\npublic class ReportRow\n{\n    public string Name { get; set; } = \"\";\n}\n";
        None(Changed("src/ReportRow.cs", dto, dto.Replace("}\n}", "}\n\n    public bool IsAdmin { get; set; }\n}", StringComparison.Ordinal)), FlagKind.OverPosting);
    }

    // ---------------------------------------------------------------- guards and validation

    private const string ServiceBase = """
        namespace Shop;

        public class CustomerService
        {
            public string NameOf(Customer customer)
            {
                if (customer == null)
                {
                    return string.Empty;
                }

                return customer.Name.Trim();
            }
        }
        """;

    [Fact]
    public void ARemovedNullGuardIsFlagged()
    {
        var head = ServiceBase.Replace("        if (customer == null)\n        {\n            return string.Empty;\n        }\n\n", "", StringComparison.Ordinal);
        var flag = One(Changed("src/CustomerService.cs", ServiceBase, head), FlagKind.GuardRemoved);

        Assert.Equal(DiffSide.Left, flag.Side);
        Assert.Equal("CustomerService.NameOf", flag.Member);
    }

    [Theory]
    [InlineData("return customer?.Name.Trim() ?? string.Empty;")]
    [InlineData("ArgumentNullException.ThrowIfNull(customer);\n        return customer.Name.Trim();")]
    [InlineData("if (!_cache.TryGet(id, out var customer)) return string.Empty;\n        return customer.Name.Trim();")]
    public void AGuardConvertedOnTheSameExpressionIsNotFlagged(string replacement)
    {
        var head = ServiceBase.Replace("        if (customer == null)\n        {\n            return string.Empty;\n        }\n\n        return customer.Name.Trim();", "        " + replacement, StringComparison.Ordinal);

        None(Changed("src/CustomerService.cs", ServiceBase, head), FlagKind.GuardRemoved);
    }

    [Fact]
    public void AGuardRemovedWithEverythingItGuardedIsNotFlagged()
    {
        var head = ServiceBase.Replace("        if (customer == null)\n        {\n            return string.Empty;\n        }\n\n        return customer.Name.Trim();", "        return _names.For(id);", StringComparison.Ordinal);

        None(Changed("src/CustomerService.cs", ServiceBase, head), FlagKind.GuardRemoved);
    }

    [Fact]
    public void ANullCheckThatFillsTheValueInIsNotAGuard()
    {
        var before = "namespace Shop;\n\npublic class Drafts\n{\n    public Draft Get(Draft? draft)\n    {\n        if (draft is null)\n        {\n            draft = new Draft();\n        }\n\n        return draft;\n    }\n}\n";
        var after = before.Replace("        if (draft is null)\n        {\n            draft = new Draft();\n        }\n\n", "", StringComparison.Ordinal);

        None(Changed("src/Drafts.cs", before, after), FlagKind.GuardRemoved);
    }

    [Fact]
    public void AGuardMovedIntoAnotherChangedFileIsNotRemoved()
    {
        var page = "namespace Shop.Pages;\n\npublic class DetailModel : PageModel\n{\n    public async Task<IActionResult> OnGetAsync(Guid id)\n    {\n        var contact = await _db.Contacts.FindAsync(id);\n        if (contact is null) return NotFound();\n        return Page();\n    }\n}\n";
        var thin = "namespace Shop.Pages;\n\npublic class DetailModel : PageModel\n{\n    public async Task<IActionResult> OnGetAsync(Guid id)\n    {\n        var contact = await _queries.LoadAsync(id);\n        Name = contact.Name;\n        return Page();\n    }\n}\n";
        var queries = "namespace Shop;\n\npublic class ContactQueries\n{\n    public async Task<Contact?> LoadAsync(Guid id)\n    {\n        var contact = await _db.Contacts.FindAsync(id);\n        if (contact is null) return NotFound();\n        return contact;\n    }\n}\n";

        None(Flags(new TestFile("src/Pages/Detail.cshtml.cs", page, thin), new TestFile("src/ContactQueries.cs", null, queries)), FlagKind.GuardRemoved);
        One(Flags(new TestFile("src/Pages/Detail.cshtml.cs", page, thin)), FlagKind.GuardRemoved);
    }

    [Fact]
    public void ARemovedModelStateCheckOrValidationAttributeIsFlagged()
    {
        var page = "namespace Shop.Pages;\n\npublic class EditModel : PageModel\n{\n    public IActionResult OnPost()\n    {\n        if (!ModelState.IsValid)\n        {\n            return Page();\n        }\n\n        return RedirectToPage();\n    }\n}\n";
        One(Changed("src/Pages/Edit.cshtml.cs", page, page.Replace("        if (!ModelState.IsValid)\n        {\n            return Page();\n        }\n\n", "", StringComparison.Ordinal)), FlagKind.GuardRemoved);

        var input = "namespace Shop;\n\npublic class Input\n{\n    [Required]\n    [StringLength(100)]\n    public string Name { get; set; } = \"\";\n}\n";
        Assert.Contains("[StringLength(100)]", One(Changed("src/Input.cs", input, input.Replace("    [StringLength(100)]\n", "", StringComparison.Ordinal)), FlagKind.GuardRemoved).Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- raw output

    [Theory]
    [InlineData("<div>@Html.Raw(note.Body)</div>", true)]
    [InlineData("<div>@note.Body</div>", false)]
    [InlineData("@* never use @Html.Raw(note.Body) here *@", false)]
    public void HtmlRawIsFlagged(string line, bool flagged)
    {
        Assert.Equal(flagged, Changed("src/Pages/_Notes.cshtml", "@model Note\n", $"@model Note\n{line}\n").Any(f => f.Kind == FlagKind.RawOutput));
    }

    [Theory]
    [InlineData("$list.html('<li>' + note.body + '</li>');", true)]
    [InlineData("$list.append(`<li>${note.body}</li>`);", true)]
    [InlineData("$('<li class=\"x\">' + name + '</li>').appendTo($list);", true)]
    [InlineData("$panel.html(markup);", false)]
    [InlineData("$body.append($row);", false)]
    [InlineData("$row.append(cell(row.number));", false)]
    [InlineData("$('#notes').text(note.body);", false)]
    public void JQueryInsertsBuiltByConcatenationAreFlagged(string line, bool flagged)
    {
        Assert.Equal(flagged, Changed("src/wwwroot/js/notes.js", "$(function () {\n});\n", $"$(function () {{\n    {line}\n}});\n").Any(f => f.Kind == FlagKind.RawOutput));
    }

    // ---------------------------------------------------------------- raw SQL and query safety

    [Theory]
    [InlineData("db.Orders.FromSqlRaw($\"SELECT * FROM Orders WHERE Status = '{status}'\")", true)]
    [InlineData("db.Orders.FromSqlRaw(\"SELECT * FROM Orders WHERE Status = '\" + status + \"'\")", true)]
    [InlineData("db.Orders.FromSqlRaw(\"SELECT * FROM Orders WHERE Status = {0}\", status)", false)]
    [InlineData("db.Orders.FromSqlInterpolated($\"SELECT * FROM Orders WHERE Status = {status}\")", false)]
    public void RawSqlWithInterpolationOrConcatenationIsFlagged(string query, bool flagged)
    {
        var before = "namespace Shop;\n\npublic class Reports\n{\n    public object Run(ShopDb db, string status)\n    {\n        return null;\n    }\n}\n";
        var after = before.Replace("        return null;", $"        return {query};", StringComparison.Ordinal);

        Assert.Equal(flagged, Changed("src/Reports.cs", before, after).Any(f => f.Kind == FlagKind.RawSql));
    }

    [Fact]
    public void CodeMentionedInACommentIsNotFlagged()
    {
        var before = "namespace Shop;\n\npublic class Notes\n{\n}\n";
        var after = "namespace Shop;\n\n/// <summary>Renders sanitised HTML, replacing <c>@Html.Raw(sanitizer.Sanitize(...))</c> calls.</summary>\n// Callers used to call db.Orders.IgnoreQueryFilters() here.\npublic class Notes\n{\n}\n";

        Assert.Empty(Changed("src/Notes.cs", before, after));
    }

    [Fact]
    public void IgnoreQueryFiltersAddedIsFlaggedButAnUnchangedOneIsNot()
    {
        var before = "namespace Shop;\n\npublic class Stats\n{\n    public object Count(ShopDb db) => db.Orders.Count();\n\n    public object All(ShopDb db) => db.Orders.IgnoreQueryFilters().ToList();\n}\n";
        var after = before.Replace("db.Orders.Count()", "db.Orders.IgnoreQueryFilters().Count()", StringComparison.Ordinal);

        var flag = One(Changed("src/Stats.cs", before, after), FlagKind.QuerySafety);
        Assert.Equal(5, flag.Line);

        // A test asserting on the raw table serves no request.
        None(Changed("tests/Shop.Tests/StatsTests.cs", before, after), FlagKind.QuerySafety);
    }

    // ---------------------------------------------------------------- swallowed errors

    [Theory]
    [InlineData("catch (IOException)\n        {\n        }", true)]
    [InlineData("catch (IOException)\n        {\n            Message = \"Could not save\";\n        }", true)]
    [InlineData("catch (IOException ex)\n        {\n            _logger.LogError(ex, \"Export failed\");\n            Message = \"Could not save\";\n        }", false)]
    [InlineData("catch (Exception ex)\n        {\n            _logger.LogError(ex, \"Import failed\");\n            throw;\n        }", false)]
    [InlineData("catch (FormatException ex)\n        {\n            throw new ImportException(\"Bad row\", ex);\n        }", false)]
    [InlineData("catch (ArgumentException ex) { Result = Error(ex.Message); }", false)]
    [InlineData("catch (Exception ex) when (ex is JsonException) { Result = null; }", true)]
    public void ACatchThatNeitherLogsNorRethrowsIsFlagged(string clause, bool flagged)
    {
        var before = "namespace Shop;\n\npublic class Export\n{\n    public void Run()\n    {\n        Write();\n    }\n}\n";
        var after = before.Replace("        Write();\n", $"        try\n        {{\n            Write();\n        }}\n        {clause}\n", StringComparison.Ordinal);

        Assert.Equal(flagged, Changed("src/Export.cs", before, after).Any(f => f.Kind == FlagKind.SwallowedErrors));
    }

    [Fact]
    public void AFilteredRetryCatchInALoopIsNotSwallowing()
    {
        var before = "namespace Shop;\n\npublic class Client\n{\n    public async Task<string> CallAsync()\n    {\n        return await SendAsync();\n    }\n}\n";
        var after = before.Replace("        return await SendAsync();\n", "        for (var retry = 0; ; retry++)\n        {\n            try\n            {\n                return await SendAsync();\n            }\n            catch (ProviderException ex) when (ex.IsTransient && retry < 3)\n            {\n                await Task.Delay(100);\n            }\n        }\n", StringComparison.Ordinal);

        None(Changed("src/Client.cs", before, after), FlagKind.SwallowedErrors);
    }

    [Theory]
    [InlineData(".fail(function () {});", true)]
    [InlineData(".fail(() => {});", true)]
    [InlineData(".fail(function () { showError(); });", false)]
    public void AnEmptyJQueryFailIsFlagged(string handler, bool flagged)
    {
        Assert.Equal(flagged, Changed("src/wwwroot/js/a.js", "$.get('/x')\n    .done(render);\n", $"$.get('/x')\n    .done(render)\n    {handler}\n").Any(f => f.Kind == FlagKind.SwallowedErrors));
    }

    // ---------------------------------------------------------------- warnings silenced

    [Theory]
    [InlineData("src/A.cs", "namespace Shop;\n", "namespace Shop;\n#pragma warning disable CS8602\n", true)]
    [InlineData("src/A.cs", "namespace Shop;\n", "namespace Shop;\n#pragma warning restore CS8602\n", false)]
    [InlineData("src/A.cs", "namespace Shop;\n", "namespace Shop;\n[SuppressMessage(\"Design\", \"CA1031\")]\npublic class A { }\n", true)]
    [InlineData(".editorconfig", "[*.cs]\n", "[*.cs]\ndotnet_diagnostic.CA2007.severity = none\n", true)]
    [InlineData(".editorconfig", "[*.cs]\n", "[*.cs]\ndotnet_diagnostic.CA2007.severity = error\n", false)]
    [InlineData("src/Shop.csproj", "<Project>\n</Project>\n", "<Project>\n  <PropertyGroup><NoWarn>CS1591</NoWarn></PropertyGroup>\n</Project>\n", true)]
    public void SilencedWarningsAreFlagged(string path, string before, string after, bool flagged)
    {
        Assert.Equal(flagged, Changed(path, before, after).Any(f => f.Kind == FlagKind.WarningsSilenced));
    }

    // ---------------------------------------------------------------- dependencies and build

    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <ItemGroup>
            <PackageReference Include="Dapper" Version="2.1.0" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void PackagesAddedOrUpgradedAreFlaggedAndNamed()
    {
        var head = Project.Replace("    <PackageReference Include=\"Dapper\" Version=\"2.1.0\" />", "    <PackageReference Include=\"Dapper\" Version=\"2.1.35\" />\n    <PackageReference Include=\"CsvHelper\" Version=\"33.0.1\" />", StringComparison.Ordinal);
        var flags = Changed("src/Shop.csproj", Project, head).Where(f => f.Kind == FlagKind.DependenciesAndBuild).Select(f => f.Message).ToList();

        Assert.Equal(["Package `Dapper` upgraded: 2.1.0 → 2.1.35.", "Package `CsvHelper` 33.0.1 added."], flags);
    }

    [Fact]
    public void OtherBuildChangesPipelinesAndCdnScriptsAreFlagged()
    {
        One(Changed("src/Shop.csproj", Project, Project.Replace("  <ItemGroup>", "  <PropertyGroup><Nullable>disable</Nullable></PropertyGroup>\n  <ItemGroup>", StringComparison.Ordinal)), FlagKind.DependenciesAndBuild);
        One(Changed(".github/workflows/build.yml", "on: push\njobs: {}\n", "on: [push, pull_request]\njobs: {}\n"), FlagKind.DependenciesAndBuild);

        var layout = "<body>\n</body>\n";
        One(Changed("src/Pages/Shared/_Layout.cshtml", layout, "<body>\n<script src=\"https://cdn.example.com/chart.min.js\"></script>\n</body>\n"), FlagKind.DependenciesAndBuild);
        None(Changed("src/Pages/Shared/_Layout.cshtml", layout, "<body>\n<script src=\"~/js/site.js\" asp-append-version=\"true\"></script>\n</body>\n"), FlagKind.DependenciesAndBuild);
    }

    // ---------------------------------------------------------------- security config

    private const string Startup = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        builder.Services.AddScoped<OrderService>();

        var app = builder.Build();
        app.UseHsts();
        app.Run();
        """;

    [Fact]
    public void CookieCorsAndHstsChangesInProgramAreFlaggedOncePerArea()
    {
        var head = Startup
            .Replace("SameSiteMode.Strict", "SameSiteMode.None", StringComparison.Ordinal)
            .Replace("builder.Services.AddScoped<OrderService>();\n", "builder.Services.AddScoped<OrderService>();\nbuilder.Services.AddCors(o => o.AddPolicy(\"x\", p => p\n    .AllowAnyOrigin()\n    .AllowAnyHeader()));\n", StringComparison.Ordinal)
            .Replace("app.UseHsts();\n", "", StringComparison.Ordinal);
        var flags = Changed("src/Program.cs", Startup, head).Where(f => f.Kind == FlagKind.SecurityConfig).ToList();

        Assert.Equal(3, flags.Count);
        Assert.Contains(flags, f => f.Message.StartsWith("Cookie options changed: `options.Cookie.SameSite = SameSiteMode.None;`", StringComparison.Ordinal));
        Assert.Contains(flags, f => f.Message.StartsWith("CORS changed: `.AllowAnyOrigin()`", StringComparison.Ordinal));
        Assert.Contains(flags, f => f.Side == DiffSide.Left && f.Message.StartsWith("HTTPS/HSTS removed", StringComparison.Ordinal));
    }

    [Fact]
    public void AppsettingsSecuritySectionsAreFlaggedButOtherSettingsAreNot()
    {
        var before = "{\n  \"Payments\": {\n    \"BankTransferEnabled\": false\n  },\n  \"Cors\": {\n    \"AllowedOrigins\": [ \"https://shop.example\" ]\n  }\n}\n";
        var feature = before.Replace("\"BankTransferEnabled\": false", "\"BankTransferEnabled\": true", StringComparison.Ordinal);
        None(Changed("src/appsettings.Development.json", before, feature), FlagKind.SecurityConfig);

        var cors = before.Replace("[ \"https://shop.example\" ]", "[ \"*\" ]", StringComparison.Ordinal);
        Assert.Equal(DiffSide.Right, One(Changed("src/appsettings.json", before, cors), FlagKind.SecurityConfig).Side);

        // The same edit in another JSON file is not security config.
        None(Changed("src/wwwroot/data/cors.json", before, cors), FlagKind.SecurityConfig);
    }

    // ---------------------------------------------------------------- identity, skipped paths, folds

    [Fact]
    public void AFlagKeepsItsIdentityWhenItsLineMoves()
    {
        var before = "namespace Shop;\n\npublic class Stats\n{\n    public object All(ShopDb db) => db.Orders.ToList();\n}\n";
        var after = before.Replace("db.Orders.ToList()", "db.Orders.IgnoreQueryFilters().ToList()", StringComparison.Ordinal);
        var moved = after.Replace("public class Stats\n{\n", "public class Stats\n{\n    // Reports need deleted orders too.\n\n", StringComparison.Ordinal);

        var first = One(Changed("src/Stats.cs", before, after), FlagKind.QuerySafety);
        var second = One(Changed("src/Stats.cs", before, moved), FlagKind.QuerySafety);

        Assert.NotEqual(first.Line, second.Line);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("Stats.All", first.Member);
    }

    [Fact]
    public void SkippedPathsAreNotRead()
    {
        var file = new TestFile("legacy/Old.cs", "namespace Shop;\n", "namespace Shop;\n#pragma warning disable CS0618\n");

        Assert.NotEmpty(Flags(file));
        Assert.Empty(Flags(new ReviewSettings { SkippedPaths = ["legacy/**"] }, file));
    }

    [Fact]
    public void AFoldThatContainsAFlagOpensAndSaysWhy()
    {
        var fold = new HunkTriage { Path = "src/Program.cs", NewStart = 10, NewEnd = 14, Class = ChangeClass.NewCode, Fold = FoldKind.Boilerplate, Reasons = ["3 registrations: 3 scoped"], ChangedLines = 3 };
        var elsewhere = fold with { NewStart = 30, NewEnd = 32 };
        var flag = new RedFlag(FlagKind.SecurityConfig, "src/Program.cs", DiffSide.Right, 12, "CORS changed.");

        var triage = RedFlags.OpenFlaggedFolds(new TriageResult([fold, elsewhere], []), [flag]);

        Assert.Equal(FoldKind.Open, triage.Hunks[0].Fold);
        Assert.Equal("contains a red flag (Security config)", triage.Hunks[0].Reasons[0]);
        Assert.Equal(FoldKind.Boilerplate, triage.Hunks[1].Fold);
        Assert.Equal([flag], triage.Flags);
    }
}
