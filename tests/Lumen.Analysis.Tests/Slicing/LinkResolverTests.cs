using Lumen.Roslyn.Slicing;

namespace Lumen.Analysis.Tests.Slicing;

/// <summary>Phase 3: each linking convention, its reason in words, and the near-misses that must not link.</summary>
public sealed class LinkResolverTests
{
    private static LinkGraph Graph(params (string Path, string Text)[] files) =>
        LinkResolver.Build(RepositoryIndex.Build(files.Select(f => new KeyValuePair<string, string>(f.Path, f.Text))));

    private static Link Single(LinkGraph graph, string from, string to, EdgeKind? kind = null) =>
        Assert.Single(graph.From(from), l => l.To == to && (kind is null || l.Kind == kind));

    private const string OrderService = """
        namespace Shop.Services;
        public interface IOrderService { }
        """;

    private const string OrderServiceImpl = """
        namespace Shop.Services;
        public sealed class OrderService : IOrderService { }
        """;

    [Fact]
    public void ConstructorInjectionOfAClassLinksWithItsReason()
    {
        var graph = Graph(
            ("Pages/Orders/Edit.cshtml.cs", "using Shop.Services; namespace Shop.Pages.Orders; public class EditModel(PricingCalculator pricing) : PageModel { }"),
            ("Services/PricingCalculator.cs", "namespace Shop.Services; public sealed class PricingCalculator { }"));

        var link = Single(graph, "Pages/Orders/Edit.cshtml.cs", "Services/PricingCalculator.cs");

        Assert.Equal(EdgeKind.Injects, link.Kind);
        Assert.Equal("`EditModel` injects `PricingCalculator`", link.Reason);
        Assert.True(link.MakesMembership);
    }

    [Fact]
    public void AnInterfaceWithOneImplementationIsFollowedToIt()
    {
        var graph = Graph(
            ("Pages/Edit.cshtml.cs", "using Shop.Services; namespace Shop.Pages; public class EditModel : PageModel { public EditModel(IOrderService orders) { } }"),
            ("Services/IOrderService.cs", OrderService),
            ("Services/OrderService.cs", OrderServiceImpl));

        Assert.Equal("`EditModel` injects `IOrderService`", Single(graph, "Pages/Edit.cshtml.cs", "Services/IOrderService.cs").Reason);
        var impl = Single(graph, "Pages/Edit.cshtml.cs", "Services/OrderService.cs");
        Assert.Equal((EdgeKind.Injects, "`EditModel` injects `IOrderService` → `OrderService`"), (impl.Kind, impl.Reason));
    }

