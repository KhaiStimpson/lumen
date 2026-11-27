using Lumen.Analysis.Tests.Support;
using Lumen.Domain;

namespace Lumen.Analysis.Tests.Triage;

/// <summary>Phase 5: each boilerplate shape folds with its summary; every near-miss stays open.</summary>
public sealed class BoilerplateClassifierTests
{
    private static List<HunkTriage> Added(string path, string text) => [.. TriageHarness.Run(new TestFile(path, null, text)).Hunks];

    private static List<HunkTriage> Changed(string path, string before, string after, params TestFile[] others) =>
        [.. TriageHarness.Run([new TestFile(path, before, after), .. others]).Hunks.Where(h => h.Path == path)];

    private static HunkTriage FoldedPart(IEnumerable<HunkTriage> hunks) => Assert.Single(hunks, h => h.Fold == FoldKind.Boilerplate);

    // ---------------------------------------------------------------- DI registrations

    private const string ProgramBase = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddRazorPages();
        builder.Services.AddScoped<IOrderService, OrderService>();

        var app = builder.Build();
        app.Run();
        """;

    [Fact]
    public void AddedRegistrationsFoldAndNameEachLifetime()
    {
        var head = ProgramBase.Replace("builder.Services.AddScoped<IOrderService, OrderService>();", """
            builder.Services.AddScoped<IOrderService, OrderService>();
            builder.Services.AddScoped<IInvoiceExportService, InvoiceExportService>();
            builder.Services.AddScoped<CustomerNoteService>();
            builder.Services.AddSingleton<ExportFileNamer>();
            """, StringComparison.Ordinal);

        var fold = FoldedPart(Changed("src/Program.cs", ProgramBase, head));

        Assert.Equal(["3 registrations: 2 scoped, 1 singleton"], fold.Reasons);
        Assert.Equal(3, fold.ChangedLines);
    }

    [Fact]
    public void AddedRegistrationsBesideARemovedOneFoldButTheRemovalStaysOpen()
    {
        var head = ProgramBase.Replace("builder.Services.AddScoped<IOrderService, OrderService>();", """
            builder.Services.AddScoped<IInvoiceExportService, InvoiceExportService>();
            builder.Services.AddSingleton<ExportFileNamer>();
            """, StringComparison.Ordinal);

        var hunks = Changed("src/Program.cs", ProgramBase, head);

        Assert.Equal(2, FoldedPart(hunks).ChangedLines);
        Assert.Contains(hunks, h => h.Fold == FoldKind.Open && h.ChangedLines == 1);
    }

    [Fact]
    public void ARegistrationInsideAnIfOrWithALambdaStaysOpen()
    {
        var head = ProgramBase.Replace("builder.Services.AddScoped<IOrderService, OrderService>();", """
            builder.Services.AddScoped<IOrderService, OrderService>();
            if (builder.Environment.IsDevelopment())
            {
                builder.Services.AddScoped<IPaymentGateway, FakeGateway>();
                builder.Services.AddScoped<IMailer, FakeMailer>();
            }
            builder.Services.AddScoped(sp => new DashboardService(sp.GetRequiredService<ShopDbContext>()));
            builder.Services.AddScoped<IClock>(_ => new FixedClock());
            """, StringComparison.Ordinal);

        Assert.All(Changed("src/Program.cs", ProgramBase, head), h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    // ---------------------------------------------------------------- DTOs

    [Fact]
    public void ANewDtoOfAutoPropertiesFoldsAndListsEveryAttribute()
    {
        var hunks = Added("src/Models/OrderEditInput.cs", """
            using System.ComponentModel.DataAnnotations;

            namespace Shop.Models;

            public sealed class OrderEditInput
            {
                [Required]
                public int Id { get; set; }

                [Required]
                public string Status { get; set; }

                [Required, StringLength(500)]
                public string? Notes { get; init; }

                public decimal ShippingCost { get; set; }
            }
            """);

        var fold = FoldedPart(hunks);
        Assert.Equal(["OrderEditInput: 4 properties · [Required] ×3 · [StringLength(500)] on Notes"], fold.Reasons);
        Assert.Contains(hunks, h => h.Fold == FoldKind.Open);
    }

    [Theory]
    [InlineData("public string Author { get; set; } = \"system\";", "an initialiser")]
    [InlineData("public string Display => $\"{Id}\";", "a computed getter")]
    [InlineData("public string Total { get { return \"x\"; } }", "a getter body")]
    [InlineData("[JsonIgnore] public string Secret { get; set; }", "a non-validation attribute")]
    [InlineData("[BindProperty(SupportsGet = true)] public string Status { get; set; }", "SupportsGet")]
    [InlineData("public void Normalise() { }", "a method")]
    [InlineData("private string _cache;", "a field")]
    public void ADtoWithOneNonBoilerplateMemberStaysOpen(string member, string because)
    {
        var hunks = Added("src/Models/NoteDto.cs", $$"""
            namespace Shop.Models;

            public sealed class NoteDto
            {
                public int Id { get; set; }

                public string Body { get; set; }