    [Fact]
    public void AnInterfaceWithTwoImplementationsGivesPossibleLinksOnly()
    {
        var graph = Graph(
            ("Pages/Checkout.cshtml.cs", "using Shop.Services; namespace Shop.Pages; public class CheckoutModel(IPaymentGateway gateway) : PageModel { }"),
            ("Services/IPaymentGateway.cs", "namespace Shop.Services; public interface IPaymentGateway { }"),
            ("Services/Card.cs", "namespace Shop.Services; public class CardPaymentGateway : IPaymentGateway { }"),
            ("Services/Bank.cs", "namespace Shop.Services; public class BankTransferPaymentGateway : IPaymentGateway { }"));

        var card = Single(graph, "Pages/Checkout.cshtml.cs", "Services/Card.cs");
        var bank = Single(graph, "Pages/Checkout.cshtml.cs", "Services/Bank.cs");

        Assert.Equal(EdgeKind.Possible, card.Kind);
        Assert.False(card.MakesMembership);
        Assert.False(bank.MakesMembership);
        Assert.Contains("which has 2 implementations", card.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnumerableOfServicesIsAPossibleLink()
    {
        var graph = Graph(
            ("Services/Runner.cs", "using System.Collections.Generic; namespace Shop.Services; public class Runner(IEnumerable<IStep> steps) { }"),
            ("Services/IStep.cs", "namespace Shop.Services; public interface IStep { }"),
            ("Services/Step.cs", "namespace Shop.Services; public class OnlyStep : IStep { }"));

        Assert.Equal(EdgeKind.Possible, Single(graph, "Services/Runner.cs", "Services/Step.cs").Kind);
    }

    [Fact]
    public void FromServicesHandlerParametersCountAsInjection()
    {
        var graph = Graph(
            ("Pages/Export.cshtml.cs", "using Shop.Services; namespace Shop.Pages; public class ExportModel : PageModel { public void OnPost([FromServices] ExportFileNamer namer) { } }"),
            ("Services/ExportFileNamer.cs", "namespace Shop.Services; public class ExportFileNamer { }"));

        Assert.Equal("`ExportModel` takes [FromServices] `ExportFileNamer`", Single(graph, "Pages/Export.cshtml.cs", "Services/ExportFileNamer.cs", EdgeKind.Injects).Reason);
    }

    [Fact]
    public void DbSetUseLinksTheEntityAndTheContextIsADeadEnd()
    {
        var graph = Graph(
            ("Services/InvoiceService.cs", "using Shop.Data; namespace Shop.Services; public class InvoiceService(ShopDbContext db) { public int Count() => db.Invoices.Count(); }"),
            ("Data/ShopDbContext.cs", "using Shop.Data.Entities; namespace Shop.Data; public class ShopDbContext : DbContext { public DbSet<Invoice> Invoices => Set<Invoice>(); public DbSet<Order> Orders => Set<Order>(); }"),
            ("Data/Entities/Invoice.cs", "namespace Shop.Data.Entities; public class Invoice { }"),
            ("Data/Entities/Order.cs", "namespace Shop.Data.Entities; public class Order { }"));

        Assert.Equal("`InvoiceService` uses `ShopDbContext.Invoices` → `Invoice`", Single(graph, "Services/InvoiceService.cs", "Data/Entities/Invoice.cs", EdgeKind.DbSet).Reason);
        Assert.DoesNotContain(graph.From("Services/InvoiceService.cs"), l => l.To == "Data/Entities/Order.cs");

        // The context names every entity; those names don't become edges out of it.
        Assert.DoesNotContain(graph.From("Data/ShopDbContext.cs"), l => l.MakesMembership && l.To.StartsWith("Data/Entities/", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateEditModelNamesResolveByNamespaceAndUsings()
    {
        var graph = Graph(
            ("Pages/Orders/Edit.cshtml.cs", "namespace Shop.Pages.Orders; public class EditModel : PageModel { public OrderDraft Draft { get; set; } }"),
            ("Pages/Orders/OrderDraft.cs", "namespace Shop.Pages.Orders; public class OrderDraft { }"),
            ("Pages/Customers/Edit.cshtml.cs", "namespace Shop.Pages.Customers; public class EditModel : PageModel { }"),
            ("Pages/Customers/OrderDraft.cs", "namespace Shop.Pages.Customers; public class OrderDraft { }"),
            ("Tests/OrdersEditModelTests.cs", "using Shop.Pages.Orders; namespace Shop.Tests; public class OrdersEditModelTests { void T() { var model = new EditModel(); } }"));

        Assert.Single(graph.From("Pages/Orders/Edit.cshtml.cs"), l => l.To == "Pages/Orders/OrderDraft.cs");
        Assert.DoesNotContain(graph.From("Pages/Orders/Edit.cshtml.cs"), l => l.To == "Pages/Customers/OrderDraft.cs");
        var test = Single(graph, "Tests/OrdersEditModelTests.cs", "Pages/Orders/Edit.cshtml.cs");
        Assert.Equal((EdgeKind.Tests, "`OrdersEditModelTests` tests `EditModel` (constructs it)"), (test.Kind, test.Reason));
        Assert.DoesNotContain(graph.From("Tests/OrdersEditModelTests.cs"), l => l.To == "Pages/Customers/Edit.cshtml.cs");
    }

    [Fact]
    public void AnAmbiguousNameLinksNowhere()
    {
        var graph = Graph(
            ("Services/A.cs", "namespace One; public class Helper { }"),
            ("Services/B.cs", "namespace Two; public class Helper { }"),
            ("Services/User.cs", "namespace Three; public class User { Helper h; }"));

        Assert.DoesNotContain(graph.From("Services/User.cs"), l => l.MakesMembership);
    }

    [Fact]
    public void APageIsPairedWithItsPageModelAndItsRouteIsUsedForLinks()
    {
        var graph = Graph(
            ("Pages/Orders/Edit.cshtml", "@page \"{id:int}\"\n@model Shop.Pages.Orders.EditModel\n<a asp-page=\"./Details\">Cancel</a>\n"),
            ("Pages/Orders/Edit.cshtml.cs", "namespace Shop.Pages.Orders; public class EditModel : PageModel { }"),
            ("Pages/Orders/Details.cshtml", "@page\n"),
            ("Pages/Orders/Details.cshtml.cs", "namespace Shop.Pages.Orders; public class DetailsModel : PageModel { }"));

        Assert.Equal("page `Orders/Edit` has its PageModel `EditModel` in `Edit.cshtml.cs`", Single(graph, "Pages/Orders/Edit.cshtml", "Pages/Orders/Edit.cshtml.cs").Reason);
        var pageLink = Single(graph, "Pages/Orders/Edit.cshtml", "Pages/Orders/Details.cshtml");
        Assert.Equal(EdgeKind.PageLink, pageLink.Kind);
        Assert.False(pageLink.MakesMembership);
    }

    [Fact]
    public void PartialsViewComponentsTagHelpersAndTheirScriptsLink()
    {
        var graph = Graph(
            ("Pages/Orders/Edit.cshtml", "@page\n<partial name=\"_OrderLines\" model=\"Model.Lines\" />\n<vc:cart-summary></vc:cart-summary>\n<currency-input asp-for=\"Input.Cost\" />\n"),
            ("Pages/Shared/_OrderLines.cshtml", "<table id=\"order-lines\"></table>\n<script src=\"~/js/partials/order-lines.js\"></script>\n"),
            ("ViewComponents/CartSummaryViewComponent.cs", "namespace Shop.ViewComponents; public class CartSummaryViewComponent : ViewComponent { }"),
            ("Pages/Shared/Components/CartSummary/Default.cshtml", "<div id=\"cart\"></div>"),
            ("TagHelpers/CurrencyInputTagHelper.cs", "namespace Shop.TagHelpers; [HtmlTargetElement(\"currency-input\")] public class CurrencyInputTagHelper : TagHelper { void P() { var s = \"<script src=\\\"/js/taghelpers/currency-input.js\\\"></script>\"; } }"),
            ("wwwroot/js/partials/order-lines.js", "$('#order-lines').on('click', function () { });"),
            ("wwwroot/js/taghelpers/currency-input.js", "// formats money"));

        Assert.Equal("`Edit.cshtml` renders partial `_OrderLines`", Single(graph, "Pages/Orders/Edit.cshtml", "Pages/Shared/_OrderLines.cshtml").Reason);
        Assert.Equal(EdgeKind.ViewComponent, Single(graph, "Pages/Orders/Edit.cshtml", "ViewComponents/CartSummaryViewComponent.cs").Kind);
        Assert.Equal(EdgeKind.ViewComponent, Single(graph, "Pages/Orders/Edit.cshtml", "Pages/Shared/Components/CartSummary/Default.cshtml").Kind);
        Assert.Equal("`Edit.cshtml` uses tag helper `<currency-input>` (`CurrencyInputTagHelper`)", Single(graph, "Pages/Orders/Edit.cshtml", "TagHelpers/CurrencyInputTagHelper.cs").Reason);
        Assert.Equal("`_OrderLines.cshtml` loads `order-lines.js`", Single(graph, "Pages/Shared/_OrderLines.cshtml", "wwwroot/js/partials/order-lines.js").Reason);
        Assert.Equal(EdgeKind.Script, Single(graph, "TagHelpers/CurrencyInputTagHelper.cs", "wwwroot/js/taghelpers/currency-input.js").Kind);
        Assert.Equal(EdgeKind.IdSelector, Single(graph, "wwwroot/js/partials/order-lines.js", "Pages/Shared/_OrderLines.cshtml").Kind);
    }

    [Fact]
    public void ALayoutsScriptsAreSiteWideAndNeverMakeMembership()
    {
        var graph = Graph(
            ("Pages/Shared/_Layout.cshtml", "<html><body>@RenderBody()<script src=\"~/js/site.js\"></script></body></html>"),
            ("wwwroot/js/site.js", "window.Shop = {};"));

        var link = Single(graph, "Pages/Shared/_Layout.cshtml", "wwwroot/js/site.js");
        Assert.Equal(EdgeKind.SiteWide, link.Kind);
        Assert.False(link.MakesMembership);
    }

    [Fact]
    public void SectionScriptsAndSrcIncludePatternsLoadPerPageScripts()
    {
        var graph = Graph(
            ("Areas/Admin/Pages/Dashboard.cshtml", "@page\n@section Scripts {\n<script asp-src-include=\"~/js/pages/admin-dashboard*.js\"></script>\n}\n"),
            ("wwwroot/js/pages/admin-dashboard.js", "//"),
            ("wwwroot/js/pages/admin-dashboard.charts.js", "//"),
            ("wwwroot/js/pages/other.js", "//"));

        Assert.Equal(2, graph.From("Areas/Admin/Pages/Dashboard.cshtml").Count(l => l.Kind == EdgeKind.Script));
        Assert.DoesNotContain(graph.From("Areas/Admin/Pages/Dashboard.cshtml"), l => l.To.EndsWith("other.js", StringComparison.Ordinal));
    }

    [Fact]
    public void LiteralAjaxHandlerUrlsResolveToTheLoadingPagesHandlers()
    {
        var graph = Graph(
            ("Pages/Orders/Edit.cshtml", "@page \"{id:int}\"\n<form method=\"post\" asp-page-handler=\"Save\"></form>\n@section Scripts { <script src=\"~/js/pages/orders-edit.js\"></script> }\n"),
            ("Pages/Orders/Edit.cshtml.cs", "namespace Shop.Pages.Orders; public class EditModel : PageModel { public void OnPostSaveAsync() { } public void OnGetLinesAsync() { } }"),
            ("Pages/Orders/Index.cshtml", "@page\n"),
            ("Pages/Orders/Index.cshtml.cs", "namespace Shop.Pages.Orders; public class IndexModel : PageModel { public void OnGetExportAsync() { } }"),
            ("wwwroot/js/pages/orders-edit.js", "$.post('?handler=Save', data);\n$.getJSON('/Orders/Index?handler=Export&id=' + id);\n"));

        Assert.Equal("`orders-edit.js` calls `?handler=Save` → `OnPostSaveAsync`",
            Single(graph, "wwwroot/js/pages/orders-edit.js", "Pages/Orders/Edit.cshtml.cs::EditModel.OnPostSaveAsync").Reason);
        Assert.Equal(EdgeKind.Ajax, Single(graph, "wwwroot/js/pages/orders-edit.js", "Pages/Orders/Index.cshtml.cs::IndexModel.OnGetExportAsync").Kind);
        Assert.Equal("`Edit.cshtml` posts to handler `Save` → `OnPostSaveAsync`",
            Single(graph, "Pages/Orders/Edit.cshtml", "Pages/Orders/Edit.cshtml.cs::EditModel.OnPostSaveAsync").Reason);
    }

    [Fact]
    public void UrlsBuiltAtRunTimeAndDataUrlsArePossibleLinksOnly()
    {
        var graph = Graph(
            ("Pages/Customers/Edit.cshtml", "@page\n<div data-url=\"@Url.Page(Model.NotesPage, \"Notes\")\"></div>\n<script src=\"~/js/notes.js\"></script>\n"),
            ("Pages/Customers/Edit.cshtml.cs", "namespace Shop.Pages; public class EditModel : PageModel { public void OnGetNotes() { } }"),
            ("wwwroot/js/notes.js", "var url = $el.data('url');\n$.ajax({ url: url, type: 'POST' });\n"));

        Assert.All(graph.From("wwwroot/js/notes.js"), l => Assert.False(l.MakesMembership));
        Assert.Contains(graph.From("wwwroot/js/notes.js"), l => l.Kind == EdgeKind.Possible && l.Reason.Contains("built at run time", StringComparison.Ordinal));
        Assert.Contains(graph.From("Pages/Customers/Edit.cshtml"), l => l.Kind == EdgeKind.Possible && l.Reason.Contains("Model.NotesPage", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdDeclaredInTwoViewsIsAPossibleLink()
    {
        var graph = Graph(
            ("Pages/A.cshtml", "<div id=\"results\"></div>"),
            ("Pages/B.cshtml", "<div id=\"results\"></div>"),
            ("wwwroot/js/results.js", "$('#results').empty();"));

        Assert.All(graph.From("wwwroot/js/results.js"), l => Assert.Equal(EdgeKind.Possible, l.Kind));
        Assert.Equal(2, graph.From("wwwroot/js/results.js").Count);
    }

    [Fact]
    public void WebFormsPagesPairWithCodeBehindAndRegisteredControls()
    {
        var graph = Graph(
            ("Web/Orders.aspx", "<%@ Page Language=\"C#\" CodeBehind=\"Orders.aspx.cs\" Inherits=\"Web.Orders\" %>\n<%@ Register Src=\"~/Controls/OrderGrid.ascx\" TagPrefix=\"uc\" TagName=\"OrderGrid\" %>\n<script src=\"/Scripts/orders.js\"></script>\n"),
            ("Web/Orders.aspx.cs", "namespace Web; public partial class Orders : System.Web.UI.Page { }"),
            ("Web/Controls/OrderGrid.ascx", "<%@ Control Language=\"C#\" CodeBehind=\"OrderGrid.ascx.cs\" %>"),
            ("Web/Controls/OrderGrid.ascx.cs", "namespace Web.Controls; public partial class OrderGrid : System.Web.UI.UserControl { }"),
            ("Web/Scripts/orders.js", "//"));

        Assert.Equal("`Orders.aspx` has its code-behind in `Orders.aspx.cs`", Single(graph, "Web/Orders.aspx", "Web/Orders.aspx.cs").Reason);
        Assert.Equal("`Orders.aspx` registers control `OrderGrid.ascx`", Single(graph, "Web/Orders.aspx", "Web/Controls/OrderGrid.ascx").Reason);
        Assert.Equal(EdgeKind.PageModel, Single(graph, "Web/Controls/OrderGrid.ascx", "Web/Controls/OrderGrid.ascx.cs").Kind);
        Assert.Equal(EdgeKind.Script, Single(graph, "Web/Orders.aspx", "Web/Scripts/orders.js").Kind);
    }

    [Fact]
    public void ARecordsPrimaryConstructorIsDataNotInjection()
    {
        var graph = Graph(
            ("Services/Importer.cs", "namespace S; public class Importer { public sealed record Row(Product Product); }"),
            ("Services/Product.cs", "namespace S; public class Product { }"));

        Assert.All(graph.From("Services/Importer.cs"), l => Assert.NotEqual(EdgeKind.Injects, l.Kind));
        Assert.Contains(graph.From("Services/Importer.cs"), l => l.Kind == EdgeKind.References && l.To == "Services/Product.cs");
    }

    [Fact]
    public void FixtureEdgesCarryTheirReasons()
    {
        var graph = LinkResolver.Build(FixtureSources.Index("head"));

        Assert.All(graph.All, l => Assert.False(string.IsNullOrWhiteSpace(l.Reason)));
        Assert.Contains(graph.All, l => l.Reason == "`EditModel` injects `IOrderService` → `OrderService`" && l.From == "src/Shop.Web/Pages/Orders/Edit.cshtml.cs");
        Assert.Contains(graph.All, l => l.Kind == EdgeKind.Possible && l.From == "src/Shop.Web/Pages/Orders/Checkout.cshtml.cs" && l.To.EndsWith("CardPaymentGateway.cs", StringComparison.Ordinal));
        Assert.Contains(graph.All, l => l.Kind == EdgeKind.SiteWide && l.To == "src/Shop.Web/wwwroot/js/site.js");
    }

    [Fact]
    public void TheIndexIsBuiltIncrementallyAcrossCommits()
    {
        var cache = new ParsedFileCache();
        var baseIndex = RepositoryIndex.Build(FixtureSources.Read("base"), cache);
        var parsedForBase = cache.Misses;

        var headIndex = RepositoryIndex.Build(FixtureSources.Read("head"), cache);

        // Unchanged files are reused as-is; only files the change touched are parsed again.
        var before = FixtureSources.Read("base").ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var touched = FixtureSources.Read("head").Count(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value);
        Assert.Equal(touched, cache.Misses - parsedForBase);
        Assert.True(cache.Hits > 0);
        Assert.Same(baseIndex.CSharp["src/Shop.Web/Data/Entities/Product.cs"], headIndex.CSharp["src/Shop.Web/Data/Entities/Product.cs"]);
        Assert.NotSame(baseIndex.CSharp["src/Shop.Web/Program.cs"], headIndex.CSharp["src/Shop.Web/Program.cs"]);
    }

    [Fact]
    public void TypesAreQualifiedByNamespaceAndMembersAreIndexed()
    {
        var index = FixtureSources.Index("head");

        var editModels = index.Named("EditModel").Select(t => t.QualifiedName).Order(StringComparer.Ordinal).ToList();
        Assert.Contains("Shop.Web.Pages.Orders.EditModel", editModels);
        Assert.Contains("Shop.Web.Pages.Customers.EditModel", editModels);
        Assert.True(editModels.Count >= 3);
        var orders = index.Named("EditModel").Single(t => t.QualifiedName == "Shop.Web.Pages.Orders.EditModel");
        Assert.Contains(orders.Members, m => m.Name == "OnPostSaveAsync" && m.Kind == "method" && m.StartLine > 0);
    }
}