                {{member}}
            }
            """);

        Assert.True(hunks.All(h => h.Fold == FoldKind.Open), because);
    }

    [Fact]
    public void ABindableModelNeverFoldsEvenWhenItLooksLikeADto()
    {
        var page = """
            namespace Shop.Pages;

            public class EditModel : PageModel
            {
                [BindProperty]
                public CustomerInput Input { get; set; } = new();
            }
            """;
        var input = """
            namespace Shop.Pages;

            public sealed class CustomerInput
            {
                public string Name { get; set; }

                public bool IsAdmin { get; set; }
            }
            """;

        var hunks = TriageHarness.Run(new TestFile("src/Pages/Edit.cshtml.cs", null, page), new TestFile("src/Pages/CustomerInput.cs", null, input)).Hunks;

        Assert.All(hunks, h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    [Fact]
    public void AHandlerParameterModelAndAnEntityNeverFold()
    {
        var page = "namespace Shop.Pages; public class ExportModel : PageModel { public void OnPostAsync(ExportRequest request) { } }";
        var request = "namespace Shop.Pages;\n\npublic sealed class ExportRequest\n{\n    public int Year { get; set; }\n\n    public int Month { get; set; }\n}\n";
        var entity = "namespace Shop.Data.Entities;\n\npublic class CustomerNote\n{\n    public int Id { get; set; }\n\n    public string Body { get; set; }\n}\n";

        var hunks = TriageHarness.Run(
            new TestFile("src/Pages/Export.cshtml.cs", null, page),
            new TestFile("src/Pages/ExportRequest.cs", null, request),
            new TestFile("src/Data/Entities/CustomerNote.cs", null, entity)).Hunks;

        Assert.All(hunks, h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    [Fact]
    public void AnExistingDtoGainingPlainPropertiesFoldsButAnyOtherEditStaysOpen()
    {
        var before = "namespace Shop.Models;\n\npublic sealed class OrderSummaryDto\n{\n    public int Id { get; set; }\n\n    public string Number { get; set; }\n}\n";
        var grown = before.Replace("    public string Number { get; set; }\n", "    public string Number { get; set; }\n\n    [Display(Name = \"Customer\")]\n    public string CustomerName { get; set; }\n", StringComparison.Ordinal);
        var retyped = before.Replace("public string Number { get; set; }", "public long Number { get; set; }", StringComparison.Ordinal);

        Assert.Contains(Changed("src/Models/OrderSummaryDto.cs", before, grown), h => h.Fold == FoldKind.Boilerplate);
        Assert.All(Changed("src/Models/OrderSummaryDto.cs", before, retyped), h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    // ---------------------------------------------------------------- mapping

    private const string MapperBase = """
        namespace Shop.Mapping;

        public static class OrderMapper
        {
            public static OrderSummaryDto ToSummary(Order order)
            {
                var dto = new OrderSummaryDto();
                return dto;
            }
        }
        """;

    [Fact]
    public void APlainAssignmentBlockFolds()
    {
        var head = MapperBase.Replace("        var dto = new OrderSummaryDto();\n", "        var dto = new OrderSummaryDto();\n        dto.Id = order.Id;\n        dto.Number = order.Number;\n        dto.Total = order.Total;\n        dto.Status = order.Status;\n", StringComparison.Ordinal);

        var fold = FoldedPart(Changed("src/Mapping/OrderMapper.cs", MapperBase, head));

        Assert.Equal(["4 assignments copying order → dto: Id, Number, Total, Status"], fold.Reasons);
    }

    [Fact]
    public void AnAssignmentWithLogicStaysOpen()
    {
        var head = MapperBase.Replace("        var dto = new OrderSummaryDto();\n", "        var dto = new OrderSummaryDto();\n        dto.Id = order.Id;\n        dto.Number = order.Number.Trim();\n        dto.Total = order.Total;\n", StringComparison.Ordinal);

        Assert.All(Changed("src/Mapping/OrderMapper.cs", MapperBase, head), h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    [Fact]
    public void CopyingOntoAnExistingObjectOrInsideAPageStaysOpen()
    {
        // Onto a parameter (an entity loaded elsewhere): where over-posting lives.
        var editorBase = "namespace Shop;\n\npublic static class ContactEditor\n{\n    public static void Apply(Contact contact, ContactInput input)\n    {\n        contact.Name = input.Name;\n    }\n}\n";
        var editor = editorBase.Replace("        contact.Name = input.Name;\n", "        contact.Name = input.Name;\n        contact.Company = input.Company;\n        contact.City = input.City;\n        contact.DoNotContact = input.DoNotContact;\n", StringComparison.Ordinal);
        Assert.All(Changed("src/ContactEditor.cs", editorBase, editor), h => Assert.Equal(FoldKind.Open, h.Fold));

        // Inside a page model, even onto a fresh object.
        var pageBase = "namespace Shop.Pages;\n\npublic class SettingsModel : PageModel\n{\n    public void OnPost()\n    {\n        var settings = new Settings();\n    }\n}\n";
        var page = pageBase.Replace("        var settings = new Settings();\n", "        var settings = new Settings();\n        settings.IsEnabled = Input.IsEnabled;\n        settings.Mode = Input.Mode;\n        settings.Notes = Input.Notes;\n", StringComparison.Ordinal);
        Assert.All(Changed("src/Pages/Settings.cshtml.cs", pageBase, page), h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    private const string ProfileBase = """
        namespace Shop.Mapping;

        public sealed class ShopProfile : Profile
        {
            public ShopProfile()
            {
                CreateMap<Order, OrderDto>();
            }
        }
        """;

    [Fact]
    public void PlainCreateMapCallsFold()
    {
        var head = ProfileBase.Replace("        CreateMap<Order, OrderDto>();\n", "        CreateMap<Order, OrderDto>();\n        CreateMap<Customer, CustomerDto>();\n        CreateMap<Invoice, InvoiceDto>();\n", StringComparison.Ordinal);

        Assert.Equal(["2 maps: Customer → CustomerDto, Invoice → InvoiceDto"], FoldedPart(Changed("src/Mapping/ShopProfile.cs", ProfileBase, head)).Reasons);
    }

    [Theory]
    [InlineData(".ForMember(d => d.Name, o => o.Ignore())")]
    [InlineData(".ReverseMap()")]
    public void AMapWithAnIgnoreOrReverseMapStaysOpen(string chained)
    {
        var head = ProfileBase.Replace("        CreateMap<Order, OrderDto>();\n", $"        CreateMap<Order, OrderDto>();\n        CreateMap<Customer, CustomerDto>(){chained};\n        CreateMap<Invoice, InvoiceDto>(){chained};\n", StringComparison.Ordinal);

        Assert.All(Changed("src/Mapping/ShopProfile.cs", ProfileBase, head), h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    // ---------------------------------------------------------------- test arrange

    [Fact]
    public void AnArrangeBlockOfConstructionFolds()
    {
        var hunks = Added("tests/Shop.Tests/OrderServiceTests.cs", """
            namespace Shop.Tests;

            public class OrderServiceTests
            {
                [Fact]
                public void SavesTheOrder()
                {
                    var customer = new Customer { Id = 7, Name = "Ada" };
                    var order = new Order { Id = 1, Customer = customer };
                    order.Number = "SO-1";
                    var sut = new OrderService(new FakeRepository());

                    sut.Save(order);

                    Assert.True(order.Saved);
                }
            }
            """);

        Assert.StartsWith("arrange in SavesTheOrder: 4 statements building Customer, Order, OrderService, FakeRepository", FoldedPart(hunks).Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AnArrangeWithAMockSetupStaysOpen()
    {
        var hunks = Added("tests/Shop.Tests/OrderServiceTests.cs", """
            namespace Shop.Tests;

            public class OrderServiceTests
            {
                [Fact]
                public void SavesTheOrder()
                {
                    var repository = new Mock<IOrderRepository>();
                    repository.Setup(r => r.Find(1)).Returns(new Order());
                    var sut = new OrderService(repository.Object);

                    sut.Save(1);
                }
            }
            """);

        Assert.All(hunks, h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    [Fact]
    public void AnArrangeThatPassesALambdaStaysOpen()
    {
        var hunks = Added("tests/Shop.Tests/ClientTests.cs", """
            namespace Shop.Tests;

            public class ClientTests
            {
                [Fact]
                public void Parses()
                {
                    var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("{}"),
                    });
                    var http = new HttpClient(handler);

                    Assert.NotNull(http);
                }
            }
            """);

        Assert.All(hunks, h => Assert.Equal(FoldKind.Open, h.Fold));
    }

    [Fact]
    public void RegistrationsSplitByOtherCodeAreSeparateRunsAndNeverHideIt()
    {
        var before = "namespace Shop;\n\npublic static class Setup\n{\n    public static void Add(IServiceCollection services)\n    {\n        services.AddScoped<A>();\n    }\n}\n";
        var after = before.Replace("        services.AddScoped<A>();\n    }\n", """
                    services.AddScoped<A>();
                    services.AddScoped<B>();
                }

                internal static void AddConsent(IServiceCollection services)
                    => services.AddScoped<ConsentService>();

                internal static void AddMore(IServiceCollection services)
                {
                    services.AddScoped<C>();
                    services.AddScoped<D>();
                }

            """.Replace("\r", "", StringComparison.Ordinal), StringComparison.Ordinal);

        var hunks = Changed("src/Setup.cs", before, after);

        Assert.All(hunks.Where(h => h.Fold == FoldKind.Boilerplate), h => Assert.Equal(h.ChangedLines, h.NewEnd - h.NewStart + 1));
        Assert.Contains(hunks, h => h.Fold == FoldKind.Open && h.ChangedLines >= 2);
    }

    [Fact]
    public void RazorMarkupNeverFolds()
    {
        var hunks = Added("src/Pages/Edit.cshtml", "@page\n<form>\n    <input asp-for=\"Input.Name\" />\n    <input type=\"hidden\" asp-for=\"Input.Id\" />\n</form>\n");

        Assert.All(hunks, h => Assert.Equal(FoldKind.Open, h.Fold));
    }
}
